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

    public bool Ready => _loadedAt != DateTime.MinValue;

    public async Task EnsureLoadedAsync()
    {
        if ((DateTime.UtcNow - _loadedAt).TotalDays < 7 && Ready) return;
        var all = new List<string>();
        foreach (var list in DefaultLists)
        {
            try
            {
                var path = Path.Combine(FileSystem.AppDataDirectory, list.Name + ".txt");
                string text;
                var info = new FileInfo(path);
                if (info.Exists && (DateTime.UtcNow - info.LastWriteTimeUtc).TotalDays < 7)
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
}
