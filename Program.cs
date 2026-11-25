using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace BtControllerAutoOff;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
    }
}

public sealed class AppSettings
{
    public int TimeoutMinutes { get; set; } = 5;
    public HashSet<string> EnabledDevices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DisabledDevices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BluetoothDeviceEntry
{
    public ulong Address { get; init; }
    public string AddressHex => Address.ToString("X12");
    public string Name { get; init; } = "Unknown Device";
    public uint ClassOfDevice { get; init; }
    public bool IsConnected { get; init; }
    public bool IsDefaultGamepad
    {
        get
        {
            uint majorClass = (ClassOfDevice >> 8) & 0x1F;
            uint minorClass = (ClassOfDevice >> 2) & 0x3F;
            return majorClass == 0x05 && (minorClass == 0x01 || minorClass == 0x02);
        }
    }
}

public sealed class TrayAppContext : ApplicationContext
{
    private const string StartupRegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "BtControllerAutoOff";

    private readonly NotifyIcon _trayIcon;
    private readonly RawInputListener _listener;
    private readonly Timer _checkTimer;
    private readonly string _settingsPath;
    private AppSettings _settings;

    private readonly Dictionary<string, DateTime> _deviceLastInput = new(StringComparer.OrdinalIgnoreCase);

    public TrayAppContext()
    {
        string appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName);
        Directory.CreateDirectory(appDir);
        _settingsPath = Path.Combine(appDir, "settings.json");
        _settings = LoadSettings();

        _listener = new RawInputListener(OnControllerInput);

        _trayIcon = new NotifyIcon { Visible = true };

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => PopulateMenu(menu);
        _trayIcon.ContextMenuStrip = menu;

        PopulateMenu(menu);
        UpdateTrayVisuals();

        _checkTimer = new Timer { Interval = 5_000 }; // Check every 5 seconds
        _checkTimer.Tick += (_, _) => CheckIdle();
        _checkTimer.Start();
    }

    private void OnControllerInput(string? btAddressHex)
    {
        // Only track Bluetooth devices; ignore wired/USB devices like sim racing wheelbases
        if (!string.IsNullOrEmpty(btAddressHex))
        {
            _deviceLastInput[btAddressHex] = DateTime.UtcNow;
        }
    }

    private bool IsDeviceManaged(BluetoothDeviceEntry dev)
    {
        if (_settings.DisabledDevices.Contains(dev.AddressHex)) return false;
        if (_settings.EnabledDevices.Contains(dev.AddressHex)) return true;
        return dev.IsDefaultGamepad;
    }

    private void ToggleDeviceManaged(BluetoothDeviceEntry dev)
    {
        if (IsDeviceManaged(dev))
        {
            _settings.EnabledDevices.Remove(dev.AddressHex);
            _settings.DisabledDevices.Add(dev.AddressHex);
        }
        else
        {
            _settings.DisabledDevices.Remove(dev.AddressHex);
            _settings.EnabledDevices.Add(dev.AddressHex);
            _deviceLastInput[dev.AddressHex] = DateTime.UtcNow;
        }
        SaveSettings();
    }

    private void PopulateMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();

        int[] presets = [1, 3, 5, 10, 15, 30];
        foreach (int mins in presets)
        {
            menu.Items.Add(new ToolStripMenuItem($"Timeout: {mins} min", null, (_, _) => SetTimeout(mins))
            {
                Checked = _settings.TimeoutMinutes == mins
            });
        }

        menu.Items.Add(new ToolStripMenuItem("Custom Timeout...", null, (_, _) => PromptCustomTimeout())
        {
            Checked = _settings.TimeoutMinutes > 0 && Array.IndexOf(presets, _settings.TimeoutMinutes) < 0
        });

        menu.Items.Add(new ToolStripMenuItem("Disable Auto-Off", null, (_, _) => SetTimeout(0))
        {
            Checked = _settings.TimeoutMinutes == 0
        });

        menu.Items.Add(new ToolStripSeparator());

        var devicesMenu = new ToolStripMenuItem("Managed Bluetooth Devices");
        var btDevices = NativeBluetooth.GetPairedDevices();

