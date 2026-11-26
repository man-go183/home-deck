using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace HomeDeck.Services;

/// <summary>
/// Scoped file access: pages only ever see ONE folder (the HomeDeck root)
/// and every new site gets a native allow/deny popup first. The grant lives
/// in Preferences; file bytes never do. Fail-safe: any error = no access.
/// </summary>
public static class HdFileGuard
{
    // v2: app-private home (LocalAppData). The v1 key ("hd.root",
    // usually Documents\HomeDeck) is deliberately abandoned so old
    // installs re-derive instead of clinging to Documents.
    const string RootKey = "hd.root2";
    const string GrantsKey = "hd.grants";

    static readonly System.Text.Json.JsonSerializerOptions Json = new() { WriteIndented = false };

    static string DefaultRoot()
    {
        // App-private home: invisible, no OneDrive sync, clean uninstall.
        // No Documents fallback — the folder is managed, not browsed.
        try { return Path.Combine(FileSystem.AppDataDirectory, "HomeDeck"); }
        catch { return "HomeDeck"; }
    }

    /// <summary>Current scoped root (created lazily). Never throws.</summary>
    public static string Root
    {
        get
        {
            try
            {
                var r = Preferences.Get(RootKey, null);
                if (!string.IsNullOrWhiteSpace(r)) return r;
                r = DefaultRoot();
                try { Directory.CreateDirectory(r); } catch { }
                Preferences.Set(RootKey, r);
                return r;
            }
            catch { return DefaultRoot(); }
        }
    }

