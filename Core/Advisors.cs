using System.Text.RegularExpressions;

namespace TcgScanner;

/// <summary>One cheaper option for a deck card.</summary>
public record SwapOption(Card Card, string Kind, double Price, string Why);

/// <summary>A deck card above the budget, with cheaper options for it.</summary>
public record BudgetSwap(LibraryEntry Entry, int Copies, List<SwapOption> Options)
{
    public Card Current => Entry.Card;
    public double BestSaving => Options.Count == 0 ? 0 : (Current.PriceUsd - Options.Min(o => o.Price)) * Copies;
}

/// <summary>A loaner card that would improve the deck, and the owned cards it would push out.</summary>
public record LoanerPick(LibraryEntry Entry, int Copies, double Price);

public class LoanerReport
{
    public List<LoanerPick> Picks { get; } = new();
    public List<(LibraryEntry entry, int copies)> Replaced { get; } = new();
    public int HealthOwnedOnly { get; set; }
    public int HealthWithLoaners { get; set; }
    public double CostToBuy => Picks.Sum(p => p.Price * p.Copies);
    /// <summary>The deck that uses the recommended loaners (entry -> copies), for "Use this deck".</summary>
    public Dictionary<LibraryEntry, int> DeckWithLoaners { get; } = new();
}

/// <summary>
/// Budget swaps: for deck cards above a price limit, suggest a cheaper printing of the same card (plays exactly the same),
/// and for Magic, Gundam and Cyberpunk, cheaper cards that do a similar job.
/// </summary>
public static class BudgetAdvisor
{
    public static async Task<List<BudgetSwap>> SuggestAsync(Game game, Library lib, string format, DeckRules rules,
        ICardSource source, double maxPrice, Action<string> progress = null, CancellationToken ct = default)
    {
        // Cyberpunk Legends set the deck's RAM and colors, so they are never swapped.
        var expensive = lib.Deck.Where(e => e.Card.PriceUsd > maxPrice && e.Card.Category is not ("Resource" or "Token" or "Legend"))
                                .OrderByDescending(e => e.Card.PriceUsd)
                                // Gundam prices cost one JustTCG request per card number (free plan: 100 a day), so check fewer cards there.
                                .Take(game == Game.Gundam ? 8 : 12).ToList();
        var deckColors = lib.Deck.SelectMany(e => e.Card.Colors).Distinct().ToHashSet();
        var result = new List<BudgetSwap>();

        foreach (var e in expensive)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(e.Card.Name);
            var options = new List<SwapOption>();

            // 1. The same card, cheaper printing: identical in play, so always the safest swap.
            try
            {
                var prints = await source.PrintingsAsync(e.Card, ct);
                if (game == Game.Pokemon)
                {
                    // Pokémon printings come without prices; look up the newest few.
                    var priced = new List<Card>();
                    foreach (var p in prints.Where(p => p.Id != e.Card.Id).Take(8))
                        if (await source.RefreshAsync(p, ct) is { } full) priced.Add(full);
                    prints = priced;
                }
                var cheaper = prints.Where(p => p.PriceUsd > 0 && p.PriceUsd < e.Card.PriceUsd && p.PriceUsd <= maxPrice &&
                                                rules.LegalityLabel(p, format) is not ("Not legal" or "Banned" or "Forbidden"))
                                    .OrderBy(p => p.PriceUsd).FirstOrDefault();
                if (cheaper != null)
                    options.Add(new SwapOption(cheaper, "Cheaper printing", cheaper.PriceUsd,
                        $"Same card, {cheaper.SetName} {cheaper.Number}".Trim()));
            }
            catch (RateLimitedException) { throw; }
            catch { /* printings are a bonus; keep going */ }

            // 2. A different card that does a similar job.
            try
            {
                var similar = game switch
                {
                    Game.Magic => await MagicAlternativesAsync((ScryfallSource)source, e.Card, format, maxPrice, ct),
                    Game.Gundam => await CatalogueAlternativesAsync(await GundamSource.CatalogueAsync(false, ct), e.Card, deckColors, maxPrice, ct),
                    Game.Cyberpunk => await CatalogueAlternativesAsync(await CyberpunkSource.CatalogueAsync(ct), e.Card, deckColors, maxPrice, ct),
                    _ => new List<SwapOption>(), // Pokémon and Yu-Gi-Oh: card effects are too specific to swap safely
                };
                options.AddRange(similar.Where(s => rules.LegalityLabel(s.Card, format) is not ("Not legal" or "Banned" or "Forbidden")));
            }
            catch (RateLimitedException) { throw; }
            catch { }

            if (options.Count > 0) result.Add(new BudgetSwap(e, e.InDeck, options.Take(4).ToList()));
        }
        return result;
    }

    // What a Magic card does, as a Scryfall search term, from common rules-text phrases.
    static readonly (string pattern, string query, string role)[] MagicRoles =
    {
        (@"counter target", "o:\"counter target\"", "counters spells"),
        (@"destroy target (creature|nonland permanent|permanent)", "o:\"destroy target\"", "removal"),
        (@"exile target (creature|nonland permanent|permanent)", "o:\"exile target\"", "removal"),
        (@"deals? \S+ damage to (any target|target creature)", "o:\"damage to\" o:target", "damage"),
        (@"search your library for (a|up to \w+) basic land", "o:\"search your library for\" o:land", "ramp"),
        (@"add \{", "o:\"add {\"", "mana"),
        (@"search your library for a card", "o:\"search your library for a card\"", "tutor"),
        (@"draws? (a card|\w+ cards)", "o:draw", "card draw"),
        (@"return target .* to its owner's hand", "o:\"to its owner's hand\"", "bounce"),
        (@"create .* token", "o:create o:token", "makes tokens"),
        (@"discards?", "o:discard", "discard"),
    };

    static async Task<List<SwapOption>> MagicAlternativesAsync(ScryfallSource scryfall, Card card, string format, double maxPrice, CancellationToken ct)
    {
        string fmt = format.ToLowerInvariant();
        string price = maxPrice.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        string colors = card.Colors.Count == 0 ? "c" : string.Join("", card.Colors);
        // Two-faced cards: the front face decides what the card is (Fable is an enchantment, not its creature back side).
        string frontType = card.TypeLine.Split(" // ")[0];
        string type = new[] { "Land", "Creature", "Planeswalker", "Battle", "Instant", "Sorcery", "Artifact", "Enchantment" }
            .FirstOrDefault(t => frontType.Contains(t)) ?? card.Category;

        if (type == "Land")
        {
            // Lands are about the mana they make: a red/white dual should be replaced by another red/white land.
            var makes = card.ProducedMana.Where(m => "WUBRG".Contains(m)).ToList();
            if (makes.Count == 0) return new List<SwapOption>();
            var lands = await scryfall.SearchAsync(
                $"f:{fmt} t:land -t:basic id<={colors} produces={string.Join("", makes).ToLowerInvariant()} usd<={price} usd>0 -!\"{card.Name}\"", 3, ct);
            string colorsText = string.Join("/", makes.Select(m => m switch { "W" => "white", "U" => "blue", "B" => "black", "R" => "red", _ => "green" }));
            return lands.Select(c => new SwapOption(c, "Similar card", c.PriceUsd, $"Also makes {colorsText} mana")).ToList();
        }

        string text = card.Text.ToLowerInvariant().Split("//")[0];
        var role = MagicRoles.FirstOrDefault(r => Regex.IsMatch(text, r.pattern));
        var q = $"f:{fmt} id<={colors} t:{type.ToLowerInvariant()} usd<={price} usd>0 -!\"{card.Name}\" mv<={card.ManaValue + 1:0}";
        if (role.query != null) q += " " + role.query;
        var found = await scryfall.SearchAsync(q, 3, ct);
        string why = role.role != null ? $"Also {role.role}, {type.ToLowerInvariant()}" : $"Similar {type.ToLowerInvariant()}";
        return found.Select(c => new SwapOption(c, "Similar card", c.PriceUsd, $"{why}, mana value {c.ManaValue:0}")).ToList();
    }

    static readonly string[] Keywords = { "Blocker", "Repair", "Breach", "First Strike", "High-Maneuver", "Support", "Suppression",
                                          "Burst", "Deploy", "Go Solo", "Blocker", "Quick", "Call" };

    /// <summary>Gundam and Cyberpunk: pick close matches from the full card list (same type and color, similar cost and stats).</summary>
    static async Task<List<SwapOption>> CatalogueAlternativesAsync(List<Card> catalogue, Card card, HashSet<string> deckColors,
                                                                    double maxPrice, CancellationToken ct)
    {
        var traits = card.Race.Split(", ", StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var cardKeywords = Keywords.Where(k => card.Text.Contains(k, StringComparison.OrdinalIgnoreCase)).ToHashSet();
        var candidates = catalogue
            .Where(c => c.Category == card.Category && c.Number != card.Number && !c.Id.Contains("_p") &&
                        c.Colors.All(col => deckColors.Count == 0 || deckColors.Contains(col)) &&
                        Math.Abs(c.Level - card.Level) <= 1 && Math.Abs(c.ManaValue - card.ManaValue) <= 1 &&
                        (card.Game != Game.Cyberpunk || c.Ram <= card.Ram)) // keep the deck within its RAM
            .Select(c => (card: c, score:
                1 - Math.Min(1, Math.Abs(c.Power - card.Power) / 6.0) * 0.5
                  - Math.Min(1, Math.Abs(c.Hp - card.Hp) / 6.0) * 0.5
                  + 0.25 * c.Race.Split(", ", StringSplitOptions.RemoveEmptyEntries).Count(traits.Contains)
                  + 0.2 * Keywords.Count(k => cardKeywords.Contains(k) && c.Text.Contains(k, StringComparison.OrdinalIgnoreCase))
                  + 0.3 * c.Quality))
            .OrderByDescending(x => x.score)
            .Take(3)
            .Select(x => JsonClone(x.card))
            .ToList();

        // Prices for these games come from JustTCG (needs the user's key); price only the closest few.
        foreach (var c in candidates) await JustTcg.ApplyAsync(c, ct);
        // Prefer options with a known price under the limit; show unknown prices only when nothing priced fits.
        var priced = candidates.Where(c => c.PriceUsd > 0 && c.PriceUsd <= maxPrice && c.PriceUsd < card.PriceUsd).ToList();
        return (priced.Count > 0 ? priced : candidates.Where(c => c.PriceUsd <= 0).ToList())
            .Take(3)
            .Select(c => new SwapOption(c, "Similar card", c.PriceUsd,
                $"Same type and color, cost {c.ManaValue:0}" + (c.PriceUsd <= 0 ? " (price unknown)" : "")))
            .ToList();
    }

    static Card JsonClone(Card c) => System.Text.Json.JsonSerializer.Deserialize<Card>(System.Text.Json.JsonSerializer.Serialize(c));

    /// <summary>
    /// Puts a budget option into the deck in place of the current card. A card you don't own is added as a loaner,
    /// so the loaner list doubles as a shopping list.
    /// </summary>
    public static LibraryEntry ApplySwap(Library lib, BudgetSwap swap, SwapOption option)
    {
        int copies = Math.Min(swap.Copies, swap.Entry.InDeck);
        swap.Entry.InDeck -= copies;
        var target = lib.Add(option.Card, 0, 0);
        target.InDeck += copies;
        // Copies in the deck that you don't own are loaners (to borrow or buy).
        int needed = Math.Max(0, target.InDeck - target.Owned);
        if (target.Loaned < needed)
        {
            target.Loaned = needed;
            if (string.IsNullOrEmpty(target.LoanNote))
                target.LoanNote = option.Price > 0 ? $"Budget swap for {swap.Current.Name}, about {Money.Format(option.Price)}" : $"Budget swap for {swap.Current.Name}";
        }
        return target;
    }
}

