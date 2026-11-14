using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using global::Windows.Graphics.Imaging;
using global::Windows.Media.Ocr;

namespace TcgScanner;

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

    public PrintHint HintFor(Game g) => g switch
    {
        Game.Magic => MagicHint,
        Game.Pokemon => PokemonHint,
        Game.YuGiOh => YugiohHint,
        Game.Gundam => GundamHint,
        _ => new PrintHint { Number = CollectorNumber }
    };
}

/// <summary>Downloads card images, including WebP, which GDI+ cannot open on its own.</summary>
public static class CardImage
{
    public static async Task<Bitmap> LoadAsync(string url, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("image/*");
        using var resp = await Http.Client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        return await DecodeAsync(bytes);
    }

    public static async Task<Bitmap> DecodeAsync(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var tmp = new Bitmap(ms);
            return new Bitmap(tmp);
        }
        catch (ArgumentException)
        {
            // Fall back to the Windows imaging codecs (WebP and others), then hand GDI+ a PNG.
            using var input = new MemoryStream(bytes);
            var decoder = await BitmapDecoder.CreateAsync(input.AsRandomAccessStream());
            using var sb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            using var output = new MemoryStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output.AsRandomAccessStream());
            encoder.SetSoftwareBitmap(sb);
            await encoder.FlushAsync();
            output.Position = 0;
            using var tmp = new Bitmap(output);
            return new Bitmap(tmp);
        }
    }
}

/// <summary>Reads card text with the OCR engine built into Windows 10/11.</summary>
public static class CardOcr
{
    static OcrEngine _engine;

    static OcrEngine Engine
    {
        get
        {
            if (_engine != null) return _engine;
            var en = new global::Windows.Globalization.Language("en-US");
            _engine = OcrEngine.IsLanguageSupported(en)
                ? OcrEngine.TryCreateFromLanguage(en)
                : OcrEngine.TryCreateFromUserProfileLanguages();
            return _engine ?? throw new InvalidOperationException(
                "Windows text recognition is not available. Add English in Settings > Time & language > Language & region.");
        }
    }

