<#
  Installs (or upgrades) Modbus Gateway on this machine. Run elevated, from the folder holding the published files:
    ModbusGatewayService.exe, ModbusGatewayTray.exe, appsettings.json

  - Copies to C:\Program Files\ModbusGateway (an existing appsettings.json is kept unless -ReplaceConfig).
  - Close any other program using the serial port first: only one program can open a COM port at a time.
  - Creates the "ModbusGateway" service: automatic start, restart on failure.
  - Firewall rule: inbound TCP on the gateway port, local subnet only.
  - Tray icon starts at login for every user, and is started now in any logged-on session.
#>
param(
    [string]$Source = $PSScriptRoot,
    [string]$Target = "C:\Program Files\ModbusGateway",
    [switch]$ReplaceConfig
)
$ErrorActionPreference = "Stop"
$svc = "ModbusGateway"
$data = "C:\ProgramData\ModbusGateway"

foreach ($f in "ModbusGatewayService.exe", "ModbusGatewayTray.exe", "appsettings.json") {
    if (-not (Test-Path (Join-Path $Source $f))) { throw "Missing $f in $Source" }
}

# 1. Stop our own service / tray if this is an upgrade
if (Get-Service $svc -ErrorAction SilentlyContinue) { Stop-Service $svc -Force; Write-Host "Stopped existing service" }
Get-Process ModbusGatewayTray -ErrorAction SilentlyContinue | Stop-Process -Force

Start-Sleep 1   # let the old service release the serial port on an upgrade

# 2. Copy files
New-Item -ItemType Directory -Force $Target, "$data\logs" | Out-Null
Copy-Item (Join-Path $Source "ModbusGatewayService.exe"), (Join-Path $Source "ModbusGatewayTray.exe") $Target -Force
$cfg = Join-Path $Target "appsettings.json"
if ($ReplaceConfig -or -not (Test-Path $cfg)) { Copy-Item (Join-Path $Source "appsettings.json") $cfg -Force; Write-Host "Wrote appsettings.json" }
else { Write-Host "Kept existing appsettings.json" }

# 3. Service
$exe = Join-Path $Target "ModbusGatewayService.exe"
if (-not (Get-Service $svc -ErrorAction SilentlyContinue)) {
    sc.exe create $svc binPath= "`"$exe`"" start= auto DisplayName= "Modbus Gateway (RTU to TCP)" | Out-Null
    sc.exe description $svc "Bridges Modbus TCP clients to a Modbus RTU (RS-485) device on a serial port." | Out-Null
    Write-Host "Created service $svc"
}
sc.exe failure $svc reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
sc.exe failureflag $svc 1 | Out-Null

# 4. Firewall
$gw = (Get-Content $cfg -Raw | ConvertFrom-Json).Gateway
$port = $gw.Tcp.Port; if (-not $port) { $port = 502 }
$statusPort = $gw.StatusPort; if (-not $statusPort) { $statusPort = 5021 }
Get-NetFirewallRule -DisplayName "Modbus Gateway TCP" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName "Modbus Gateway TCP" -Direction Inbound -Protocol TCP -LocalPort $port `
    -Program $exe -RemoteAddress LocalSubnet -Action Allow -Profile Any | Out-Null
Write-Host "Firewall: TCP $port from local subnet"

# 5. Start
Start-Service $svc
Start-Sleep 3
$st = try { Invoke-RestMethod "http://127.0.0.1:$statusPort/status" -TimeoutSec 3 } catch { $null }
if ($st) {
    Write-Host "Service running: $($st.portName) open=$($st.portOpen) ($($st.portState)), TCP $($st.tcpEndpoint)"
    if (-not $st.portOpen) {
        Write-Warning "$($st.portName) is not open. Check the port name in $cfg and that no other program is using it; the service retries every few seconds."
    }
}
else { Write-Warning "Service started but /status didn't answer yet - check $data\logs" }

# 6. Tray: start at login for everyone, and now in logged-on sessions
$tray = Join-Path $Target "ModbusGatewayTray.exe"
Set-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "ModbusGatewayTray" -Value "`"$tray`""
$lnk = Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "Modbus Gateway.lnk"
$sh = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk); $sh.TargetPath = $tray; $sh.Arguments = "--show"; $sh.Save()

$users = (query user 2>$null) | Select-Object -Skip 1 | ForEach-Object { ($_.Trim() -replace '^>', '' -split '\s+')[0] }
foreach ($u in $users) {
    $task = "ModbusGatewayTrayStart"
    $action = New-ScheduledTaskAction -Execute $tray
    $principal = New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive
    Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Force | Out-Null
    Start-ScheduledTask -TaskName $task
    Start-Sleep 1
    Unregister-ScheduledTask -TaskName $task -Confirm:$false
    Write-Host "Started tray for $u"
}
Write-Host "Done."
