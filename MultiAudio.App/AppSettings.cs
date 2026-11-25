using System.Text.Json;

namespace MultiAudio.App;

/// <summary>Small per-user settings, stored next to the saved profiles (%AppData%\MultiAudio\settings.json).</summary>
public static class AppSettings
{
    private sealed class Data
    {
        public string? DefaultOutputId { get; set; }
        public string? DefaultOutputName { get; set; } // shown if that device isn't connected
    }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiAudio", "settings.json");

    private static readonly Data Current = Load();

    /// <summary>Where newly started apps are sent. null = automatic (the PC's Realtek speakers);
    /// "" = off (leave them on Windows' default); otherwise an MMDevice id.</summary>
    public static string? DefaultOutputId => Current.DefaultOutputId;
    public static string? DefaultOutputName => Current.DefaultOutputName;

    /// <summary>Returns false if the choice couldn't be saved (it still applies until Audix closes).</summary>
    public static bool SetDefaultOutput(string? id, string? name)
    {
        Current.DefaultOutputId = id;
        Current.DefaultOutputName = name;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current));
            return true;
        }
        catch { return false; }
    }

    private static Data Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); } // unreadable file: fall back to automatic
    }
}
