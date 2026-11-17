using System.Text.Json;
using HomeDeck.Models;
using Microsoft.Maui.Storage;

namespace HomeDeck.Services;

/// <summary>
/// Profiles + prefs stored in MAUI Preferences as JSON.
/// First run seeds from the bundled profiles.default.json.
/// </summary>
public sealed class ProfileStore
{
    const string ProfilesKey = "hd.profiles";
    const string PrefsKey = "hd.prefs";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    List<Profile> _profiles = new();
    ShellPrefs _prefs = new();

    public IReadOnlyList<Profile> Profiles => _profiles;
    public ShellPrefs Prefs => _prefs;

    public async Task LoadAsync()
    {
        try
        {
            var raw = Preferences.Get(ProfilesKey, null);
            if (!string.IsNullOrEmpty(raw))
            {
                _profiles = JsonSerializer.Deserialize<List<Profile>>(raw, Json) ?? new();
            }
            else
            {
                _profiles = await LoadSeedAsync();
                SaveProfiles();
            }

            var praw = Preferences.Get(PrefsKey, null);
            if (!string.IsNullOrEmpty(praw))
                _prefs = JsonSerializer.Deserialize<ShellPrefs>(praw, Json) ?? new();
        }
        catch
        {
            _profiles = new();
            _prefs = new();
        }
    }

    async Task<List<Profile>> LoadSeedAsync()
    {
        try
        {
            using var s = await FileSystem.OpenAppPackageFileAsync("profiles.default.json");
            using var r = new StreamReader(s);
            var list = JsonSerializer.Deserialize<List<Profile>>(await r.ReadToEndAsync(), Json);
            if (list is { Count: > 0 }) return list;
        }
        catch { /* fall through to hardcoded seed */ }
        return new() { new Profile { Name = "Home", Url = "https://perchance.org/home-deck", ColorHex = "#B3402E" } };
    }

    public void SaveProfiles()
        => Preferences.Set(ProfilesKey, JsonSerializer.Serialize(_profiles, Json));

    public void SavePrefs()
        => Preferences.Set(PrefsKey, JsonSerializer.Serialize(_prefs, Json));

    public Profile Add(string name, string url, bool adBlock)
    {
        var p = new Profile { Name = name, Url = url, AdBlock = adBlock };
        _profiles.Add(p);
        SaveProfiles();
        return p;
    }

    public void Remove(Profile p)
    {
        _profiles.Remove(p);
        SaveProfiles();
    }
}
