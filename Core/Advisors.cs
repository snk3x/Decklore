using System.Text.RegularExpressions;

namespace TcgScanner;

/// <summary>A stronger card that could take a weak deck card's place.</summary>
public record UpgradeOption(Card Card, double Price, string Why)
{
    /// <summary>Gundam and Cyberpunk have several different cards with the same name, so show the card number there.</summary>
    public string Label => Card.Game == Game.Magic || string.IsNullOrEmpty(Card.Number) ? Card.Name : $"{Card.Name} ({Card.Number})";
}

/// <summary>One of the deck's weakest cards, with stronger cards that do a similar job (best first).</summary>
public record UpgradeSuggestion(LibraryEntry Entry, int Copies, List<UpgradeOption> Options)
{
    public Card Current => Entry.Card;
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
/// Upgrades: finds the weakest cards in the deck and lists stronger, more played cards that do a similar job,
/// ranked best first with their prices. It only recommends; nothing in the deck changes. Picks can be saved
/// to the collection as wishlist loaners, which Loaner picks can then test in the deck.
/// </summary>
public static class UpgradeAdvisor
{
    /// <summary>Pokémon and Yu-Gi-Oh! card effects are too specific to match "a similar job" safely.</summary>
    public static bool Supports(Game game) => game is Game.Magic or Game.Gundam or Game.Cyberpunk;

    public static async Task<List<UpgradeSuggestion>> SuggestAsync(Game game, Library lib, string format, DeckRules rules,
        ICardSource source, Action<string> progress = null, CancellationToken ct = default)
    {
        var result = new List<UpgradeSuggestion>();
        if (!Supports(game)) return result;

        // Basic lands, Resources, tokens and Cyberpunk Legends (they set the deck's RAM and colors) are never upgraded here.
        var weakest = lib.Deck.Where(e => e.Card.Category is not ("Resource" or "Token" or "Legend") && !e.Card.UnlimitedCopies &&
                                          !e.Card.TypeLine.Contains("Basic"))
                              .OrderBy(e => e.Card.Quality)
                              // Gundam prices cost one JustTCG request per card number (free plan: 100 a day), so check fewer cards there.
                              .Take(game == Game.Gundam ? 6 : 8).ToList();
        var deckColors = lib.Deck.SelectMany(e => e.Card.Colors).Distinct().ToHashSet();
        var inDeck = lib.Deck.Select(e => game == Game.Magic ? e.Card.Name : e.Card.Number).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var e in weakest)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(e.Card.Name);
            List<UpgradeOption> options;
            try
            {
                options = game switch
                {
                    Game.Magic => await MagicUpgradesAsync((ScryfallSource)source, e.Card, format, deckColors, inDeck, ct),
                    Game.Gundam => await CatalogueUpgradesAsync(await GundamSource.CatalogueAsync(false, ct), e.Card, deckColors, inDeck, ct),
                    _ => await CatalogueUpgradesAsync(await CyberpunkSource.CatalogueAsync(ct), e.Card, deckColors, inDeck, ct),
                };
            }
            catch (RateLimitedException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch { continue; }
            options = options.Where(o => rules.LegalityLabel(o.Card, format) is not ("Not legal" or "Banned" or "Forbidden")).Take(3).ToList();
            if (options.Count > 0) result.Add(new UpgradeSuggestion(e, e.InDeck, options));
        }
        return result;
    }

