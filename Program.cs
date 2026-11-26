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
    public int? BatteryPercent { get; init; }
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
    private readonly Timer _tickTimer;
    private readonly string _settingsPath;
    private AppSettings _settings;

    private readonly Dictionary<string, DateTime> _deviceLastInput = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _hidBatteryCache = new(StringComparer.OrdinalIgnoreCase);
    private int? _fallbackSinglePadBattery;
    private List<BluetoothDeviceEntry> _cachedConnectedManaged = new();
    private DateTime _lastBtScanUtc = DateTime.MinValue;
    private uint _lastXInputPacketSum;

    public TrayAppContext()
    {
        string appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName);
        Directory.CreateDirectory(appDir);
        _settingsPath = Path.Combine(appDir, "settings.json");
        _settings = LoadSettings();

        _listener = new RawInputListener(OnControllerInput, OnControllerBattery);

        _trayIcon = new NotifyIcon { Visible = true };

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => PopulateMenu(menu);
        _trayIcon.ContextMenuStrip = menu;

        PopulateMenu(menu);
        UpdateTrayVisuals();

        _tickTimer = new Timer { Interval = 1_000 };
        _tickTimer.Tick += (_, _) => OnTick();
        _tickTimer.Start();
    }

    private void OnControllerInput(string? btAddressHex)
    {
        DateTime now = DateTime.UtcNow;

        if (!string.IsNullOrEmpty(btAddressHex) && _deviceLastInput.ContainsKey(btAddressHex))
        {
            _deviceLastInput[btAddressHex] = now;
        }
        else
        {
            foreach (var dev in _cachedConnectedManaged)
            {
                _deviceLastInput[dev.AddressHex] = now;
            }
        }
    }

    private void OnControllerBattery(string? btAddressHex, int percent)
    {
        if (!string.IsNullOrEmpty(btAddressHex))
        {
            _hidBatteryCache[btAddressHex] = percent;
        }
        _fallbackSinglePadBattery = percent;
    }

    private int? GetEffectiveBattery(BluetoothDeviceEntry dev)
    {
        if (_hidBatteryCache.TryGetValue(dev.AddressHex, out int hidBat)) return hidBat;
        if (dev.BatteryPercent.HasValue) return dev.BatteryPercent.Value;
        if (_cachedConnectedManaged.Count == 1 && _fallbackSinglePadBattery.HasValue)
            return _fallbackSinglePadBattery.Value;
        return null;
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
        _lastBtScanUtc = DateTime.MinValue;
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
                int? bat = dev.IsConnected ? GetEffectiveBattery(dev) : null;
                string statusTag = dev.IsConnected
                    ? (bat.HasValue ? $" [Connected - {bat.Value}%]" : " [Connected]")
                    : "";

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
                _hidBatteryCache.Remove(dev.AddressHex);
                _fallbackSinglePadBattery = null;
            }
        }
        _lastBtScanUtc = DateTime.MinValue;
        return count;
    }

    private bool HasXInputActivity()
    {
        uint sum = 0;
        for (uint i = 0; i < 4; i++)
        {
            if (XInputGetState(i, out var state) == 0)
            {
                sum += state.dwPacketNumber;
            }
        }

        if (sum != _lastXInputPacketSum)
        {
            _lastXInputPacketSum = sum;
            return true;
        }
        return false;
    }

    private void OnTick()
    {
        if (_settings.TimeoutMinutes <= 0) return;

        DateTime now = DateTime.UtcNow;

        if ((now - _lastBtScanUtc).TotalSeconds >= 5)
        {
            _lastBtScanUtc = now;
            _cachedConnectedManaged = NativeBluetooth.GetPairedDevices()
                .Where(d => d.IsConnected && IsDeviceManaged(d))
                .ToList();

            var activeHexes = new HashSet<string>(_cachedConnectedManaged.Select(d => d.AddressHex), StringComparer.OrdinalIgnoreCase);
            foreach (var staleKey in _deviceLastInput.Keys.Where(k => !activeHexes.Contains(k)).ToList())
            {
                _deviceLastInput.Remove(staleKey);
                _hidBatteryCache.Remove(staleKey);
            }

            if (_cachedConnectedManaged.Count == 0)
            {
                _fallbackSinglePadBattery = null;
            }
        }

        if (_cachedConnectedManaged.Count == 0)
        {
            UpdateTrayTooltipText(null, null);
            return;
        }

        if (HasXInputActivity())
        {
            foreach (var dev in _cachedConnectedManaged)
            {
                _deviceLastInput[dev.AddressHex] = now;
            }
        }

        TimeSpan timeout = TimeSpan.FromMinutes(_settings.TimeoutMinutes);
        TimeSpan? maxIdle = null;
        int? displayBattery = null;

        foreach (var dev in _cachedConnectedManaged)
        {
            if (!_deviceLastInput.TryGetValue(dev.AddressHex, out DateTime lastSeen))
            {
                _deviceLastInput[dev.AddressHex] = now;
                lastSeen = now;
            }

            TimeSpan idle = now - lastSeen;
            if (maxIdle == null || idle > maxIdle) maxIdle = idle;

            int? bat = GetEffectiveBattery(dev);
            if (bat.HasValue && (displayBattery == null || bat.Value < displayBattery.Value))
            {
                displayBattery = bat.Value;
            }

            if (idle >= timeout)
            {
                if (NativeBluetooth.DisconnectDevice(dev.Address))
                {
                    _deviceLastInput[dev.AddressHex] = now;
                    _hidBatteryCache.Remove(dev.AddressHex);
                    _fallbackSinglePadBattery = null;
                    _lastBtScanUtc = DateTime.MinValue;
                }
            }
        }

        UpdateTrayTooltipText(maxIdle, displayBattery);
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
        UpdateTrayTooltipText(null, null);
    }

    private void UpdateTrayTooltipText(TimeSpan? currentIdle, int? batteryPercent)
    {
        if (_settings.TimeoutMinutes <= 0)
        {
            _trayIcon.Text = "BT Controller Auto-Off (Disabled)";
            return;
        }

        string idlePart = currentIdle.HasValue ? $" | Idle: {(int)currentIdle.Value.TotalSeconds}s" : "";
        string batPart = batteryPercent.HasValue ? $" | Bat: {batteryPercent.Value}%" : "";
        string text = $"BT Auto-Off ({_settings.TimeoutMinutes}m){idlePart}{batPart}";
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
        _tickTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Icon?.Dispose();
        _listener.DestroyHandle();
        Application.Exit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);
}

