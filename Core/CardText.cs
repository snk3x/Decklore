using System.Text.RegularExpressions;

namespace TcgScanner;

/// <summary>A word read from a card image, with its position in that image.</summary>
public record TextWord(string Text, double X, double Y, double Width, double Height);

/// <summary>A line of words read from a card image.</summary>
public record TextLine(string Text, IReadOnlyList<TextWord> Words)
{
    public double Top => Words.Count == 0 ? 0 : Words.Min(w => w.Y);
}

public class ScanResult
{
    public List<Game> GameGuesses { get; set; } = new();
    public bool GameConfident { get; set; }
    public List<string> NameCandidates { get; set; } = new();
    public string CollectorNumber { get; set; }
    public string FullText { get; set; } = "";
    public string PrintText { get; set; } = "";
    public PrintHint MagicHint { get; set; } = new();
    public PrintHint PokemonHint { get; set; } = new();
    public PrintHint YugiohHint { get; set; } = new();
    public PrintHint GundamHint { get; set; } = new();
    /// <summary>How many typical card-game clues were found ("Creature", "HP", "ATK/", "Cost"...), per game.</summary>
    public Dictionary<Game, int> GameScores { get; set; } = new();
    /// <summary>The strongest game evidence; ordinary text (labels, posters) scores 0 or 1.</summary>
    public int CardEvidence => GameScores.Count == 0 ? 0 : GameScores.Values.Max();

    public PrintHint HintFor(Game g) => g switch
    {
        Game.Magic => MagicHint,
        Game.Pokemon => PokemonHint,
        Game.YuGiOh => YugiohHint,
        Game.Gundam => GundamHint,
        _ => new PrintHint { Number = CollectorNumber }
    };
}

/// <summary>
/// Picks the card a scan shows. Instead of taking the first name candidate that matches anything, it looks up
/// several and keeps the card whose name (and subtitle) best fits everything that was read.
/// </summary>
public static class CardMatcher
{
    public static async Task<Card> FindBestAsync(ICardSource source, ScanResult scan, Game game, Action<string> progress = null,
                                                 CancellationToken ct = default)
    {
        var candidates = scan.NameCandidates.Take(6).ToList();
        var hint = scan.HintFor(game);
        Card best = null;
        double bestScore = double.MinValue;
        var seen = new HashSet<string>();
        foreach (var cand in candidates)
        {
            progress?.Invoke(cand);
            var card = await source.FindAsync(cand, hint, ct);
            if (card == null || !seen.Add(card.Id + "|" + card.Number)) continue;
            // A card confirmed by its printed set code or number is the answer.
            if (card.ExactPrinting) return card;
            double score = Score(card, candidates);
            if (score > bestScore) { bestScore = score; best = card; }
            if (score >= 0.92) break;
        }
        return best;
    }

    /// <summary>How well a card's name parts (name, and subtitle after ':') match the text read from the card, 0 to 1.</summary>
    public static double Score(Card card, IReadOnlyList<string> candidates)
    {
        var parts = card.Name.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parts.Count == 0 || candidates.Count == 0) return 0;
        double PartScore(string part) => candidates.Max(c =>
        {
            double r = TextUtil.Ratio(part, c);
            string p = TextUtil.Norm(part), n = TextUtil.Norm(c);
            if (n.Length >= 4 && p.Contains(n)) r = Math.Max(r, 0.5 + 0.5 * n.Length / p.Length);
            if (p.Length >= 4 && n.Contains(p)) r = Math.Max(r, 0.9);
            return r;
        });
        return parts.Average(PartScore);
    }
}