    static int Rating(Card c) => (int)Math.Round(c.Quality * 100);

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
        (@"deals? (\S+|x) damage", "o:deals o:damage", "damage"), // burn aimed at players too (Lava Spike, Searing Blaze)
    };

    // Creatures with no clear role: keep what makes them good in combat (a hasty attacker should stay hasty).
    static readonly string[] MagicKeywords = { "Haste", "Prowess", "Flying", "First strike", "Double strike", "Trample", "Lifelink",
                                               "Deathtouch", "Menace", "Flash", "Vigilance" };

    /// <summary>Magic: Scryfall sorted by EDHREC rank, so the most played (usually strongest) cards come first.</summary>
    static async Task<List<UpgradeOption>> MagicUpgradesAsync(ScryfallSource scryfall, Card card, string format, HashSet<string> deckColors,
                                                              HashSet<string> inDeck, CancellationToken ct)
    {
        string fmt = format.ToLowerInvariant();
        var colorSet = deckColors.Where(c => "WUBRG".Contains(c)).ToList();
        string colors = colorSet.Count == 0 ? "c" : string.Join("", colorSet);
        // Two-faced cards: the front face decides what the card is (Fable is an enchantment, not its creature back side).
        string frontType = card.TypeLine.Split(" // ")[0];
        string type = new[] { "Land", "Creature", "Planeswalker", "Battle", "Instant", "Sorcery", "Artifact", "Enchantment" }
            .FirstOrDefault(t => frontType.Contains(t)) ?? card.Category;

        string query, why;
        if (type == "Land")
        {
            // Lands are about the mana they make: a red/white land should be upgraded to a better red/white land.
            var makes = card.ProducedMana.Where(m => "WUBRG".Contains(m)).ToList();
            if (makes.Count == 0) return new List<UpgradeOption>();
            query = $"f:{fmt} t:land -t:basic id<={colors} produces={string.Join("", makes).ToLowerInvariant()}";
            why = "Makes the same colors";
        }
        else
        {
            string text = card.Text.ToLowerInvariant().Split("//")[0];
            var role = MagicRoles.FirstOrDefault(r => Regex.IsMatch(text, r.pattern));
            query = $"f:{fmt} id<={colors} t:{type.ToLowerInvariant()} mv<={card.ManaValue + 1:0}";
            // Stay in the card's own colors, so a red card is upgraded to a red card, not a colorless artifact.
            var own = card.Colors.Where(c => "WUBRG".Contains(c)).ToList();
            if (own.Count > 0) query += $" c>={string.Join("", own).ToLowerInvariant()}";
            if (role.query != null) query += " " + role.query;
            why = role.role != null ? $"Also {role.role}" : $"Stronger {type.ToLowerInvariant()}";
            if (role.query == null && type == "Creature")
            {
                var kw = MagicKeywords.FirstOrDefault(k => Regex.IsMatch(text, $@"\b{k.ToLowerInvariant()}\b"));
                if (kw != null)
                {
                    query += $" keyword:\"{kw.ToLowerInvariant()}\"";
                    why = $"Also has {kw.ToLowerInvariant()}";
                }
            }
        }
        var found = await scryfall.SearchAsync(query, 12, ct);
        return found.Where(c => !inDeck.Contains(c.Name) && c.Quality > card.Quality + 0.05)
                    .OrderByDescending(c => c.Quality)
                    .Select(c => new UpgradeOption(c, c.PriceUsd,
                        $"{why}, mana value {c.ManaValue:0}, rating {Rating(c)} vs {Rating(card)}"))
                    .ToList();
    }

    static readonly string[] Keywords = { "Blocker", "Repair", "Breach", "First Strike", "High-Maneuver", "Support", "Suppression",
                                          "Burst", "Deploy", "Go Solo", "Quick", "Call" };

    /// <summary>Gundam and Cyberpunk: the strongest cards of the same type and colors at about the same cost.</summary>
    static async Task<List<UpgradeOption>> CatalogueUpgradesAsync(List<Card> catalogue, Card card, HashSet<string> deckColors,
                                                                  HashSet<string> inDeck, CancellationToken ct)
    {
        var traits = card.Race.Split(", ", StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var cardKeywords = Keywords.Where(k => card.Text.Contains(k, StringComparison.OrdinalIgnoreCase)).ToHashSet();
        var candidates = catalogue
            .Where(c => c.Category == card.Category && !inDeck.Contains(c.Number) && !c.Id.Contains("_p") &&
                        c.Colors.All(col => deckColors.Count == 0 || deckColors.Contains(col)) &&
                        c.Level <= card.Level + 1 && c.ManaValue <= card.ManaValue + 1 &&
                        c.Quality > card.Quality + 0.05 &&
                        (card.Game != Game.Cyberpunk || c.Ram <= card.Ram)) // keep the deck within its RAM
            // Strength first, with a nudge toward cards that share traits or keywords with the one they'd replace.
            .Select(c => (card: c, score: c.Quality
                  + 0.05 * c.Race.Split(", ", StringSplitOptions.RemoveEmptyEntries).Count(traits.Contains)
                  + 0.05 * Keywords.Count(k => cardKeywords.Contains(k) && c.Text.Contains(k, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(x => x.score)
            .GroupBy(x => x.card.Number).Select(g => g.First()) // one printing per card number
            .Take(3)
            .Select(x => JsonClone(x.card))
            .ToList();

        // Prices for these games come from JustTCG (needs the user's key).
        foreach (var c in candidates) await JustTcg.ApplyAsync(c, ct);
        return candidates.Select(c => new UpgradeOption(c, c.PriceUsd,
            $"Same type and colors, cost {c.ManaValue:0}, rating {Rating(c)} vs {Rating(card)}" +
            (c.PriceUsd <= 0 ? " (price unknown)" : ""))).ToList();
    }

    static Card JsonClone(Card c) => System.Text.Json.JsonSerializer.Deserialize<Card>(System.Text.Json.JsonSerializer.Serialize(c));

    /// <summary>
    /// Saves an upgrade to the collection as a wishlist loaner (enough copies to replace the weak card). The deck is not changed;
    /// Loaner picks shows how the deck would look with it.
    /// </summary>
    public static LibraryEntry AddToWishlist(Library lib, UpgradeSuggestion suggestion, UpgradeOption option)
    {
        var target = lib.Add(option.Card, 0, 0);
        int copies = Math.Max(1, suggestion.Copies);
        if (target.Loaned < copies) target.Loaned = copies;
        if (string.IsNullOrEmpty(target.LoanNote))
            target.LoanNote = $"Wishlist: upgrade for {suggestion.Current.Name}" + (option.Price > 0 ? $", about {Money.Format(option.Price)}" : "");
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