public sealed unsafe class RawInputListener : NativeWindow
{
    private const int WM_INPUT = 0x00FF;
    private const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
    private const uint GIDC_REMOVAL = 2;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_DEVNOTIFY = 0x00002000;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIM_TYPEHID = 2;

    private static readonly Regex BtMacRegex = new(@"(?:_|&|DEV_)([0-9A-Fa-f]{12})(?:#|&|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BtColonMacRegex = new(@"([0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5})", RegexOptions.Compiled);

    private readonly Action<string?> _onActiveInput;
    private readonly Action<string?, int> _onBatteryReport;
    private readonly Dictionary<IntPtr, HidDeviceState> _deviceStates = new();

    private sealed class HidDeviceState
    {
        public string? BtMac;
        public int LastBatteryPercent = -1;
        public readonly Dictionary<byte, byte[]> LastPayloadByReportId = new();
    }

    public RawInputListener(Action<string?> onActiveInput, Action<string?, int> onBatteryReport)
    {
        _onActiveInput = onActiveInput;
        _onBatteryReport = onBatteryReport;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) });

        var devices = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x05, dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, hwndTarget = Handle },
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x04, dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, hwndTarget = Handle },
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x08, dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, hwndTarget = Handle }
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
                mac = ResolveBluetoothMacFromPath(path);
            }
        }

        var state = new HidDeviceState { BtMac = mac };
        _deviceStates[hDevice] = state;
        return state;
    }

    private static string? ExtractMac(string text)
    {
        var m1 = BtMacRegex.Match(text);
        if (m1.Success) return m1.Groups[1].Value.ToUpperInvariant();

        var m2 = BtColonMacRegex.Match(text);
        if (m2.Success) return m2.Groups[1].Value.Replace(":", "").ToUpperInvariant();

        return null;
    }

    private static string? ResolveBluetoothMacFromPath(string rawPath)
    {
        var direct = ExtractMac(rawPath);
        if (direct != null) return direct;

        string instanceId = rawPath;
        if (instanceId.StartsWith(@"\\?\")) instanceId = instanceId[4..];
        int guidIdx = instanceId.LastIndexOf("#{", StringComparison.Ordinal);
        if (guidIdx > 0) instanceId = instanceId[..guidIdx];
        instanceId = instanceId.Replace('#', '\\');

        if (CM_Locate_DevNode(out uint devInst, instanceId, 0) == 0)
        {
            uint current = devInst;
            char[] idBuf = new char[260];
            for (int depth = 0; depth < 6; depth++)
            {
                if (CM_Get_Parent(out uint parent, current, 0) != 0) break;
                Array.Clear(idBuf);
                if (CM_Get_Device_ID(parent, idBuf, idBuf.Length, 0) == 0)
                {
                    string parentId = new string(idBuf).TrimEnd('\0');
                    var parentMac = ExtractMac(parentId);
                    if (parentMac != null) return parentMac;
                }
                current = parent;
            }
        }

        return null;
    }

    private void ProcessRawInput(IntPtr hRawInput)
    {
        uint size = 0;
        uint headerSize = (uint)sizeof(RAWINPUTHEADER);
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0 || size > 2048) return;

        byte* buffer = stackalloc byte[(int)size];
        if (GetRawInputData(hRawInput, RID_INPUT, (IntPtr)buffer, ref size, headerSize) != size) return;

        RAWINPUTHEADER* header = (RAWINPUTHEADER*)buffer;
        if (header->dwType != RIM_TYPEHID) return;

        var state = GetOrCreateState(header->hDevice);

        int rawHidOffset = (int)headerSize;
        uint dwSizeHid = *(uint*)(buffer + rawHidOffset);
        uint dwCount = *(uint*)(buffer + rawHidOffset + 4);
        if (dwSizeHid < 2 || dwCount == 0) return;

        byte* baseReportPtr = buffer + rawHidOffset + 8;
        bool isExtendedBtReport = dwSizeHid >= 64;
        int inspectLen = isExtendedBtReport ? 11 : (int)Math.Min(dwSizeHid - 1, 12);

        for (uint pkt = 0; pkt < dwCount; pkt++)
        {
            byte* reportPtr = baseReportPtr + (pkt * dwSizeHid);
            byte reportId = reportPtr[0];

            // Extract battery directly from 78-byte DualSense (0x31, byte 53) or DS4 (0x11, byte 30) Bluetooth reports
            if (dwSizeHid == 78)
            {
                int? sonyPct = null;
                if (reportId == 0x31)
                {
                    int levelRaw = reportPtr[53] & 0x0F; // 0..10 scale
                    sonyPct = Math.Min(100, levelRaw * 10 + 5);
                }
                else if (reportId == 0x11)
                {
                    int levelRaw = reportPtr[30] & 0x0F; // 0..10 scale
                    sonyPct = Math.Min(100, levelRaw * 10 + 5);
                }

                if (sonyPct.HasValue && sonyPct.Value != state.LastBatteryPercent)
                {
                    state.LastBatteryPercent = sonyPct.Value;
                    _onBatteryReport(state.BtMac, sonyPct.Value);
                }
            }

            if (!state.LastPayloadByReportId.TryGetValue(reportId, out byte[]? prev))
            {
                var initial = new byte[inspectLen];
                for (int i = 0; i < inspectLen; i++) initial[i] = reportPtr[i + 1];
                state.LastPayloadByReportId[reportId] = initial;
                continue;
            }

            bool active = false;
            for (int i = 0; i < inspectLen; i++)
            {
                if (isExtendedBtReport && (i == 0 || i == 7)) continue;

                byte curr = reportPtr[i + 1];
                int diff = Math.Abs(curr - prev[i]);
                int threshold = (isExtendedBtReport && i >= 8) ? 0 : 14;
                if (diff > threshold)
                {
                    active = true;
                    break;
                }
            }

            if (active)
            {
                for (int i = 0; i < inspectLen; i++) prev[i] = reportPtr[i + 1];
                _onActiveInput(state.BtMac);
                return;
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

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNode(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID(uint dnDevInst, char[] Buffer, int BufferLen, uint ulFlags);
}

public static class NativeBluetooth
{
    private const uint IOCTL_BTH_DISCONNECT_DEVICE = 0x0041000C;
    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;

    private static DEVPROPKEY DEVPKEY_Device_BatteryLevel = new()
    {
        fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"),
        pid = 2
    };

    private static readonly Regex MacExtractRegex = new(@"(?:_|&|DEV_)([0-9A-Fa-f]{12})(?:#|&|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static List<BluetoothDeviceEntry> GetPairedDevices()
    {
        var results = new List<BluetoothDeviceEntry>();
        var radioParams = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        IntPtr hFindRadio = BluetoothFindFirstRadio(ref radioParams, out IntPtr hRadio);
        if (hFindRadio == IntPtr.Zero) return results;

        Dictionary<string, int>? batteryByMac = null;

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
                    string hex = deviceInfo.Address.ToString("X12");
                    int? battery = null;
                    if (deviceInfo.fConnected)
                    {
                        batteryByMac ??= QueryPnpBluetoothBatteryLevels();
                        if (batteryByMac.TryGetValue(hex, out int pct))
                        {
                            battery = pct;
                        }
                    }

                    results.Add(new BluetoothDeviceEntry
                    {
                        Address = deviceInfo.Address,
                        Name = string.IsNullOrWhiteSpace(deviceInfo.szName) ? hex : deviceInfo.szName,
                        ClassOfDevice = deviceInfo.ulClassofDevice,
                        IsConnected = deviceInfo.fConnected,
                        BatteryPercent = battery
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

    private static Dictionary<string, int> QueryPnpBluetoothBatteryLevels()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        IntPtr devInfoSet = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1)) return map;

        try
        {
            var devData = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            char[] idBuf = new char[260];

            for (uint idx = 0; SetupDiEnumDeviceInfo(devInfoSet, idx, ref devData); idx++)
            {
                if (!SetupDiGetDeviceProperty(devInfoSet, ref devData, ref DEVPKEY_Device_BatteryLevel,
                    out uint propType, out byte batteryVal, 1, out _, 0))
                {
                    continue;
                }

                if (batteryVal > 100) continue;

                uint current = devData.DevInst;
                for (int depth = 0; depth < 6; depth++)
                {
                    Array.Clear(idBuf);
                    if (CM_Get_Device_ID(current, idBuf, idBuf.Length, 0) == 0)
                    {
                        string id = new string(idBuf).TrimEnd('\0');
                        var match = MacExtractRegex.Match(id);
                        if (match.Success)
                        {
                            map[match.Groups[1].Value.ToUpperInvariant()] = batteryVal;
                            break;
                        }
                    }

                    if (CM_Get_Parent(out uint parent, current, 0) != 0) break;
                    current = parent;
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        return map;
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
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
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

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceProperty(
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, ref DEVPROPKEY PropertyKey,
        out uint PropertyType, out byte PropertyBuffer, uint PropertyBufferSize, out uint RequiredSize, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID(uint dnDevInst, char[] Buffer, int BufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

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