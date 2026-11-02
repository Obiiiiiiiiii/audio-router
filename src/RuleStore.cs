using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioRouter;

/// <summary>What the user wants for one app, keyed by exe file name (e.g. "spotify.exe").</summary>
sealed class AppRule
{
    public string DisplayName { get; set; } = "";

    /// <summary>Saved volume, 0..1. Null means "don't touch the volume".</summary>
    public float? Volume { get; set; }

    public bool? Muted { get; set; }

    /// <summary>True once the user has picked an output for this app. Until then we leave routing alone.</summary>
    public bool ManageOutput { get; set; }

    /// <summary>Preferred output device (MMDevice id). Null means "system default".</summary>
    public string? OutputDeviceId { get; set; }

    /// <summary>Where to go when the preferred output is unavailable (e.g. Bluetooth earbuds off). Null means "system default".</summary>
    public string? BackupDeviceId { get; set; }
}

sealed class RuleData
{
    public Dictionary<string, AppRule> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every output device we've ever seen, so disconnected ones can still be picked and named.</summary>
    public Dictionary<string, string> KnownDevices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set once we've applied the first-run "start with Windows" default.</summary>
    public bool StartupConfigured { get; set; }
}

/// <summary>Loads and saves rules.json. Writes go to a temp file first so a crash can't corrupt the rules.</summary>
sealed class RuleStore
{
    public static string Folder { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioRouter");

    static string FilePath => System.IO.Path.Combine(Folder, "rules.json");

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public RuleData Data { get; private set; } = new();

    readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 400 };

    public RuleStore()
    {
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
    }

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<RuleData>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded != null)
                {
                    // Re-wrap so lookups stay case-insensitive after deserialisation.
                    Data = new RuleData
                    {
                        Apps = new(loaded.Apps ?? new(), StringComparer.OrdinalIgnoreCase),
                        KnownDevices = new(loaded.KnownDevices ?? new(), StringComparer.OrdinalIgnoreCase),
                        StartupConfigured = loaded.StartupConfigured,
                    };
                }
            }
            Log.Info($"Loaded {Data.Apps.Count} app rules");
        }
        catch (Exception ex)
        {
            Log.Error("Could not read rules.json, keeping a backup and starting fresh", ex);
            try { File.Copy(FilePath, FilePath + ".broken", overwrite: true); } catch { }
            Data = new RuleData();
        }
    }

    public AppRule? Get(string appKey) => Data.Apps.TryGetValue(appKey, out var r) ? r : null;

    public AppRule GetOrCreate(string appKey, string displayName)
    {
        if (!Data.Apps.TryGetValue(appKey, out var rule))
        {
            rule = new AppRule { DisplayName = displayName };
            Data.Apps[appKey] = rule;
        }
        else if (!string.IsNullOrEmpty(displayName))
        {
            rule.DisplayName = displayName;
        }
        return rule;
    }

    public void Forget(string appKey)
    {
        Data.Apps.Remove(appKey);
        SaveSoon();
    }

    /// <summary>Debounced save, so dragging a slider doesn't hammer the disk.</summary>
    public void SaveSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Saving rules failed", ex);
        }
    }
}
