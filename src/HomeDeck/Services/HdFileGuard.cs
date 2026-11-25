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
            try { var n = Path.GetFileName(full); if (!string.IsNullOrEmpty(n)) name = n; } catch { }
            return true;
        }
        catch { return false; }
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
            if (TryFileCaller(callerUrl, out var key, out var hash, out var name))
            {
                var g = LoadGrants();
                if (hash != null && g.TryGetValue(key, out var e) && e != null && e.Always && e.H == hash)
                    return Task.FromResult(true);
                // "Always" binds to this exact file content; an unhashable
                // file degrades to once (nothing persistable to re-check).
                return PromptAsync(name, op, g2 => { g2[key] = new FileGrant { Always = true, H = hash }; }, hash != null, hash == null);
            }
            if (host.Length == 0) return PromptAsync("This page", op, null, false, false);
            var gh = LoadGrants();
            if (gh.TryGetValue(host, out var eh) && eh != null && eh.Always) return Task.FromResult(true);
            return PromptAsync(host, op, g2 => { g2[host] = new FileGrant { Always = true }; }, true, false);
        }
        catch { return Task.FromResult(false); }
    }

    static async Task<bool> PromptAsync(string who, string op, Action<Dictionary<string, FileGrant>>? remember, bool canPersist, bool degradeAlwaysToOnce)
    {
        try
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page == null) return false;
            if (string.IsNullOrEmpty(who)) who = "This page";
            var choice = await MainThread.InvokeOnMainThreadAsync(
                () => page.DisplayActionSheetAsync($"{who} wants to {op} in your HomeDeck folder.",
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
        }
        catch { }
        contentHash = null;
        host = (host ?? "").Trim().ToLowerInvariant();
        display = string.IsNullOrEmpty(host) ? "This page" : host;
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
            return s.Apps.Any(a => a != null && a.Id == appId);
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
            sets[siteKey] = new AppPickSet { Apps = clean, H = siteHash };
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
