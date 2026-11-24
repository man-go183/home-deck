using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace HomeDeck;

/// <summary>
/// Classic Win32 radio details — no package identity needed, so these work
/// in the UNPACKAGED exe too (unlike the WinRT capability APIs).
/// - WiFi: Native Wifi API (wlanapi): current connection + nearby networks.
/// - Bluetooth: BluetoothFindFirstRadio/Device (Bthprops): radios + devices.
/// Details only (names, addresses, signal, class) — no pairing/GATT in v1.
/// All fail-soft JSON, never throws across the boundary.
/// </summary>
static class LegacyRadioInfo
{
    static string Fail(string r) => "{\"ok\":false,\"error\":\"" + r + "\"}";
    static string Ok(object data) => JsonSerializer.Serialize(new { ok = true, data });

    // ---------------- WiFi (wlanapi.dll) ----------------

    enum WLAN_INTERFACE_STATE : uint
    { NotReady, Connected, AdHocFormed, Disconnecting, Disconnected, Associating, Discovering, Authenticating }

    [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint v, IntPtr r, out uint n, out IntPtr h);
    [DllImport("wlanapi.dll")] static extern uint WlanCloseHandle(IntPtr h, IntPtr r);
    [DllImport("wlanapi.dll")] static extern uint WlanEnumInterfaces(IntPtr h, IntPtr r, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);
    [DllImport("wlanapi.dll")] static extern uint WlanQueryInterface(IntPtr h, ref Guid g, uint op, IntPtr r, out uint sz, out IntPtr data, IntPtr t);
    [DllImport("wlanapi.dll")] static extern uint WlanScan(IntPtr h, ref Guid g, IntPtr ssid, IntPtr ie, IntPtr r);
    [DllImport("wlanapi.dll")] static extern uint WlanGetAvailableNetworkList(IntPtr h, ref Guid g, uint flags, IntPtr r, out IntPtr list);

    const uint WLAN_CURRENT_CONNECTION = 7;
    const int IFACE_ITEM = 532;   // Guid(16) + descr WCHAR[256](512) + state(4)
    const int NET_ITEM = 628;     // see layout notes in WifiScan
    const int CONN_ASSOC = 520;   // WLAN_CONNECTION_ATTRIBUTES fixed head

    static Guid ReadGuid(IntPtr p) => Marshal.PtrToStructure<Guid>(p);

    static string ReadSsid(IntPtr p, int lenOff, int bytesOff, int max)
    {
        try
        {
            int len = Marshal.ReadInt32(p, lenOff);
            if (len <= 0 || len > max) return "";
            byte[] b = new byte[len];
            Marshal.Copy(p + bytesOff, b, 0, len);
            return Encoding.UTF8.GetString(b);
        }
        catch { return ""; }
    }

    static string Mac6(IntPtr p, int off)
    {
        try
        {
            byte[] b = new byte[6];
            Marshal.Copy(p + off, b, 0, 6);
            return BitConverter.ToString(b).Replace("-", ":");
        }
        catch { return ""; }
    }

