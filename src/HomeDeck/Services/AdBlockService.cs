using System.Text.Json;
using Microsoft.Maui.Storage;

namespace HomeDeck.Services;

/// <summary>
/// v1 ad &amp; tracker blocking. Same lists Brave uses (EasyList, EasyPrivacy,
/// Brave Unbreak), matched with a C# engine supporting the ABP-subset those
/// lists rely on: ||domain^ anchors, |exact prefixes, * wildcards, plain
/// substrings, @@ exceptions, and $third-party / $domain= / resource-type
/// options. Cosmetic (element-hide) rules are accepted and ignored — network
/// blocking is what the WebView hooks enforce.
///
/// UPGRADE PATH: this class is the only consumer of filter lists. To switch to
/// Brave's adblock-rust, compile it as a cdylib per target ABI, DllImport the
/// C ABI (engine_create/add_rules/matches), and implement IFilterEngine with
/// it. No other file changes.
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
    IFilterEngine _engine = new AbpEngine(Array.Empty<string>());
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
                all.AddRange(text.Split('\n'));
            }
            catch { /* stale cache or nothing; keep going */ }
        }
        if (all.Count > 0)
        {
            _engine = new AbpEngine(all);
            _loadedAt = DateTime.UtcNow;
        }
    }

    public bool ShouldBlock(string url, string pageHost, string resourceType)
    {
        if (!Ready) return false;
        try { return _engine.ShouldBlock(url, pageHost, resourceType); }
        catch { return false; } // fail open: never break a page on matcher error
    }

    // ---- minimal ABP-subset engine -------------------------------------------

    sealed class AbpEngine : IFilterEngine
    {
        readonly List<Rule> _block = new();
        readonly List<Rule> _allow = new();

        public AbpEngine(IEnumerable<string> lines)
        {
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '!' || line[0] == '[') continue;
                if (line.StartsWith("##") || line.Contains("##") || line.Contains("#@#")) continue; // cosmetic: ignore
                try
                {
                    var r = Rule.Parse(line);
                    if (r != null) (r.Allow ? _allow : _block).Add(r);
                }
                catch { /* skip bad lines */ }
            }
        }

        public bool ShouldBlock(string url, string pageHost, string resourceType)
        {
            string host;
            try { host = new Uri(url).Host.ToLowerInvariant(); }
            catch { return false; }
            bool thirdParty = !SameSite(host, pageHost.ToLowerInvariant());
            foreach (var r in _allow)
                if (r.Hits(url, host, thirdParty, resourceType)) return false;
            foreach (var r in _block)
                if (r.Hits(url, host, thirdParty, resourceType)) return true;
            return false;
        }

        static bool SameSite(string a, string b)
            => a == b || a.EndsWith("." + b) || b.EndsWith("." + a);
    }

    sealed class Rule
    {
        public bool Allow;
        public string Pattern = "";
        public bool AnchorHost;   // ||domain^
        public bool AnchorStart;  // |prefix
        public bool AnchorEnd;    // suffix|
        public string[] Domains = Array.Empty<string>(); // $domain= allowlist
        public bool ThirdPartyOnly;
        public bool FirstPartyOnly;
        public HashSet<string> Types = new(); // $script,$image,... (empty = all)

        static readonly HashSet<string> KnownTypes = new()
            { "script", "image", "stylesheet", "xmlhttprequest", "subdocument", "font", "media", "websocket", "other" };

        public static Rule? Parse(string line)
        {
            var r = new Rule();
            if (line.StartsWith("@@")) { r.Allow = true; line = line[2..]; }

            var optIdx = line.LastIndexOf('$');
            if (optIdx > 0 && !line[(optIdx + 1)..].Contains(' '))
            {
                foreach (var opt in line[(optIdx + 1)..].Split(','))
                {
                    var o = opt.Trim();
                    if (o == "third-party") r.ThirdPartyOnly = true;
                    else if (o == "first-party" || o == "~third-party") r.FirstPartyOnly = true;
                    else if (o.StartsWith("domain=")) r.Domains = o[7..].Split('|');
                    else if (KnownTypes.Contains(o)) r.Types.Add(o);
                    else if (o.StartsWith("~") && KnownTypes.Contains(o[1..])) { /* negated type: ignore */ }
                    else if (o is "important" or "match-case" or "document" or "elemhide" or "generichide") { }
                    else if (o.Contains('=')) { } // unknown key=val: ignore
                    else return null; // unknown bare option: safest to drop the rule
                }
                line = line[..optIdx];
            }

            if (line.StartsWith("||")) { r.AnchorHost = true; line = line[2..]; }
            else if (line.StartsWith("|")) { r.AnchorStart = true; line = line[1..]; }
            if (line.EndsWith("|")) { r.AnchorEnd = true; line = line[..^1]; }
            if (line.EndsWith("^")) line = line[..^1];
            r.Pattern = line;
            return string.IsNullOrEmpty(r.Pattern) ? null : r;
        }

        public bool Hits(string url, string host, bool thirdParty, string resourceType)
        {
            if (ThirdPartyOnly && !thirdParty) return false;
            if (FirstPartyOnly && thirdParty) return false;
            if (Types.Count > 0 && !Types.Contains(resourceType)) return false;
            if (Domains.Length > 0)
            {
                bool ok = false;
                foreach (var d in Domains)
                {
                    var dd = d.TrimStart('~');
                    bool neg = d.StartsWith("~");
                    bool match = host == dd || host.EndsWith("." + dd);
                    if (match && !neg) { ok = true; break; }
                    if (match && neg) return false;
                }
                if (!ok) return false;
            }

            var urlL = url.ToLowerInvariant();
            var pat = Pattern.ToLowerInvariant();
            if (AnchorHost)
            {
                var core = pat.Split('^')[0].Split('*')[0].TrimEnd('/');
                return host == core || host.EndsWith("." + core);
            }
            if (AnchorStart)
            {
                var rx = "^" + Regex.Escape(pat).Replace(@"\*", ".*");
                return System.Text.RegularExpressions.Regex.IsMatch(urlL, rx);
            }
            if (pat.Contains("*"))
            {
                var parts = pat.Split('*', StringSplitOptions.RemoveEmptyEntries);
                int pos = 0;
                foreach (var part in parts)
                {
                    int at = urlL.IndexOf(part, pos, StringComparison.Ordinal);
                    if (at < 0) return false;
                    pos = at + part.Length;
                }
                return !AnchorEnd || urlL.EndsWith(parts[^1]);
            }
            return AnchorEnd ? urlL.EndsWith(pat) : urlL.Contains(pat);
        }
    }
}
