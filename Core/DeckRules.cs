using System.Text;
using System.Text.RegularExpressions;

namespace TcgScanner;

public enum Severity { Error, Warning, Tip }

public record Issue(Severity Severity, string Message);

public class DeckReport
{
    public List<Issue> Issues { get; } = new();
    public List<string> Stats { get; } = new();

    public void Error(string m) => Issues.Add(new Issue(Severity.Error, m));
    public void Warn(string m) => Issues.Add(new Issue(Severity.Warning, m));
    public void Tip(string m) => Issues.Add(new Issue(Severity.Tip, m));

    public int Health => Math.Clamp(100
        - 25 * Issues.Count(i => i.Severity == Severity.Error)
        - 8 * Issues.Count(i => i.Severity == Severity.Warning), 0, 100);

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"Deck health: {Health}/100\n\n");
        foreach (var s in Stats) sb.Append(s).Append('\n');

        void Section(string title, Severity sev)
        {
            var items = Issues.Where(i => i.Severity == sev).ToList();
            if (items.Count == 0) return;
            sb.Append('\n').Append(title).Append('\n');
            foreach (var i in items) sb.Append("  • ").Append(i.Message).Append('\n');
        }

        Section("Must fix", Severity.Error);
        Section("Should fix", Severity.Warning);
        Section("Suggestions", Severity.Tip);
        if (Issues.Count == 0) sb.Append("\nNo problems found.\n");
        return sb.ToString();
    }
}

public abstract class DeckRules
{
    public abstract Game Game { get; }
    public abstract string[] Formats { get; }
    public abstract DeckReport Analyze(Library lib, string format);
    /// <summary>Rebuilds the deck (sets InDeck) using only cards in the collection. Returns a change log.</summary>
    public abstract Task<List<string>> OptimizeAsync(Library lib, string format, CancellationToken ct);
    public abstract string LegalityLabel(Card c, string format);
    public abstract string CostHeader { get; }
    public abstract string CostText(Card c);

    protected static List<string> ApplyPlan(Library lib, Dictionary<LibraryEntry, int> plan,
                                            Dictionary<LibraryEntry, string> reasons)
    {
        var log = new List<string>();
        foreach (var e in lib.Entries.Values.OrderBy(e => e.Card.Name).ToList())
        {
            int n = plan.GetValueOrDefault(e);
            if (n == e.InDeck) continue;
            int d = n - e.InDeck;
            string why = d < 0 ? $"  ({reasons.GetValueOrDefault(e, "weaker fit")})" : "";
            log.Add($"{(d > 0 ? "+" : "")}{d} {e.Card.Name}{why}");
            e.InDeck = n;
        }
        if (log.Count == 0) log.Add("No changes. This is already the best deck your collection can make.");
        return log;
    }
}

// ====================================================================== Magic: The Gathering

public class MagicRules : DeckRules
{
    readonly ScryfallSource _src;
    public MagicRules(ScryfallSource src) => _src = src;

    public override Game Game => Game.Magic;
    public override string[] Formats => new[] { "Standard", "Pioneer", "Modern", "Legacy", "Vintage", "Pauper", "Commander" };
    public override string CostHeader => "Mana value";
    public override string CostText(Card c) => c.Category == "Land" ? "" : c.ManaValue.ToString("0");

    static string Key(string f) => f.ToLowerInvariant();
    static string Leg(Card c, string f) => c.Legality.GetValueOrDefault(Key(f), "not_legal");
    static bool IsLegal(Card c, string f) => Leg(c, f) is "legal" or "restricted";

    static int Limit(Card c, string f)
    {
        if (c.UnlimitedCopies) return 999;
        if (Key(f) == "commander") return 1;
        return Leg(c, f) == "restricted" ? 1 : 4;
    }

    public override string LegalityLabel(Card c, string f) => Leg(c, f) switch
    {
        "legal" => "Legal",
        "restricted" => "Restricted",
        "banned" => "Banned",
        _ => "Not legal"
    };

    static readonly string[] BucketNames = { "0-1", "2", "3", "4", "5", "6+" };
    static int Bucket(double mv) => mv <= 1 ? 0 : mv >= 6 ? 5 : (int)mv - 1;
    static double[] IdealCurve(bool cmdr) => cmdr
        ? new[] { .12, .22, .22, .18, .12, .14 }
        : new[] { .17, .27, .22, .16, .10, .08 };

    static int TargetLands(double avg, bool cmdr) => cmdr
        ? Math.Clamp((int)Math.Round(31 + 2 * avg), 33, 40)
        : Math.Clamp((int)Math.Round(19.6 + 1.6 * avg), 20, 27);

    static readonly Dictionary<string, string> ColorNames = new()
        { ["W"] = "white", ["U"] = "blue", ["B"] = "black", ["R"] = "red", ["G"] = "green" };
    static readonly Dictionary<string, string> BasicNames = new()
        { ["W"] = "Plains", ["U"] = "Island", ["B"] = "Swamp", ["R"] = "Mountain", ["G"] = "Forest" };
    static int ColorOrder(string c) => "WUBRG".IndexOf(c, StringComparison.Ordinal);

    static void CountPips(string cost, Dictionary<string, double> pips, int mult)
    {
        foreach (Match m in Regex.Matches(cost ?? "", @"\{([^}]+)\}"))
        {
            var letters = m.Groups[1].Value.Split('/').Where(x => x.Length == 1 && "WUBRG".Contains(x)).ToList();
            foreach (var l in letters) pips[l] = pips.GetValueOrDefault(l) + (double)mult / letters.Count;
        }
    }

