// FanControl plugin for the Dell Precision 7820 Tower (and similar Dell workstations).
//
// Controls: the BIOS fan-zone floor tokens (default 0x503-0x506) through Dell's SMBIOS
//           WMI interface (root\wmi BFn.DoBFn). A zone value is a MINIMUM fan level;
//           the firmware still ramps above it. Each token is probed at startup and only
//           the ones this BIOS supports are shown.
// Sensors:  every fan tachometer and temperature sensor Dell Command | Monitor reports
//           (root\dcim\sysman), polled on a background thread because one query takes ~2 s.
// Config:   optional FanControl.DellPrecision7820.json next to the dll (written with the
//           defaults on first run) to add/rename/remove zones, pair fans, or hide sensors.
//
// Writes are rate-limited (only on change, at most ONE write across ALL zones every
// MinWriteInterval, never less than 30 s) because every zone write rewrites the same UEFI
// "Setup" variable in SPI flash.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using FanControl.Plugins;

namespace DellPrecision7820
{
    public sealed class DellPrecision7820Plugin : IPlugin2
    {
        readonly IPluginLogger _log;
        PluginConfig _cfg;
        DellSmbios _smbios;
        readonly List<ZoneControl> _zones = new List<ZoneControl>();
        readonly Dictionary<string, DcimSensor> _dcim = new Dictionary<string, DcimSensor>(); // insertion-ordered (no removals)
        Thread _poller;
        CancellationTokenSource _cts;

        public DellPrecision7820Plugin(IPluginLogger logger) { _log = logger; }

        public string Name => "Dell Precision 7820";

        // hard floor, shared by all zones: they all live in the same UEFI "Setup" variable,
        // so every zone write is another SetVariable on that one variable in SPI flash
        internal const double MinWriteIntervalFloorSeconds = 30;
        internal TimeSpan MinWriteInterval => TimeSpan.FromSeconds(Math.Max(MinWriteIntervalFloorSeconds, _cfg.MinWriteIntervalSeconds));

        public void Initialize()
        {
            _cfg = PluginConfig.Load(Log);

            _zones.Clear();
            try { _smbios = new DellSmbios(); }
            catch (Exception e) { Log("no zone controls: " + e.Message); _smbios = null; }

            if (_smbios != null)
            {
                foreach (var z in _cfg.Zones)
                {
                    int token;
                    try { token = z.ParseToken(); }
                    catch (Exception e) { Log($"bad zone entry '{z.Name}': {e.Message}"); continue; }

                    int t;
                    try { t = _smbios.ReadToken(token); }
                    catch (Exception e) { Log($"zone 0x{token:X4} ({z.Name}) not supported by this BIOS, skipped: {e.Message}"); continue; }

                    // keep the original ids of the 7820 zones so saved FanControl profiles still match
                    var key = token switch { 0x0504 => "cpumem", 0x0503 => "pcie", 0x0505 => "upperpcie", 0x0506 => "flexbay", _ => $"{token:X4}" };
                    _zones.Add(new ZoneControl(this, key, z.Name ?? $"Zone 0x{token:X4}", token, t, z.PairedFan));
                    Log($"zone 0x{token:X4} ({z.Name}) value at start = {t}");
                }
            }

            if (_smbios != null) WarnIfZonesIgnored();

            _dcim.Clear();
            try { PollDcim(); }
            catch (Exception e) { Log("Dell Command | Monitor not available, no RPM/temp sensors: " + e.Message); }

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            var interval = TimeSpan.FromSeconds(Math.Max(1, _cfg.SensorPollSeconds));
            _poller = new Thread(() =>
            {
                while (!ct.WaitHandle.WaitOne(interval))
                {
                    try { PollDcim(); } catch (Exception e) { Log("DCIM poll failed: " + e.Message); }
                }
            }) { IsBackground = true, Name = "DellPrecision7820 DCIM poll" };
            _poller.Start();
        }

