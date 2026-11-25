using System.Text.Json;

namespace MultiAudio.App;

/// <summary>One saved route inside a profile. Apps are keyed by name (PIDs change
/// between runs); devices by id with a name fallback.</summary>
public sealed class RouteConfig
{
    public string AppName { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public int VolumePct { get; set; } = 100;
    public int DelayMs { get; set; } = 0;
}

public sealed class Profile
{
    public string Name { get; set; } = "";
    public List<RouteConfig> Routes { get; set; } = new();
}

/// <summary>Loads/saves named routing profiles to a JSON file under %AppData%.</summary>
public static class ProfileStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiAudio"); // legacy folder name: renaming it would orphan saved profiles
    private static readonly string FilePath = Path.Combine(Dir, "profiles.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static List<Profile> LoadAll()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<Profile>>(json) ?? new();
        }
        catch { return new(); }
    }

    public static void SaveAll(List<Profile> profiles)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(profiles, Options));
    }

    /// <summary>Insert or replace a profile by name (case-insensitive), then persist.</summary>
    public static List<Profile> Upsert(Profile profile)
    {
        var all = LoadAll();
        all.RemoveAll(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
        all.Add(profile);
        SaveAll(all);
        return all;
    }

    public static List<Profile> Delete(string name)
    {
        var all = LoadAll();
        all.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        SaveAll(all);
        return all;
    }

    public static string PathOnDisk => FilePath;
}
