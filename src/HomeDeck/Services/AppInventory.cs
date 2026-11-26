using Microsoft.Maui.ApplicationModel;

namespace HomeDeck.Services;

/// <summary>Installed launchable apps + native launching, app side only.
/// The full inventory never crosses the bridge: the pick sheet shows it
/// to the human, the site learns only picked IDs, and launching takes
/// no arguments — the pick was the consent. Everything fail-closed.</summary>
public static class AppInventory
{
    /// <summary>All installed launch targets (id + name), native side
    /// only. Never throws; empty on failure.</summary>
    public static Task<List<HdFileGuard.AppRef>> GetInstalledAsync()
        => Task.Run(() =>
        {
            try
            {
#if WINDOWS
                return GetWindowsApps();
#elif ANDROID
                return GetAndroidApps();
#else
                return new List<HdFileGuard.AppRef>();
#endif
            }
            catch { return new List<HdFileGuard.AppRef>(); }
        });

    /// <summary>Launch a picked app natively (real OS process / task).
    /// Fire-and-forget: ok means the OS accepted it. Never throws.</summary>
    public static Task<string> LaunchAsync(string appId)
    {
#if ANDROID
        return LaunchAndroidAsync(appId);
#else
        return Task.Run(() =>
        {
            try
            {
#if WINDOWS
                if (string.IsNullOrWhiteSpace(appId) || appId.Length > 500)
                    return Fail("bad-id");
                appId = appId.Trim();
                if (appId.StartsWith("aumid:", StringComparison.OrdinalIgnoreCase))
                {
                    var aumid = appId.Substring(6).Trim();
                    if (!IsAumid(aumid)) return Fail("bad-id");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "shell:AppsFolder\\" + aumid,
                        UseShellExecute = true
                    });
                    return "{\"ok\":true}";
                }
                string full;
                try { full = Path.GetFullPath(appId); }
                catch { return Fail("bad-id"); }
                if (!Path.IsPathFullyQualified(full)
                    || !full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(full))
                    return Fail("not-found");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = full,
                    UseShellExecute = true
                });
                return "{\"ok\":true}";
#else
                return Fail("not-implemented");
#endif
            }
            catch (Exception e) { return Fail(e.Message); }
        });
#endif
    }

    static string Fail(string detail)
    {
        try { return "{\"ok\":false,\"error\":" + System.Text.Json.JsonSerializer.Serialize(detail) + "}"; }
        catch { return "{\"ok\":false,\"error\":\"failed\"}"; }
    }

