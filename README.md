# FanControl plugin for the Dell Precision 7820 Tower

A [FanControl](https://github.com/Rem0o/FanControl.Releases) plugin that lets you drive the
fans of a **Dell Precision 7820 Tower** from FanControl, using only Dell's own firmware
interfaces. No third-party kernel driver is needed, so it works with Memory Integrity (HVCI),
Secure Boot and the Vulnerable Driver Blocklist turned on.

The usual Dell fan tools (DellFanManagement, i8kfan, FanControl's built-in Dell support) rely on
the legacy Dell SMM "i8k" interface. The 7820's firmware doesn't implement that interface, so
those tools can't control the fans on this machine. This plugin uses a different path.

> [!CAUTION]
> ## ⚠️ This plugin writes to your BIOS flash chip
>
> The fan-zone controls are **BIOS setup settings**. **Every time a zone value changes, the BIOS
> writes it to the motherboard's SPI flash chip** (the chip that holds the BIOS). This was
> verified on real hardware: each write changes the UEFI `Setup` variable in NVRAM.
>
> Flash chips survive only a **limited number of erase/write cycles**. Wearing out the BIOS
> flash can **corrupt BIOS settings or leave the machine unable to boot**, and repairing that
> usually means replacing or reprogramming the chip on the motherboard.
>
> - The plugin writes **only when a value changes**, and **at most once every 30 seconds in
>   total, across all zones** (all zones are stored in the same BIOS variable). 30 seconds is a
>   hard minimum that the config file can't lower.
> - **Use fixed values or coarse, stepped curves** with wide hysteresis. Don't drive these
>   controls with smooth curves that change all the time.
> - **This is not a normal fan controller.** Use it to set a sensible fan floor, not to chase
>   the temperature second by second.
>
> **You use this plugin at your own risk.** See [the flash write details](#zone-writes-are-stored-in-flash)
> and the [Disclaimer](#disclaimer).

## What you get

The plugin adapts to the machine it runs on. At startup it checks which fan zones your BIOS
supports and which fans and temperature sensors are present, and it shows exactly those.
Machines with one or two CPUs, extra PCIe or FlexBay fans, or an HDD fan all work without
changes.

**Controls** (one per BIOS fan zone the BIOS supports):

| Control in FanControl | BIOS token | Fans affected (measured on a dual-CPU 7820) |
|---|---|---|
| Zone CPU/Memory | `0x0504` | CPU0, CPU1, SYS1, SYS2, REAR0, REAR1 |
| Zone PCIe | `0x0503` | SYS0 |
| Zone Upper PCIe | `0x0505` | small effect; more if upper PCIe fans are fitted |
| Zone FlexBay | `0x0506` | FlexBay fan, if fitted |

Which fans a zone drives depends on how the machine is configured, so yours may differ.
A zone token the BIOS doesn't support is skipped, and a message is logged.

> [!IMPORTANT]
> **The zone controls only work when the BIOS fan mode is `Auto`.** The `Low` and `High` modes
> are built-in BIOS fan-curve profiles that ignore the zone settings. In those modes the
> controls appear to do nothing. The plugin never changes the fan mode; it logs a warning at
> startup if the mode isn't `Auto`. Set it in BIOS setup or with the companion PowerShell
> script (`Dell7820Fan.ps1 mode auto`). Verified on hardware: in `High`, a CPU/Memory floor of
> 80 % changed nothing; in `Auto`, the CPU fans went from about 1500 to about 3450 rpm.

A zone value (0–100 %) sets a **minimum** fan level. The firmware keeps its own thermal control
and still speeds the fans up above that minimum when the system gets hot. You can raise the
fans above what the BIOS would choose. You can't push them below the firmware's own curve.

**Sensors** (read from Dell Command | Monitor):

- RPM of **every** fan Dell reports, under Dell's own names (for example `CPU0 FAN`, `SYS0 FAN`,
  `REAR0 FAN`). Fans with no reading at startup (not fitted) are hidden by default.
- **Every** temperature sensor Dell reports.

Each zone is paired with a fan sensor when that fan exists (CPU/Memory → `CPU0 FAN`,
PCIe → `SYS0 FAN`), so FanControl's pairing and calibration features work.

## Configuration (optional)

On first start the plugin writes `FanControl.DellPrecision7820.json`, filled with the defaults,
next to the plugin dll. Edit it and restart FanControl to:

- add, rename or remove zones (for example on another Dell model with different zone tokens)
- change which fan sensor a zone is paired with
- hide sensors, or show fans that have no reading
- change the write rate limit and the sensor poll interval

```jsonc
{
  "zones": [
    { "token": "0x0504", "name": "Zone CPU/Memory", "pairedFan": "CPU0 FAN" },
    { "token": "0x0503", "name": "Zone PCIe",       "pairedFan": "SYS0 FAN" },
    { "token": "0x0505", "name": "Zone Upper PCIe" },
    { "token": "0x0506", "name": "Zone FlexBay" }
  ],
  "hideSensors": [],               // sensor names exactly as shown in FanControl
  "showEmptySensors": false,       // true = also list fans/sensors with no reading
  "minWriteIntervalSeconds": 30,   // min seconds between ANY two zone writes (flash wear! floor 30)
  "sensorPollSeconds": 3
}
```

`pairedFan` must match a fan name exactly as FanControl shows it. Only add tokens you know are
fan-zone settings that take a 0–100 value: writing an arbitrary BIOS token can change unrelated
settings. If the file is invalid, the plugin logs the error and uses the defaults.

## Requirements

- Dell Precision 7820 Tower. Tested on BIOS 2.53.1. Other Dell workstations with the same
  zone tokens, such as the 5820 and 7920, may work, but they haven't been tested.
- Windows 10/11.
- [FanControl](https://getfancontrol.com/), **.NET 10 build** (tested with v284). The plugin is
  compiled against .NET 10, so it won't load in the .NET 4.8 or .NET 8 builds of FanControl.
  FanControl runs elevated, which the plugin needs: the Dell SMBIOS WMI interface denies access
  to non-admin processes.
- **Optional:** [Dell Command | Monitor](https://www.dell.com/support/kbdoc/en-us/000177080/dell-command-monitor)
  for the RPM and temperature sensors. Without it the zone controls still work, but you get no
  sensors from this plugin.

### Without Dell Command | Monitor

You don't strictly need it. Zone control works without it, and **FanControl already reads the
CPU temperatures itself**, using its own signed hardware-monitoring components, which keep
working with Memory Integrity turned on. Build your curves on FanControl's built-in **CPU
temperature** sensors (CPU package / core temperatures) instead of the Dell ones.

What you lose without Dell Command | Monitor:

- **Fan RPM.** Windows has no other way to read the fan speeds on this machine without a
  third-party driver. FanControl's pairing and calibration features need RPM, so they won't work.
- **Board temperatures**: front panel, PCIe ambient, memory and CPU VR.

## Installation

1. Download `FanControl.DellPrecision7820.zip` from the [Releases](../../releases) page.
2. In FanControl, open **Settings → Plugins → Install plugin…** and pick the zip.
   You can also copy `FanControl.DellPrecision7820.dll` into FanControl's `Plugins` folder yourself.
3. Restart FanControl. The four zone controls and the Dell sensors will appear.

## Zone writes are stored in flash

The zone tokens are BIOS setup settings. **Each change is written to the BIOS NVRAM (SPI flash).**
A test confirmed it: every zone write changes one byte of the UEFI `Setup` variable.

Flash chips last for a limited number of write cycles, so the plugin keeps writes down:

- It writes only when the value actually changes.
- It makes at most one write every 30 seconds, **shared across all zones**, because every
  zone is stored in the same `Setup` variable. If several zones change at once, they are
  written one after another, oldest change first, so a full update of four zones can take up
  to 2 minutes. `minWriteIntervalSeconds` can raise the limit, but values below 30 are ignored.
- On exit it puts back the values the zones had when FanControl started.

Even so, **a curve that keeps moving can still write up to 120 times an hour**, which is
about 1 million writes a year if FanControl runs around the clock. Each write adds a ~1.8 KB
copy of the variable to the 512 KB settings store. The flash only gets erased when that store
fills up and is compacted, roughly every 100–200 writes. With chips typically rated for
~100,000 erases, that worst case works out to roughly 10–20 years. Real use with a stepped
curve writes far less. Recommendations:

- Prefer **fixed values** or a **stepped curve with wide steps**, plus generous
  **hysteresis / step-up / step-down** settings in FanControl, so a zone changes rarely.
- Avoid fine-grained, constantly moving curves on these controls.

Use at your own risk. See [Disclaimer](#disclaimer).

## How it works

- **Control:** Dell's SMBIOS calling interface over WMI (`root\wmi`, class `BFn`, method `DoBFn`).
  Class 0 / select 0 reads a token and class 1 / select 0 writes one. This is the same interface
  Dell Command | Configure (`cctk`) uses. Calling it directly takes about 50 ms, much faster than
  starting `cctk`.
- **Sensors:** `root\dcim\sysman` → `DCIM_NumericSensor`, provided by Dell Command | Monitor.
  One query takes about 2 seconds, so a background thread polls it every 3 seconds and FanControl
  reads the cached values.

### Limitations

- **No per-fan control.** The CPU/Mem zone moves six fans together, because the BIOS exposes
  only these four zone tokens. Individual fans are reachable only through the embedded
  controller's port I/O, which needs a raw kernel driver. That is out of scope for this plugin.
- Control is a minimum, not an exact duty cycle: the firmware can always run fans faster.
- The plugin can't make the fans quieter than the BIOS's own automatic behaviour.

## Building from source

No .NET SDK is required. `build.ps1` uses the Roslyn compiler that ships with PowerShell 7 and
compiles against the installed .NET 10 runtime assemblies. This matters: the output must
target the same runtime as FanControl, not PowerShell's newer one.

Prerequisites: PowerShell 7, the .NET 10 runtime, and a FanControl install. The build references
`FanControl.Plugins.dll` and `System.Management.dll` from that install.

```powershell
pwsh -File build.ps1 -FanControlDir "C:\path\to\FanControl"           # build only
pwsh -File build.ps1 -FanControlDir "C:\path\to\FanControl" -Install  # build + copy into Plugins\
```

If you have the .NET SDK you can also build it as an ordinary class library that references
`FanControl.Plugins.dll` and `System.Management`.

## Troubleshooting

FanControl writes the plugin's messages, prefixed with `[Dell7820]`, to its log file.

| Symptom | Likely cause |
|---|---|
| `Dell SMBIOS WMI interface (root\wmi:BFn) not found` | Not a supported Dell, or the Dell WMI ACPI driver isn't present. |
| `Access denied` | FanControl isn't running elevated. |
| The zone controls do nothing | The BIOS fan mode isn't `Auto` (look for `WARNING: BIOS fan mode is …` in the log). Switch it to `Auto`. |
| `Dell Command \| Monitor not available` | Dell Command \| Monitor isn't installed. The controls still work, but there are no Dell sensors. Use FanControl's built-in CPU temperatures (see [Without Dell Command \| Monitor](#without-dell-command--monitor)). |
| `write of token 0x05xx failed, status …` | The BIOS rejected the write. A BIOS admin password can block setting changes. |
| `zone 0x05xx (…) not supported by this BIOS, skipped` | Normal on models without that zone. Remove it from the config to silence the message. |
| `paired fan '…' not found, left unpaired` | That fan isn't present. Set `pairedFan` to one of your fan names, or remove it. |
| A fan or sensor is missing | It had no reading at startup. Set `showEmptySensors` to `true`. |

## Disclaimer

This is an unofficial community project. It is not affiliated with or endorsed by Dell.
It changes BIOS settings on your machine. The authors aren't responsible for damage, data loss
or hardware wear. Keep an eye on your temperatures after setting it up.

## License

[MIT](LICENSE). The software is provided "as is", without warranty of any kind.
