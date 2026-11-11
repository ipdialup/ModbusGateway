<#
  Removes Modbus Gateway. Run elevated.
    -KeepFiles    leaves C:\Program Files\ModbusGateway in place (just stops and removes the service).
    -RemoveData   also deletes C:\ProgramData\ModbusGateway (logs).
#>
param([switch]$KeepFiles, [switch]$RemoveData, [string]$Target = "C:\Program Files\ModbusGateway")
$ErrorActionPreference = "Continue"
$svc = "ModbusGateway"
$data = "C:\ProgramData\ModbusGateway"

Get-Process ModbusGatewayTray -ErrorAction SilentlyContinue | Stop-Process -Force
if (Get-Service $svc -ErrorAction SilentlyContinue) {
    Stop-Service $svc -Force
    sc.exe delete $svc | Out-Null
    Write-Host "Removed service $svc"
}
Remove-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "ModbusGatewayTray" -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "Modbus Gateway.lnk") -ErrorAction SilentlyContinue
Get-NetFirewallRule -DisplayName "Modbus Gateway TCP" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if (-not $KeepFiles) { Start-Sleep 1; Remove-Item $Target -Recurse -Force -ErrorAction SilentlyContinue }

if ($RemoveData) { Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue; Write-Host "Removed $data" }
Write-Host "Done."
