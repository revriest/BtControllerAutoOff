# BT Controller Auto-Off

A lightweight Windows 11 system tray utility that automatically powers off idle Bluetooth controllers (DualSense, 8BitDo, Xbox, Switch Pro, etc.) without virtual drivers (no ViGEmBus) or background bloat.

## Why?
Unlike consoles, Windows 11 does not disconnect Bluetooth gamepads after inactivity—leaving DualSense and third-party controllers awake until their batteries drain. Full remappers solve this by installing virtual drivers that can strip native DualSense features or conflict with anti-cheat systems.

**BT Controller Auto-Off** passively monitors genuine HID button and analog stick activity via Windows Raw Input (`hid.dll`) and severs the Bluetooth link (`IOCTL_BTH_DISCONNECT_DEVICE`) once the idle timer expires—causing the controller's firmware to shut down cleanly.

## Features
- **Zero Virtual Drivers:** Pure Win32 Raw Input + native Bluetooth APIs.
- **Per-Device Idle Timers:** Tracks each Bluetooth controller's MAC address independently and ignores wired USB peripherals.
- **Noise & Gyro Filtering:** Inspects actual HID button/axis usages (`HidP_GetUsages` / `HidP_GetUsageValue`), ignoring DualSense rolling Bluetooth sequence counters and gyro drift.
- **Managed Bluetooth Devices Menu:** Automatically selects standard Bluetooth gamepads while letting you opt-in or opt-out any paired Bluetooth device.
- **Live Tray Status:** Hover over the tray icon to see the current inactivity counter; toggle **Run at Windows Startup** directly from the menu.

## Download & Run
1. Download `BtControllerAutoOff.exe` from the [Releases](https://github.com/revriest/BtControllerAutoOff/releases) page.
2. Run `BtControllerAutoOff.exe` (requires the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)).
3. Right-click the gamepad icon in your system tray to set your idle timeout or enable **Run at Windows Startup**.

> **Note on Windows SmartScreen:** Because this binary is unsigned, Windows may display a *Windows protected your PC* prompt on first run. Click **More info → Run anyway**, or inspect `Program.cs` and compile from source below.

## Build From Source
```powershell
git clone https://github.com/revriest/BtControllerAutoOff.git
cd BtControllerAutoOff
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false
```
