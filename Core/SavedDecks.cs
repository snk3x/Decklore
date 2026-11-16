using System.Text.Json.Serialization;

namespace TcgScanner;

/// <summary>One card in a saved deck. The card data is kept so the deck still opens if the card was later removed from the collection.</summary>
public class SavedCard
{
    public Card Card { get; set; }
    public int Qty { get; set; }
}

/// <summary>A deck saved by name, with the format it was built for.</summary>
public class SavedDeck
{
    public string Name { get; set; } = "";
    public string Format { get; set; } = "";
    public DateTime Saved { get; set; }
    public List<SavedCard> Cards { get; set; } = new();

    [JsonIgnore] public int Count => Cards.Sum(c => c.Qty);
    [JsonIgnore] public double Value => Cards.Sum(c => c.Qty * c.Card.PriceUsd);
}

/// <summary>
/// Save, open, rename and delete decks. Opening a deck replaces what's on the Deck tab (take a LibrarySnapshot first
/// so Undo can bring the previous deck back). Owned and loaner counts are never changed.
/// </summary>
public static class DeckStore
{
    public static SavedDeck Find(Library lib, string name) =>
        lib.SavedDecks.FirstOrDefault(d => string.Equals(d.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Saves the current deck under this name, replacing a saved deck with the same name.</summary>
    public static SavedDeck Save(Library lib, string name, string format)
    {
        name = name.Trim();
        var deck = Find(lib, name);
        if (deck == null) lib.SavedDecks.Add(deck = new SavedDeck());
        deck.Name = name;
        deck.Format = format;
        deck.Saved = DateTime.Now;
        deck.Cards = lib.Deck.Select(e => new SavedCard { Card = e.Card, Qty = e.InDeck }).ToList();
        lib.OpenDeck = name;
        return deck;
    }

    /// <summary>Puts a saved deck on the Deck tab. Cards no longer in the collection are added back with 0 owned.</summary>
    public static void Open(Library lib, SavedDeck deck)
    {
        foreach (var e in lib.Entries.Values) e.InDeck = 0;
        foreach (var c in deck.Cards)
        {
            if (!lib.Entries.TryGetValue(Library.KeyOf(c.Card), out var entry)) entry = lib.Add(c.Card, 0, 0);
            entry.InDeck += c.Qty;
        }
        lib.OpenDeck = deck.Name;
    }

    /// <summary>True when the deck on the Deck tab differs from its saved copy (or has cards and was never saved).</summary>
    public static bool HasUnsavedChanges(Library lib)
    {
        var saved = Find(lib, lib.OpenDeck);
        if (saved == null) return lib.DeckCount > 0;
        var now = lib.Deck.ToDictionary(e => Library.KeyOf(e.Card), e => e.InDeck);
        var then = saved.Cards.GroupBy(c => Library.KeyOf(c.Card)).ToDictionary(g => g.Key, g => g.Sum(c => c.Qty));
        return now.Count != then.Count || now.Any(kv => then.GetValueOrDefault(kv.Key) != kv.Value);
    }

    /// <summary>Returns an error message, or null when renamed.</summary>
    public static string Rename(Library lib, SavedDeck deck, string newName)
    {
        newName = newName?.Trim() ?? "";
        if (newName.Length == 0) return "Type a name for the deck.";
        var other = Find(lib, newName);
        if (other != null && other != deck) return $"You already have a deck called {other.Name}.";
        if (string.Equals(lib.OpenDeck, deck.Name, StringComparison.OrdinalIgnoreCase)) lib.OpenDeck = newName;
        deck.Name = newName;
        return null;
    }

    public static void Delete(Library lib, SavedDeck deck)
    {
        lib.SavedDecks.Remove(deck);
        if (string.Equals(lib.OpenDeck, deck.Name, StringComparison.OrdinalIgnoreCase)) lib.OpenDeck = "";
    }

    /// <summary>"Burn (unsaved changes)", "New deck (not saved)", or the deck name.</summary>
    public static string Title(Library lib)
    {
        bool changed = HasUnsavedChanges(lib);
        if (Find(lib, lib.OpenDeck) == null) return lib.DeckCount == 0 ? "New deck" : "New deck (not saved)";
        return lib.OpenDeck + (changed ? " (unsaved changes)" : "");
    }

    /// <summary>A name for a new deck that isn't taken yet: "Modern deck", "Modern deck 2", ...</summary>
    public static string SuggestName(Library lib, string format)
    {
        string baseName = $"{format} deck";
        if (Find(lib, baseName) == null) return baseName;
        for (int i = 2; ; i++) if (Find(lib, $"{baseName} {i}") == null) return $"{baseName} {i}";
    }
}