    public override DeckReport Analyze(Library lib, string format)
    {
        var r = new DeckReport();
        bool cmdr = Key(format) == "commander";
        var deck = lib.Deck.ToList();
        int total = deck.Sum(e => e.InDeck);
        var lands = deck.Where(e => e.Card.Category == "Land").ToList();
        var spells = deck.Where(e => e.Card.Category != "Land").ToList();
        int landCount = lands.Sum(e => e.InDeck), spellCount = spells.Sum(e => e.InDeck);
        double avg = spellCount == 0 ? 0 : spells.Sum(e => e.Card.ManaValue * e.InDeck) / spellCount;
        var curve = new int[6];
        foreach (var e in spells) curve[Bucket(e.Card.ManaValue)] += e.InDeck;
        var identity = deck.SelectMany(e => e.Card.Colors).Distinct().OrderBy(ColorOrder).ToList();

        r.Stats.Add($"Cards: {total} ({spellCount} spells, {landCount} lands)");
        r.Stats.Add($"Creatures: {spells.Where(e => e.Card.Category == "Creature").Sum(e => e.InDeck)}");
        r.Stats.Add($"Average mana value of spells: {avg:0.00}");
        r.Stats.Add("Mana curve: " + string.Join("   ", Enumerable.Range(0, 6).Select(i => $"{BucketNames[i]}: {curve[i]}")));
        r.Stats.Add("Colors: " + (identity.Count == 0 ? "colorless" : string.Join(", ", identity.Select(c => ColorNames[c]))));

        if (total == 0)
        {
            r.Error("The deck is empty. Add cards from the Collection tab, or press Optimize to build one.");
            return r;
        }

        if (cmdr)
        {
            if (total != 100) r.Error($"Commander decks need exactly 100 cards. You have {total}.");
        }
        else if (total < 60) r.Error($"{format} decks need at least 60 cards. You have {total}.");
        else if (total > 60) r.Tip($"{total - 60} cards over 60. Extra cards make your best cards show up less often.");

        foreach (var e in deck.Where(e => !IsLegal(e.Card, format)))
            r.Error($"{e.Card.Name} is {(Leg(e.Card, format) == "banned" ? "banned" : "not legal")} in {format}.");
        foreach (var g in deck.GroupBy(e => e.Card.Name))
        {
            int n = g.Sum(e => e.InDeck), lim = Limit(g.First().Card, format);
            if (n > lim) r.Error($"{g.Key}: {n} copies, the limit is {lim}.");
        }

        if (cmdr)
        {
            var commander = deck.Where(e => e.Card.IsLegendaryCreature)
                                .OrderByDescending(e => e.Card.Colors.Count).ThenByDescending(e => e.Card.Quality).FirstOrDefault();
            if (commander == null) r.Error("No legendary creature in the deck to be the commander.");
            else
            {
                r.Stats.Insert(0, $"Commander: {commander.Card.Name}");
                foreach (var e in deck.Where(e => !e.Card.Colors.All(commander.Card.Colors.Contains)))
                    r.Error($"{e.Card.Name} is outside {commander.Card.Name}'s color identity.");
            }
        }
        else if (identity.Count > 3) r.Warn("Four or more colors makes the mana unreliable. Consider cutting to two colors.");

        int target = TargetLands(avg, cmdr);
        if (!cmdr && total > 60) target = (int)Math.Round(target * total / 60.0);
        if (Math.Abs(landCount - target) >= 2)
            r.Warn($"{landCount} lands. With an average mana value of {avg:0.0}, about {target} is recommended.");

        if (spellCount >= 10)
        {
            var ideal = IdealCurve(cmdr);
            if ((double)curve[5] / spellCount > ideal[5] + 0.07)
                r.Warn($"Top-heavy: {curve[5]} spells cost 6 or more. Swap some for cheaper plays.");
            if ((double)curve[1] / spellCount < ideal[1] - 0.1)
                r.Tip($"Only {curve[1]} two-mana spells. More cheap plays make the early turns smoother.");
        }

        var pips = new Dictionary<string, double>();
        foreach (var e in spells) CountPips(e.Card.ManaCost, pips, e.InDeck);
        double pipTotal = pips.Values.Sum();
        if (landCount > 0 && pipTotal > 0)
            foreach (var (color, p) in pips.OrderBy(kv => ColorOrder(kv.Key)))
            {
                int sources = lands.Where(l => l.Card.ProducedMana.Contains(color)).Sum(l => l.InDeck);
                int needed = (int)Math.Ceiling(landCount * p / pipTotal * 0.8);
                if (sources < needed)
                    r.Warn($"Only {sources} lands make {ColorNames[color]} mana. About {needed} or more would cast those spells reliably.");
            }

        var weak = spells.Where(e => e.Card.Quality < 0.2).OrderBy(e => e.Card.Quality).Take(5).Select(e => e.Card.Name).ToList();
        if (weak.Count > 0) r.Tip("Rarely played cards worth reconsidering: " + string.Join(", ", weak) + ".");
        return r;
    }

    public override async Task<List<string>> OptimizeAsync(Library lib, string format, CancellationToken ct)
    {
        bool cmdr = Key(format) == "commander";
        int size = cmdr ? 100 : 60;
        var all = lib.Entries.Values.ToList();
        var reasons = new Dictionary<LibraryEntry, string>();
        foreach (var e in all.Where(e => e.InDeck > 0 && !IsLegal(e.Card, format))) reasons[e] = $"not legal in {format}";

        var usable = all.Where(e => IsLegal(e.Card, format) && (e.Owned > 0 || e.InDeck > 0)).ToList();
        int Avail(LibraryEntry e) => e.Card.UnlimitedCopies ? 999 : Math.Min(Limit(e.Card, format), Math.Max(e.Owned, e.InDeck));

        LibraryEntry commander = null;
        HashSet<string> colors;
        if (cmdr)
        {
            commander = usable.Where(e => e.Card.IsLegendaryCreature)
                              .OrderByDescending(e => e.InDeck > 0)
                              .ThenByDescending(e => e.Card.Colors.Count)
                              .ThenByDescending(e => e.Card.Quality).FirstOrDefault();
            if (commander == null)
                return new List<string> { "Commander needs a legendary creature. Scan or add one, then optimize again." };
            colors = commander.Card.Colors.ToHashSet();
        }
        else colors = PickColors(usable);

        bool Fits(Card c) => c.Colors.All(colors.Contains);
        foreach (var e in all.Where(e => e.InDeck > 0 && !reasons.ContainsKey(e) && !Fits(e.Card)))
            reasons[e] = "outside the deck's colors";

        var plan = new Dictionary<LibraryEntry, int>();
        if (commander != null) plan[commander] = 1;
        int slots = size - (cmdr ? 1 : 0);

        var spellPool = usable.Where(e => e.Card.Category != "Land" && Fits(e.Card) && e != commander).ToList();
        int lands = cmdr ? 37 : 24;
        Dictionary<LibraryEntry, int> spells = new();
        for (int pass = 0; pass < 2; pass++)
        {
            spells = PickSpells(spellPool, slots - lands, cmdr, Avail);
            int cnt = spells.Values.Sum();
            double avg = cnt == 0 ? 3 : spells.Sum(kv => kv.Key.Card.ManaValue * kv.Value) / cnt;
            lands = TargetLands(avg, cmdr);
        }
        foreach (var kv in spells) plan[kv.Key] = kv.Value;
        int spellCount = spells.Values.Sum();

        // Nonbasic lands that make the deck's colors.
        int maxNonbasic = cmdr ? 15 : 8, nonbasic = 0;
        var landPool = usable
            .Where(e => e.Card.Category == "Land" && !e.Card.UnlimitedCopies && Fits(e.Card) &&
                        (e.Card.ProducedMana.Count == 0 ? e.InDeck > 0 : e.Card.ProducedMana.Any(colors.Contains)))
            .OrderByDescending(e => e.Card.Quality + (e.InDeck > 0 ? 0.25 : 0));
        foreach (var e in landPool)
        {
            int take = Math.Min(Avail(e), Math.Min(maxNonbasic, lands) - nonbasic);
            if (take <= 0) break;
            plan[e] = take;
            nonbasic += take;
        }

        // Basic lands split by colored mana symbols.
        int basics = lands - nonbasic;
        var pips = colors.ToDictionary(c => c, _ => 0.0);
        foreach (var kv in spells) CountPips(kv.Key.Card.ManaCost, pips, kv.Value);
        foreach (var k in pips.Keys.ToList()) if (!colors.Contains(k)) pips.Remove(k);
        if (pips.Count > 0 && pips.Values.Sum() == 0) foreach (var k in pips.Keys.ToList()) pips[k] = 1;

        var split = new Dictionary<string, int>();
        if (pips.Count == 0) split["C"] = basics;
        else
        {
            double tot = pips.Values.Sum();
            foreach (var (c, p) in pips) split[c] = (int)Math.Floor(basics * p / tot);
            int left = basics - split.Values.Sum();
            foreach (var c in pips.OrderByDescending(kv => basics * kv.Value / tot - split[kv.Key]).Select(kv => kv.Key).Take(left))
                split[c]++;
        }
        var log = new List<string>();
        foreach (var (color, n) in split)
        {
            if (n <= 0) continue;
            string name = color == "C" ? "Wastes" : BasicNames[color];
            var entry = all.FirstOrDefault(e => e.Card.Name == name && e.Card.UnlimitedCopies);
            if (entry == null)
            {
                var card = await _src.ExactAsync(name, ct);
                if (card == null) { log.Add($"Could not fetch {name}; add basic lands by hand."); continue; }
                entry = lib.Add(card, 0, 0);
            }
            plan[entry] = plan.GetValueOrDefault(entry) + n;
        }

        var result = ApplyPlan(lib, plan, reasons);
        string colorText = colors.Count == 0 ? "colorless" : string.Join(", ", colors.OrderBy(ColorOrder).Select(c => ColorNames[c]));
        result.Insert(0, (commander != null ? $"Commander: {commander.Card.Name}. " : "") +
                         $"Colors: {colorText}. {spellCount} spells and {lands} lands.");
        int shortBy = slots - lands - spellCount;
        if (shortBy > 0)
            result.Insert(1, $"Your collection is {shortBy} playable {colorText} spells short of a full deck. Scan more cards.");
        result.InsertRange(1, log);
        return result;
    }

