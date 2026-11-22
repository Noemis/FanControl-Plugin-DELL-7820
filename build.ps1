# Builds FanControl.DellPrecision7820.dll for FanControl (.NET 10) without a .NET SDK:
# uses the Roslyn compiler bundled with PowerShell 7, compiling against the installed
# .NET 10 runtime assemblies so the output doesn't reference pwsh's newer runtime.
#   pwsh -File build.ps1 [-FanControlDir <path>] [-Install]
param(
    [string]$FanControlDir = "${env:ProgramFiles(x86)}\FanControl",
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')

$rt = Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.NETCore.App' -Directory |
    Where-Object Name -like '10.*' | Sort-Object { [version]$_.Name } | Select-Object -Last 1
if (-not $rt) { throw '.NET 10 runtime not found' }

$refPaths = @(Get-ChildItem $rt.FullName -Filter *.dll | Where-Object {
        try { [void][Reflection.AssemblyName]::GetAssemblyName($_.FullName); $true } catch { $false } } |
    ForEach-Object FullName)
$refPaths += Join-Path $FanControlDir 'FanControl.Plugins.dll'
$refPaths += Join-Path $FanControlDir 'System.Management.dll'
$refs = [Microsoft.CodeAnalysis.MetadataReference[]]@($refPaths | ForEach-Object {
        [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_) })

$src = Get-Content (Join-Path $PSScriptRoot 'DellPrecision7820Plugin.cs') -Raw
$parseOpts = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::new([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Latest)
$tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($src, $parseOpts, '', $null, [Threading.CancellationToken]::None)
$opts = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).
    WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
$comp = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('FanControl.DellPrecision7820',
    [Microsoft.CodeAnalysis.SyntaxTree[]]@($tree), $refs, $opts)

$out = Join-Path $PSScriptRoot 'FanControl.DellPrecision7820.dll'
$fs = [IO.File]::Create($out)
try { $res = $comp.Emit($fs) } finally { $fs.Dispose() }
$res.Diagnostics | Where-Object { $_.Severity -ge [Microsoft.CodeAnalysis.DiagnosticSeverity]::Warning } |
    ForEach-Object { $_.ToString() }
if (-not $res.Success) { Remove-Item $out; throw 'build failed' }
"built $out against .NET $($rt.Name)"

if ($Install) {
    Copy-Item $out (Join-Path $FanControlDir 'Plugins') -Force
    "installed to $FanControlDir\Plugins (restart FanControl to load it)"
}