    /// <summary>Change the scoped root (must already exist — the OS folder
    /// picker is the consent, the user chose it with their own hands).
    /// Every read/write still needs that site's own grant.</summary>
    public static bool SetRoot(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
            Preferences.Set(RootKey, Path.GetFullPath(path));
            return true;
        }
        catch { return false; }
    }

    /// <summary>True when path resolves inside Root (canonical, no escape).
    /// Never throws.</summary>
    public static bool IsUnderRoot(string? path, out string full)
    {
        full = "";
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            full = Path.GetFullPath(path);
            return full.Equals(root, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Persisted file-access grant. For file:// callers the
    /// grant is bound to the calling page's content hash (H): replace
    /// the homepage file and the stored hash stops matching, so the
    /// shell re-prompts instead of the new bytes inheriting trust.
    /// https callers keep plain host keys (web content changes by
    /// nature; the user granted the origin).</summary>
    sealed class FileGrant
    {
        public bool Always { get; set; }
        public string? H { get; set; }
        /// Toggle state from Advanced settings: true = blocked-but-kept
        /// (grant survives; calls deny silently without re-prompting).
        public bool Off { get; set; }
    }

    static Dictionary<string, FileGrant> LoadGrants()
    {
        try
        {
            var raw = Preferences.Get(GrantsKey, null);
            if (!string.IsNullOrEmpty(raw))
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, FileGrant>>(raw, Json) ?? new();
        }
        catch { }
        // Any parse failure (including the pre-hash string format)
        // resets to no grants: fail closed, re-prompt.
        return new();
    }

    /// <summary>Resolve a native-observed caller URL to a grant key.
    /// file:// callers are keyed by their own page path ("file|...") plus
    /// the page file's SHA-256; anything else returns false and the
    /// caller falls back to the legacy host key. Page-supplied hosts
    /// must never reach here as identity - the URL has to come from
    /// the native view. Never throws.</summary>
    static bool TryFileCaller(string? callerUrl, out string key, out string? hash, out string name)
    {
        key = ""; hash = null; name = "This page";
        try
        {
            if (string.IsNullOrWhiteSpace(callerUrl)) return false;
            var u = new Uri(callerUrl);
            if (!u.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)) return false;
            var full = Path.GetFullPath(u.LocalPath);
            if (!File.Exists(full)) return false;
            key = "file|" + full;
            try
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var s = File.OpenRead(full);
                hash = Convert.ToHexString(sha.ComputeHash(s));
            }
            catch { hash = null; }
            name = FriendlyFileName(full);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Native-observed http(s) host (audit H1/option 4): when the
    /// native view URL parses as http(s), it wins over any page-supplied
    /// host string. Null when not an http(s) URL. Never throws.</summary>
    public static string? NativeHttpHost(string? callerUrl)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(callerUrl)) return null;
            var u = new Uri(callerUrl);
            if (!u.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                && !u.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return null;
            var h = u.Host?.Trim().ToLowerInvariant();
            return string.IsNullOrEmpty(h) ? null : h;
        }
        catch { return null; }
    }

    /// <summary>Audit M1(a): name the top host in prompts for blank-origin
    /// callers instead of a bare "This page". "" only reaches here as a
    /// top-level blank page now (subframes have no bridge): derive an
    /// honest label from the native-observed URL. Persistence stays off
    /// for these callers (see EnsureAccessAsync). Never throws.</summary>
    static string BlankDisplay(string? callerUrl)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(callerUrl)) return "This page";
            var u = new Uri(callerUrl);
            return u.Scheme.ToLowerInvariant() switch
            {
                "data" => "This embedded (data:) page",
                "about" => "This blank page",
                _ => "This page",
            };
        }
        catch { return "This page"; }
    }

    /// <summary>Native allow/deny for a site. Runs on the UI thread, outside
    /// page content so a site can't fake it. The grant lives in
    /// Preferences; file bytes never do. Fail-safe: any error = no access.
    /// callerUrl is the native-observed page URL (never page-supplied):
    /// file:// pages get a per-file, content-hash-bound grant so a
    /// replaced homepage file re-prompts; https pages keep host keys.</summary>
    public static Task<bool> EnsureAccessAsync(string? host, string op, string? callerUrl = null)
    {
        host = (host ?? "").Trim().ToLowerInvariant();
        try
        {
            bool isFile = TryFileCaller(callerUrl, out var fkey, out var fhash, out var fname);
            if (!isFile)
            {
                // Native-observed https identity wins (audit H1): never
                // key or name a prompt by a page-supplied string when the
                // view URL is known.
                var nh = NativeHttpHost(callerUrl);
                if (nh != null) host = nh;
            }
            if (!isFile && host.Length == 0) return PromptAsync(BlankDisplay(callerUrl), op, null, false, false, Perms.Scope(Perms.ForOp(op)));
            string siteKey = isFile ? fkey : host;
            string? siteHash = isFile ? fhash : null;
            string display = isFile ? fname : host;
            var perm = Perms.ForOp(op);
            if (perm == null)
            {
                // Unknown op: legacy bundle behavior (grant keyed by site).
                var g0 = LoadGrants();
                if (g0.TryGetValue(siteKey, out var b0) && b0 != null && b0.Always && HashOk(siteKey, siteHash, b0))
                    return Task.FromResult(true);
                bool persist0 = !(isFile && siteHash == null);
                return PromptAsync(display, op, persist0 ? g2 => { g2[siteKey] = new FileGrant { Always = true, H = siteHash }; } : null, persist0, !persist0, Perms.Scope(null));
            }
            var g = LoadGrants();
            var key = Perms.Key(siteKey, perm);
            if (g.TryGetValue(key, out var ex) && ex != null && ex.Always && HashOk(siteKey, siteHash, ex))
                return Task.FromResult(!ex.Off);
            // Legacy file-bundle migration: one grant covered both groups.
            if (Perms.IsFileGroup(perm) && g.TryGetValue(siteKey, out var b) && b != null && b.Always && HashOk(siteKey, siteHash, b))
            {
                g[key] = new FileGrant { Always = true, H = b.H };
                var sib = Perms.Key(siteKey, Perms.SiblingFile(perm));
                if (g.TryGetValue(sib, out var so) && so != null && so.Always) g.Remove(siteKey);
                SaveGrants(g);
                return Task.FromResult(true);
            }
            bool persist = !(isFile && siteHash == null);
            if (!persist) return PromptAsync(display, op, null, false, true, Perms.Scope(perm));
            return PromptAsync(display, op, g2 => { g2[key] = new FileGrant { Always = true, H = siteHash }; }, true, false, Perms.Scope(perm));
        }
        catch { return Task.FromResult(false); }
    }

    static bool HashOk(string siteKey, string? siteHash, FileGrant e)
    {
        try
        {
            if (!siteKey.StartsWith("file|", StringComparison.Ordinal)) return true;
            return siteHash != null && e.H != null && e.H.Equals(siteHash, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static void SaveGrants(Dictionary<string, FileGrant> g)
    {
        try { Preferences.Set(GrantsKey, System.Text.Json.JsonSerializer.Serialize(g, Json)); } catch { }
    }

    static async Task<bool> PromptAsync(string who, string op, Action<Dictionary<string, FileGrant>>? remember, bool canPersist, bool degradeAlwaysToOnce, string? scope)
    {
        try
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page == null) return false;
            if (string.IsNullOrEmpty(who)) who = "This page";
            if (string.IsNullOrEmpty(scope)) scope = "";
            var choice = await MainThread.InvokeOnMainThreadAsync(
                () => page.DisplayActionSheetAsync($"{who} wants to {op}{scope}.",
                    "Deny", null, "Always allow", "Just once"));
            if (choice == "Always allow" && canPersist && remember != null)
            {
                try
                {
                    var g = LoadGrants();
                    remember(g);
                    Preferences.Set(GrantsKey, System.Text.Json.JsonSerializer.Serialize(g, Json));
                }
                catch { }
                return true;
            }
            if (choice == "Always allow" && degradeAlwaysToOnce) return true;
            return choice == "Just once";
        }
        catch { return false; }
    }

    /// <summary>One picked launch target: opaque ID (exe path or
    /// "aumid:…" on Windows, package name on Android) + display name.
    /// Picked IDs (+names) are the only app facts a site ever learns.</summary>
    public sealed class AppRef
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    /// <summary>A site's picked launch set + the page-content hash it was
    /// picked from (file callers only). Replacing the page file
    /// invalidates the set, same rule as file grants.</summary>
    sealed class AppPickSet
    {
        public List<AppRef> Apps { get; set; } = new();
        public string? H { get; set; }
        /// Disabled app ids (settings toggles). Blocked-but-kept, like Off.
        public List<string> Off { get; set; } = new();
    }

    const string AppPicksKey = "hd.apppicks";

    /// <summary>Stable site identity shared by every capability store:
    /// file:// callers key by their own page path ("file|…"), https by
    /// host ("" when unknown). contentHash binds file callers to exact
    /// page bytes (null when none). Never throws.</summary>
    public static string SiteKey(string? host, string? callerUrl, out string? contentHash, out string display)
    {
        try
        {
            if (TryFileCaller(callerUrl, out var key, out var hash, out var name))
            { contentHash = hash; display = name; return key; }
            var nh = NativeHttpHost(callerUrl);
            if (nh != null) { contentHash = null; display = nh; return nh; }
        }
        catch { }
        contentHash = null;
        host = (host ?? "").Trim().ToLowerInvariant();
        display = string.IsNullOrEmpty(host) ? BlankDisplay(callerUrl) : host;
        return host;
    }

    static Dictionary<string, AppPickSet> LoadAppPicks()
    {
        try
        {
            var raw = Preferences.Get(AppPicksKey, null);
            if (!string.IsNullOrEmpty(raw))
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, AppPickSet>>(raw, Json) ?? new();
        }
        catch { }
        return new();
    }

    static void SaveAppPicks(Dictionary<string, AppPickSet> sets)
    {
        try { Preferences.Set(AppPicksKey, System.Text.Json.JsonSerializer.Serialize(sets, Json)); } catch { }
    }

    /// <summary>True when appId is in the site's picked set AND the set
    /// still binds (file callers: stored hash equals current page hash).
    /// Never throws; any doubt = false.</summary>
    public static bool IsAppPicked(string siteKey, string? siteHash, string appId)
    {
        try
        {
            if (string.IsNullOrEmpty(siteKey) || string.IsNullOrEmpty(appId) || appId.Length > 500) return false;
            var sets = LoadAppPicks();
            if (!sets.TryGetValue(siteKey, out var s) || s == null) return false;
            if (siteKey.StartsWith("file|", StringComparison.Ordinal))
            {
                if (siteHash == null || s.H == null || !s.H.Equals(siteHash, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return s.Apps.Any(a => a != null && a.Id == appId) && (s.Off == null || !s.Off.Contains(appId));
        }
        catch { return false; }
    }

    /// <summary>Replace the site's picked set (confirm = user ticked;
    /// empty = user cleared it). Binds file callers to current bytes.
    /// Never throws.</summary>
    public static void SetAppPicks(string siteKey, List<AppRef> apps, string? siteHash)
    {
        try
        {
            if (string.IsNullOrEmpty(siteKey)) return;
            var sets = LoadAppPicks();
            var clean = new List<AppRef>();
            foreach (var a in apps.Take(100))
            {
                try
                {
                    var id = (a?.Id ?? "").Trim();
                    var nm = (a?.Name ?? "").Trim();
                    if (id.Length == 0 || id.Length > 500) continue;
                    if (clean.Any(x => x.Id == id)) continue;
                    if (nm.Length > 120) nm = nm.Substring(0, 120);
                    clean.Add(new AppRef { Id = id, Name = nm });
                }
                catch { }
            }
            List<string> keep = new();
            try
            {
                if (sets.TryGetValue(siteKey, out var prev) && prev?.Off != null)
                    keep = prev.Off.Where(o => !string.IsNullOrEmpty(o) && clean.Any(x => x.Id == o)).Distinct().ToList();
            }
            catch { }
            sets[siteKey] = new AppPickSet { Apps = clean, H = siteHash, Off = keep };
            SaveAppPicks(sets);
        }
        catch { }
    }

    /// <summary>Picked refs the site may currently use (for the sheet
    /// pre-tick and the picked-apps answer). Empty when unbound: blank
    /// callers, transient pages, replaced files. Never throws.</summary>
    public static List<AppRef> PickedAppRefs(string siteKey, string? siteHash)
    {
        try
        {
            var sets = LoadAppPicks();
            if (!sets.TryGetValue(siteKey, out var s) || s == null) return new();
            if (siteKey.StartsWith("file|", StringComparison.Ordinal))
            {
                if (siteHash == null || s.H == null || !s.H.Equals(siteHash, StringComparison.OrdinalIgnoreCase)) return new();
            }
            return s.Apps.Where(a => a != null && !string.IsNullOrEmpty(a.Id)).DistinctBy(a => a.Id).ToList();
        }
        catch { return new(); }
    }

    /// <summary>IDs the site may currently use (for pre-ticking the
    /// sheet). Empty when unbound. Never throws.</summary>
    public static List<string> PickedAppIds(string siteKey, string? siteHash)
    {
        try { return PickedAppRefs(siteKey, siteHash).Select(a => a.Id).ToList(); }
        catch { return new(); }
    }

    /// Permission ids (stable, stored after "||" in grant keys).
    public static class Perms
    {
        public const string FilesRead = "files-read";
        public const string FilesEdit = "files-edit";
        public const string Clipboard = "clipboard";
        public const string Notifications = "notifications";
        public const string Nearby = "nearby";
        public const string Device = "device";
        public static string? ForOp(string op) => op switch
        {
            "list files" or "read file info" or "read files" => FilesRead,
            "write files" or "create folders" or "delete files" => FilesEdit,
            "read the clipboard" or "write the clipboard" => Clipboard,
            "read notifications" or "send notifications" => Notifications,
            "scan nearby devices" => Nearby,
            "read device info" or "use vibration" or "read brightness" or "set brightness" => Device,
            _ => null,
        };
        public static string Key(string siteKey, string perm) => siteKey + "||" + perm;
        public static bool IsFileGroup(string perm) => perm == FilesRead || perm == FilesEdit;
        public static string SiblingFile(string perm) => perm == FilesRead ? FilesEdit : FilesRead;
        public static string Scope(string? perm) => perm switch
        {
            FilesRead or FilesEdit => " in your HomeDeck folder",
            _ => "",
        };
        public static readonly (string Id, string Title, string Sub)[] All = new[]
        {
            (FilesRead, "Read files & folders", "List, view info, read contents"),
            (FilesEdit, "Modify files & folders", "Write, create folders, delete"),
            (Clipboard, "Clipboard", "Read and write the clipboard"),
            (Notifications, "Notifications", "Read notifications, send notifications"),
            (Nearby, "Nearby devices", "Bluetooth and Wi-Fi scans"),
            (Device, "Device info", "Battery, network, device model"),
        };
    }

    /// One site's standing for one permission (settings UI).
    public sealed class SitePerm
    {
        public string SiteKey = "";
        public string Display = "";
        public bool Allowed;
    }

    /// <summary>Optional homepage-slot title resolver (set by the UI layer,
    /// which owns profiles): sanitized profile id → "ogname · profile".</summary>
    public static Func<string, string?>? HomepageTitleFor;

    static string DisplayFor(string siteKey)
    {
        try
        {
            if (siteKey.StartsWith("file|", StringComparison.Ordinal))
                return FriendlyFileName(siteKey.Substring(5));
        }
        catch { }
        return string.IsNullOrEmpty(siteKey) ? "This page" : siteKey;
    }

    /// <summary>Friendly name for a local page file (audit Q1): homepage
    /// slots are named by profile id (a Guid), so resolve through
    /// HomepageTitleFor ("ogname · profile") instead of showing the raw
    /// slot id. Non-slot files fall back to the file name. Never throws.</summary>
    static string FriendlyFileName(string full)
    {
        try
        {
            if (string.Equals(Path.GetFileName(Path.GetDirectoryName(full)), "homepages", StringComparison.OrdinalIgnoreCase))
            {
                var t = HomepageTitleFor?.Invoke(Path.GetFileNameWithoutExtension(full));
                if (!string.IsNullOrEmpty(t)) return t;
            }
        }
        catch { }
        try
        {
            var n = Path.GetFileName(full);
            if (!string.IsNullOrEmpty(n)) return n;
        }
        catch { }
        return "This page";
    }

    /// All sites holding permId (exact entries + legacy file bundles).
    /// Never throws.
    public static List<SitePerm> PermSites(string permId)
    {
        var list = new List<SitePerm>();
        try
        {
            var g = LoadGrants();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string suf = "||" + permId;
            foreach (var kv in g)
            {
                try
                {
                    if (kv.Key.EndsWith(suf, StringComparison.Ordinal))
                    {
                        var skey = kv.Key.Substring(0, kv.Key.Length - suf.Length);
                        if (seen.Add(skey))
                            list.Add(new SitePerm { SiteKey = skey, Display = DisplayFor(skey), Allowed = kv.Value != null && kv.Value.Always && !kv.Value.Off });
                    }
                    else if (!kv.Key.Contains("||") && Perms.IsFileGroup(permId) && kv.Value != null && kv.Value.Always && seen.Add(kv.Key))
                    {
                        list.Add(new SitePerm { SiteKey = kv.Key, Display = DisplayFor(kv.Key), Allowed = true });
                    }
                }
                catch { }
            }
            list.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        return list;
    }

    public static int PermAllowedCount(string permId)
    {
        try { int n = 0; foreach (var x in PermSites(permId)) if (x.Allowed) n++; return n; }
        catch { return 0; }
    }

    /// Toggle off (blocked-but-kept). Migrates file bundles first so the
    /// sibling group keeps working. Never throws.
    public static void SetPermOff(string siteKey, string permId, bool off)
    {
        try
        {
            if (string.IsNullOrEmpty(siteKey) || string.IsNullOrEmpty(permId)) return;
            var g = LoadGrants();
            var key = Perms.Key(siteKey, permId);
            if (!g.TryGetValue(key, out var en) || en == null)
            {
                en = new FileGrant { Always = true };
                if (Perms.IsFileGroup(permId) && g.TryGetValue(siteKey, out var b) && b != null && b.Always)
                {
                    en.H = b.H;
                    var sib = Perms.Key(siteKey, Perms.SiblingFile(permId));
                    if (!g.ContainsKey(sib)) g[sib] = new FileGrant { Always = true, H = b.H };
                    g.Remove(siteKey);
                }
                else if (!Perms.IsFileGroup(permId)) return;
                g[key] = en;
            }
            en.Off = off;
            SaveGrants(g);
        }
        catch { }
    }

    /// Revoke: delete the entry (next use re-prompts). For file groups a
    /// legacy bundle is pinned to the sibling so only this group drops.
    /// Never throws.
    public static void DeletePermEntry(string siteKey, string permId)
    {
        try
        {
            if (string.IsNullOrEmpty(siteKey) || string.IsNullOrEmpty(permId)) return;
            var g = LoadGrants();
            g.Remove(Perms.Key(siteKey, permId));
            if (Perms.IsFileGroup(permId) && g.TryGetValue(siteKey, out var b) && b != null)
            {
                g[Perms.Key(siteKey, Perms.SiblingFile(permId))] = new FileGrant { Always = b.Always, H = b.H };
                g.Remove(siteKey);
            }
            SaveGrants(g);
        }
        catch { }
    }

    /// Disabled app ids for a site (settings toggles). Never throws.
    public static List<string> AppOffIds(string siteKey, string? siteHash)
    {
        try
        {
            var sets = LoadAppPicks();
            if (!sets.TryGetValue(siteKey, out var s) || s == null || s.Off == null) return new();
            if (siteKey.StartsWith("file|", StringComparison.Ordinal))
            {
                if (siteHash == null || s.H == null || !s.H.Equals(siteHash, StringComparison.OrdinalIgnoreCase)) return new();
            }
            return s.Off.Where(o => !string.IsNullOrEmpty(o)).Distinct().ToList();
        }
        catch { return new(); }
    }

    /// Toggle one picked app off/on (blocked-but-kept). No-op when the
    /// set is unbound. Never throws.
    public static void SetAppOff(string siteKey, string? siteHash, string appId, bool off, bool trusted = false)
    {
        try
        {
            if (string.IsNullOrEmpty(siteKey) || string.IsNullOrEmpty(appId)) return;
            var sets = LoadAppPicks();
            if (!sets.TryGetValue(siteKey, out var s) || s == null) return;
            if (!trusted && siteKey.StartsWith("file|", StringComparison.Ordinal))
            {
                if (siteHash == null || s.H == null || !s.H.Equals(siteHash, StringComparison.OrdinalIgnoreCase)) return;
            }
            s.Off ??= new List<string>();
            if (off) { if (!s.Off.Contains(appId)) s.Off.Add(appId); }
            else s.Off.Remove(appId);
            SaveAppPicks(sets);
        }
        catch { }
    }

    /// One picked app across sites (settings Apps section).
    public sealed class AppSite
    {
        public string SiteKey = "";
        public string Display = "";
        public bool Enabled;
    }

    /// All distinct picked app ids with a display name (first seen).
    /// Never throws.
    public static List<AppRef> AllPickedApps()
    {
        var list = new List<AppRef>();
        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in LoadAppPicks())
            {
                if (kv.Value?.Apps == null) continue;
                foreach (var a in kv.Value.Apps)
                {
                    if (a == null || string.IsNullOrEmpty(a.Id) || !seen.Add(a.Id)) continue;
                    list.Add(new AppRef { Id = a.Id, Name = string.IsNullOrEmpty(a.Name) ? a.Id : a.Name });
                }
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        return list;
    }

    /// Sites holding appId, with per-site enabled state. Never throws.
    public static List<AppSite> AppSites(string appId)
    {
        var list = new List<AppSite>();
        try
        {
            if (string.IsNullOrEmpty(appId)) return list;
            foreach (var kv in LoadAppPicks())
            {
                try
                {
                    if (kv.Value?.Apps == null || !kv.Value.Apps.Any(a => a != null && a.Id == appId)) continue;
                    bool en = kv.Value.Off == null || !kv.Value.Off.Contains(appId);
                    list.Add(new AppSite { SiteKey = kv.Key, Display = DisplayFor(kv.Key), Enabled = en });
                }
                catch { }
            }
            list.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        return list;
    }

    /// Remove one app from one site's set (settings unpick). Drops the
    /// site entry when the set empties. Never throws.
    public static void RemoveAppPick(string siteKey, string appId)
    {
        try
        {
            if (string.IsNullOrEmpty(siteKey) || string.IsNullOrEmpty(appId)) return;
            var sets = LoadAppPicks();
            if (!sets.TryGetValue(siteKey, out var s) || s == null) return;
            s.Apps.RemoveAll(a => a == null || a.Id == appId);
            s.Off?.Remove(appId);
            if (s.Apps.Count == 0) sets.Remove(siteKey);
            SaveAppPicks(sets);
        }
        catch { }
    }

    /// <summary>App-owned homepage slot for a profile: one fixed file, so
    /// renames can't break it and orphans are impossible (import
    /// overwrites). Lives in the scoped root so pages can reach it through
    /// the bridge too (self-editing homepage).</summary>
    public static string HomepagePath(string? profileId)
    {
        try
        {
            var safe = new string(((profileId ?? "home").Where(char.IsLetterOrDigit)).ToArray());
            if (safe.Length == 0) safe = "home";
            return Path.Combine(Root, "homepages", safe + ".html");
        }
        catch { return Path.Combine(DefaultRoot(), "homepages", "home.html"); }
    }

}