    static HashSet<string> PickColors(List<LibraryEntry> usable)
    {
        var deckSpells = usable.Where(e => e.InDeck > 0 && e.Card.Category != "Land").ToList();
        var w = new Dictionary<string, double>();
        if (deckSpells.Count > 0)
            foreach (var e in deckSpells) foreach (var c in e.Card.Colors) w[c] = w.GetValueOrDefault(c) + e.InDeck;
        else
            foreach (var e in usable.Where(e => e.Card.Category != "Land"))
                foreach (var c in e.Card.Colors) w[c] = w.GetValueOrDefault(c) + e.Card.Quality * Math.Min(e.Owned, 4);
        if (w.Count == 0) return new HashSet<string>();
        double tot = w.Values.Sum();
        var ordered = w.OrderByDescending(kv => kv.Value).ToList();
        int max = deckSpells.Count > 0 ? 3 : 2;
        return ordered.Where((kv, i) => i < 2 || (i < max && kv.Value / tot >= 0.2)).Select(kv => kv.Key).ToHashSet();
    }

    static Dictionary<LibraryEntry, int> PickSpells(List<LibraryEntry> pool, int target, bool cmdr, Func<LibraryEntry, int> avail)
    {
        var ideal = IdealCurve(cmdr);
        var counts = new int[6];
        var chosen = new Dictionary<LibraryEntry, int>();
        var byName = new Dictionary<string, int>();
        int total = 0, nameLimit = cmdr ? 1 : 4;
        while (total < target)
        {
            LibraryEntry best = null;
            double bestScore = double.MinValue;
            foreach (var e in pool)
            {
                int have = chosen.GetValueOrDefault(e);
                if (have >= avail(e)) continue;
                if (!e.Card.UnlimitedCopies && byName.GetValueOrDefault(e.Card.Name) >= Math.Min(nameLimit, avail(e))) continue;
                int b = Bucket(e.Card.ManaValue);
                double need = ideal[b] * target - counts[b];
                double s = e.Card.Quality + (e.InDeck > 0 ? 0.25 : 0) - 0.03 * have + (need > 0 ? 0.3 : -0.5 + 0.15 * need - 0.12 * b); // overfilling expensive slots hurts more
                if (s > bestScore) { bestScore = s; best = e; }
            }
            if (best == null) break;
            chosen[best] = chosen.GetValueOrDefault(best) + 1;
            byName[best.Card.Name] = byName.GetValueOrDefault(best.Card.Name) + 1;
            counts[Bucket(best.Card.ManaValue)]++;
            total++;
        }
        return chosen;
    }
}

// ====================================================================== Pokémon TCG

public class PokemonRules : DeckRules
{
    readonly TcgdexSource _src;
    public PokemonRules(TcgdexSource src) => _src = src;

    public override Game Game => Game.Pokemon;
    public override string[] Formats => new[] { "Standard", "Expanded", "Unlimited" };
    public override string CostHeader => "HP";
    public override string CostText(Card c) => c.Category == "Pokemon" && c.Hp > 0 ? c.Hp.ToString() : "";

    static string Key(string f) => f.ToLowerInvariant();
    static bool IsLegal(Card c, string f) => Key(f) == "unlimited" || c.Legality.GetValueOrDefault(Key(f)) == "legal";
    public override string LegalityLabel(Card c, string f) => IsLegal(c, f) ? "Legal" : "Not legal";

    static bool IsAceSpec(Card c) => c.Rarity.Contains("ACE SPEC", StringComparison.OrdinalIgnoreCase) ||
                                     c.Text.Contains("ACE SPEC", StringComparison.OrdinalIgnoreCase);
    static bool IsRadiant(Card c) => c.Name.StartsWith("Radiant ", StringComparison.OrdinalIgnoreCase);
    static readonly string[] BasicTypes = { "Grass", "Fire", "Water", "Lightning", "Psychic", "Fighting", "Darkness", "Metal" };

    static string EnergyTypeOf(Card c) =>
        BasicTypes.FirstOrDefault(t => c.Name.Contains(t, StringComparison.OrdinalIgnoreCase));