        // The fan-mode tokens (0x332 auto, 0x334, 0x335 low, 0x333 high) all read back one shared
        // value. Zone floors only apply in "auto" (0); low/high are BIOS fan-curve profiles that
        // ignore the zones. The plugin never changes the mode, it only warns.
        void WarnIfZonesIgnored()
        {
            try
            {
                int mode = _smbios.ReadToken(0x0332);
                if (mode != 0)
                {
                    var name = mode switch { 2 => "low", 3 => "high", _ => mode.ToString(CultureInfo.InvariantCulture) };
                    Log($"WARNING: BIOS fan mode is '{name}', which ignores the zone controls. " +
                        "Set the fan mode to Auto (BIOS setup, or Dell7820Fan.ps1 mode auto) for the zones to take effect.");
                }
            }
            catch (Exception e) { Log("could not read fan mode: " + e.Message); }
        }

        public void Load(IPluginSensorsContainer container)
        {
            lock (_dcim)
            {
                var hidden = new HashSet<string>(_cfg.HideSensors ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                foreach (var s in _dcim.Values)
                {
                    if (hidden.Contains(s.Name)) continue;
                    // sensors with no reading at startup are not fitted (e.g. HDD/PSU fan) unless asked for
                    if (s.Value == null && !_cfg.ShowEmptySensors) continue;
                    (s.IsFan ? container.FanSensors : container.TempSensors).Add(s);
                }

                // pair each zone with its configured fan, if that fan exists on this machine
                foreach (var z in _zones)
                {
                    if (string.IsNullOrWhiteSpace(z.PairedFanName)) continue;
                    var fan = _dcim.Values.FirstOrDefault(s => s.IsFan &&
                        string.Equals(s.Name, z.PairedFanName, StringComparison.OrdinalIgnoreCase));
                    if (fan != null) z.PairedFanSensorId = fan.Id;
                    else Log($"paired fan '{z.PairedFanName}' for {z.Name} not found, left unpaired");
                }
            }
            container.ControlSensors.AddRange(_zones);
        }

        public void Update() => FlushOne();

        DateTime _lastWrite = DateTime.MinValue;
        readonly object _writeLock = new object();

        // Writes at most one pending zone per MinWriteInterval (shared by all zones);
        // the zone that has been waiting longest goes first so none is starved.
        internal void FlushOne()
        {
            lock (_writeLock)
            {
                if (DateTime.UtcNow - _lastWrite < MinWriteInterval) return;
                var next = _zones.Where(z => z.HasPending).OrderBy(z => z.PendingSince).FirstOrDefault();
                if (next == null) return;
                _lastWrite = DateTime.UtcNow; // count failed attempts too, so a failing BIOS isn't hammered
                next.WritePending();
            }
        }

        public void Close()
        {
            _cts?.Cancel();
            _poller?.Join(TimeSpan.FromSeconds(5));
            _cts?.Dispose(); _cts = null; _poller = null;
            foreach (var z in _zones)
            {
                try { z.Reset(); } catch (Exception e) { Log($"restore {z.Name} failed: {e.Message}"); }
            }
            _zones.Clear();
        }

        void PollDcim()
        {
            using var searcher = new ManagementObjectSearcher(@"root\dcim\sysman",
                "SELECT DeviceID, ElementName, CurrentReading, SensorType FROM DCIM_NumericSensor");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var type = Convert.ToInt32(mo["SensorType"]);
                    if (type != 2 && type != 5) continue; // 2 = temperature, 5 = tachometer
                    var name = ((string)mo["ElementName"] ?? "").Split(':').Last().Trim();
                    var devId = (string)mo["DeviceID"];
                    if (string.IsNullOrEmpty(devId)) devId = name;
                    var reading = mo["CurrentReading"];
                    float? value = reading == null ? (float?)null : Convert.ToSingle(reading);
                    lock (_dcim)
                    {
                        var id = DcimSensor.MakeId(devId);
                        if (!_dcim.TryGetValue(id, out var s))
                        {
                            if (string.IsNullOrEmpty(name)) name = devId;
                            // keep display names unique (FanControl shows them side by side)
                            var display = name; int n = 2;
                            while (_dcim.Values.Any(o => o.Name == display)) display = $"{name} #{n++}";
                            _dcim[id] = s = new DcimSensor(id, display, type == 5);
                        }
                        s.Latest = value;
                    }
                }
            }
        }

