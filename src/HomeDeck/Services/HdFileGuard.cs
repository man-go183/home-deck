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
    const string RootKey = "hd.root";
    const string GrantsKey = "hd.grants";

    static readonly System.Text.Json.JsonSerializerOptions Json = new() { WriteIndented = false };

    static string DefaultRoot()
    {
        try
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(docs))
                return Path.Combine(docs, "HomeDeck");
        }
        catch { }
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

    static Dictionary<string, string> LoadGrants()
    {
        try
        {
            var raw = Preferences.Get(GrantsKey, null);
            if (!string.IsNullOrEmpty(raw))
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(raw, Json) ?? new();
        }
        catch { }
        return new();
    }

    /// <summary>Native allow/deny for a site. Runs on the UI thread, outside
    /// page content so a site can't fake it. Empty host = always ask, never
    /// persisted. Returns true for "always" + "once". Never throws.</summary>
    public static Task<bool> EnsureAccessAsync(string? host, string op)
    {
        host = (host ?? "").Trim().ToLowerInvariant();
        try
        {
            if (host.Length == 0) return PromptAsync(host, op);
            var g = LoadGrants();
            if (g.TryGetValue(host, out var v) && v == "always") return Task.FromResult(true);
            return PromptAsync(host, op);
        }
        catch { return Task.FromResult(false); }
    }

    static async Task<bool> PromptAsync(string host, string op)
    {
        try
        {
            var page = Application.Current?.MainPage;
            if (page == null) return false;
            var who = string.IsNullOrEmpty(host) ? "This page" : host;
            var choice = await MainThread.InvokeOnMainThreadAsync(() =>
                page.DisplayActionSheet($"{who} wants to {op} in your HomeDeck folder.",
                    "Deny", null, "Always allow", "Just once"));
            if (choice == "Always allow" && host.Length > 0)
            {
                try
                {
                    var g = LoadGrants();
                    g[host] = "always";
                    Preferences.Set(GrantsKey, System.Text.Json.JsonSerializer.Serialize(g, Json));
                }
                catch { }
                return true;
            }
            return choice == "Just once";
        }
        catch { return false; }
    }
}