    public override DeckReport Analyze(Library lib, string format)
    {
        var r = new DeckReport();
        var deck = lib.Deck.ToList();
        int total = deck.Sum(e => e.InDeck);
        int Sum(Func<LibraryEntry, bool> f) => deck.Where(f).Sum(e => e.InDeck);
        int mons = Sum(e => e.Card.Category == "Pokemon"), trainers = Sum(e => e.Card.Category == "Trainer"),
            energy = Sum(e => e.Card.Category == "Energy"), basics = Sum(e => e.Card.Category == "Pokemon" && e.Card.SubType == "Basic"),
            supporters = Sum(e => e.Card.SubType == "Supporter");

        r.Stats.Add($"Cards: {total}");
        r.Stats.Add($"Pokémon: {mons} ({basics} Basic)   Trainers: {trainers} ({supporters} Supporters)   Energy: {energy}");

        if (total == 0)
        {
            r.Error("The deck is empty. Add cards from the Collection tab, or press Optimize to build one.");
            return r;
        }
        if (total != 60) r.Error($"Pokémon decks need exactly 60 cards. You have {total}.");
        if (basics == 0) r.Error("The deck needs at least one Basic Pokémon.");

        foreach (var e in deck.Where(e => !IsLegal(e.Card, format)))
            r.Error($"{e.Card.Name} ({e.Card.SetName}) is not legal in {format}.");
        foreach (var g in deck.Where(e => !e.Card.UnlimitedCopies).GroupBy(e => e.Card.Name, StringComparer.OrdinalIgnoreCase))
        {
            int n = g.Sum(e => e.InDeck);
            if (n > 4) r.Error($"{g.Key}: {n} copies, the limit is 4.");
        }
        if (Sum(e => IsAceSpec(e.Card)) > 1) r.Error("Only one ACE SPEC card is allowed per deck.");
        if (Sum(e => IsRadiant(e.Card)) > 1) r.Error("Only one Radiant Pokémon is allowed per deck.");

        var names = deck.Select(e => e.Card.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool rareCandy = names.Contains("Rare Candy");
        foreach (var e in deck.Where(e => e.Card.Category == "Pokemon" && e.Card.EvolvesFrom != ""))
        {
            if (names.Contains(e.Card.EvolvesFrom)) continue;
            if (e.Card.SubType == "Stage2" && rareCandy) r.Tip($"{e.Card.Name} relies on Rare Candy because {e.Card.EvolvesFrom} is missing.");
            else r.Error($"{e.Card.Name} evolves from {e.Card.EvolvesFrom}, which is not in the deck.");
        }

        if (mons < 10) r.Warn($"Only {mons} Pokémon. Most decks run 12 to 20 so they never run out of attackers.");
        else if (mons > 22) r.Warn($"{mons} Pokémon is a lot. Trainers usually give more consistency.");
        if (trainers < 25) r.Warn($"Only {trainers} Trainers. Most decks run 28 to 38 for draw and search.");
        if (supporters < 6) r.Tip($"Only {supporters} Supporters. Draw Supporters help you find your key cards each turn.");

        var needed = deck.Where(e => e.Card.Category == "Pokemon").SelectMany(e => e.Card.AttackCostTypes).Where(BasicTypes.Contains).Distinct();
        var energyTypes = deck.Where(e => e.Card.Category == "Energy").Select(e => EnergyTypeOf(e.Card)).Where(t => t != null).ToHashSet();
        bool special = deck.Any(e => e.Card.Category == "Energy" && e.Card.SubType != "Basic");
        foreach (var t in needed.Where(t => !energyTypes.Contains(t)))
            r.Warn(special ? $"No basic {t} Energy. Make sure your Special Energy covers {t} attacks."
                           : $"Attacks need {t} Energy but the deck has none.");
        if (energy < 6 && needed.Any()) r.Warn($"Only {energy} Energy. Most decks need 8 to 14.");
        else if (energy > 16) r.Tip($"{energy} Energy is a lot. Swap a few for draw Trainers.");
        return r;
    }

    public override async Task<List<string>> OptimizeAsync(Library lib, string format, CancellationToken ct)
    {
        var all = lib.Entries.Values.ToList();
        var reasons = new Dictionary<LibraryEntry, string>();
        foreach (var e in all.Where(e => e.InDeck > 0 && !IsLegal(e.Card, format))) reasons[e] = $"not legal in {format}";
        var usable = all.Where(e => IsLegal(e.Card, format) && (e.Owned > 0 || e.InDeck > 0)).ToList();
        int Own(LibraryEntry e) => e.Card.UnlimitedCopies ? 999 : Math.Max(e.Owned, e.InDeck);

        var plan = new Dictionary<LibraryEntry, int>();
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool aceUsed = false, radiantUsed = false;
        int Count(string n) => byName.GetValueOrDefault(n);
        bool CanAdd(LibraryEntry e, int extra = 0)
        {
            var c = e.Card;
            if (plan.GetValueOrDefault(e) + extra >= Own(e)) return false;
            if (!c.UnlimitedCopies && Count(c.Name) + extra >= 4) return false;
            if (IsAceSpec(c) && aceUsed) return false;
            if (IsRadiant(c) && radiantUsed) return false;
            return true;
        }
        void Add(LibraryEntry e)
        {
            plan[e] = plan.GetValueOrDefault(e) + 1;
            byName[e.Card.Name] = Count(e.Card.Name) + 1;
            if (IsAceSpec(e.Card)) aceUsed = true;
            if (IsRadiant(e.Card)) radiantUsed = true;
        }
        int Total(string cat) => plan.Where(kv => kv.Key.Card.Category == cat).Sum(kv => kv.Value);

        var types = PickTypes(usable);
        var mons = usable.Where(e => e.Card.Category == "Pokemon" &&
                                     (e.InDeck > 0 || e.Card.Colors.Count == 0 || e.Card.Colors.Any(types.Contains) ||
                                      e.Card.Colors.Contains("Colorless"))).ToList();

        // A unit is a Pokémon plus any earlier stages needed so that copies of each stage
        // are never more than copies of the stage before it.
        List<LibraryEntry> Unit(LibraryEntry e)
        {
            var unit = new List<LibraryEntry> { e };
            var pending = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [e.Card.Name] = 1 };
            var cur = e.Card;
            for (int guard = 0; guard < 3 && cur.EvolvesFrom != ""; guard++)
            {
                string pre = cur.EvolvesFrom;
                int need = Count(cur.Name) + pending.GetValueOrDefault(cur.Name);
                int have = Count(pre) + pending.GetValueOrDefault(pre);
                if (have >= need) break;
                var preEntry = usable.FirstOrDefault(x => x.Card.Category == "Pokemon" &&
                                                         x.Card.Name.Equals(pre, StringComparison.OrdinalIgnoreCase) &&
                                                         CanAdd(x, unit.Count(u => u == x)) && have < 4);
                if (preEntry == null) return null;
                unit.Add(preEntry);
                pending[pre] = pending.GetValueOrDefault(pre) + 1;
                cur = preEntry.Card;
            }
            return unit;
        }

        const int monTarget = 16;
        while (Total("Pokemon") < monTarget)
        {
            List<LibraryEntry> best = null;
            double bestScore = double.MinValue;
            int basicsSoFar = plan.Where(kv => kv.Key.Card.SubType == "Basic" && kv.Key.Card.Category == "Pokemon").Sum(kv => kv.Value);
            foreach (var e in mons)
            {
                if (!CanAdd(e)) continue;
                var unit = Unit(e);
                if (unit == null || Total("Pokemon") + unit.Count > monTarget + 1) continue;
                double s = unit.Average(u => u.Card.Quality + (u.InDeck > 0 ? 0.3 : 0)) - 0.04 * Count(e.Card.Name)
                           + (e.Card.SubType == "Basic" && basicsSoFar < 4 ? 0.2 : 0);
                if (s > bestScore) { bestScore = s; best = unit; }
            }
            if (best == null) break;
            foreach (var u in best) Add(u);
        }

        // Energy types the chosen attackers need, weighted by how often they appear.
        var need = new Dictionary<string, double>();
        foreach (var kv in plan.Where(kv => kv.Key.Card.Category == "Pokemon"))
            foreach (var t in kv.Key.Card.AttackCostTypes.Where(BasicTypes.Contains))
                need[t] = need.GetValueOrDefault(t) + kv.Value;
        if (need.Count == 0) foreach (var t in types.Where(BasicTypes.Contains)) need[t] = 1;
        int energyTarget = need.Count == 0 ? 0 : 11;

        int trainerTarget = 60 - Total("Pokemon") - energyTarget;
        var trainers = usable.Where(e => e.Card.Category == "Trainer").ToList();
        while (Total("Trainer") < trainerTarget)
        {
            int sup = plan.Where(kv => kv.Key.Card.SubType == "Supporter").Sum(kv => kv.Value);
            var best = trainers.Where(e => CanAdd(e))
                .OrderByDescending(e => e.Card.Quality + (e.InDeck > 0 ? 0.3 : 0) - 0.05 * Count(e.Card.Name)
                                        + (e.Card.SubType == "Supporter" && sup < 10 ? 0.25 : 0))
                .FirstOrDefault();
            if (best == null) break;
            Add(best);
        }

        // Special Energy first (at most 4), then basics to reach 60.
        foreach (var e in usable.Where(e => e.Card.Category == "Energy" && e.Card.SubType != "Basic")
                                .OrderByDescending(e => e.InDeck > 0).ThenByDescending(e => e.Card.Quality))
            while (Total("Energy") < 4 && plan.Values.Sum() < 60 && CanAdd(e) && (e.InDeck > 0 || need.Count > 0)) Add(e);

        var log = new List<string>();
        int remaining = 60 - plan.Values.Sum();
        if (remaining > 0 && need.Count > 0)
        {
            double tot = need.Values.Sum();
            var split = need.ToDictionary(kv => kv.Key, kv => (int)Math.Floor(remaining * kv.Value / tot));
            foreach (var t in need.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(remaining - split.Values.Sum()))
                split[t]++;
            foreach (var (type, n) in split)
            {
                if (n <= 0) continue;
                var entry = usable.FirstOrDefault(e => e.Card.UnlimitedCopies && EnergyTypeOf(e.Card) == type)
                            ?? all.FirstOrDefault(e => e.Card.UnlimitedCopies && EnergyTypeOf(e.Card) == type && IsLegal(e.Card, format));
                if (entry == null)
                {
                    var card = await _src.BasicEnergyAsync(type, ct);
                    if (card == null) { log.Add($"Could not fetch basic {type} Energy; add it by hand."); continue; }
                    entry = lib.Add(card, 0, 0);
                }
                plan[entry] = plan.GetValueOrDefault(entry) + n;
            }
        }

        var result = ApplyPlan(lib, plan, reasons);
        int totalCards = plan.Values.Sum();
        result.Insert(0, $"Main types: {(types.Count == 0 ? "none found" : string.Join(", ", types))}. " +
                         $"{Total("Pokemon")} Pokémon, {Total("Trainer")} Trainers, {Total("Energy")} Energy.");
        if (totalCards < 60) result.Insert(1, $"Your collection is {60 - totalCards} cards short of 60. Scan more Trainers and Pokémon.");
        if (Total("Energy") > 16) result.Insert(1, "Basic Energy is filling space because the collection is short on Trainers.");
        result.InsertRange(1, log);
        return result;
    }