        internal DellSmbios Smbios => _smbios;
        internal void Log(string msg) => _log?.Log("[Dell7820] " + msg);

        // ---------------------------------------------------------------- sensors

        sealed class DcimSensor : IPluginSensor
        {
            public DcimSensor(string id, string name, bool isFan) { Id = id; Name = name; IsFan = isFan; }
            public static string MakeId(string deviceId) => "Dell7820/" + deviceId.Replace(' ', '_');
            public string Id { get; }
            public string Name { get; }
            public bool IsFan { get; }
            public volatile object Boxed;
            public float? Latest { set => Boxed = value; }
            public float? Value => (float?)Boxed;
            public void Update() { }
        }

        sealed class ZoneControl : IPluginControlSensor2
        {
            readonly DellPrecision7820Plugin _p;
            readonly int _token;
            readonly int _original;
            int _written;
            int? _pending;

            public ZoneControl(DellPrecision7820Plugin p, string key, string label, int token, int original, string pairedFanName)
            {
                _p = p; Key = key; Name = label; _token = token; _original = original; _written = original;
                PairedFanName = pairedFanName;
            }

            public string Key { get; }
            public string Id => "Dell7820/zone/" + Key;
            public string Name { get; }
            public string PairedFanName { get; }
            public string PairedFanSensorId { get; set; }
            public float? Value => _pending ?? _written;
            public void Update() { }

            public bool HasPending => _pending.HasValue;
            public DateTime PendingSince { get; private set; }

            public void Set(float val)
            {
                int v = (int)Math.Round(Math.Clamp(val, 0f, 100f));
                if (v == _written) { _pending = null; }
                else
                {
                    if (_pending == null) PendingSince = DateTime.UtcNow; // keep its place in the queue
                    _pending = v;
                }
                _p.FlushOne();
            }

            public void Reset()
            {
                _pending = null;
                if (_written != _original) Write(_original);
            }

            // called by the plugin's shared rate limiter only
            public void WritePending()
            {
                if (_pending is int v) { _pending = null; Write(v); }
            }

