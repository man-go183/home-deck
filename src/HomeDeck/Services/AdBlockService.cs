using Microsoft.Maui.Storage;

namespace HomeDeck.Services;

/// <summary>
/// Per-profile ad &amp; tracker blocking, decided by Brave's adblock-rust
/// (via <see cref="RustFilterEngine"/> + the hdblock native library) and
/// enforced at the WebView request layer on each platform. Same three lists
/// Brave uses (EasyList, EasyPrivacy, Brave Unbreak), refreshed from the
/// network with a 7-day on-device cache. Without the native library (or
/// before lists load) blocking is off and everything loads — fail open.
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
        new("brave-unbreak", "https://raw.githubusercontent.com/brave/adblock-lists/master/brave-lists/brave-unbreak.txt"),
    };

    readonly HttpClient _http = new();
    readonly RustFilterEngine _engine = new();
    DateTime _loadedAt = DateTime.MinValue;

    /// <summary>List refresh cadence in days (global pref, default 7).</summary>
    public int RefreshDays { get; set; } = 7;

    public bool Ready => _loadedAt != DateTime.MinValue;

    public async Task EnsureLoadedAsync(bool force = false)
    {
        int days = Math.Max(1, RefreshDays);
        if (!force && (DateTime.UtcNow - _loadedAt).TotalDays < days && Ready) return;
        var all = new List<string>();
        foreach (var list in DefaultLists)
        {
            try
            {
                var path = Path.Combine(FileSystem.AppDataDirectory, list.Name + ".txt");
                string text;
                var info = new FileInfo(path);
                if (info.Exists && !force && (DateTime.UtcNow - info.LastWriteTimeUtc).TotalDays < days)
                {
                    text = await File.ReadAllTextAsync(path);
                }
                else
                {
                    text = await _http.GetStringAsync(list.Url);
                    await File.WriteAllTextAsync(path, text);
                }
                all.Add(text);
            }
            catch { /* stale cache or nothing; keep going */ }
        }
        if (all.Count > 0 && RustFilterEngine.LoadAllRules(string.Join("\n", all)))
            _loadedAt = DateTime.UtcNow;
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
    }

    public IReadOnlyList<ListStatus> GetListStatus()
    {
        var res = new List<ListStatus>();
        foreach (var list in DefaultLists)
        {
            try
            {
                var path = Path.Combine(FileSystem.AppDataDirectory, list.Name + ".txt");
                var info = new FileInfo(path);
                res.Add(new ListStatus
                {
                    Name = list.Name,
                    UpdatedUtc = info.Exists ? info.LastWriteTimeUtc : null,
                    Bytes = info.Exists ? info.Length : 0,
                });
            }
            catch { res.Add(new ListStatus { Name = list.Name }); }
        }
        return res;
    }
}
