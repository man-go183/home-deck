using HomeDeck.Models;
using Microsoft.Maui.Storage;

namespace HomeDeck.Services;

/// <summary>
/// Per-profile ad &amp; tracker blocking, decided by Brave's adblock-rust
/// (via <see cref="RustFilterEngine"/> + the hdblock native library) and
/// enforced at the WebView request layer on each platform. Same sources
/// Brave uses (EasyList, EasyPrivacy, Brave Specific, Brave First-Party),
/// refreshed from the network with an on-device cache. Without the native
/// library (or before lists load) blocking is off and everything loads —
/// fail open.
/// </summary>
public sealed class AdBlockService
{
    public interface IFilterEngine
    {
        bool ShouldBlock(string url, string pageHost, string resourceType);
    }

    public sealed class ListSource
    {
        public string Name { get; }
        public string Url { get; }
        public ListSource(string name, string url) { Name = name; Url = url; }
    }

    public static readonly ListSource[] DefaultLists =
    {
        new("easylist", "https://easylist.to/easylist/easylist.txt"),
        new("easyprivacy", "https://easylist.to/easylist/easyprivacy.txt"),
        new("brave-specific", "https://raw.githubusercontent.com/brave/adblock-lists/master/brave-lists/brave-specific.txt"),
        new("brave-firstparty", "https://raw.githubusercontent.com/brave/adblock-lists/master/brave-lists/brave-firstparty.txt"),
    };

    readonly HttpClient _http = new();
    readonly RustFilterEngine _engine = new();
    DateTime _loadedAt = DateTime.MinValue;

    HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);
    List<CustomList> _customs = new();

    /// <summary>Push the user's list config (page owns prefs, service
    /// owns loading). Must be called before any load.</summary>
    public void ApplyConfig(IEnumerable<string>? disabled, IEnumerable<CustomList>? customs)
    {
        _disabled = new HashSet<string>(disabled ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _customs = (customs ?? Enumerable.Empty<CustomList>())
            .Select(c => new CustomList { Name = c.Name, Url = c.Url, Enabled = c.Enabled }).ToList();
    }

    /// <summary>One loadable list: built-in or custom.</summary>
    sealed class ActiveSource
    {
        public string Name = "";
        public string Key = "";
        public string Url = "";
        public bool Enabled = true;
        public bool IsCustom;
    }

    /// <summary>Filesystem-safe cache key for a list name.</summary>
    public static string CacheKey(string name)
    {
        var s = new string(name.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray()).Trim('_');
        return s.Length == 0 ? "custom" : s;
    }

    List<ActiveSource> AllSources()
    {
        var res = DefaultLists
            .Select(l => new ActiveSource { Name = l.Name, Key = CacheKey(l.Name), Url = l.Url, Enabled = !_disabled.Contains(l.Name) })
            .ToList();
        var seen = new HashSet<string>(res.Select(r => r.Key));
        foreach (var c in _customs)
        {
            var key = CacheKey(c.Name);
            if (!seen.Add(key)) continue; // blank or collides — page validates, belt-and-braces
            res.Add(new ActiveSource { Name = c.Name, Key = key, Url = c.Url, Enabled = c.Enabled, IsCustom = true });
        }
        return res;
    }

    /// <summary>List refresh cadence in days (global pref, default 7).</summary>
    public int RefreshDays { get; set; } = 7;

    public bool Ready => _loadedAt != DateTime.MinValue;

    public async Task EnsureLoadedAsync(bool force = false)
    {
        int days = Math.Max(1, RefreshDays);
        if (!force && (DateTime.UtcNow - _loadedAt).TotalDays < days && Ready) return;
        DropRetiredCaches();
        var all = new List<string>();
        foreach (var list in AllSources())
        {
            if (!list.Enabled) continue; // switched off: keep cache, exclude from engine
            try
            {
                var path = Path.Combine(FileSystem.AppDataDirectory, list.Key + ".txt");
                string? text = null;
                var info = new FileInfo(path);
                if (info.Exists && !force && (DateTime.UtcNow - info.LastWriteTimeUtc).TotalDays < days)
                {
                    text = await File.ReadAllTextAsync(path);
                    if (!LooksLikeList(text))
                    {
                        // Poisoned cache (e.g. a portal/parked page saved as a
                        // "list"): drop it and treat the list as undownloaded.
                        try { File.Delete(path); } catch { }
                        text = null;
                        info = new FileInfo(path);
                    }
                }
                if (text == null)
                {
                    string fresh = await DownloadListAsync(list.Url);
                    if (LooksLikeList(fresh))
                    {
                        await File.WriteAllTextAsync(path, fresh);
                        text = fresh;
                    }
                    else if (info.Exists)
                    {
                        // Upstream stubbed/emptied (brave-unbreak shrank to one
                        // comment line) or returned junk: keep the stale copy,
                        // never poison the cache with a bad download.
                        var stale = await File.ReadAllTextAsync(path);
                        if (LooksLikeList(stale)) text = stale;
                    }
                    // else: no cache and nothing usable upstream — skip, fail open.
                }
                if (LooksLikeList(text))
                    all.Add(text);
            }
            catch { /* stale cache or nothing; keep going */ }
        }
        if (all.Count > 0 && RustFilterEngine.LoadAllRules(string.Join("\n", all)))
            _loadedAt = DateTime.UtcNow;
    }

    /// <summary>Rebuild the engine from on-disk caches only (no network):
    /// instant apply after a list is toggled. Empty set clears the engine.</summary>
    public async Task ReloadFromCacheAsync()
    {
        var all = new List<string>();
        foreach (var list in AllSources())
        {
            if (!list.Enabled) continue;
            try
            {
                var text = await File.ReadAllTextAsync(
                    Path.Combine(FileSystem.AppDataDirectory, list.Key + ".txt"));
                if (LooksLikeList(text)) all.Add(text);
            }
            catch { }
        }
        if (RustFilterEngine.LoadAllRules(string.Join("\n", all)))
            _loadedAt = DateTime.UtcNow;
    }

    /// <summary>Delete one list's cache file (custom-list removal).</summary>
    public void DeleteCacheFor(string key)
    {
        try { File.Delete(Path.Combine(FileSystem.AppDataDirectory, key + ".txt")); } catch { }
    }

    /// <summary>Download list text. Rejects HTML responses (parked
    /// domains, captive portals and ISP hijacks serve pages for any URL —
    /// a 404-page must never become a "filter list"). Throws on HTTP or
    /// content errors so callers keep the stale cache.</summary>
    async Task<string> DownloadListAsync(string url)
    {
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var mt = resp.Content.Headers.ContentType?.MediaType;
        if (mt != null && mt.Contains("html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("HTML response, not a filter list");
        return await resp.Content.ReadAsStringAsync();
    }

    /// <summary>True when text plausibly is a filter list: holds at least
    /// one real rule and isn't a saved HTML page.</summary>
    static bool LooksLikeList(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var t = text.TrimStart();
        if (t.StartsWith("<", StringComparison.Ordinal)) return false;
        return HasRules(text);
    }
    static bool HasRules(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0 && t[0] != '!' && t[0] != '[') return true;
        }
        return false;
    }

    /// <summary>Delete cache files for lists no longer configured
    /// (retired built-ins, removed customs). Disabled-but-kept lists are
    /// configured, so their caches survive. Avatars live in a subfolder;
    /// nothing else stores .txt at the root.</summary>
    void DropRetiredCaches()
    {
        try
        {
            var keep = new HashSet<string>(AllSources().Select(l => l.Key + ".txt"));
            foreach (var f in Directory.GetFiles(FileSystem.AppDataDirectory, "*.txt"))
                if (!keep.Contains(Path.GetFileName(f)))
                    try { File.Delete(f); } catch { }
        }
        catch { }
    }

    public bool ShouldBlock(string url, string pageHost, string resourceType)
    {
        if (!Ready) return false;
        try { return _engine.ShouldBlock(url, pageHost, resourceType); }
        catch { return false; } // fail open: never break a page on matcher error
    }

    /// <summary>Element-hide CSS for a page host, or "" when none/unknown.</summary>
    public string GetCosmeticCssForHost(string pageHost)
    {
        if (!Ready || string.IsNullOrEmpty(pageHost)) return "";
        try { return RustFilterEngine.GetCosmeticCss("https://" + pageHost + "/"); }
        catch { return ""; }
    }

    /// <summary>Element-hide CSS including generic rules for the page's
    /// classes/ids (from <see cref="ParseClassSnapshot"/>).</summary>
    public string GetCosmeticCssForClasses(string pageHost, string classesCsv, string idsCsv)
    {
        if (!Ready || string.IsNullOrEmpty(pageHost)) return "";
        try { return RustFilterEngine.GetCosmeticCssForClasses("https://" + pageHost + "/", classesCsv, idsCsv); }
        catch { return ""; }
    }

    /// <summary>
    /// Parse the class/id snapshot JS collected in the page
    /// (<c>{"c":[...],"i":[...]}</c>, possibly JSON-encoded twice by the
    /// WebView bridge). Never throws; empty arrays on any failure.
    /// </summary>
    public static void ParseClassSnapshot(string? raw, out string[] classes, out string[] ids)
    {
        classes = Array.Empty<string>();
        ids = Array.Empty<string>();
        try
        {
            if (string.IsNullOrEmpty(raw)) return;
            var inner = raw.Trim();
            // WebView bridges JSON-encode the string result once more.
            if (inner.StartsWith("\""))
            {
                var s = System.Text.Json.JsonSerializer.Deserialize<string>(inner);
                if (string.IsNullOrEmpty(s)) return;
                inner = s;
            }
            using var doc = System.Text.Json.JsonDocument.Parse(inner);
            if (doc.RootElement.TryGetProperty("c", out var c))
                classes = c.EnumerateArray()
                    .Select(e => e.GetString() ?? "")
                    .Where(s => s.Length > 0 && s.Length <= 128)
                    .Distinct().Take(2000).ToArray();
            if (doc.RootElement.TryGetProperty("i", out var i))
                ids = i.EnumerateArray()
                    .Select(e => e.GetString() ?? "")
                    .Where(s => s.Length > 0 && s.Length <= 128)
                    .Distinct().Take(500).ToArray();
        }
        catch { }
    }

    /// <summary>JS snippet collecting the page's classes/ids for generic
    /// cosmetic matching. Capped (2000 classes, 500 ids).</summary>
    public const string ClassSnapshotJs =
        "(function(){try{var s={},o=[];document.querySelectorAll('[class]').forEach(function(e){e.classList.forEach(function(x){if(!s[x]){s[x]=1;o.push(x)}})});var t={},p=[];document.querySelectorAll('[id]').forEach(function(e){if(e.id&&!t[e.id]){t[e.id]=1;p.push(e.id)}});return JSON.stringify({c:o.slice(0,2000),i:p.slice(0,500)})}catch(e){return '{}'}})()";

    /// <summary>On-device filter-list status for the settings panel.</summary>
    public sealed class ListStatus
    {
        public string Name { get; set; } = "";
        public DateTime? UpdatedUtc { get; set; }
        public long Bytes { get; set; }
        public bool HasCache => Bytes > 0;
        public bool Enabled { get; set; } = true;
        public bool IsCustom { get; set; }
    }

    public IReadOnlyList<ListStatus> GetListStatus()
    {
        var res = new List<ListStatus>();
        foreach (var list in AllSources())
        {
            try
            {
                var path = Path.Combine(FileSystem.AppDataDirectory, list.Key + ".txt");
                var info = new FileInfo(path);
                res.Add(new ListStatus
                {
                    Name = list.Name,
                    UpdatedUtc = info.Exists ? info.LastWriteTimeUtc : null,
                    Bytes = info.Exists ? info.Length : 0,
                    Enabled = list.Enabled,
                    IsCustom = list.IsCustom,
                });
            }
            catch { res.Add(new ListStatus { Name = list.Name, Enabled = list.Enabled, IsCustom = list.IsCustom }); }
        }
        return res;
    }
}
