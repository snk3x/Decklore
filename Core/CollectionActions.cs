using System.Text;
using System.Text.RegularExpressions;

namespace TcgScanner;

/// <summary>Plain text decklists: "4 Lightning Bolt", "3 Arven SVI 166", "3x Ash Blossom & Joyous Spring".</summary>
public static class DeckList
{
    static readonly string[] Headers = { "deck", "main", "main deck", "sideboard", "side deck", "extra deck", "commander", "companion", "maybeboard" };

    public static List<(int qty, string name, string number)> Parse(string text)
    {
        var list = new List<(int, string, string)>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#") || line.StartsWith("!")) continue;
            if (Headers.Contains(line.TrimEnd(':').ToLowerInvariant())) continue;
            if (Regex.IsMatch(line, @"^[\p{L} ]+:\s*\d*$")) continue; // "Pokémon: 12", "Trainer: 30"
            if (Regex.IsMatch(line, @"^\d+$")) continue;              // card ids from .ydk files

            int qty = 1;
            string name = line, number = null;
            var m = Regex.Match(line, @"^(\d+)\s*x?\s+(.+)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                qty = int.Parse(m.Groups[1].Value);
                name = m.Groups[2].Value;
            }
            name = Regex.Replace(name, @"\s*\([^)]*\)\s*\S*\s*$", "");                                    // Magic: "Name (SET) 123"
            var pm = Regex.Match(name, @"^(.*?)\s+[A-Z][A-Z0-9]{1,4}(?:-[A-Z]{1,3})?\s+(\d{1,3}[a-z]?)$"); // Pokémon: "Name SET 123"
            if (pm.Success)
            {
                name = pm.Groups[1].Value;
                number = pm.Groups[2].Value;
            }
            name = name.Replace("*", "").Trim();
            if (name.Length >= 2 && qty > 0) list.Add((qty, name, number));
        }
        return list;
    }

    public static string Format(Library lib)
    {
        var sb = new StringBuilder();
        string lastGroup = null;
        foreach (var e in lib.Deck.OrderBy(e => e.Card.IsExtraDeck).ThenBy(e => e.Card.Category).ThenBy(e => e.Card.Name))
        {
            string group = e.Card.IsExtraDeck ? "Extra Deck" : e.Card.Category;
            if (group != lastGroup)
            {
                if (lastGroup != null) sb.AppendLine();
                sb.AppendLine($"// {group}");
                lastGroup = group;
            }
            sb.AppendLine($"{e.InDeck} {e.Card.Name}");
        }
        return sb.ToString();
    }

    /// <summary>Collection as CSV with values, for spreadsheets.</summary>
    public static string Csv(Library lib)
    {
        static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        static string N(double v) => v > 0 ? v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "";
        var sb = new StringBuilder("Name,Set,Number,Type,Owned,In deck,Price each (USD),Value owned (USD),Price source,Price date\r\n");
        foreach (var e in lib.Entries.Values.Where(e => e.Owned > 0 || e.InDeck > 0).OrderBy(e => e.Card.Name))
        {
            var c = e.Card;
            sb.Append(string.Join(",", Q(c.Name), Q(c.SetName), Q(c.Number), Q(c.TypeLine), e.Owned, e.InDeck,
                                  N(c.PriceUsd), N(c.PriceUsd * e.Owned), Q(c.PriceSource), Q(c.PriceUpdated?.ToString("d") ?? "")));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }
}

/// <summary>
/// A copy of a collection's counts taken before a deck action (Optimize, Starter deck, Import, Clear).
/// Restoring it puts back both "owned" and "in deck" counts and removes any cards the action added,
/// so a starter deck added by mistake disappears from the collection too.
/// </summary>
public sealed class LibrarySnapshot
{
    readonly Library _library;
    readonly Dictionary<LibraryEntry, (int owned, int inDeck)> _counts;

    public LibrarySnapshot(Library library)
    {
        _library = library;
        _counts = library.Entries.Values.ToDictionary(e => e, e => (e.Owned, e.InDeck));
    }

    /// <summary>True when this snapshot belongs to that collection (each game has its own).</summary>
    public bool IsFor(Library library) => ReferenceEquals(_library, library);

    /// <summary>Puts the collection back exactly as it was. Returns how many added cards were removed.</summary>
    public int Restore()
    {
        int removed = 0;
        foreach (var e in _library.Entries.Values.ToList())
        {
            if (_counts.TryGetValue(e, out var c)) { e.Owned = c.owned; e.InDeck = c.inDeck; }
            else { _library.Remove(e); removed++; }
        }
        return removed;
    }
}

public static class CollectionActions
{
    /// <summary>Swaps a collection entry to another printing, keeping its counts. Returns the entry now holding them.</summary>
    public static LibraryEntry ChangePrinting(Library lib, LibraryEntry entry, Card printing)
    {
        printing.ExactPrinting = true;
        int owned = entry.Owned, inDeck = entry.InDeck;
        lib.Remove(entry);
        return lib.Add(printing, owned, inDeck);
    }

    /// <summary>Builds a Gundam starter deck (50 cards plus 10 Resources). Returns a short summary.</summary>
    public static async Task<string> BuildStarterAsync(Library lib, StarterDeck deck, bool addToCollection, bool replaceDeck,
                                                       CancellationToken ct = default)
    {
        var catalogue = await GundamSource.CatalogueAsync(false, ct);
        if (replaceDeck) foreach (var e in lib.Entries.Values) e.InDeck = 0;

        var missing = new List<string>();
        foreach (var (number, qty) in deck.Cards)
        {
            // Starter decks contain the regular printing, so prefer the base card over alternate arts.
            var card = catalogue.Where(c => c.Number == number).OrderBy(c => c.Id.Contains("_p")).FirstOrDefault();
            if (card == null) { missing.Add(number); continue; }
            var entry = lib.Entries.Values.FirstOrDefault(e => e.Card.Id == card.Id) ?? lib.Add(card, 0, 0);
            if (addToCollection) entry.Owned += qty;
            entry.InDeck += qty;
        }

        // Every starter box includes 10 Resource cards for the Resource Deck.
        var resource = catalogue.Where(c => c.Category == "Resource").OrderBy(c => c.Id.Contains("_p")).ThenBy(c => c.Id).FirstOrDefault();
        if (resource != null)
        {
            var entry = lib.Entries.Values.FirstOrDefault(e => e.Card.Id == resource.Id) ?? lib.Add(resource, 0, 0);
            if (addToCollection) entry.Owned += 10;
            if (replaceDeck || lib.Deck.Where(e => e.Card.Category == "Resource").Sum(e => e.InDeck) == 0) entry.InDeck = 10;
        }

        return $"Built {deck.Code} {deck.Name}: {deck.Total} cards plus a 10-card Resource Deck." +
               (addToCollection ? " Its cards were added to your collection." : "") +
               (missing.Count > 0 ? $"\nNot found in the card database: {string.Join(", ", missing)}." : "") +
               (deck.Note != "" ? $"\nNote: {deck.Note}" : "") +
               "\n\nTip: scan your booster pack cards, then press Optimize to upgrade this deck with them.";
    }
}