        if (btDevices.Count == 0)
        {
            devicesMenu.DropDownItems.Add(new ToolStripMenuItem("(No Bluetooth devices found)") { Enabled = false });
        }
        else
        {
            foreach (var dev in btDevices.OrderByDescending(d => d.IsConnected).ThenBy(d => d.Name))
            {
                string statusTag = dev.IsConnected ? " [Connected]" : "";
                var devItem = new ToolStripMenuItem($"{dev.Name}{statusTag}")
                {
                    Checked = IsDeviceManaged(dev)
                };
                var captured = dev;
                devItem.Click += (_, _) => ToggleDeviceManaged(captured);
                devicesMenu.DropDownItems.Add(devItem);
            }
        }
        menu.Items.Add(devicesMenu);

        menu.Items.Add(new ToolStripMenuItem("Run at Windows Startup", null, (_, _) => ToggleRunAtStartup())
        {
            Checked = IsRunAtStartupEnabled()
        });

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Disconnect Managed Devices Now", null, (_, _) =>
        {
            int count = DisconnectManagedNow();
            _trayIcon.ShowBalloonTip(2000, "BT Controller Auto-Off",
                count > 0 ? $"Disconnected {count} device(s)." : "No connected managed devices found.",
                ToolTipIcon.Info);
        });

        menu.Items.Add("Exit", null, (_, _) => ExitApp());
    }

    private int DisconnectManagedNow()
    {
        int count = 0;
        DateTime now = DateTime.UtcNow;
        foreach (var dev in NativeBluetooth.GetPairedDevices().Where(d => d.IsConnected && IsDeviceManaged(d)))
        {
            if (NativeBluetooth.DisconnectDevice(dev.Address))
            {
                count++;
                _deviceLastInput[dev.AddressHex] = now;
            }
        }
        return count;
    }

    private void CheckIdle()
    {
        if (_settings.TimeoutMinutes <= 0) return;

        DateTime now = DateTime.UtcNow;
        TimeSpan timeout = TimeSpan.FromMinutes(_settings.TimeoutMinutes);
        var connectedManaged = NativeBluetooth.GetPairedDevices()
            .Where(d => d.IsConnected && IsDeviceManaged(d))
            .ToList();

        if (connectedManaged.Count == 0)
        {
            UpdateTrayTooltipText(null);
            return;
        }

        var activeHexes = new HashSet<string>(connectedManaged.Select(d => d.AddressHex), StringComparer.OrdinalIgnoreCase);
        foreach (var staleKey in _deviceLastInput.Keys.Where(k => !activeHexes.Contains(k)).ToList())
        {
            _deviceLastInput.Remove(staleKey);
        }

        TimeSpan? maxIdle = null;

        foreach (var dev in connectedManaged)
        {
            if (!_deviceLastInput.TryGetValue(dev.AddressHex, out DateTime lastSeen))
            {
                _deviceLastInput[dev.AddressHex] = now;
                lastSeen = now;
            }

            TimeSpan idle = now - lastSeen;
            if (maxIdle == null || idle > maxIdle) maxIdle = idle;

            if (idle >= timeout)
            {
                if (NativeBluetooth.DisconnectDevice(dev.Address))
                {
                    _deviceLastInput[dev.AddressHex] = now;
                }
            }
        }

        UpdateTrayTooltipText(maxIdle);
    }

    private void PromptCustomTimeout()
    {
        using var form = new Form
        {
            Text = "Custom Idle Timeout",
            Width = 280,
            Height = 140,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var label = new Label { Left = 15, Top = 15, Width = 230, Text = "Disconnect after inactivity (minutes):" };
        var numeric = new NumericUpDown
        {
            Left = 15,
            Top = 40,
            Width = 100,
            Minimum = 1,
            Maximum = 240,
            Value = Math.Max(1, _settings.TimeoutMinutes)
        };
        var okButton = new Button { Text = "Save", Left = 130, Top = 39, Width = 80, DialogResult = DialogResult.OK };

        form.Controls.Add(label);
        form.Controls.Add(numeric);
        form.Controls.Add(okButton);
        form.AcceptButton = okButton;

        if (form.ShowDialog() == DialogResult.OK)
        {
            SetTimeout((int)numeric.Value);
        }
    }

    private void SetTimeout(int minutes)
    {
        _settings.TimeoutMinutes = minutes;
        DateTime now = DateTime.UtcNow;
        foreach (var key in _deviceLastInput.Keys.ToList())
        {
            _deviceLastInput[key] = now;
        }
        SaveSettings();
        UpdateTrayVisuals();
    }

    private void UpdateTrayVisuals()
    {
        bool enabled = _settings.TimeoutMinutes > 0;
        var oldIcon = _trayIcon.Icon;
        _trayIcon.Icon = TrayIconGenerator.CreateControllerIcon(enabled);
        oldIcon?.Dispose();
        UpdateTrayTooltipText(null);
    }

    private void UpdateTrayTooltipText(TimeSpan? currentIdle)
    {
        if (_settings.TimeoutMinutes <= 0)
        {
            _trayIcon.Text = "BT Controller Auto-Off (Disabled)";
            return;
        }

        string idlePart = currentIdle.HasValue ? $" | Idle: {(int)currentIdle.Value.TotalSeconds}s" : "";
        string text = $"BT Auto-Off ({_settings.TimeoutMinutes}m){idlePart}";
        // NotifyIcon.Text max length in Windows is 63 chars
        _trayIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private static bool IsRunAtStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegKey, false);
        return key?.GetValue(AppName) is string;
    }

    private static void ToggleRunAtStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegKey, true);
        if (key == null) return;

        if (key.GetValue(AppName) != null)
            key.DeleteValue(AppName, false);
        else if (!string.IsNullOrEmpty(Environment.ProcessPath))
            key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
    }

    private AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    private void SaveSettings()
    {
        try
        {
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings));
        }
        catch { }
    }

    private void ExitApp()
    {
        _checkTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Icon?.Dispose();
        _listener.DestroyHandle();
        Application.Exit();
    }
}