    public static async Task<ScanResult> ReadAsync(Bitmap card)
    {
        var result = new ScanResult();
        var full = await RecognizeAsync(card, 1400);
        result.FullText = full.Text ?? "";

        // The name sits in the top part of every supported game's card. Read that band again, enlarged.
        int bandH = Math.Max(8, (int)(card.Height * 0.2));
        using var band = card.Clone(new Rectangle(0, 0, card.Width, bandH), card.PixelFormat);
        var top = await RecognizeAsync(band, 1800);

        var candidates = new List<string>();
        foreach (var line in top.Lines.OrderBy(l => l.Words.Min(w => w.BoundingRect.Y)))
            candidates.AddRange(Segments(line).Select(Clean));

        // Fallback: every line of the full card, top to bottom.
        foreach (var line in full.Lines.OrderBy(l => l.Words.Min(w => w.BoundingRect.Y)).Take(8))
            candidates.AddRange(Segments(line).Select(Clean));

        result.NameCandidates = candidates
            .Where(c => c.Count(char.IsLetter) >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        // Set codes and collector numbers are tiny: read the lower part of the card again, enlarged.
        int y0 = (int)(card.Height * 0.62);
        using var lower = card.Clone(new Rectangle(0, y0, card.Width, card.Height - y0), card.PixelFormat);
        var low = await RecognizeAsync(lower, 2200);
        // The very bottom strip holds the collector number on Pokémon and Magic cards: enlarge it a lot.
        int y1 = (int)(card.Height * 0.9);
        using var strip = card.Clone(new Rectangle(0, y1, card.Width, card.Height - y1), card.PixelFormat);
        using var stripHi = HighContrast(strip);
        var fine = await RecognizeAsync(stripHi, Math.Max(2400, card.Width * 4));
        string printText = string.Join("\n", fine.Lines.Select(l => l.Text)) + "\n" +
                           string.Join("\n", low.Lines.Select(l => l.Text)) + "\n" +
                           string.Join("\n", full.Lines.Select(l => l.Text));
        result.PrintText = printText;
        ParsePrinting(result, printText.ToUpperInvariant());

        GuessGame(result);
        return result;
    }

    /// <summary>Turns letters OCR commonly confuses with digits back into digits.</summary>
    static string Digits(string s) => new(s.Select(ch => ch switch
    {
        'O' or 'D' or 'Q' => '0', 'I' or 'L' => '1', 'Z' => '2', 'S' => '5', 'G' => '6', 'B' => '8', _ => ch
    }).ToArray());

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

        // Gundam: "GD01-001", "ST04-012", "EB01-061". OCR often reads 0 as O and 1 as I or L, so fix the digit positions.
        var gcg = Regex.Match(t, @"([A-Z]{2})([0-9OILZSGBDQ]{2})\s*-\s*([0-9OILZSGBDQ]{3})");
        if (gcg.Success)
            r.GundamHint.Number = $"{gcg.Groups[1].Value}{Digits(gcg.Groups[2].Value)}-{Digits(gcg.Groups[3].Value)}";

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
                                     @"CYBERPUNK", @"WEIRDCO", @"CD PROJEKT", @"\{(PLAY|SPEND|CALL)\}", @"NIGHT CITY", @"\bRIVAL\b"),
            [Game.Gundam] = Count(@"GUNDAM", @"BANDAI", @"SUNRISE", @"\bDEPLOY\b", @"WHEN PAIRED", @"\bBURST\b", @"\bBLOCKER\b",
                                  @"\bREPAIR\b", @"\bBREACH\b", @"\bLV\.\s?\d", @"ACTIVATE\W{0,2}MAIN", @"EARTH FEDERATION", @"ZEON",
                                  @"\bPILOT\b", @"\b[A-Z]{2}[0-9OIL]{2}\s?-\s?[0-9OILSG]{3}", @"\bCOST\b", @"\bTRAIT", @"\bLINK\b"),
        };
        var ordered = scores.OrderByDescending(kv => kv.Value).ToList();
        r.GameConfident = ordered[0].Value >= 2 && ordered[0].Value > ordered[1].Value;
        r.GameGuesses = ordered.Select(kv => kv.Key).ToList();
    }

    /// <summary>Split a line where words are far apart (for example name on the left, cost or HP on the right).</summary>
    static IEnumerable<string> Segments(OcrLine line)
    {
        var words = line.Words.ToList();
        if (words.Count == 0) yield break;
        double h = words.Average(w => w.BoundingRect.Height);
        var seg = new List<string> { words[0].Text };
        double right = words[0].BoundingRect.X + words[0].BoundingRect.Width;
        for (int i = 1; i < words.Count; i++)
        {
            var b = words[i].BoundingRect;
            if (b.X - right > h * 1.8)
            {
                yield return string.Join(" ", seg);
                seg.Clear();
            }
            seg.Add(words[i].Text);
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

    static async Task<OcrResult> RecognizeAsync(Bitmap bmp, int targetWidth)
    {
        double scale = bmp.Width < targetWidth ? (double)targetWidth / bmp.Width : 1;
        double max = OcrEngine.MaxImageDimension;
        scale = Math.Min(scale, Math.Min(max / bmp.Width, max / bmp.Height));

        Bitmap work = bmp;
        bool owns = false;
        if (Math.Abs(scale - 1) > 0.01)
        {
            work = Resize(bmp, Math.Max(1, (int)(bmp.Width * scale)), Math.Max(1, (int)(bmp.Height * scale)));
            owns = true;
        }
        try
        {
            using var ms = new MemoryStream();
            work.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            using var ras = ms.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(ras);
            using var sb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            return await Engine.RecognizeAsync(sb);
        }
        finally
        {
            if (owns) work.Dispose();
        }
    }

    /// <summary>Grayscale with boosted contrast, which helps tiny print.</summary>
    static Bitmap HighContrast(Bitmap src)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        const float k = 1.8f, t = -0.4f;
        var m = new ColorMatrix(new[]
        {
            new[] { 0.299f * k, 0.299f * k, 0.299f * k, 0, 0 },
            new[] { 0.587f * k, 0.587f * k, 0.587f * k, 0, 0 },
            new[] { 0.114f * k, 0.114f * k, 0.114f * k, 0, 0 },
            new[] { 0f, 0, 0, 1, 0 },
            new[] { t, t, t, 0, 1 },
        });
        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(m);
        using var g = Graphics.FromImage(dst);
        g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        return dst;
    }

    static Bitmap Resize(Bitmap src, int w, int h)
    {
        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, w, h);
        return dst;
    }
}