#if WINDOWS
    static bool IsAumid(string s)
    {
        try
        {
            if (string.IsNullOrEmpty(s) || s.Length > 300) return false;
            foreach (var c in s)
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' || c == '!'))
                    return false;
            return s.Contains('!');
        }
        catch { return false; }
    }

    static List<HdFileGuard.AppRef> GetWindowsApps()
    {
        var list = new List<HdFileGuard.AppRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string id, string name)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return;
                id = id.Trim(); name = name.Trim();
                if (!seen.Add(id)) return;
                if (name.Length > 120) name = name.Substring(0, 120);
                list.Add(new HdFileGuard.AppRef { Id = id, Name = name });
            }
            catch { }
        }
        // Store apps for the current user (no special capability).
        try
        {
            string? ownFamily = null;
            try { ownFamily = Windows.ApplicationModel.Package.Current.Id.FamilyName; } catch { }
            var pm = new Windows.Management.Deployment.PackageManager();
            foreach (var pkg in pm.FindPackagesForUser(""))
            {
                try
                {
                    if (!string.IsNullOrEmpty(ownFamily) && pkg.Id.FamilyName == ownFamily) continue;
                    foreach (var entry in pkg.GetAppListEntries())
                    {
                        try
                        {
                            var n = entry.DisplayInfo.DisplayName ?? "";
                            var aumid = entry.AppUserModelId ?? "";
                            if (!string.IsNullOrWhiteSpace(n) && !string.IsNullOrWhiteSpace(aumid))
                                Add("aumid:" + aumid.Trim(), n.Trim());
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
        // Classic Start-menu links (hand-parsed .lnk, no COM, trim-safe).
        var roots = new List<string>();
        try
        {
            foreach (var k in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu })
            {
                try
                {
                    var d = Environment.GetFolderPath(k);
                    if (!string.IsNullOrEmpty(d)) roots.Add(Path.Combine(d, "Programs"));
                }
                catch { }
            }
        }
        catch { }
        int files = 0;
        foreach (var r in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            ScanLinks(r, 0, ref files, Add);
        try { list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)); } catch { }
        return list;
    }

    static void ScanLinks(string dir, int depth, ref int files, Action<string, string> add)
    {
        try
        {
            if (depth > 2 || files > 2000 || !Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*.lnk"))
            {
                try
                {
                    if (++files > 2000) return;
                    var target = ResolveLnkTarget(f);
                    if (!string.IsNullOrEmpty(target) && File.Exists(target)
                        && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        add(target, Path.GetFileNameWithoutExtension(f) ?? "");
                }
                catch { }
            }
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                try { ScanLinks(d, depth + 1, ref files, add); } catch { }
            }
        }
        catch { }
    }

    /// <summary>Minimal .lnk target reader: header + IDList skip +
    /// LinkInfo LocalBasePath (ANSI in system codepage, or Unicode).
    /// Null when absent. Never throws.</summary>
    static string? ResolveLnkTarget(string lnk)
    {
        try
        {
            var b = File.ReadAllBytes(lnk);
            if (b.Length < 78 || b[0] != 0x4C) return null;
            int flags = BitConverter.ToInt32(b, 0x14);
            int at = 0x4C;
            if ((flags & 0x01) != 0) // HasLinkTargetIDList
            {
                if (at + 2 > b.Length) return null;
                at += 2 + BitConverter.ToUInt16(b, at);
            }
            if ((flags & 0x02) == 0) return null; // no LinkInfo
            if (at + 12 > b.Length) return null;
            int infoSize = BitConverter.ToInt32(b, at);
            int infoFlags = BitConverter.ToInt32(b, at + 8);
            if ((infoFlags & 0x01) == 0) return null; // need a local base path
            bool unicode = (infoFlags & 0x80) != 0 && infoSize >= 36;
            int offPos = unicode ? at + 28 : at + 16;
            if (offPos + 4 > b.Length) return null;
            int start = at + BitConverter.ToInt32(b, offPos);
            if (start <= at || start >= b.Length) return null;
            if (unicode)
            {
                int end = start;
                while (end + 1 < b.Length && !(b[end] == 0 && b[end + 1] == 0)) end += 2;
                if (end + 1 >= b.Length) return null;
                return System.Text.Encoding.Unicode.GetString(b, start, end - start);
            }
            int stop = start;
            while (stop < b.Length && b[stop] != 0) stop++;
            if (stop >= b.Length) return null;
            return System.Text.Encoding.Default.GetString(b, start, stop - start);
        }
        catch { return null; }
    }
#endif

#if ANDROID
    static List<HdFileGuard.AppRef> GetAndroidApps()
    {
        var list = new List<HdFileGuard.AppRef>();
        try
        {
            var act = Platform.CurrentActivity;
            var pm = act?.PackageManager;
            if (pm == null) return list;
            var intent = new Android.Content.Intent(Android.Content.Intent.ActionMain);
            intent.AddCategory(Android.Content.Intent.CategoryLauncher);
            var apps = pm.QueryIntentActivities(intent, 0);
            if (apps == null) return list;
            string? own = null;
            try { own = act!.PackageName; } catch { }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in apps.Take(2000))
            {
                try
                {
                    var pkg = r?.ActivityInfo?.PackageName ?? "";
                    if (pkg.Length == 0 || !seen.Add(pkg)) continue;
                    if (!string.IsNullOrEmpty(own) && pkg == own) continue;
                    string label;
                    try { label = r!.LoadLabel(pm)?.ToString() ?? ""; } catch { label = ""; }
                    if (string.IsNullOrWhiteSpace(label)) label = pkg;
                    label = label.Trim();
                    if (label.Length > 120) label = label.Substring(0, 120);
                    list.Add(new HdFileGuard.AppRef { Id = pkg, Name = label });
                }
                catch { }
            }
            try { list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)); } catch { }
        }
        catch { }
        return list;
    }

    static bool IsPackage(string s)
    {
        try
        {
            if (string.IsNullOrEmpty(s) || s.Length > 200) return false;
            var parts = s.Split('.');
            if (parts.Length < 2) return false;
            foreach (var p in parts)
            {
                if (p.Length == 0 || p.Length > 63) return false;
                if (!(char.IsLetter(p[0]) || p[0] == '_')) return false;
                foreach (var c in p)
                    if (!(char.IsLetterOrDigit(c) || c == '_'))
                        return false;
            }
            return true;
        }
        catch { return false; }
    }

    static async Task<string> LaunchAndroidAsync(string appId)
    {
        try
        {
            if (!IsPackage((appId ?? "").Trim())) return Fail("bad-id");
            var pkg = appId!.Trim();
            return await MainThread.InvokeOnMainThreadAsync(() =>
            {
                try
                {
                    var act = Platform.CurrentActivity;
                    var li = act?.PackageManager?.GetLaunchIntentForPackage(pkg);
                    if (act == null || li == null) return Fail("not-found");
                    li.AddFlags(Android.Content.ActivityFlags.NewTask);
                    act.StartActivity(li);
                    return "{\"ok\":true}";
                }
                catch (Exception e) { return Fail(e.Message); }
            });
        }
        catch (Exception e) { return Fail(e.Message); }
    }