    static List<string> PickTypes(List<LibraryEntry> usable)
    {
        var w = new Dictionary<string, double>();
        var inDeck = usable.Where(e => e.InDeck > 0 && e.Card.Category == "Pokemon").ToList();
        if (inDeck.Count > 0)
            foreach (var e in inDeck) foreach (var t in e.Card.Colors) w[t] = w.GetValueOrDefault(t) + e.InDeck;
        else
            foreach (var e in usable.Where(e => e.Card.Category == "Pokemon"))
                foreach (var t in e.Card.Colors) w[t] = w.GetValueOrDefault(t) + e.Card.Quality * Math.Min(e.Owned, 4);
        w.Remove("Colorless");
        return w.OrderByDescending(kv => kv.Value).Take(2).Select(kv => kv.Key).ToList();
    }
}

// ====================================================================== Yu-Gi-Oh!

public class YugiohRules : DeckRules
{
    public override Game Game => Game.YuGiOh;
    public override string[] Formats => new[] { "Advanced", "Traditional", "Unlimited" };
    public override string CostHeader => "Level";
    public override string CostText(Card c) => c.Level > 0 ? c.Level.ToString() : "";

    static string Key(string f) => f.ToLowerInvariant();
    static string Leg(Card c, string f) => c.Legality.GetValueOrDefault(Key(f), "legal");
    static int Limit(Card c, string f) => Leg(c, f) switch { "banned" => 0, "limited" => 1, "semi-limited" => 2, _ => 3 };

    public override string LegalityLabel(Card c, string f) => Leg(c, f) switch
    {
        "banned" => "Forbidden",
        "limited" => "Limited (1)",
        "semi-limited" => "Semi-limited (2)",
        _ => "Legal"
    };

    public override DeckReport Analyze(Library lib, string format)
    {
        var r = new DeckReport();
        var deck = lib.Deck.ToList();
        var main = deck.Where(e => !e.Card.IsExtraDeck).ToList();
        var extra = deck.Where(e => e.Card.IsExtraDeck).ToList();
        int mainCount = main.Sum(e => e.InDeck), extraCount = extra.Sum(e => e.InDeck);
        int monsters = main.Where(e => e.Card.Category == "Monster").Sum(e => e.InDeck);
        int spells = main.Where(e => e.Card.Category == "Spell").Sum(e => e.InDeck);
        int traps = main.Where(e => e.Card.Category == "Trap").Sum(e => e.InDeck);

        r.Stats.Add($"Main Deck: {mainCount} ({monsters} monsters, {spells} spells, {traps} traps)");
        r.Stats.Add($"Extra Deck: {extraCount}");

        if (mainCount + extraCount == 0)
        {
            r.Error("The deck is empty. Add cards from the Collection tab, or press Optimize to build one.");
            return r;
        }
        if (mainCount < 40) r.Error($"The Main Deck needs at least 40 cards. You have {mainCount}.");
        else if (mainCount > 60) r.Error($"The Main Deck can have at most 60 cards. You have {mainCount}.");
        else if (mainCount > 40) r.Tip($"{mainCount - 40} cards over 40. A 40-card deck draws your best cards most often.");
        if (extraCount > 15) r.Error($"The Extra Deck can have at most 15 cards. You have {extraCount}.");

        foreach (var g in deck.GroupBy(e => e.Card.Name))
        {
            int n = g.Sum(e => e.InDeck), lim = Limit(g.First().Card, format);
            if (n > lim)
                r.Error(lim == 0 ? $"{g.Key} is Forbidden in {format}." : $"{g.Key}: {n} copies, the limit is {lim} in {format}.");
        }

        if (mainCount >= 20)
        {
            double m = (double)monsters / mainCount;
            if (m < 0.3) r.Warn($"Only {monsters} monsters. You may often have nothing to summon.");
            else if (m > 0.7) r.Warn($"{monsters} monsters and few spells or traps. Add removal and protection.");
        }
        int bigOnes = main.Where(e => e.Card.Category == "Monster" && e.Card.Level >= 7).Sum(e => e.InDeck);
        if (bigOnes > 5) r.Tip($"{bigOnes} monsters of Level 7 or higher need two tributes. Make sure you have ways to summon them.");

        var weak = main.Where(e => e.Card.Quality < 0.35).OrderBy(e => e.Card.Quality).Take(5).Select(e => e.Card.Name).ToList();
        if (weak.Count > 0) r.Tip("Rarely played cards worth reconsidering: " + string.Join(", ", weak) + ".");
        return r;
    }

    public override Task<List<string>> OptimizeAsync(Library lib, string format, CancellationToken ct)
    {
        var all = lib.Entries.Values.ToList();
        var reasons = new Dictionary<LibraryEntry, string>();
        foreach (var e in all.Where(e => e.InDeck > 0 && Limit(e.Card, format) == 0)) reasons[e] = $"Forbidden in {format}";
        var usable = all.Where(e => Limit(e.Card, format) > 0 && (e.Owned > 0 || e.InDeck > 0)).ToList();

        var plan = new Dictionary<LibraryEntry, int>();
        var byName = new Dictionary<string, int>();
        bool CanAdd(LibraryEntry e) => plan.GetValueOrDefault(e) < Math.Max(e.Owned, e.InDeck) &&
                                       byName.GetValueOrDefault(e.Card.Name) < Limit(e.Card, format);
        void Add(LibraryEntry e)
        {
            plan[e] = plan.GetValueOrDefault(e) + 1;
            byName[e.Card.Name] = byName.GetValueOrDefault(e.Card.Name) + 1;
        }

        // Synergy: the most common monster type and attribute among cards already in the deck.
        var seed = usable.Where(e => e.InDeck > 0 && e.Card.Category == "Monster").ToList();
        if (seed.Count == 0) seed = usable.Where(e => e.Card.Category == "Monster").OrderByDescending(e => e.Card.Quality).Take(10).ToList();
        string topRace = seed.GroupBy(e => e.Card.Race).OrderByDescending(g => g.Sum(e => Math.Max(1, e.InDeck))).Select(g => g.Key).FirstOrDefault();
        string topAttr = seed.SelectMany(e => e.Card.Colors).GroupBy(a => a).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        double Score(LibraryEntry e) => e.Card.Quality + (e.InDeck > 0 ? 0.3 : 0) - 0.05 * plan.GetValueOrDefault(e)
                                        + (e.Card.Category == "Monster" && e.Card.Race == topRace ? 0.15 : 0)
                                        + (e.Card.Colors.Contains(topAttr) ? 0.1 : 0);

        var targets = new Dictionary<string, int> { ["Monster"] = 20, ["Spell"] = 14, ["Trap"] = 6 };
        var main = usable.Where(e => !e.Card.IsExtraDeck).ToList();
        int MainTotal() => plan.Where(kv => !kv.Key.Card.IsExtraDeck).Sum(kv => kv.Value);
        int Cat(string c) => plan.Where(kv => !kv.Key.Card.IsExtraDeck && kv.Key.Card.Category == c).Sum(kv => kv.Value);
        while (MainTotal() < 40)
        {
            var best = main.Where(CanAdd)
                .OrderByDescending(e => Score(e) + (Cat(e.Card.Category) < targets[e.Card.Category] ? 0.35 : 0))
                .FirstOrDefault();
            if (best == null) break;
            Add(best);
        }

        foreach (var e in usable.Where(e => e.Card.IsExtraDeck).OrderByDescending(Score).ToList())
            while (plan.Where(kv => kv.Key.Card.IsExtraDeck).Sum(kv => kv.Value) < 15 && CanAdd(e)) Add(e);

        var result = ApplyPlan(lib, plan, reasons);
        result.Insert(0, $"Main Deck {MainTotal()} ({Cat("Monster")} monsters, {Cat("Spell")} spells, {Cat("Trap")} traps). " +
                         $"Extra Deck {plan.Where(kv => kv.Key.Card.IsExtraDeck).Sum(kv => kv.Value)}." +
                         (topRace != null ? $" Built around {topRace} monsters." : ""));
        if (MainTotal() < 40) result.Insert(1, $"Your collection is {40 - MainTotal()} cards short of a 40-card Main Deck. Scan more cards.");
        return Task.FromResult(result);
    }
}

