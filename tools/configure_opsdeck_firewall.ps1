param(
    [Parameter(Mandatory=$true)][string]$ProgramPath,
    [Parameter(Mandatory=$true)][string]$DeckIp,
    [switch]$Apply
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts=[IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
$runtime=[IO.Path]::GetFullPath((Join-Path $root 'runtime'))
$program=[IO.Path]::GetFullPath($ProgramPath)
if(-not (Test-Path -LiteralPath $program -PathType Leaf)){throw 'OpsDeck host executable not found.'}
if(-not ($program.StartsWith($artifacts,[StringComparison]::OrdinalIgnoreCase) -or $program.StartsWith($runtime,[StringComparison]::OrdinalIgnoreCase))){throw 'ProgramPath must be an OpsDeck artifacts or runtime host.'}
if([IO.Path]::GetFileName($program) -ne 'OpsDeck.Host.exe'){throw 'ProgramPath must end in OpsDeck.Host.exe.'}
$ipObj=$null
if(-not [Net.IPAddress]::TryParse($DeckIp,[ref]$ipObj) -or $ipObj.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork){throw 'DeckIp must be one IPv4 address.'}
if([Net.IPAddress]::IsLoopback($ipObj) -or $DeckIp -eq '0.0.0.0' -or $DeckIp -eq '255.255.255.255'){throw 'DeckIp is not a valid remote deck address.'}
$ruleName='ALAZ-OPSDECK-TCP-47231'
$display='ALAZ OPSDECK Telemetry TCP 47231'
$legacy=@(Get-NetFirewallRule -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object {
    $_.DisplayName -eq 'OpsDeck.Host' -or $_.Name -eq $ruleName -or $_.DisplayName -eq $display
})
$plan=[ordered]@{
    mode=if($Apply){'APPLY'}else{'DRY_RUN'}
    program=$program
    protocol='TCP'
    local_port=47231
    remote_address=$DeckIp
    profiles=@('Public','Private')
    legacy_rules=$legacy.Count
    inbound_udp_required=$false
}
if(-not $Apply){$plan | ConvertTo-Json -Depth 3;exit 0}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=[Security.Principal.WindowsPrincipal]::new($identity)
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator rights are required to apply firewall rules.'}
Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -Name $ruleName -DisplayName $display -Direction Inbound -Action Allow `
    -Program $program -Protocol TCP -LocalPort 47231 -RemoteAddress $DeckIp -Profile Public,Private | Out-Null
$created=Get-NetFirewallRule -Name $ruleName -ErrorAction Stop
$pf=$created|Get-NetFirewallPortFilter
$af=$created|Get-NetFirewallAddressFilter
$app=$created|Get-NetFirewallApplicationFilter
if($created.Direction -ne 'Inbound' -or $created.Action -ne 'Allow' -or $pf.Protocol -ne 'TCP' -or $pf.LocalPort -ne '47231' -or $af.RemoteAddress -ne $DeckIp -or $app.Program -ne $program){throw 'Created firewall rule failed verification.'}
$removed=0
foreach($r in $legacy){
    if($r.Name -eq $ruleName){continue}
    Remove-NetFirewallRule -Name $r.Name -ErrorAction Stop
    $removed++
}
$result=[ordered]@{
    applied=$true
    rule=$ruleName
    display_name=$display
    program=$program
    protocol='TCP'
    local_port=47231
    remote_address=$DeckIp
    profiles=@('Public','Private')
    removed_legacy_rules=$removed
    inbound_udp_rules_added=0
}
$result | ConvertTo-Json -Depth 3