            void Write(int v)
            {
                try
                {
                    _p.Smbios.WriteToken(_token, v);
                    _written = v;
                }
                catch (Exception e) { _p.Log($"write {Name}={v} failed: {e.Message}"); }
            }
        }
    }

    // -------------------------------------------------------------------- config

    sealed class ZoneDef
    {
        // hex string ("0x0504") or number
        [JsonPropertyName("token")] public JsonElement Token { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("pairedFan")] public string PairedFan { get; set; }

        public int ParseToken()
        {
            if (Token.ValueKind == JsonValueKind.Number) return Token.GetInt32();
            var s = Token.ValueKind == JsonValueKind.String ? Token.GetString().Trim() : throw new FormatException("token missing");
            return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? int.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : int.Parse(s, CultureInfo.InvariantCulture);
        }

        public static ZoneDef Of(int token, string name, string pairedFan) => new ZoneDef
        {
            Token = JsonDocument.Parse($"\"0x{token:X4}\"").RootElement.Clone(),
            Name = name,
            PairedFan = pairedFan,
        };
    }

    sealed class PluginConfig
    {
        [JsonPropertyName("zones")] public List<ZoneDef> Zones { get; set; }
        [JsonPropertyName("hideSensors")] public List<string> HideSensors { get; set; } = new List<string>();
        [JsonPropertyName("showEmptySensors")] public bool ShowEmptySensors { get; set; }
        [JsonPropertyName("minWriteIntervalSeconds")] public double MinWriteIntervalSeconds { get; set; } = 30;
        [JsonPropertyName("sensorPollSeconds")] public double SensorPollSeconds { get; set; } = 3;

        // Fan zones known on the Precision 7820. Which fans each zone drives depends on the
        // machine's fan configuration; zones the BIOS doesn't support are skipped at runtime.
        static List<ZoneDef> DefaultZones() => new List<ZoneDef>
        {
            ZoneDef.Of(0x0504, "Zone CPU/Memory",  "CPU0 FAN"),
            ZoneDef.Of(0x0503, "Zone PCIe",        "SYS0 FAN"),
            ZoneDef.Of(0x0505, "Zone Upper PCIe",  null),
            ZoneDef.Of(0x0506, "Zone FlexBay",     null),
        };

        static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static PluginConfig Load(Action<string> log)
        {
            var dir = Path.GetDirectoryName(typeof(PluginConfig).Assembly.Location);
            var path = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "FanControl.DellPrecision7820.json");
            PluginConfig cfg = null;
            if (path != null && File.Exists(path))
            {
                try
                {
                    cfg = JsonSerializer.Deserialize<PluginConfig>(File.ReadAllText(path), JsonOpts);
                    log($"config loaded from {path}");
                }
                catch (Exception e) { log($"config {path} invalid, using defaults: {e.Message}"); }
            }
            bool fresh = cfg == null;
            cfg ??= new PluginConfig();
            cfg.Zones ??= DefaultZones();
            if (fresh && path != null && !File.Exists(path))
            {
                try { File.WriteAllText(path, JsonSerializer.Serialize(cfg, JsonOpts)); log($"default config written to {path}"); }
                catch (Exception e) { log($"could not write default config: {e.Message}"); }
            }
            return cfg;
        }
    }

    // -------------------------------------------------------------------- SMBIOS via WMI

    sealed class DellSmbios
    {
        readonly ManagementObject _bfn;
        readonly ManagementClass _bdat;
        readonly object _lock = new object();

        public DellSmbios()
        {
            using var cls = new ManagementClass(@"root\wmi:BFn");
            foreach (ManagementObject i in cls.GetInstances()) _bfn = i;
            if (_bfn == null) throw new InvalidOperationException(@"Dell SMBIOS WMI interface (root\wmi:BFn) not found");
            _bdat = new ManagementClass(@"root\wmi:BDat");
        }

        int[] Call(int cls, int select, params int[] input)
        {
            var buf = new byte[32768];
            buf[0] = (byte)cls; buf[1] = (byte)(cls >> 8);
            buf[2] = (byte)select; buf[3] = (byte)(select >> 8);
            for (int k = 0; k < input.Length; k++)
                BitConverter.GetBytes(input[k]).CopyTo(buf, 4 + 4 * k);
            lock (_lock)
            {
                using var d = _bdat.CreateInstance();
                d["Bytes"] = buf;
                using var p = _bfn.GetMethodParameters("DoBFn");
                p["Data"] = d;
                using var r = _bfn.InvokeMethod("DoBFn", p, null);
                var o = (byte[])((ManagementBaseObject)r["Data"])["Bytes"];
                return Enumerable.Range(0, 4).Select(i => BitConverter.ToInt32(o, 20 + 4 * i)).ToArray();
            }
        }

        public int ReadToken(int id)
        {
            var o = Call(0, 0, id, 0, 0, 0);
            if (o[0] != 0) throw new InvalidOperationException($"read of token 0x{id:X4} failed, status {o[0]}");
            return o[1];
        }

        public void WriteToken(int id, int value)
        {
            var o = Call(1, 0, id, value, 0, 0);
            if (o[0] != 0) throw new InvalidOperationException($"write of token 0x{id:X4} failed, status {o[0]}");
        }
    }
}