public sealed unsafe class RawInputListener : NativeWindow
{
    private const int WM_INPUT = 0x00FF;
    private const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
    private const uint GIDC_REMOVAL = 2;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_DEVNOTIFY = 0x00002000;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIM_TYPEHID = 2;
    private const uint HIDP_STATUS_SUCCESS = 0x00110000;

    private static readonly Regex BtMacRegex = new(@"_([0-9A-Fa-f]{12})(?:#|&|$)", RegexOptions.Compiled);

    private readonly Action<string?> _onActiveInput;
    private readonly Dictionary<IntPtr, HidDeviceState> _deviceStates = new();

    private sealed class HidDeviceState
    {
        public string? BtMac;
        public byte[]? PreparsedData;
        public ushort[] LastButtons = Array.Empty<ushort>();
        public readonly Dictionary<ushort, uint> LastAxes = new();
    }

    public RawInputListener(Action<string?> onActiveInput)
    {
        _onActiveInput = onActiveInput;
        CreateHandle(new CreateParams());

        var devices = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x05, dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, hwndTarget = Handle },
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x04, dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, hwndTarget = Handle }
        };

        RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT)
        {
            ProcessRawInput(m.LParam);
        }
        else if (m.Msg == WM_INPUT_DEVICE_CHANGE && (uint)m.WParam == GIDC_REMOVAL)
        {
            _deviceStates.Remove(m.LParam);
        }
        base.WndProc(ref m);
    }

    private HidDeviceState GetOrCreateState(IntPtr hDevice)
    {
        if (_deviceStates.TryGetValue(hDevice, out var existing))
            return existing;

        string? mac = null;
        uint charCount = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref charCount);
        if (charCount > 0 && charCount < 512)
        {
            char* nameBuf = stackalloc char[(int)charCount];
            if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, (IntPtr)nameBuf, ref charCount) > 0)
            {
                string path = new(nameBuf);
                if (path.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase))
                {
                    var match = BtMacRegex.Match(path);
                    if (match.Success)
                        mac = match.Groups[1].Value.ToUpperInvariant();
                }
            }
        }

        byte[]? preparsed = null;
        uint preparsedSize = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref preparsedSize);
        if (preparsedSize > 0 && preparsedSize < 65536)
        {
            preparsed = new byte[preparsedSize];
            fixed (byte* pData = preparsed)
            {
                if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, (IntPtr)pData, ref preparsedSize) == uint.MaxValue)
                    preparsed = null;
            }
        }

        var state = new HidDeviceState { BtMac = mac, PreparsedData = preparsed };
        _deviceStates[hDevice] = state;
        return state;
    }

    private void ProcessRawInput(IntPtr hRawInput)
    {
        uint size = 0;
        uint headerSize = (uint)sizeof(RAWINPUTHEADER);
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0 || size > 1024) return;

        byte* buffer = stackalloc byte[(int)size];
        if (GetRawInputData(hRawInput, RID_INPUT, (IntPtr)buffer, ref size, headerSize) != size) return;

        RAWINPUTHEADER* header = (RAWINPUTHEADER*)buffer;
        if (header->dwType != RIM_TYPEHID) return;

        var state = GetOrCreateState(header->hDevice);
        // Ignore non-Bluetooth controllers completely
        if (string.IsNullOrEmpty(state.BtMac) || state.PreparsedData == null) return;

        int rawHidOffset = (int)headerSize;
        uint dwSizeHid = *(uint*)(buffer + rawHidOffset);
        uint dwCount = *(uint*)(buffer + rawHidOffset + 4);
        if (dwSizeHid == 0 || dwCount == 0) return;

        byte* reportPtr = buffer + rawHidOffset + 8;

        fixed (byte* preparsedPtr = state.PreparsedData)
        {
            // 1. Check standard Gamepad Button Page (0x09)
            uint usageCount = 32;
            ushort* usages = stackalloc ushort[32];
            uint status = HidP_GetUsages(0, 0x09, 0, usages, ref usageCount, (IntPtr)preparsedPtr, (IntPtr)reportPtr, dwSizeHid);
            if (status == HIDP_STATUS_SUCCESS)
            {
                bool buttonsChanged = usageCount != state.LastButtons.Length;
                if (!buttonsChanged)
                {
                    for (int i = 0; i < usageCount; i++)
                    {
                        if (state.LastButtons[i] != usages[i])
                        {
                            buttonsChanged = true;
                            break;
                        }
                    }
                }

                if (buttonsChanged)
                {
                    state.LastButtons = usageCount == 0 ? Array.Empty<ushort>() : new ushort[usageCount];
                    for (int i = 0; i < usageCount; i++) state.LastButtons[i] = usages[i];
                    _onActiveInput(state.BtMac);
                    return;
                }
            }

            // 2. Check Generic Desktop analog axes (0x30=X, 0x31=Y, 0x32=Z, 0x33=Rx, 0x34=Ry, 0x35=Rz, 0x39=Hat/DPad)
            ReadOnlySpan<ushort> axisUsages = [0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x39];
            foreach (ushort usage in axisUsages)
            {
                if (HidP_GetUsageValue(0, 0x01, 0, usage, out uint val, (IntPtr)preparsedPtr, (IntPtr)reportPtr, dwSizeHid) == HIDP_STATUS_SUCCESS)
                {
                    if (!state.LastAxes.TryGetValue(usage, out uint prevVal))
                    {
                        state.LastAxes[usage] = val;
                    }
                    else
                    {
                        // Hat switch (0x39) is discrete; analog axes use a ~5% deadzone (3000 out of 65535, or 12 out of 255)
                        uint threshold = usage == 0x39 ? 0u : (val > 255 || prevVal > 255 ? 3000u : 12u);
                        long diff = Math.Abs((long)val - prevVal);
                        if (diff > threshold)
                        {
                            state.LastAxes[usage] = val;
                            _onActiveInput(state.BtMac);
                            return;
                        }
                    }
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern uint HidP_GetUsages(
        int ReportType, ushort UsagePage, ushort LinkCollection,
        ushort* UsageList, ref uint UsageLength, IntPtr PreparsedData, IntPtr Report, uint ReportLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern uint HidP_GetUsageValue(
        int ReportType, ushort UsagePage, ushort LinkCollection, ushort Usage,
        out uint UsageValue, IntPtr PreparsedData, IntPtr Report, uint ReportLength);
}

public static class NativeBluetooth
{
    private const uint IOCTL_BTH_DISCONNECT_DEVICE = 0x0041000C;

    public static List<BluetoothDeviceEntry> GetPairedDevices()
    {
        var results = new List<BluetoothDeviceEntry>();
        var radioParams = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        IntPtr hFindRadio = BluetoothFindFirstRadio(ref radioParams, out IntPtr hRadio);
        if (hFindRadio == IntPtr.Zero) return results;

        try
        {
            var searchParams = new BLUETOOTH_DEVICE_SEARCH_PARAMS
            {
                dwSize = Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
                fReturnConnected = true,
                fReturnAuthenticated = true,
                fReturnRemembered = true,
                hRadio = hRadio
            };

            var deviceInfo = new BLUETOOTH_DEVICE_INFO { dwSize = Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>() };
            IntPtr hFindDevice = BluetoothFindFirstDevice(ref searchParams, ref deviceInfo);
            if (hFindDevice == IntPtr.Zero) return results;

            try
            {
                do
                {
                    results.Add(new BluetoothDeviceEntry
                    {
                        Address = deviceInfo.Address,
                        Name = string.IsNullOrWhiteSpace(deviceInfo.szName) ? deviceInfo.Address.ToString("X12") : deviceInfo.szName,
                        ClassOfDevice = deviceInfo.ulClassofDevice,
                        IsConnected = deviceInfo.fConnected
                    });
                }
                while (BluetoothFindNextDevice(hFindDevice, ref deviceInfo));
            }
            finally
            {
                BluetoothFindDeviceClose(hFindDevice);
            }
        }
        finally
        {
            CloseHandle(hRadio);
            BluetoothFindRadioClose(hFindRadio);
        }

        return results;
    }

    public static bool DisconnectDevice(ulong address)
    {
        var radioParams = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        IntPtr hFindRadio = BluetoothFindFirstRadio(ref radioParams, out IntPtr hRadio);
        if (hFindRadio == IntPtr.Zero) return false;

        try
        {
            return DeviceIoControl(hRadio, IOCTL_BTH_DISCONNECT_DEVICE, ref address, sizeof(ulong), IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(hRadio);
            BluetoothFindRadioClose(hFindRadio);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_FIND_RADIO_PARAMS { public int dwSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
    {
        public int dwSize;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_DEVICE_INFO
    {
        public int dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [DllImport("BluetoothAPIs.dll", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

    [DllImport("BluetoothAPIs.dll", SetLastError = true)]
    private static extern bool BluetoothFindRadioClose(IntPtr hFind);

    [DllImport("BluetoothAPIs.dll", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS pbtsp, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("BluetoothAPIs.dll", SetLastError = true)]
    private static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("BluetoothAPIs.dll", SetLastError = true)]
    private static extern bool BluetoothFindDeviceClose(IntPtr hFind);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode, ref ulong lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);
}

public static class TrayIconGenerator
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon CreateControllerIcon(bool isEnabled)
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using (var bgBrush = new SolidBrush(Color.FromArgb(30, 34, 42)))
        using (var bgPath = CreateRoundedRect(new Rectangle(1, 1, 30, 30), 7))
        {
            g.FillPath(bgBrush, bgPath);
        }

        using (var padBrush = new SolidBrush(isEnabled ? Color.White : Color.FromArgb(150, 155, 165)))
        {
            g.FillEllipse(padBrush, 5, 11, 8, 13);
            g.FillEllipse(padBrush, 19, 11, 8, 13);
            g.FillRectangle(padBrush, 9, 11, 14, 8);
        }

        using (var cutoutBrush = new SolidBrush(Color.FromArgb(30, 34, 42)))
        {
            g.FillRectangle(cutoutBrush, 8, 13, 2, 5);
            g.FillRectangle(cutoutBrush, 6, 15, 6, 2);

            g.FillEllipse(cutoutBrush, 22, 13, 2, 2);
            g.FillEllipse(cutoutBrush, 20, 15, 2, 2);
        }

        Color statusColor = isEnabled
            ? Color.FromArgb(0, 112, 243)
            : Color.FromArgb(235, 150, 40);

        using (var borderBrush = new SolidBrush(Color.FromArgb(30, 34, 42)))
        using (var dotBrush = new SolidBrush(statusColor))
        {
            g.FillEllipse(borderBrush, 20, 2, 10, 10);
            g.FillEllipse(dotBrush, 21, 3, 8, 8);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}