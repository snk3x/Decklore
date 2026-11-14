using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using global::Windows.Graphics.Imaging;
using global::Windows.Media.Ocr;

namespace TcgScanner;

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

/// <summary>Reads card text with the OCR engine built into Windows 10/11; the shared CardText does the interpreting.</summary>
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
        var full = await RecognizeAsync(card, 1400);

        // The name sits in the top part of every supported game's card. Read that band again, enlarged.
        int bandH = Math.Max(8, (int)(card.Height * 0.2));
        using var band = card.Clone(new Rectangle(0, 0, card.Width, bandH), card.PixelFormat);
        var top = await RecognizeAsync(band, 1800);

        // Set codes and collector numbers are tiny: read the lower part of the card again, enlarged.
        int y0 = (int)(card.Height * 0.62);
        using var lower = card.Clone(new Rectangle(0, y0, card.Width, card.Height - y0), card.PixelFormat);
        var low = await RecognizeAsync(lower, 2200);

        // The very bottom strip holds the collector number on Pokémon and Magic cards: enlarge it a lot.
        int y1 = (int)(card.Height * 0.9);
        using var strip = card.Clone(new Rectangle(0, y1, card.Width, card.Height - y1), card.PixelFormat);
        using var stripHi = HighContrast(strip);
        var fine = await RecognizeAsync(stripHi, Math.Max(2400, card.Width * 4));

        return CardText.Analyze(full, top, low, fine);
    }

    static List<TextLine> ToLines(OcrResult r) => r.Lines
        .Select(l => new TextLine(l.Text, l.Words
            .Select(w => new TextWord(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height))
            .ToList()))
        .ToList();

    static async Task<List<TextLine>> RecognizeAsync(Bitmap bmp, int targetWidth)
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
            return ToLines(await Engine.RecognizeAsync(sb));
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