// ====================================================================== Cyberpunk TCG

public class CyberpunkRules : DeckRules
{
    public override Game Game => Game.Cyberpunk;
    public override string[] Formats => new[] { "Standard", "Casual" };
    public override string CostHeader => "Cost";
    public override string CostText(Card c) => c.Category == "Legend" && c.ManaValue <= 0 ? "" : c.ManaValue.ToString("0");

    static bool IsLegal(Card c, string f) => f == "Casual" || c.Legality.GetValueOrDefault("standard", "legal") == "legal";
    public override string LegalityLabel(Card c, string f) => IsLegal(c, f) ? "Legal" : "Not legal";

    static readonly string[] Colors = { "Red", "Yellow", "Green", "Blue" };
    static readonly string[] BucketNames = { "0-2", "3-4", "5-6", "7+" };
    static readonly double[] IdealCurve = { .30, .35, .22, .13 };
    static int Bucket(double cost) => cost <= 2 ? 0 : cost <= 4 ? 1 : cost <= 6 ? 2 : 3;
    static readonly Dictionary<string, int> TypeTargets = new() { ["Unit"] = 22, ["Program"] = 12, ["Gear"] = 6 };

    static Dictionary<string, int> RamOf(IEnumerable<Card> legends)
    {
        var ram = Colors.ToDictionary(c => c, _ => 0);
        foreach (var l in legends)
            foreach (var col in l.Colors) ram[col] = ram.GetValueOrDefault(col) + Math.Max(l.Ram, 0);
        return ram;
    }

    static bool RamOk(Card c, Dictionary<string, int> ram) =>
        c.Ram <= 0 || c.Colors.Count == 0 || c.Colors.All(col => ram.GetValueOrDefault(col) >= c.Ram);

    public override DeckReport Analyze(Library lib, string format)
    {
        var r = new DeckReport();
        var deck = lib.Deck.ToList();
        var legends = deck.Where(e => e.Card.Category == "Legend").ToList();
        var main = deck.Where(e => e.Card.Category != "Legend").ToList();
        int legendCount = legends.Sum(e => e.InDeck), mainCount = main.Sum(e => e.InDeck);
        var ram = RamOf(legends.SelectMany(e => Enumerable.Repeat(e.Card, e.InDeck)));
        double avg = mainCount == 0 ? 0 : main.Sum(e => e.Card.ManaValue * e.InDeck) / mainCount;
        var curve = new int[4];
        foreach (var e in main) curve[Bucket(e.Card.ManaValue)] += e.InDeck;
        int Count(string t) => main.Where(e => e.Card.Category == t).Sum(e => e.InDeck);

        r.Stats.Add("Legends: " + (legends.Count == 0 ? "none" : string.Join(", ", legends.Select(e => e.Card.Name))));
        r.Stats.Add("RAM: " + string.Join("   ", Colors.Where(c => ram[c] > 0).Select(c => $"{c} {ram[c]}")));
        r.Stats.Add($"Main deck: {mainCount} ({Count("Unit")} units, {Count("Program")} programs, {Count("Gear")} gear)");
        r.Stats.Add($"Average cost: {avg:0.00}");
        r.Stats.Add("Cost curve: " + string.Join("   ", Enumerable.Range(0, 4).Select(i => $"{BucketNames[i]}: {curve[i]}")));
        int sellable = main.Where(e => e.Card.Sellable).Sum(e => e.InDeck);
        r.Stats.Add($"Cards with a Sell tag: {sellable}");

        if (legendCount + mainCount == 0)
        {
            r.Error("The deck is empty. Add cards from the Collection tab, or press Optimize to build one.");
            return r;
        }
        if (legendCount != 3) r.Error($"A deck needs exactly 3 Legends. You have {legendCount}.");
        foreach (var g in legends.GroupBy(e => e.Card.BaseName).Where(g => g.Sum(e => e.InDeck) > 1))
            r.Error($"Your Legends must have different names, but {g.Key} appears more than once.");
        if (mainCount < 40) r.Error($"The main deck needs at least 40 cards. You have {mainCount}.");
        else if (mainCount > 50) r.Error($"The main deck can have at most 50 cards. You have {mainCount}.");
        else if (mainCount > 40) r.Tip($"{mainCount - 40} cards over 40. A smaller deck draws your best cards more often.");

        foreach (var g in main.GroupBy(e => e.Card.Name).Where(g => g.Sum(e => e.InDeck) > 3))
            r.Error($"{g.Key}: {g.Sum(e => e.InDeck)} copies, the limit is 3.");
        foreach (var e in deck.Where(e => !IsLegal(e.Card, format)))
            r.Error($"{e.Card.Name} is not legal in {format}.");
        if (legendCount > 0)
            foreach (var e in main.Where(e => !RamOk(e.Card, ram)))
                r.Error($"{e.Card.Name} needs {e.Card.Ram} {string.Join("/", e.Card.Colors)} RAM; your Legends give {e.Card.Colors.Select(c => ram.GetValueOrDefault(c)).DefaultIfEmpty(0).Min()}.");

        if (mainCount >= 20)
        {
            if (Count("Unit") < mainCount * 0.4) r.Warn($"Only {Count("Unit")} units. You need units on the field to attack and defend.");
            if ((double)curve[3] / mainCount > IdealCurve[3] + 0.08) r.Warn($"Top-heavy: {curve[3]} cards cost 7 or more.");
            if ((double)curve[0] / mainCount < IdealCurve[0] - 0.12) r.Tip($"Only {curve[0]} cards cost 2 or less. Cheap plays help you keep pace early.");
            if (sellable < 10 && main.Any(e => e.Card.Sellable))
                r.Tip($"Only {sellable} cards have a Sell tag. You earn Eddies by selling one card each turn, so you may run short of money. (Cards scanned before this update show the tag after Refresh prices.)");
        }
        return r;
    }

