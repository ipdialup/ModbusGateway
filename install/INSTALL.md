# Installing Modbus Gateway

Modbus Gateway bridges Modbus TCP clients to a Modbus RTU (RS-485) device on a Windows serial port. This package holds:

| File | What it is |
|---|---|
| `ModbusGatewayService.exe` | The gateway, run as the Windows service `ModbusGateway` |
| `ModbusGatewayTray.exe` | Tray icon and status window |
| `appsettings.json` | Default configuration |
| `install.ps1` | Installs or upgrades |
| `uninstall.ps1` | Removes it |

The exes are self-contained, so .NET does not need to be installed. They need 64-bit Windows 10/11 or Windows Server 2016 or later.

## Before you install

1. Plug in the USB RS-485 adapter and note its COM port (Device Manager > Ports (COM & LPT)).
2. Close any other program that uses that COM port. Only one program can open a COM port at a time.
3. Check the serial settings in `appsettings.json` match your device. The defaults are `COM4`, 9600 baud, 8 data bits, no parity, 1 stop bit, with Modbus TCP on port 502. See [Configuration](#configuration).

## Install

1. Unzip the package to a folder, for example `C:\Temp\ModbusGateway`.
2. Open PowerShell **as Administrator** in that folder.
3. Run:

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass -Force
   Get-ChildItem | Unblock-File
   .\install.ps1
   ```

The exes aren't code-signed, so Windows SmartScreen may warn you the first time they run. Click **More info**, then **Run anyway**.

The installer:
- Copies the files to `C:\Program Files\ModbusGateway`.
- Creates the `ModbusGateway` service. It starts automatically and restarts if it fails.
- Adds a firewall rule allowing the Modbus TCP port from the local subnet only.
- Starts the tray app at login for every user, and starts it now for anyone logged on.
- Adds a "Modbus Gateway" desktop shortcut that opens the status window.

It ends by printing the service status. `open=True` means the serial port is open. If you see `open=False`, check the port name and that nothing else is using the port.

## Upgrade

Unzip the new package and run `.\install.ps1` again, as above. Your existing `C:\Program Files\ModbusGateway\appsettings.json` is kept. To overwrite it with the package's defaults, run `.\install.ps1 -ReplaceConfig`.

## Check it is working

- **Tray icon:** green means OK, amber means errors in the last minute, red means the serial port is closed, and grey means the service is not running. Double-click it for the full status window.
- **Status JSON:** open http://127.0.0.1:5021/status on the gateway machine.
- **Logs:** `C:\ProgramData\ModbusGateway\logs`, one file per day, kept for 30 days.
- **Clients:** point your Modbus TCP client (for example Home Assistant) at this machine's IP on port 502, using the device's slave/unit ID.

## Configuration

Edit `C:\Program Files\ModbusGateway\appsettings.json` and then restart the service:

```powershell
Restart-Service ModbusGateway
```

| Setting (under `Gateway`) | Default | Notes |
|---|---|---|
| `Serial.PortName` | `COM4` | |
| `Serial.BaudRate` | `9600` | |
| `Serial.Parity` | `None` | None, Even or Odd |
| `Serial.DataBits` | `8` | |
| `Serial.StopBits` | `One` | One or Two |
| `Tcp.Bind` | `0.0.0.0` | The address to listen on |
| `Tcp.Port` | `502` | Run `install.ps1` again after changing it, so the firewall rule follows |
| `Tcp.MaxClients` | `8` | |
| `ResponseTimeoutMs` | `1000` | How long to wait for the device to reply |
| `InterFrameDelayMs` | `50` | Quiet time between frames. Some devices need one. |
| `ReadRetries` | `1` | Reads are retried; writes never are |
| `StatusPort` | `5021` | Status endpoint, bound to 127.0.0.1 only |

## Uninstall

From an elevated PowerShell in the package folder (a copy of `uninstall.ps1` is fine from anywhere):

```powershell
.\uninstall.ps1              # removes the service, tray startup, shortcut, firewall rule and program files
.\uninstall.ps1 -KeepFiles   # leaves C:\Program Files\ModbusGateway in place
.\uninstall.ps1 -RemoveData  # also deletes the logs in C:\ProgramData\ModbusGateway
```