/// <summary>
/// Loaner picks: builds the best deck from owned cards, then again with loaners available, and reports which loaners
/// would make it into the deck, what they would push out, and the deck health either way. Nothing is changed.
/// </summary>
public static class LoanerAdvisor
{
    public static async Task<LoanerReport> RecommendAsync(Library lib, string format, DeckRules rules, CancellationToken ct = default)
    {
        var report = new LoanerReport();
        var original = new LibrarySnapshot(lib);
        try
        {
            // Pure loaners already sitting in the deck shouldn't count as owned for the owned-only build.
            foreach (var e in lib.Entries.Values.Where(e => e.Owned == 0 && e.Loaned > 0)) e.InDeck = 0;
            await rules.OptimizeAsync(lib, format, ct);
            var ownedOnly = lib.Entries.Values.ToDictionary(e => e, e => e.InDeck);
            report.HealthOwnedOnly = rules.Analyze(lib, format).Health;
            original.Restore();

            // Same build, with loaners usable as if owned.
            var loaned = lib.Entries.Values.Where(e => e.Loaned > 0).ToDictionary(e => e, e => e.Owned);
            foreach (var (e, owned) in loaned) e.Owned = owned + e.Loaned;
            await rules.OptimizeAsync(lib, format, ct);
            report.HealthWithLoaners = rules.Analyze(lib, format).Health;
            foreach (var e in lib.Entries.Values.Where(e => e.InDeck > 0)) report.DeckWithLoaners[e] = e.InDeck;

            foreach (var (e, owned) in loaned)
            {
                int used = Math.Max(0, e.InDeck - owned);
                if (used > 0) report.Picks.Add(new LoanerPick(e, used, e.Card.PriceUsd));
            }
            foreach (var (e, copies) in ownedOnly)
            {
                int now = report.DeckWithLoaners.GetValueOrDefault(e);
                if (copies > now && e.Loaned == 0) report.Replaced.Add((e, copies - now));
            }
        }
        finally { original.Restore(); }
        return report;
    }