    public override Task<List<string>> OptimizeAsync(Library lib, string format, CancellationToken ct)
    {
        var all = lib.Entries.Values.ToList();
        var reasons = new Dictionary<LibraryEntry, string>();
        foreach (var e in all.Where(e => e.InDeck > 0 && !IsLegal(e.Card, format))) reasons[e] = $"not legal in {format}";
        var usable = all.Where(e => IsLegal(e.Card, format) && (e.Owned > 0 || e.InDeck > 0)).ToList();
        int Own(LibraryEntry e) => Math.Max(e.Owned, e.InDeck);

        var legendPool = usable.Where(e => e.Card.Category == "Legend")
                               .OrderByDescending(e => e.Card.Quality + (e.InDeck > 0 ? 0.3 : 0)).Take(15).ToList();
        var mainPool = usable.Where(e => e.Card.Category != "Legend").ToList();
        if (legendPool.Select(e => e.Card.BaseName).Distinct().Count() < 3)
            return Task.FromResult(new List<string> { "A deck needs 3 Legends with different names. Scan more Legends, then optimize again." });

        // Try every trio of Legends and keep the one whose RAM unlocks the strongest 40-card main deck.
        Dictionary<LibraryEntry, int> bestPlan = null;
        List<LibraryEntry> bestTrio = null;
        double bestScore = double.MinValue;
        for (int a = 0; a < legendPool.Count; a++)
            for (int b = a + 1; b < legendPool.Count; b++)
                for (int c = b + 1; c < legendPool.Count; c++)
                {
                    var trio = new List<LibraryEntry> { legendPool[a], legendPool[b], legendPool[c] };
                    if (trio.Select(t => t.Card.BaseName).Distinct().Count() < 3) continue;
                    var ram = RamOf(trio.Select(t => t.Card));
                    var (plan, score) = PickMain(mainPool.Where(e => RamOk(e.Card, ram)).ToList(), Own);
                    score += trio.Sum(t => t.Card.Quality + (t.InDeck > 0 ? 0.3 : 0));
                    if (score > bestScore) { bestScore = score; bestPlan = plan; bestTrio = trio; }
                }

        foreach (var t in bestTrio) bestPlan[t] = 1;
        var finalRam = RamOf(bestTrio.Select(t => t.Card));
        foreach (var e in all.Where(e => e.InDeck > 0 && !reasons.ContainsKey(e) && e.Card.Category != "Legend" && !RamOk(e.Card, finalRam)))
            reasons[e] = "not enough RAM in that color";

        var result = ApplyPlan(lib, bestPlan, reasons);
        int mainCount = bestPlan.Where(kv => kv.Key.Card.Category != "Legend").Sum(kv => kv.Value);
        result.Insert(0, $"Legends: {string.Join(", ", bestTrio.Select(t => t.Card.Name))}. " +
                         $"RAM: {string.Join(", ", Colors.Where(c => finalRam[c] > 0).Select(c => $"{c} {finalRam[c]}"))}. Main deck {mainCount} cards.");
        if (mainCount < 40) result.Insert(1, $"Your collection is {40 - mainCount} cards short of a 40-card main deck for these Legends. Scan more cards.");
        return Task.FromResult(result);
    }

    static (Dictionary<LibraryEntry, int> plan, double score) PickMain(List<LibraryEntry> pool, Func<LibraryEntry, int> own)
    {
        var plan = new Dictionary<LibraryEntry, int>();
        var byName = new Dictionary<string, int>();
        var curve = new int[4];
        var types = new Dictionary<string, int>();
        double score = 0;
        for (int n = 0; n < 40; n++)
        {
            LibraryEntry best = null;
            double bestS = double.MinValue;
            foreach (var e in pool)
            {
                int have = plan.GetValueOrDefault(e);
                if (have >= own(e) || byName.GetValueOrDefault(e.Card.Name) >= 3) continue;
                int b = Bucket(e.Card.ManaValue);
                double curveNeed = IdealCurve[b] * 40 - curve[b];
                double typeNeed = TypeTargets.GetValueOrDefault(e.Card.Category, 4) - types.GetValueOrDefault(e.Card.Category);
                double s = e.Card.Quality + (e.InDeck > 0 ? 0.25 : 0) - 0.04 * have
                           + (typeNeed > 0 ? 0.25 : -0.2)
                           + (curveNeed > 0 ? 0.2 : -0.3 - 0.05 * b);
                if (s > bestS) { bestS = s; best = e; }
            }
            if (best == null) break;
            plan[best] = plan.GetValueOrDefault(best) + 1;
            byName[best.Card.Name] = byName.GetValueOrDefault(best.Card.Name) + 1;
            curve[Bucket(best.Card.ManaValue)]++;
            types[best.Card.Category] = types.GetValueOrDefault(best.Card.Category) + 1;
            score += best.Card.Quality + 1; // the +1 rewards a fuller deck
        }
        return (plan, score);
    }
}

// ====================================================================== Gundam Card Game

public class GundamRules : DeckRules
{
    public override Game Game => Game.Gundam;
    public override string[] Formats => new[] { "Standard" };
    public override string CostHeader => "Lv / Cost";
    public override string CostText(Card c) => c.Category is "Resource" or "Token" ? "" : $"{c.Level} / {c.ManaValue:0}";

    static bool IsMain(Card c) => c.Category is "Unit" or "Pilot" or "Command" or "Base";
    static bool IsLegal(Card c) => c.Legality.GetValueOrDefault("standard", "legal") == "legal";
    public override string LegalityLabel(Card c, string f) => c.Category == "Token" ? "Token (not a deck card)" : IsLegal(c) ? "Legal" : "Not legal";

    static readonly string[] BucketNames = { "Lv 1-2", "Lv 3-4", "Lv 5-6", "Lv 7+" };
    static readonly double[] IdealCurve = { .25, .40, .25, .10 };
    static int Bucket(int level) => level <= 2 ? 0 : level <= 4 ? 1 : level <= 6 ? 2 : 3;
    static readonly Dictionary<string, int> TypeTargets = new() { ["Unit"] = 28, ["Pilot"] = 10, ["Command"] = 10, ["Base"] = 2 };

    /// <summary>A Unit links when a Pilot it names (or a Pilot with a trait it names) is in the deck.</summary>
    static bool Links(Card unit, IEnumerable<Card> pilots) =>
        unit.LinkRefs.Any(r => pilots.Any(p => TextUtil.Norm(p.Name) == TextUtil.Norm(r) ||
                                               p.Race.Split(", ").Any(t => TextUtil.Norm(t) == TextUtil.Norm(r))));