/// <summary>
/// Turns the text read from a card (by Windows OCR or Android ML Kit) into a scan result:
/// which game it is, name candidates, and printing details. Each platform reads four regions:
/// the whole card, the top band (name), the lower part, and the enlarged bottom strip (fine print).
/// </summary>
public static class CardText
{
    public static ScanResult Analyze(IReadOnlyList<TextLine> full, IReadOnlyList<TextLine> top,
                                     IReadOnlyList<TextLine> lower, IReadOnlyList<TextLine> strip)
    {
        var result = new ScanResult { FullText = string.Join("\n", full.Select(l => l.Text)) };

        var candidates = new List<string>();
        foreach (var line in top.OrderBy(l => l.Top))
            candidates.AddRange(Segments(line).Select(Clean));
        // Fallback: every line of the full card, top to bottom.
        foreach (var line in full.OrderBy(l => l.Top).Take(8))
            candidates.AddRange(Segments(line).Select(Clean));
        result.NameCandidates = candidates
            .Where(c => c.Count(char.IsLetter) >= 3 && !IsLabel(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        result.PrintText = string.Join("\n", strip.Select(l => l.Text)) + "\n" +
                           string.Join("\n", lower.Select(l => l.Text)) + "\n" +
                           result.FullText;
        ParsePrinting(result, result.PrintText.ToUpperInvariant());

        // Gundam card numbers are printed at the top; prefer a number read there over a look-alike in effect text.
        var topNumber = GundamNumber(string.Join("\n", top.Select(l => l.Text)).ToUpperInvariant());
        if (topNumber != null) result.GundamHint.Number = topNumber;
        GuessGame(result);
        return result;
    }

    /// <summary>Turns letters OCR commonly confuses with digits back into digits.</summary>
    static string Digits(string s) => new(s.Select(ch => ch switch
    {
        'O' or 'D' or 'Q' => '0', 'I' or 'L' => '1', 'Z' => '2', 'S' => '5', 'G' => '6', 'B' => '8', _ => ch
    }).ToArray());

    /// <summary>Gundam: "GD01-001", "ST04-012", "EB01-061". OCR often reads 0 as O and 1 as I or L, so fix the digit positions.</summary>
    static string GundamNumber(string t)
    {
        var m = Regex.Match(t, @"\b([A-Z]{2})([0-9OILZSGBDQ]{2})\s*-\s*([0-9OILZSGBDQ]{3})");
        return m.Success ? $"{m.Groups[1].Value}{Digits(m.Groups[2].Value)}-{Digits(m.Groups[3].Value)}" : null;
    }

    /// <summary>Card-type labels and printed codes that OCR picks up near the name but never are the name.</summary>
    static readonly HashSet<string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        "trap", "trap card", "spell", "spell card", "monster", "legend", "unit", "program", "gear", "cost", "pilot",
        "command", "base", "resource", "stag", "stage", "basic", "trainer", "supporter", "item", "energy", "pokemon tool",
        "pokémon tool", "creature", "instant", "sorcery", "land", "enchantment", "artifact", "token", "ex", "cos", "cosr",
        "a cost", "link", "trait", "lv", "ap", "hp",
    };

    static bool IsLabel(string c) =>
        Labels.Contains(c.Trim()) ||
        Regex.IsMatch(c, @"^[A-Za-z]{1,4}\d+\s*-\s*\w+$");  // a printed card code such as "GD05-017unO"

    static void ParsePrinting(ScanResult r, string t)
    {
        // "025/165" (Pokémon, older Magic)
        var slash = Regex.Match(t, @"(?<!\d)(\d{1,3})\s*/\s*(\d{1,3})(?!\d)");
        if (slash.Success)
        {
            r.CollectorNumber = slash.Groups[1].Value.TrimStart('0');
            r.PokemonHint.Number = r.CollectorNumber;
            r.PokemonHint.Total = slash.Groups[2].Value.TrimStart('0');
        }

        // Pokémon promos: "SWSH234", "SM04", "SVP 085"
        var promo = Regex.Match(t, @"\b(SWSH|SVP|SM|XY|BW|HGSS|DP)\s?(\d{2,3})\b");
        if (promo.Success && string.IsNullOrEmpty(r.PokemonHint.Number))
            r.PokemonHint.Number = promo.Groups[1].Value + promo.Groups[2].Value;

        r.GundamHint.Number = GundamNumber(t);

        // Yu-Gi-Oh: "LOB-EN001", "MP22-EN123", "SDK-001"
        var ygo = Regex.Match(t, @"\b([A-Z0-9]{2,5})\s*-\s*(EN|E|NA)?\s*(\d{3})\b");
        if (ygo.Success)
            r.YugiohHint.SetCode = $"{ygo.Groups[1].Value}-{ygo.Groups[2].Value}{ygo.Groups[3].Value}";

        // Magic: "M21 • EN" next to the collector number "0157" or "157/274"
        var set = Regex.Match(t, @"\b([A-Z0-9]{3,5})\s*[•·\*\.\-]?\s*EN\b");
        if (set.Success && !Regex.IsMatch(set.Groups[1].Value, @"^\d+$")) r.MagicHint.SetCode = set.Groups[1].Value;
        var mnum = slash.Success ? slash.Groups[1] : null;
        mnum ??= Regex.Match(t, @"\b(\d{1,4})\s+[CURMLSTP]\b") is { Success: true } m1 ? m1.Groups[1] : null;
        mnum ??= Regex.Match(t, @"\b(0\d{3})\b") is { Success: true } m2 ? m2.Groups[1] : null;
        if (mnum != null) r.MagicHint.Number = mnum.Value.TrimStart('0');
    }

    static void GuessGame(ScanResult r)
    {
        string t = r.FullText.ToUpperInvariant();
        int Count(params string[] patterns) => patterns.Count(p => Regex.IsMatch(t, p));

        var scores = new Dictionary<Game, int>
        {
            [Game.YuGiOh] = Count(@"ATK\s*/", @"DEF\s*/", @"\[\s*SPELL CARD", @"\[\s*TRAP CARD", @"LINK\s*-\s*\d", @"/\s*EFFECT\s*\]",
                                  @"KONAMI", @"\[(SPELLCASTER|WARRIOR|DRAGON|FIEND|MACHINE|ZOMBIE|BEAST)"),
            [Game.Pokemon] = Count(@"\bHP\s*\d{2,3}", @"\d{2,3}\s*HP\b", @"WEAKNESS", @"RESISTANCE", @"RETREAT", @"EVOLVES FROM",
                                   @"POK[EÉ]MON", @"\bSUPPORTER\b", @"\bTRAINER\b"),
            [Game.Magic] = Count(@"\bCREATURE\b", @"\bINSTANT\b", @"\bSORCERY\b", @"\bENCHANTMENT\b", @"\bARTIFACT\b", @"\bPLANESWALKER\b",
                                 @"ILLUS\.", @"WIZARDS OF THE COAST", @"\bLEGENDARY\b", @"\bFLYING\b", @"\bTRAMPLE\b"),
            [Game.Cyberpunk] = Count(@"\bRAM\b", @"€\s?\$", @"\bEDDIES?\b", @"GO SOLO", @"\bCALL\}", @"\bGIGS?\b", @"NETRUNNER",
                                     @"CYBERPUNK", @"WEIRDCO", @"CD PROJEKT", @"\{(PLAY|SPEND|CALL)\}", @"NIGHT CITY", @"\bRIVAL\b",
                                     @"\b(UNIT|PROGRAM|GEAR|LEGEND)\b", @"ILLUST\."),
            [Game.Gundam] = Count(@"GUNDAM", @"BANDAI", @"SUNRISE", @"\bDEPLOY\b", @"WHEN PAIRED", @"\bBURST\b", @"\bBLOCKER\b",
                                  @"\bREPAIR\b", @"\bBREACH\b", @"\bLV\.\s?\d", @"ACTIVATE\W{0,2}MAIN", @"EARTH FEDERATION", @"ZEON",
                                  @"\bPILOT\b", @"\b[A-Z]{2}[0-9OIL]{2}\s?-\s?[0-9OILSG]{3}", @"\bCOST\b", @"\bTRAIT", @"\bLINK\b"),
        };
        r.GameScores = scores;
        var ordered = scores.OrderByDescending(kv => kv.Value).ToList();
        r.GameConfident = ordered[0].Value >= 2 && ordered[0].Value > ordered[1].Value;
        r.GameGuesses = ordered.Select(kv => kv.Key).ToList();
    }

    /// <summary>Split a line where words are far apart (for example name on the left, cost or HP on the right).</summary>
    static IEnumerable<string> Segments(TextLine line)
    {
        var words = line.Words;
        if (words.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(line.Text)) yield return line.Text;
            yield break;
        }
        double h = words.Average(w => w.Height);
        var seg = new List<string> { words[0].Text };
        double right = words[0].X + words[0].Width;
        for (int i = 1; i < words.Count; i++)
        {
            var b = words[i];
            if (b.X - right > h * 1.8)
            {
                yield return string.Join(" ", seg);
                seg.Clear();
            }
            seg.Add(b.Text);
            right = b.X + b.Width;
        }
        yield return string.Join(" ", seg);
    }

    static string Clean(string s)
    {
        s = Regex.Replace(s, @"(?i)evolves from.*$", " ");
        s = Regex.Replace(s, @"(?i)\b(basic|stage\s*[12]|stage|hp)\b", " ");
        s = Regex.Replace(s, @"[^\p{L}\p{N}' ,\-\.!&:]", " ");
        s = Regex.Replace(s, @"\b\d+\b", " ");
        s = Regex.Replace(s, @"\s+", " ");
        return s.Trim(' ', '-', '.', ',', ':', '\'');
    }
}
