using System.Text.Json;

namespace TcgScanner;

/// <summary>
/// A backup file: every game's collection, loaners, saved decks and chosen formats, in one JSON file the user can keep
/// anywhere (Google Drive, OneDrive, a USB stick) and restore on any device, Windows or Android. The JustTCG key is
/// not included (it's encrypted for each device), and neither are price or card caches (they rebuild themselves).
/// </summary>
public static class Backup
{
    /// <summary>Default name for a new backup file.</summary>
    public const string FileName = "Decklore backup.json";
    const string AppName = "Decklore";
    const int CurrentVersion = 1;

    public class BackupFile
    {
        public string App { get; set; } = AppName;
        public int Version { get; set; } = CurrentVersion;
        public DateTime Created { get; set; }
        public AppData Data { get; set; }
    }

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Create(AppData data) =>
        JsonSerializer.Serialize(new BackupFile { Created = DateTime.Now, Data = data }, Options);

    public static void Write(Stream stream, AppData data)
    {
        using var w = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
        w.Write(Create(data));
    }

    /// <summary>
    /// Reads a backup file. Also accepts the app's own "Card library.json", so a copy of that file works too.
    /// Throws InvalidDataException with a plain message when the file isn't a Decklore backup.
    /// </summary>
    public static (AppData data, DateTime? created) Read(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw new InvalidDataException("That file isn't a Decklore backup (it couldn't be read)."); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("That file isn't a Decklore backup.");
            try
            {
                if (root.TryGetProperty("App", out var app) && app.ValueKind == JsonValueKind.String && root.TryGetProperty("Data", out _))
                {
                    if (app.GetString() != AppName) throw new InvalidDataException("That file isn't a Decklore backup.");
                    var file = JsonSerializer.Deserialize<BackupFile>(json);
                    if (file.Version > CurrentVersion)
                        throw new InvalidDataException("That backup was made by a newer version of Decklore. Update the app, then restore it.");
                    return (Clean(file.Data), file.Created);
                }
                if (root.TryGetProperty("Libraries", out _))
                    return (Clean(JsonSerializer.Deserialize<AppData>(json)), null);
            }
            catch (JsonException) { }
            throw new InvalidDataException("That file isn't a Decklore backup.");
        }
    }

    /// <summary>Fills in anything missing, so a hand-edited or partial file can't crash the app.</summary>
    static AppData Clean(AppData d)
    {
        d ??= new AppData();
        d.Libraries ??= new();
        d.Formats ??= new();
        foreach (var lib in d.Libraries.Values.Where(l => l != null))
        {
            lib.Entries ??= new();
            foreach (var key in lib.Entries.Where(kv => kv.Value?.Card == null).Select(kv => kv.Key).ToList()) lib.Entries.Remove(key);
            lib.SavedDecks ??= new();
            lib.SavedDecks.RemoveAll(s => s == null);
            foreach (var s in lib.SavedDecks) { s.Cards ??= new(); s.Cards.RemoveAll(c => c?.Card == null); }
            lib.OpenDeck ??= "";
        }
        foreach (var g in d.Libraries.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) d.Libraries.Remove(g);
        return d;
    }

    /// <summary>"Magic: 120 cards, 3 decks" per game, for the confirmation before restoring.</summary>
    public static string Summary(AppData d)
    {
        var lines = Enum.GetValues<Game>()
            .Where(g => d.Libraries.TryGetValue(g, out var l) && (l.Entries.Count > 0 || l.SavedDecks.Count > 0))
            .Select(g =>
            {
                var l = d.Libraries[g];
                int owned = l.Entries.Values.Sum(e => e.Owned), loaned = l.Entries.Values.Sum(e => e.Loaned);
                return $"{GameInfo.Short(g)}: {owned} card{(owned == 1 ? "" : "s")} owned" +
                       (loaned > 0 ? $", {loaned} loaner{(loaned == 1 ? "" : "s")}" : "") +
                       $", {l.SavedDecks.Count} saved deck{(l.SavedDecks.Count == 1 ? "" : "s")}";
            }).ToList();
        return lines.Count == 0 ? "No cards or decks." : string.Join("\n", lines);
    }

    /// <summary>
    /// Replaces the app's data with a restored backup. The current data is saved first as "Before restore.json" in the
    /// app's folder, so a mistaken restore can be undone by restoring that file.
    /// </summary>
    public static string Restore(AppData current, AppData restored)
    {
        string safety = Path.Combine(AppFolder.Dir, "Before restore.json");
        try
        {
            Directory.CreateDirectory(AppFolder.Dir);
            File.WriteAllText(safety, Create(current));
        }
        catch { safety = null; }
        current.Libraries = restored.Libraries;
        current.Formats = restored.Formats;
        current.LastGame = restored.LastGame;
        Storage.Save(current);
        return safety;
    }
}
