using System.Text.Json;
using System.Text.Json.Serialization;

namespace TcgScanner;

public enum Game { Magic, Pokemon, YuGiOh, Cyberpunk, Gundam }

public static class GameInfo
{
    public static string Short(Game g) => g switch
    {
        Game.Magic => "Magic",
        Game.Pokemon => "Pokémon",
        Game.YuGiOh => "Yu-Gi-Oh!",
        Game.Cyberpunk => "Cyberpunk",
        _ => "Gundam"
    };
}

/// <summary>A card as returned by one of the card databases, normalized across games.</summary>
public class Card
{
    public Game Game { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TypeLine { get; set; } = "";
    /// <summary>Magic: Land/Creature/Instant... Pokémon: Pokemon/Trainer/Energy. Yu-Gi-Oh: Monster/Spell/Trap.</summary>
    public string Category { get; set; } = "";
    /// <summary>Pokémon: Basic/Stage1/Stage2, Supporter/Item..., Basic/Special energy. Yu-Gi-Oh: frame type.</summary>
    public string SubType { get; set; } = "";
    public double ManaValue { get; set; }
    public string ManaCost { get; set; } = "";
    /// <summary>Magic: color identity. Pokémon: types. Yu-Gi-Oh: attribute.</summary>
    public List<string> Colors { get; set; } = new();
    public List<string> ProducedMana { get; set; } = new();
    public List<string> AttackCostTypes { get; set; } = new();
    public string EvolvesFrom { get; set; } = "";
    public string Race { get; set; } = "";
    public int Hp { get; set; }
    /// <summary>Cyberpunk: unit power and the RAM the card needs (or a Legend provides).</summary>
    public int Power { get; set; }
    public int Ram { get; set; }
    /// <summary>Cyberpunk: the name without subtitle; Legends must have different base names.</summary>
    public string BaseName { get; set; } = "";
    /// <summary>Cyberpunk: the card has a Sell tag, so it can be sold for Eddies.</summary>
    public bool Sellable { get; set; }
    /// <summary>Gundam: Pilot names (or traits) that turn this Unit into a Link Unit.</summary>
    public List<string> LinkRefs { get; set; } = new();
    public int Level { get; set; }
    public bool UnlimitedCopies { get; set; }
    public bool IsLegendaryCreature { get; set; }
    public bool IsExtraDeck { get; set; }
    public bool HasAbility { get; set; }
    public string Rarity { get; set; } = "";
    public string SetName { get; set; } = "";
    public string Number { get; set; } = "";
    /// <summary>Lowercase format key to legal/banned/restricted/limited/semi-limited/not_legal.</summary>
    public Dictionary<string, string> Legality { get; set; } = new();
    /// <summary>0..1 rating used by the optimizer (popularity or a heuristic).</summary>
    public double Quality { get; set; }
    public string ImageUrl { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Market price in US dollars for one copy. 0 means no price is known.</summary>
    public double PriceUsd { get; set; }
    /// <summary>Set code of this printing (Magic set code, Pokémon set id, Yu-Gi-Oh set code).</summary>
    public string SetCode { get; set; } = "";
    /// <summary>True when the printing was confirmed from the card's set code or number, or picked by the user.</summary>
    public bool ExactPrinting { get; set; }
    public string PriceSource { get; set; } = "";
    public DateTime? PriceUpdated { get; set; }
}

public static class Money
{
    public static string Format(double usd) =>
        usd <= 0 ? "" : "$" + usd.ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Printing details read from the card (set code, collector number, set size).</summary>
public class PrintHint
{
    public string SetCode { get; set; }
    public string Number { get; set; }
    public string Total { get; set; }
    public string Rarity { get; set; }
    public bool IsEmpty => string.IsNullOrEmpty(SetCode) && string.IsNullOrEmpty(Number);
    public override string ToString() =>
        string.Join(" ", new[] { SetCode, Number + (string.IsNullOrEmpty(Total) ? "" : "/" + Total) }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public class LibraryEntry
{
    public Card Card { get; set; }
    public int Owned { get; set; }
    public int InDeck { get; set; }
    /// <summary>Loaner copies: cards you don't own but could use (borrowed, or thinking of buying). Not counted in collection value.</summary>
    public int Loaned { get; set; }
    /// <summary>Who lent it, or where and for how much you could buy it.</summary>
    public string LoanNote { get; set; } = "";
}

public class Library
{
    public Dictionary<string, LibraryEntry> Entries { get; set; } = new();

    [JsonIgnore] public IEnumerable<LibraryEntry> Deck => Entries.Values.Where(e => e.InDeck > 0);
    [JsonIgnore] public int DeckCount => Entries.Values.Sum(e => e.InDeck);

    /// <summary>Magic and Pokémon ids are already per printing; Yu-Gi-Oh ids are per card name.</summary>
    public static string KeyOf(Card c)
    {
        var key = string.IsNullOrEmpty(c.Id) ? c.Name : c.Id;
        return c.Game == Game.YuGiOh && c.ExactPrinting ? $"{key}|{c.Number}|{c.Rarity}" : key;
    }

    public LibraryEntry Add(Card c, int owned, int inDeck)
    {
        var key = KeyOf(c);
        if (!Entries.TryGetValue(key, out var e))
            Entries[key] = e = new LibraryEntry { Card = c };
        e.Owned += owned;
        e.InDeck += inDeck;
        return e;
    }

    public void Remove(LibraryEntry e)
    {
        var key = Entries.FirstOrDefault(kv => kv.Value == e).Key;
        if (key != null) Entries.Remove(key);
    }
}

public class AppData
{
    public Dictionary<Game, Library> Libraries { get; set; } = new();
    public Dictionary<Game, string> Formats { get; set; } = new();
    public Game LastGame { get; set; }
    /// <summary>Budget swaps suggest replacements for deck cards above this price (US dollars).</summary>
    public double BudgetLimit { get; set; } = 5;

    public Library Get(Game g)
    {
        if (!Libraries.TryGetValue(g, out var l)) Libraries[g] = l = new Library();
        return l;
    }
}

public static class Storage
{
    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TCG deck scanner");
    static string FilePath => Path.Combine(Dir, "Card library.json");

    public static AppData Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppData>(File.ReadAllText(FilePath)) ?? new AppData();
        }
        catch
        {
            try { File.Copy(FilePath, Path.Combine(Dir, "Card library unreadable copy.json"), true); } catch { }
        }
        return new AppData();
    }

    public static void Save(AppData data)
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, true);
    }
}