    /// <summary>
    /// Puts the recommended deck (with loaners) in place, built the same way as the recommendation so basic lands
    /// and Energy come along. Owned counts are left as they were. Take a snapshot first so it can be undone.
    /// </summary>
    public static async Task UseDeckAsync(Library lib, string format, DeckRules rules, CancellationToken ct = default)
    {
        var loaned = lib.Entries.Values.Where(e => e.Loaned > 0).ToDictionary(e => e, e => e.Owned);
        foreach (var (e, owned) in loaned) e.Owned = owned + e.Loaned;
        try { await rules.OptimizeAsync(lib, format, ct); }
        finally { foreach (var (e, owned) in loaned) e.Owned = owned; }
    }

    public static string Describe(LoanerReport r)
    {
        if (r.Picks.Count == 0)
            return "None of your loaner cards would improve this deck. Add loaners by scanning with the loaner switch on, or by name.";
        var lines = new List<string>
        {
            $"Deck health with your owned cards: {r.HealthOwnedOnly}/100. With the loaners below: {r.HealthWithLoaners}/100.",
            "",
            "Recommended loaners:",
        };
        lines.AddRange(r.Picks.OrderByDescending(p => p.Entry.Card.Quality).Select(p =>
            $"  + {p.Copies}x {p.Entry.Card.Name}" + (p.Price > 0 ? $"  ({Money.Format(p.Price)} each)" : "") +
            (string.IsNullOrEmpty(p.Entry.LoanNote) ? "" : $"  [{p.Entry.LoanNote}]")));
        if (r.Replaced.Count > 0)
        {
            lines.Add("");
            lines.Add("They would replace:");
            lines.AddRange(r.Replaced.Select(x => $"  - {x.copies}x {x.entry.Card.Name}"));
        }
        if (r.CostToBuy > 0) { lines.Add(""); lines.Add($"Cost to buy the recommended loaners: about {Money.Format(r.CostToBuy)}."); }
        return string.Join("\n", lines);
    }
}