    public override DeckReport Analyze(Library lib, string format)
    {
        var r = new DeckReport();
        var deck = lib.Deck.ToList();
        var main = deck.Where(e => IsMain(e.Card)).ToList();
        var resources = deck.Where(e => e.Card.Category == "Resource").ToList();
        var tokens = deck.Where(e => e.Card.Category == "Token").ToList();
        int mainCount = main.Sum(e => e.InDeck), resourceCount = resources.Sum(e => e.InDeck);
        int Count(string t) => main.Where(e => e.Card.Category == t).Sum(e => e.InDeck);
        var colors = main.SelectMany(e => e.Card.Colors).Distinct().ToList();
        var curve = new int[4];
        foreach (var e in main) curve[Bucket(e.Card.Level)] += e.InDeck;
        double avgLevel = mainCount == 0 ? 0 : main.Sum(e => e.Card.Level * e.InDeck) / (double)mainCount;
        var pilots = main.Where(e => e.Card.Category == "Pilot").Select(e => e.Card).ToList();
        var units = main.Where(e => e.Card.Category == "Unit").ToList();
        int linked = units.Where(e => Links(e.Card, pilots)).Sum(e => e.InDeck);

        r.Stats.Add($"Deck: {mainCount} ({Count("Unit")} units, {Count("Pilot")} pilots, {Count("Command")} commands, {Count("Base")} bases)");
        r.Stats.Add($"Resource Deck: {resourceCount}");
        r.Stats.Add("Colors: " + (colors.Count == 0 ? "none" : string.Join(", ", colors)));
        r.Stats.Add($"Average level: {avgLevel:0.0}");
        r.Stats.Add("Level curve: " + string.Join("   ", Enumerable.Range(0, 4).Select(i => $"{BucketNames[i]}: {curve[i]}")));
        r.Stats.Add($"Units that can link with a Pilot in the deck: {linked} of {Count("Unit")}");

        if (mainCount + resourceCount == 0)
        {
            r.Error("The deck is empty. Add cards from the Collection tab, or press Optimize to build one.");
            return r;
        }
        if (mainCount != 50) r.Error($"A deck needs exactly 50 cards. You have {mainCount}.");
        if (resourceCount == 0) r.Warn("You also need a separate 10-card Resource Deck. Optimize adds one for you.");
        else if (resourceCount != 10) r.Error($"The Resource Deck needs exactly 10 Resource cards. You have {resourceCount}.");
        if (colors.Count > 2) r.Error($"A deck can use at most 2 colors. This one uses {colors.Count}: {string.Join(", ", colors)}.");
        foreach (var g in main.GroupBy(e => e.Card.Number).Where(g => g.Sum(e => e.InDeck) > 4))
            r.Error($"{g.First().Card.Name} ({g.Key}): {g.Sum(e => e.InDeck)} copies, the limit is 4 per card number.");
        foreach (var e in tokens)
            r.Error($"{e.Card.Name} is a token or EX card, which isn't built into a deck.");

        if (mainCount >= 30)
        {
            if (Count("Unit") < mainCount * 0.4) r.Warn($"Only {Count("Unit")} units. Units are how you break shields and win.");
            if ((double)curve[3] / mainCount > IdealCurve[3] + 0.08) r.Warn($"Top-heavy: {curve[3]} cards are Lv.7 or higher. You can only play a card once you have that many Resources.");
            if ((double)curve[0] / mainCount < IdealCurve[0] - 0.12) r.Tip($"Only {curve[0]} cards are Lv.2 or lower. Cheap cards keep your early turns busy.");
            if (Count("Pilot") == 0) r.Tip("No pilots. Pairing a Pilot adds AP and HP, and a matching Pilot lets a Unit attack the turn it's deployed.");
            else if (linked < Count("Unit") / 3) r.Tip("Few of your Units have their Link Pilot in the deck. Linked Units can attack right away.");
        }
        return r;
    }

    public override async Task<List<string>> OptimizeAsync(Library lib, string format, CancellationToken ct)
    {
        var all = lib.Entries.Values.ToList();
        var reasons = new Dictionary<LibraryEntry, string>();
        foreach (var e in all.Where(e => e.InDeck > 0 && e.Card.Category == "Token")) reasons[e] = "tokens aren't deck cards";
        var usable = all.Where(e => IsLegal(e.Card) && IsMain(e.Card) && (e.Owned > 0 || e.InDeck > 0)).ToList();
        int Own(LibraryEntry e) => Math.Max(e.Owned, e.InDeck);

        // Pick up to 2 colors: the deck's own colors if it has cards, otherwise the strongest colors in the collection.
        var weights = new Dictionary<string, double>();
        var seed = usable.Where(e => e.InDeck > 0).ToList();
        foreach (var e in seed.Count > 0 ? seed : usable)
            foreach (var col in e.Card.Colors)
                weights[col] = weights.GetValueOrDefault(col) + (seed.Count > 0 ? e.InDeck : e.Card.Quality * Math.Min(Own(e), 4));
        var colors = weights.OrderByDescending(kv => kv.Value).Take(2).Select(kv => kv.Key).ToHashSet();
        bool Fits(Card c) => c.Colors.Count == 0 || c.Colors.All(colors.Contains);
        foreach (var e in all.Where(e => e.InDeck > 0 && !reasons.ContainsKey(e) && IsMain(e.Card) && !Fits(e.Card)))
            reasons[e] = "outside the deck's 2 colors";

        var pool = usable.Where(e => Fits(e.Card)).ToList();
        var poolPilots = pool.Where(e => e.Card.Category == "Pilot").Select(e => e.Card).ToList();
        var poolUnits = pool.Where(e => e.Card.Category == "Unit").Select(e => e.Card).ToList();

        var plan = new Dictionary<LibraryEntry, int>();
        var byNumber = new Dictionary<string, int>();
        var curve = new int[4];
        var types = new Dictionary<string, int>();
        for (int n = 0; n < 50; n++)
        {
            LibraryEntry best = null;
            double bestS = double.MinValue;
            foreach (var e in pool)
            {
                int have = plan.GetValueOrDefault(e);
                if (have >= Own(e) || byNumber.GetValueOrDefault(e.Card.Number) >= 4) continue;
                int b = Bucket(e.Card.Level);
                double curveNeed = IdealCurve[b] * 50 - curve[b];
                double typeNeed = TypeTargets.GetValueOrDefault(e.Card.Category, 2) - types.GetValueOrDefault(e.Card.Category);
                double link = e.Card.Category == "Unit" && Links(e.Card, poolPilots) ? 0.15
                            : e.Card.Category == "Pilot" && poolUnits.Any(u => Links(u, new[] { e.Card })) ? 0.15 : 0;
                double s = e.Card.Quality + link + (e.InDeck > 0 ? 0.25 : 0) - 0.04 * have
                           + (typeNeed > 0 ? 0.25 : -0.25)
                           + (curveNeed > 0 ? 0.2 : -0.3 - 0.05 * b);
                if (s > bestS) { bestS = s; best = e; }
            }
            if (best == null) break;
            plan[best] = plan.GetValueOrDefault(best) + 1;
            byNumber[best.Card.Number] = byNumber.GetValueOrDefault(best.Card.Number) + 1;
            curve[Bucket(best.Card.Level)]++;
            types[best.Card.Category] = types.GetValueOrDefault(best.Card.Category) + 1;
        }
        int mainCount = plan.Values.Sum();

        // Resource Deck: 10 Resource cards, using ones you own first.
        var log = new List<string>();
        int need = 10;
        foreach (var e in all.Where(e => e.Card.Category == "Resource" && e.Owned > 0).OrderByDescending(e => e.InDeck))
        {
            int take = Math.Min(need, e.Owned);
            if (take <= 0) break;
            plan[e] = take;
            need -= take;
        }
        if (need > 0)
        {
            try
            {
                var catalogue = await GundamSource.CatalogueAsync(false, ct);
                var resource = catalogue.Where(c => c.Category == "Resource").OrderBy(c => c.Id.Contains("_p")).ThenBy(c => c.Id).FirstOrDefault();
                if (resource != null)
                {
                    var entry = all.FirstOrDefault(e => e.Card.Id == resource.Id) ?? lib.Add(resource, 0, 0);
                    plan[entry] = plan.GetValueOrDefault(entry) + need;
                }
            }
            catch { log.Add("Could not fetch a Resource card; add 10 Resource cards by hand."); }
        }

        var result = ApplyPlan(lib, plan, reasons);
        result.Insert(0, $"Colors: {(colors.Count == 0 ? "none" : string.Join(", ", colors))}. Deck {mainCount} cards " +
                         $"({types.GetValueOrDefault("Unit")} units, {types.GetValueOrDefault("Pilot")} pilots, " +
                         $"{types.GetValueOrDefault("Command")} commands, {types.GetValueOrDefault("Base")} bases), plus a 10-card Resource Deck.");
        if (mainCount < 50) result.Insert(1, $"Your collection is {50 - mainCount} cards short of a 50-card deck in these colors. Scan more cards.");
        result.InsertRange(1, log);
        return result;
    }
}
