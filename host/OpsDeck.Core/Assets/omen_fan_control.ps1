<#
.SYNOPSIS
Reads and applies HP OMEN fan-control settings through installed signed OMEN Gaming Hub components.

.NOTES
Uses HP's own publisher-shared profile backend and PerformanceControl reload IPC.
No driver is installed and no EC/BIOS register is accessed directly.
#>
[CmdletBinding()]
param(
    [ValidateSet("status","auto","manual","max")]
    [string]$Action = "status",
    [ValidateRange(50,100)]
    [int]$SpeedPct = 70,
    [switch]$InternalWorker
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $InternalWorker) {
    $windowsPowerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    $forward = @("-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",$PSCommandPath,
        "-Action",$Action,"-SpeedPct",$SpeedPct,"-InternalWorker")
    & $windowsPowerShell @forward
    exit $LASTEXITCODE
}

if ($Action -eq "manual" -and (($SpeedPct % 5) -ne 0)) {
    throw "Manual fan speed must be 50-100 in 5 percent steps."
}

$package = Get-AppxPackage -Name "AD2F1837.OMENCommandCenter" -ErrorAction Stop
if (-not $package -or $package.PublisherId -ne "v10z8vjag6ke6") {
    throw "Expected signed OMEN Command Center package was not found."
}
$assemblyRoot = Join-Path $package.InstallLocation "OmenCommandCenterApp"
$profileFolder = Join-Path $env:LOCALAPPDATA "Publishers\v10z8vjag6ke6\OMEN"
$profileFile = Join-Path $profileFolder "profiles.json"
if (-not (Test-Path -LiteralPath $profileFile)) {
    throw "OMEN shared profile store was not found."
}

Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Win32;

public static class OpsDeckOmenFanBridge
{
    private static object _wrapper;
    private static Type _wrapperType;
    private static string _featurePath;

    private static Assembly LoadBytes(string path) { return Assembly.Load(File.ReadAllBytes(path)); }

    public static void Initialize(string root, string folder, string featurePath)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (s,e) => {
            string name = new AssemblyName(e.Name).Name + ".dll";
            if (name.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase)) return null;
            string path = Path.Combine(root, name);
            return File.Exists(path) ? LoadBytes(path) : null;
        };

        var profiles = LoadBytes(Path.Combine(root, "OmenProfilesBackend.dll"));
        var providerType = profiles.GetType("Hp.Omen.SDKs.ProfileProvider", true);
        var provider = Activator.CreateInstance(
            providerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new object[] { "profiles.json", folder },
            CultureInfo.InvariantCulture);

        _wrapperType = profiles.GetType("Hp.Omen.SDKs.OmenProfilesBackend.JsonRegistryStorageWrapper", true);
        _wrapper = Activator.CreateInstance(
            _wrapperType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new object[] { provider },
            CultureInfo.InvariantCulture);
        _featurePath = featurePath;
    }

    private static MethodInfo Method(string name, int argc)
    {
        return _wrapperType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .First(m => m.Name == name && m.GetParameters().Length == argc);
    }

    public static object Get(string key)
    {
        return Method("GetValue", 2).Invoke(_wrapper, new object[] { _featurePath, key });
    }

    public static void SetDword(string key, int value)
    {
        var method = _wrapperType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == "SetValue" && m.GetParameters().Length == 4);
        object result;
        if (method != null) result = method.Invoke(_wrapper, new object[] { _featurePath, key, value, RegistryValueKind.DWord });
        else result = Method("SetValue", 3).Invoke(_wrapper, new object[] { _featurePath, key, value });
        if (result is bool && !(bool)result) throw new InvalidOperationException("OMEN profile write was rejected: " + key);
    }

    public static void Reload(string root)
    {
        var module = LoadBytes(Path.Combine(root, "HP.Omen.PerformanceControlModule.dll"));
        var modelType = module.GetType("HP.Omen.PerformanceControlModule.Models.FanControlModel", true);
        var model = Activator.CreateInstance(
            modelType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new object[0],
            CultureInfo.InvariantCulture);
        var reload = modelType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .First(m => m.Name == "SendReloadSettings" && m.GetParameters().Length == 2);
        var result = reload.Invoke(model, new object[] { true, false });
        Task task = result as Task; if (task != null) task.GetAwaiter().GetResult();
    }
}
"@ -Language CSharp

$raw = Get-Content -LiteralPath $profileFile -Raw | ConvertFrom-Json
$zero = "00000000-0000-0000-0000-000000000000"
$userId = if ($raw.ACTIVEUSER -and $raw.ACTIVEUSER -ne "null") { [string]$raw.ACTIVEUSER } else { $zero }
$profileId = if ($raw.ACTIVEPROFILE -and $raw.ACTIVEPROFILE -ne "null") { [string]$raw.ACTIVEPROFILE } else { $zero }
$featurePath = "OmenProfile\$userId\$profileId\PerformanceControl"
[OpsDeckOmenFanBridge]::Initialize($assemblyRoot, $profileFolder, $featurePath)

function Read-Int([string]$key, [int]$fallback) {
    $value = [OpsDeckOmenFanBridge]::Get($key)
    if ($null -eq $value) { return $fallback }
    return [Convert]::ToInt32($value, [Globalization.CultureInfo]::InvariantCulture)
}

$fanSpeedRaw = [OpsDeckOmenFanBridge]::Get("FanSpeed")
$manualSupported = ($null -ne $fanSpeedRaw)
if ($Action -eq "manual" -and -not $manualSupported) {
    throw "OMEN profile does not expose manual FanSpeed."
}

if ($Action -ne "status") {
    $thermal = switch ($Action) {
        "max" { 0 }
        "auto" { 1 }
        "manual" { 2 }
    }
    $maxFan = if ($Action -eq "max") { 1 } else { 0 }

    [OpsDeckOmenFanBridge]::SetDword("MaxFan", [int]$maxFan)
    [OpsDeckOmenFanBridge]::SetDword("ThermalControl", [int]$thermal)
    if ($Action -eq "manual") {
        [OpsDeckOmenFanBridge]::SetDword("FanSpeed", [int]$SpeedPct)
    }
    [OpsDeckOmenFanBridge]::Reload($assemblyRoot)
    Start-Sleep -Milliseconds 700
}

$thermalNow = Read-Int "ThermalControl" 3
$maxFanNow = Read-Int "MaxFan" 0
$speedNow = Read-Int "FanSpeed" 70
$modeNow = switch ($thermalNow) {
    0 { "max" }
    1 { "auto" }
    2 { "manual" }
    default { "unknown" }
}
if ($maxFanNow -eq 1) { $modeNow = "max" }

[ordered]@{
    schema = "opsdeck.omen-fan-control.v1"
    supported = $true
    manual_supported = $manualSupported
    mode = $modeNow
    mode_id = $thermalNow
    fan_speed_pct = $speedNow
    max_fan = ($maxFanNow -eq 1)
    action = $Action
    profile_user = $userId
    profile_id = $profileId
    vendor_version = $package.Version.ToString()
} | ConvertTo-Json -Compress