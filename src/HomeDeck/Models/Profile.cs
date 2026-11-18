namespace HomeDeck.Models;

/// <summary>A launch profile: one card on the launcher, one page in the shell.</summary>
public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled";
    public string Url { get; set; } = "https://perchance.org/";
    public string ColorHex { get; set; } = "#B3402E";

    /// <summary>Per-profile ad &amp; tracker blocking (Brave-style lists).</summary>
    public bool AdBlock { get; set; } = true;

    /// <summary>Avatar source: "default" (person glyph on color),
    /// "gradient" (preset index in <see cref="AvatarValue"/>), or
    /// "file" (image filename under AppData/avatars/). Unknown = default.</summary>
    public string AvatarKind { get; set; } = "default";
    public string AvatarValue { get; set; } = "";

    public string PageId
    {
        get
        {
            try
            {
                var u = new Uri(Url);
                var seg = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                return seg.Length > 0 ? seg[^1] : u.Host;
            }
            catch { return Url; }
        }
    }
}

/// <summary>App-level prefs: ask-every-time + last-opened profile.</summary>
public sealed class ShellPrefs
{
    public bool AskEveryTime { get; set; } = true;
    public string? LastProfileId { get; set; }
}