#endif

    /// <summary>True on platforms where running-state is unavailable by
    /// policy (Android: dropped — no usage-stats toggle, no data).</summary>
    public static bool RunningLimited =>
#if ANDROID
        true;
#else
        false;
#endif

    /// <summary>Running flags for picked IDs, native side only. Windows
    /// matches visible windows to picked exe paths / AUMIDs; everywhere
    /// else every flag is false. Never throws.</summary>
    public static Dictionary<string, bool> RunningFlags(System.Collections.Generic.IEnumerable<string> ids)
    {
        var flags = new Dictionary<string, bool>(StringComparer.Ordinal);
        try
        {
            foreach (var id in ids)
            {
                try { if (!string.IsNullOrEmpty(id) && !flags.ContainsKey(id)) flags[id] = false; } catch { }
            }
            if (flags.Count == 0) return flags;
#if WINDOWS
            try { MarkRunningWindows(flags); } catch { }
#endif
        }
        catch { }
        return flags;
    }

#if WINDOWS
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint len, System.Text.StringBuilder id);

    /// <summary>Flip flags for picked apps owning a visible window: exe
    /// paths via the process image, Store apps via the window process's
    /// application user model id. Never throws.</summary>
    static void MarkRunningWindows(Dictionary<string, bool> flags)
    {
        var wantedExe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wantedAumid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in flags.Keys)
        {
            if (id.StartsWith("aumid:", StringComparison.OrdinalIgnoreCase))
            {
                var a = id.Substring(6);
                if (!string.IsNullOrEmpty(a)) wantedAumid.Add(a);
            }
            else wantedExe.Add(id);
        }
        if (wantedExe.Count == 0 && wantedAumid.Count == 0) return;
        try
        {
            EnumWindows((hWnd, _) =>
            {
                try
                {
                    if (!IsWindowVisible(hWnd)) return true;
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == 0) return true;
                    string? image = null, aumid = null;
                    try
                    {
                        using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                        try { image = proc.MainModule?.FileName; } catch { }
                    }
                    catch { }
                    if (wantedAumid.Count > 0)
                    {
                        IntPtr h = IntPtr.Zero;
                        try
                        {
                            h = OpenProcess(0x1000, false, pid);
                            if (h != IntPtr.Zero)
                            {
                                uint len = 0;
                                GetApplicationUserModelId(h, ref len, null!);
                                if (len > 1)
                                {
                                    var sb = new System.Text.StringBuilder((int)len);
                                    if (GetApplicationUserModelId(h, ref len, sb) == 0)
                                        aumid = sb.ToString();
                                }
                            }
                        }
                        catch { }
                        finally { try { if (h != IntPtr.Zero) CloseHandle(h); } catch { } }
                    }
                    if (!string.IsNullOrEmpty(image))
                    {
                        foreach (var id in flags.Keys)
                        {
                            if (flags[id] || wantedExe.Count == 0) continue;
                            if (!id.StartsWith("aumid:", StringComparison.OrdinalIgnoreCase)
                                && string.Equals(id, image, StringComparison.OrdinalIgnoreCase))
                                flags[id] = true;
                        }
                    }
                    if (!string.IsNullOrEmpty(aumid))
                    {
                        foreach (var id in flags.Keys)
                        {
                            if (flags[id]) continue;
                            if (id.StartsWith("aumid:", StringComparison.OrdinalIgnoreCase)
                                && string.Equals(id.Substring(6), aumid, StringComparison.OrdinalIgnoreCase))
                                flags[id] = true;
                        }
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }
#endif

    /// <summary>Full picked-apps answer for one site: [{id,name,running}]
    /// + limited flag. Unbound callers (transient pages, replaced files)
    /// get an empty list — fail closed, no error needed. Never throws.</summary>
    public static string PickedAppsJson(string siteKey, string? siteHash)
    {
        try
        {
            var refs = HdFileGuard.PickedAppRefs(siteKey, siteHash);
            var flags = RunningFlags(refs.Select(a => a.Id));
            var off = HdFileGuard.AppOffIds(siteKey, siteHash);
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                ok = true,
                limited = RunningLimited,
                data = refs.Select(a => new
                {
                    id = a.Id,
                    name = a.Name,
                    running = flags.TryGetValue(a.Id, out var r) && r,
                    enabled = !off.Contains(a.Id)
                })
            });
        }
        catch { return "{\"ok\":false,\"error\":\"failed\"}"; }
    }
}