    public static string WifiScan()
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out h) != 0 || h == IntPtr.Zero)
                return Fail("wlan-unavailable");
            if (WlanEnumInterfaces(h, IntPtr.Zero, out IntPtr list) != 0)
                return Fail("no-interface");
            int n;
            var guids = new System.Collections.Generic.List<Guid>();
            try
            {
                n = Marshal.ReadInt32(list);
                for (int i = 0; i < Math.Min(n, 16); i++)
                    guids.Add(ReadGuid(list + 8 + i * IFACE_ITEM));
            }
            finally { WlanFreeMemory(list); }
            if (guids.Count == 0) return Fail("no-interface");

            var ifaces = new System.Collections.Generic.List<object>();
            object? current = null;
            foreach (var g in guids)
            {
                string ssid = "", bssid = "";
                int signal = -1, state = -1;
                var gg0 = g;
                if (WlanQueryInterface(h, ref gg0, WLAN_CURRENT_CONNECTION,
                        IntPtr.Zero, out _, out IntPtr conn) == 0)
                {
                    try
                    {
                        state = Marshal.ReadInt32(conn);
                        ssid = ReadSsid(conn + CONN_ASSOC, 0, 4, 32);
                        bssid = Mac6(conn + CONN_ASSOC, 40);
                        signal = Marshal.ReadInt32(conn + CONN_ASSOC, 56);
                        var c = new { ssid, bssid, signal };
                        if (state == 1 && current == null) current = c;
                        ifaces.Add(new { state, ssid, bssid, signal });
                    }
                    finally { WlanFreeMemory(conn); }
                }
                else ifaces.Add(new { state, ssid, bssid, signal });
            }

            // trigger a fresh scan, then read the (possibly refreshed) list
            var gg = guids[0];
            try { WlanScan(h, ref gg, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); } catch { }
            System.Threading.Thread.Sleep(4000);

            var nets = new System.Collections.Generic.List<object>();
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (var g in guids)
            {
                var ggg = g;
                if (WlanGetAvailableNetworkList(h, ref ggg, 0x03, IntPtr.Zero, out IntPtr nl) != 0)
                    continue;
                try
                {
                    int count = Marshal.ReadInt32(nl);
                    for (int i = 0; i < Math.Min(count, 128); i++)
                    {
                        IntPtr it = nl + 8 + i * NET_ITEM;
                        string s = ReadSsid(it, 512, 516, 32);
                        if (string.IsNullOrEmpty(s) || !seen.Add(s)) continue;
                        int sig = Marshal.ReadInt32(it, 604);
                        bool sec = Marshal.ReadInt32(it, 608) != 0;
                        bool connected = (Marshal.ReadUInt32(it, 620) & 0x01) != 0;
                        nets.Add(new { ssid = s, signal = sig, secured = sec, connected });
                    }
                }
                finally { WlanFreeMemory(nl); }
            }
            nets.Sort((a, b) => SignalOf(b).CompareTo(SignalOf(a)));
            return Ok(new { current, networks = nets });
        }
        catch (Exception e) { return Fail(e.Message); }
        finally { if (h != IntPtr.Zero) try { WlanCloseHandle(h, IntPtr.Zero); } catch { } }
    }

    static int SignalOf(object o)
    {
        try { return (int)(o.GetType().GetProperty("signal")?.GetValue(o) ?? -1); }
        catch { return -1; }
    }

    // ---------------- Bluetooth (Bthprops.cpl) ----------------

    [StructLayout(LayoutKind.Sequential)] struct FindRadioParams { public uint dwSize; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct RadioInfo
    {
        public uint dwSize; public ulong address;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
        public uint ulClassofDevice; public ushort lmpSubversion; public ushort manufacturer;
    }
    [StructLayout(LayoutKind.Sequential)] struct DeviceSearchParams
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
        public byte cTimeoutMultiplier; public byte _pad0; public ushort _pad1; public IntPtr hRadio;
    }
    [StructLayout(LayoutKind.Sequential)] struct SysTime
    { public ushort y, mo, dow, d, h, mi, s, ms; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct DeviceInfo
    {
        public uint dwSize; public ulong Address; public uint ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
        public SysTime stLastSeen; public SysTime stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
    }

    [DllImport("Bthprops.cpl", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr BluetoothFindFirstRadio(ref FindRadioParams p, out IntPtr hRadio);
    [DllImport("Bthprops.cpl")] static extern bool BluetoothFindNextRadio(IntPtr hFind, out IntPtr hRadio);
    [DllImport("Bthprops.cpl")] static extern bool BluetoothFindRadioClose(IntPtr hFind);
    [DllImport("Bthprops.cpl", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr BluetoothFindFirstDevice(ref DeviceSearchParams p, ref DeviceInfo d);
    [DllImport("Bthprops.cpl", CharSet = CharSet.Unicode)]
    static extern bool BluetoothFindNextDevice(IntPtr hFind, ref DeviceInfo d);
    [DllImport("Bthprops.cpl")] static extern bool BluetoothFindDeviceClose(IntPtr hFind);
    [DllImport("Bthprops.cpl", CharSet = CharSet.Unicode)]
    static extern uint BluetoothGetRadioInfo(IntPtr hRadio, ref RadioInfo r);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static string BtMac(ulong a)
    {
        byte[] b = BitConverter.GetBytes(a);
        return $"{b[5]:X2}:{b[4]:X2}:{b[3]:X2}:{b[2]:X2}:{b[1]:X2}:{b[0]:X2}";
    }

    static string MajorClass(uint cod) => ((cod >> 8) & 0x1F) switch
    {
        1 => "computer", 2 => "phone", 3 => "network", 4 => "audio-video",
        5 => "peripheral", 6 => "imaging", 7 => "wearable", 8 => "toy",
        9 => "health", 0 => "misc", 31 => "uncategorized", _ => "unknown"
    };

    public static string BluetoothDevices(bool inquiry)
    {
        var radios = new System.Collections.Generic.List<object>();
        var devs = new System.Collections.Generic.List<object>();
        var seen = new System.Collections.Generic.HashSet<ulong>();
        try
        {
            var frp = new FindRadioParams { dwSize = (uint)Marshal.SizeOf<FindRadioParams>() };
            IntPtr hFind = BluetoothFindFirstRadio(ref frp, out IntPtr hRadio);
            if (hFind != IntPtr.Zero)
            {
                try
                {
                    bool more = true;
                    while (more)
                    {
                        try
                        {
                            var ri = new RadioInfo { dwSize = (uint)Marshal.SizeOf<RadioInfo>() };
                            if (BluetoothGetRadioInfo(hRadio, ref ri) == 0)
                                radios.Add(new { name = ri.szName ?? "", address = BtMac(ri.address) });
                        }
                        catch { }
                        try { CloseHandle(hRadio); } catch { }
                        more = BluetoothFindNextRadio(hFind, out hRadio);
                    }
                }
                finally { try { BluetoothFindRadioClose(hFind); } catch { } }
            }
            if (radios.Count == 0) return Fail("bt-off");

            void Pass(bool auth, bool rem, bool unk, bool conn, bool issue, byte mult)
            {
                var sp = new DeviceSearchParams
                {
                    dwSize = (uint)Marshal.SizeOf<DeviceSearchParams>(),
                    fReturnAuthenticated = auth, fReturnRemembered = rem,
                    fReturnUnknown = unk, fReturnConnected = conn,
                    fIssueInquiry = issue, cTimeoutMultiplier = mult, hRadio = IntPtr.Zero
                };
                var di = new DeviceInfo { dwSize = (uint)Marshal.SizeOf<DeviceInfo>() };
                IntPtr h = BluetoothFindFirstDevice(ref sp, ref di);
                if (h == IntPtr.Zero) return;
                try
                {
                    bool go = true;
                    while (go)
                    {
                        try
                        {
                            if (seen.Add(di.Address))
                                devs.Add(new
                                {
                                    name = di.szName ?? "",
                                    address = BtMac(di.Address),
                                    major = MajorClass(di.ulClassofDevice),
                                    connected = di.fConnected,
                                    remembered = di.fRemembered,
                                    authenticated = di.fAuthenticated
                                });
                        }
                        catch { }
                        di = new DeviceInfo { dwSize = (uint)Marshal.SizeOf<DeviceInfo>() };
                        go = BluetoothFindNextDevice(h, ref di);
                    }
                }
                finally { try { BluetoothFindDeviceClose(h); } catch { } }
            }

            Pass(true, true, true, true, false, 0);          // fast: cached
            if (inquiry) Pass(false, false, true, false, true, 5); // ~6s discovery
            return Ok(new { radios, devices = devs });
        }
        catch (Exception e) { return Fail(e.Message); }
    }
}
