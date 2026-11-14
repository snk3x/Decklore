using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TcgScanner;

static class Http
{
    public static readonly HttpClient Client = Create();

    static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("TCGDeckScanner/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
    }

    /// <summary>Returns null for "not found" style responses; throws on network failure.</summary>
    public static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var resp = await Client.GetAsync(url, ct);
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return null;
            bool retryable = (int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests;
            if (retryable && attempt < 3)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1 + attempt * 2);
                await Task.Delay(wait > TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : wait, ct);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(s, cancellationToken: ct);
        }
    }
}

static class Json
{
    public static JsonElement O(this JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) ? v : default;

    public static string S(this JsonElement e, string p)
    {
        var v = e.O(p);
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => ""
        };
    }

    public static double D(this JsonElement e, string p)
    {
        var v = e.O(p);
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
        return 0;
    }

    public static IEnumerable<JsonElement> A(this JsonElement e, string p)
    {
        var v = e.O(p);
        return v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : Enumerable.Empty<JsonElement>();
    }

    public static List<string> Strs(this JsonElement e, string p) =>
        e.A(p).Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToList();

    public static bool True(this JsonElement e, string p) => e.O(p).ValueKind == JsonValueKind.True;
}

public interface ICardSource
{
    Game Game { get; }
    /// <summary>Find a card by (possibly misspelled) name. The hint narrows it to one printing when possible.</summary>
    Task<Card> FindAsync(string name, PrintHint hint, CancellationToken ct = default);
    /// <summary>Fetch the same printing again, for fresh prices and legality.</summary>
    Task<Card> RefreshAsync(Card card, CancellationToken ct = default);
    /// <summary>Every printing of this card. Entries may be partial; pass the chosen one to RefreshAsync.</summary>
    Task<List<Card>> PrintingsAsync(Card card, CancellationToken ct = default);
}

static class TextUtil
{
    public static string Norm(string s)
    {
        var d = (s ?? "").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Compare codes the way OCR reads them: case, spaces and O/0 mix-ups ignored.</summary>
    public static string Code(string s) =>
        Regex.Replace((s ?? "").ToUpperInvariant(), @"\s", "").Replace('O', '0').Replace('I', '1');

    /// <summary>True when two names are close (OCR typos allowed), or one contains the other.</summary>
    public static bool Similar(string a, string b)
    {
        a = Norm(a); b = Norm(b);
        if (a.Length == 0 || b.Length == 0) return false;
        string front = a.Split(" // ")[0];
        if (a.Contains(b) || b.Contains(front)) return true;
        return Ratio(front, b) >= 0.6;
    }

    /// <summary>1.0 for identical strings, falling toward 0 as edits are needed.</summary>
    public static double Ratio(string a, string b)
    {
        a = Norm(a); b = Norm(b);
        if (a.Length == 0 || b.Length == 0) return 0;
        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return 1 - (double)d[a.Length, b.Length] / Math.Max(a.Length, b.Length);
    }

    public static string Num(string s) => (s ?? "").Trim().TrimStart('0').ToUpperInvariant();
}

// ---------------------------------------------------------------- Magic (Scryfall)

public class ScryfallSource : ICardSource
{
    public Game Game => Game.Magic;

    public async Task<Card> FindAsync(string name, PrintHint hint, CancellationToken ct = default)
    {
        string num = TextUtil.Num(hint?.Number);
        // Set code plus collector number names one exact card. Trust it when the name roughly agrees,
        // because stylized frames can make the name itself hard to read.
        if (!string.IsNullOrEmpty(hint?.SetCode) && num != "")
        {
            var exact = await Get($"https://api.scryfall.com/cards/{Uri.EscapeDataString(hint.SetCode.ToLowerInvariant())}/{Uri.EscapeDataString(num.ToLowerInvariant())}", ct);
            if (exact != null && TextUtil.Similar(exact.Name, name))
            {
                exact.ExactPrinting = true;
                return exact;
            }
        }

        var card = await Get("https://api.scryfall.com/cards/named?fuzzy=" + Uri.EscapeDataString(name.Trim()), ct);
        if (card == null || hint == null || hint.IsEmpty) return card;

        var prints = await PrintingsAsync(card, ct);
        Card pick = null;
        if (!string.IsNullOrEmpty(hint.SetCode))
            pick = prints.Where(p => TextUtil.Code(p.SetCode) == TextUtil.Code(hint.SetCode))
                         .OrderBy(p => TextUtil.Num(p.Number) == num ? 0 : 1).FirstOrDefault();
        if (pick == null && num != "")
        {
            var byNum = prints.Where(p => TextUtil.Num(p.Number) == num).ToList();
            if (byNum.Count == 1) pick = byNum[0];
        }
        if (pick == null) return card;
        pick.ExactPrinting = true;
        return pick;
    }

    public Task<Card> ExactAsync(string name, CancellationToken ct = default) =>
        Get("https://api.scryfall.com/cards/named?exact=" + Uri.EscapeDataString(name), ct);

    public async Task<Card> RefreshAsync(Card card, CancellationToken ct = default)
    {
        var c = await Get("https://api.scryfall.com/cards/" + Uri.EscapeDataString(card.Id), ct);
        if (c != null) c.ExactPrinting = card.ExactPrinting;
        return c;
    }

    public async Task<List<Card>> PrintingsAsync(Card card, CancellationToken ct = default)
    {
        var list = new List<Card>();
        string url = "https://api.scryfall.com/cards/search?unique=prints&order=released&dir=desc&q=" +
                     Uri.EscapeDataString($"!\"{card.Name}\"");
        for (int page = 0; page < 4 && !string.IsNullOrEmpty(url); page++)
        {
            using var doc = await Http.GetJsonAsync(url, ct);
            await Task.Delay(100, ct);
            if (doc == null) break;
            list.AddRange(doc.RootElement.A("data").Select(Parse));
            url = doc.RootElement.True("has_more") ? doc.RootElement.S("next_page") : null;
        }
        return list;
    }

    static async Task<Card> Get(string url, CancellationToken ct)
    {
        using var doc = await Http.GetJsonAsync(url, ct);
        await Task.Delay(100, ct); // Scryfall asks for 50-100 ms between requests
        return doc == null ? null : Parse(doc.RootElement);
    }

    static Card Parse(JsonElement r)
    {
        var faces = r.A("card_faces").ToList();
        var front = faces.Count > 0 ? faces[0] : r;
        string type = r.S("type_line");
        string frontType = type.Split(" // ")[0];
        string oracle = r.S("oracle_text");
        if (oracle == "" && faces.Count > 0) oracle = string.Join("\n//\n", faces.Select(f => f.S("oracle_text")));
        string cost = r.S("mana_cost");
        if (cost == "") cost = front.S("mana_cost");
        var img = r.O("image_uris");
        if (img.ValueKind != JsonValueKind.Object) img = front.O("image_uris");

        var c = new Card
        {
            Game = Game.Magic,
            Id = r.S("id"),
            Name = r.S("name"),
            TypeLine = type,
            Category = Category(frontType),
            ManaValue = r.D("cmc"),
            ManaCost = cost,
            Colors = r.Strs("color_identity"),
            ProducedMana = r.Strs("produced_mana"),
            Text = oracle,
            ImageUrl = img.S("normal"),
            Rarity = r.S("rarity"),
            SetName = r.S("set_name"),
            SetCode = r.S("set").ToUpperInvariant(),
            Number = r.S("collector_number"),
        };
        var legal = r.O("legalities");
        if (legal.ValueKind == JsonValueKind.Object)
            foreach (var p in legal.EnumerateObject())
                c.Legality[p.Name.ToLowerInvariant()] = p.Value.GetString() ?? "not_legal";

        c.UnlimitedCopies = type.Contains("Basic Land") ||
                            oracle.Contains("A deck can have any number of cards named", StringComparison.OrdinalIgnoreCase);
        c.IsLegendaryCreature = (frontType.Contains("Legendary") && frontType.Contains("Creature")) ||
                                oracle.Contains("can be your commander", StringComparison.OrdinalIgnoreCase);
        // EDHREC rank is a good proxy for how often a card is played. Lower rank = better.
        double rank = r.D("edhrec_rank");
        c.Quality = rank <= 0 ? 0.15 : Math.Clamp(1 - Math.Sqrt(rank / 30000.0), 0.02, 1);

        // Scryfall prices are TCGplayer market prices for this exact printing.
        var prices = r.O("prices");
        foreach (var (key, label) in new[] { ("usd", "TCGplayer market"), ("usd_foil", "TCGplayer market, foil"), ("usd_etched", "TCGplayer market, etched") })
        {
            double v = prices.D(key);
            if (v <= 0) continue;
            c.PriceUsd = v;
            c.PriceSource = label;
            break;
        }
        c.PriceUpdated = DateTime.Now;
        return c;
    }

    static string Category(string t)
    {
        foreach (var k in new[] { "Land", "Creature", "Planeswalker", "Battle", "Instant", "Sorcery", "Artifact", "Enchantment" })
            if (t.Contains(k)) return k;
        return "Other";
    }
}

// ---------------------------------------------------------------- Pokémon (TCGdex)

public class TcgdexSource : ICardSource
{
    const string Base = "https://api.tcgdex.net/v2/en/";
    public Game Game => Game.Pokemon;

    record SetInfo(int Order, string Name, int Official, int Total);
    static Dictionary<string, SetInfo> _sets;

    static async Task<Dictionary<string, SetInfo>> SetsAsync(CancellationToken ct)
    {
        if (_sets != null) return _sets;
        var d = new Dictionary<string, SetInfo>(StringComparer.OrdinalIgnoreCase);
        using var doc = await Http.GetJsonAsync(Base + "sets", ct);
        if (doc != null && doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                var cc = s.O("cardCount");
                d[s.S("id")] = new SetInfo(i++, s.S("name"), (int)cc.D("official"), (int)cc.D("total"));
            }
        }
        return _sets = d;
    }

    static string SetIdOf(string cardId)
    {
        int dash = cardId.LastIndexOf('-');
        return dash > 0 ? cardId[..dash] : cardId;
    }

    public async Task<Card> FindAsync(string name, PrintHint hint, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length < 3) return null;
        var items = await ListAsync(name, ct);
        if (items.Count == 0) return null;

        var exact = items.Where(i => TextUtil.Norm(i.name) == TextUtil.Norm(name)).ToList();
        var group = exact.Count > 0 ? exact : items.GroupBy(i => i.name).OrderBy(g => g.Key.Length).First().ToList();

        string num = TextUtil.Num(hint?.Number);
        if (num != "")
        {
            var sets = await SetsAsync(ct);
            var byNum = group.Where(i => TextUtil.Num(i.localId) == num).ToList();
            bool exactMatch = byNum.Count == 1;
            if (int.TryParse(hint.Total, out int total) && total > 0)
            {
                // The number after the slash is the set's official size, which tells sets apart.
                var bySize = byNum.Where(i => sets.TryGetValue(SetIdOf(i.id), out var s) && (s.Official == total || s.Total == total)).ToList();
                if (bySize.Count > 0) { byNum = bySize; exactMatch = bySize.Count == 1; }
            }
            var hit = byNum.OrderByDescending(i => sets.GetValueOrDefault(SetIdOf(i.id))?.Order ?? -1).FirstOrDefault();
            if (hit.id != null)
            {
                var c = await DetailAsync(hit.id, ct);
                if (c != null) { c.ExactPrinting = exactMatch; return c; }
            }
        }
        return await PreferStandardAsync(group, ct);
    }

    public async Task<List<Card>> PrintingsAsync(Card card, CancellationToken ct = default)
    {
        var sets = await SetsAsync(ct);
        var items = await ListAsync(card.Name, ct);
        return items.Where(i => TextUtil.Norm(i.name) == TextUtil.Norm(card.Name))
            .Select(i =>
            {
                var s = sets.GetValueOrDefault(SetIdOf(i.id));
                return new Card
                {
                    Game = Game.Pokemon, Id = i.id, Name = i.name, Number = i.localId, SetCode = SetIdOf(i.id),
                    SetName = s?.Name ?? SetIdOf(i.id),
                    Rarity = s != null && s.Official > 0 ? $"{i.localId}/{s.Official:000}" : "",
                };
            })
            .OrderByDescending(c => sets.GetValueOrDefault(c.SetCode)?.Order ?? -1)
            .ToList();
    }

    public async Task<Card> BasicEnergyAsync(string type, CancellationToken ct = default)
    {
        var items = await ListAsync(type + " Energy", ct);
        var names = new[] { TextUtil.Norm(type + " Energy"), TextUtil.Norm("Basic " + type + " Energy") };
        var group = items.Where(i => names.Contains(TextUtil.Norm(i.name))).ToList();
        Card fallback = null;
        foreach (var i in Enumerable.Reverse(group).Take(8))
        {
            var c = await DetailAsync(i.id, ct);
            if (c == null || c.SubType != "Basic") continue;
            fallback ??= c;
            if (c.Legality.GetValueOrDefault("standard") == "legal") return c;
        }
        return fallback;
    }

    async Task<Card> PreferStandardAsync(List<(string id, string localId, string name)> group, CancellationToken ct)
    {
        var sets = await SetsAsync(ct);
        Card fallback = null;
        foreach (var i in group.OrderByDescending(i => sets.GetValueOrDefault(SetIdOf(i.id))?.Order ?? -1).Take(6))
        {
            var c = await DetailAsync(i.id, ct);
            if (c == null) continue;
            fallback ??= c;
            if (c.Legality.GetValueOrDefault("standard") == "legal") return c;
        }
        return fallback;
    }

    static async Task<List<(string id, string localId, string name)>> ListAsync(string name, CancellationToken ct)
    {
        using var doc = await Http.GetJsonAsync(Base + "cards?name=" + Uri.EscapeDataString(name), ct);
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array) return new();
        // Skip Pokémon TCG Pocket (mobile game) cards, which share the database.
        return doc.RootElement.EnumerateArray()
            .Where(e => !e.S("image").Contains("/tcgp/") && !Regex.IsMatch(e.S("id"), @"^(A\d|P-A)"))
            .Select(e => (e.S("id"), e.S("localId"), e.S("name"))).ToList();
    }

    public async Task<Card> RefreshAsync(Card card, CancellationToken ct = default)
    {
        var c = await DetailAsync(card.Id, ct);
        if (c != null) c.ExactPrinting = card.ExactPrinting;
        return c;
    }

    static async Task<Card> DetailAsync(string id, CancellationToken ct)
    {
        using var doc = await Http.GetJsonAsync(Base + "cards/" + Uri.EscapeDataString(id), ct);
        if (doc == null) return null;
        var r = doc.RootElement;
        string cat = r.S("category");
        var c = new Card
        {
            Game = Game.Pokemon,
            Id = r.S("id"),
            Name = r.S("name"),
            Category = cat,
            Colors = r.Strs("types"),
            Hp = (int)r.D("hp"),
            EvolvesFrom = r.S("evolveFrom"),
            Rarity = r.S("rarity"),
            SetName = r.O("set").S("name"),
            SetCode = r.O("set").S("id"),
            Number = r.S("localId"),
        };
        c.SubType = cat switch
        {
            "Pokemon" => r.S("stage"),
            "Trainer" => r.S("trainerType"),
            _ => r.S("energyType") is "Normal" or "Basic" ? "Basic" : "Special"
        };
        c.TypeLine = c.SubType == "" ? cat : $"{cat} ({c.SubType})";
        if (c.Colors.Count > 0 && cat == "Pokemon") c.TypeLine += " " + string.Join("/", c.Colors);
        c.AttackCostTypes = r.A("attacks").SelectMany(a => a.Strs("cost")).Where(t => t != "Colorless").Distinct().ToList();
        c.HasAbility = r.A("abilities").Any();
        var img = r.S("image");
        c.ImageUrl = img == "" ? "" : img + "/high.png";

        var legal = r.O("legal");
        c.Legality["standard"] = legal.True("standard") ? "legal" : "not_legal";
        c.Legality["expanded"] = legal.True("expanded") ? "legal" : "not_legal";
        c.Legality["unlimited"] = "legal";

        var text = new StringBuilder();
        foreach (var k in new[] { "effect", "description" })
            if (r.S(k) != "") text.AppendLine(r.S(k));
        foreach (var a in r.A("abilities")) text.AppendLine($"Ability {a.S("name")}: {a.S("effect")}");
        foreach (var a in r.A("attacks")) text.AppendLine($"{a.S("name")} [{string.Join(",", a.Strs("cost"))}] {a.S("damage")} {a.S("effect")}".Trim());
        c.Text = text.ToString().Trim();
        c.UnlimitedCopies = cat == "Energy" && c.SubType == "Basic";

        // TCGdex has no play-rate data, so rating is a simple strength heuristic.
        c.Quality = cat switch
        {
            "Pokemon" => 0.1 + Math.Min(c.Hp, 340) / 340.0 * 0.5 + (c.HasAbility ? 0.2 : 0) +
                         (Regex.IsMatch(c.Name, @"\b(ex|EX|GX|V|VMAX|VSTAR)$") ? 0.2 : 0),
            "Trainer" => c.SubType switch { "Supporter" => 0.6, "Item" => 0.55, "Tool" => 0.45, _ => 0.4 },
            _ => c.SubType == "Basic" ? 0.3 : 0.45
        };

        // TCGplayer market price, normal printing first, then holo variants.
        var tcp = r.O("pricing").O("tcgplayer");
        if (tcp.ValueKind == JsonValueKind.Object)
        {
            var variants = new[] { "normal", "holofoil", "reverse-holofoil" }
                .Concat(tcp.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object).Select(p => p.Name))
                .Distinct();
            foreach (var v in variants)
            {
                var o = tcp.O(v);
                double price = o.D("marketPrice") > 0 ? o.D("marketPrice") : o.D("midPrice");
                if (price <= 0) continue;
                c.PriceUsd = price;
                c.PriceSource = v == "normal" ? "TCGplayer market" : $"TCGplayer market, {v.Replace('-', ' ')}";
                break;
            }
        }
        c.PriceUpdated = DateTime.Now;
        return c;
    }
}

// ---------------------------------------------------------------- Yu-Gi-Oh! (YGOPRODeck)

public class YgoSource : ICardSource
{
    const string Base = "https://db.ygoprodeck.com/api/v7/cardinfo.php?misc=yes&";
    public Game Game => Game.YuGiOh;

    public async Task<Card> FindAsync(string name, PrintHint hint, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length < 3) return null;
        return await Query("name=" + Uri.EscapeDataString(name), name, hint, ct)
               ?? await Query("fname=" + Uri.EscapeDataString(name), name, hint, ct);
    }

    public Task<Card> RefreshAsync(Card card, CancellationToken ct = default) =>
        Query("id=" + Uri.EscapeDataString(card.Id), card.Name,
              card.ExactPrinting ? new PrintHint { SetCode = card.Number, Rarity = card.Rarity } : null, ct);

    public async Task<List<Card>> PrintingsAsync(Card card, CancellationToken ct = default)
    {
        using var doc = await Http.GetJsonAsync(Base + "id=" + Uri.EscapeDataString(card.Id), ct);
        var r = doc?.RootElement.A("data").FirstOrDefault() ?? default;
        if (r.ValueKind != JsonValueKind.Object) return new();
        return r.A("card_sets")
            .Select(s => Parse(r, new PrintHint { SetCode = s.S("set_code"), Rarity = s.S("set_rarity") }))
            .ToList();
    }

    static async Task<Card> Query(string q, string name, PrintHint hint, CancellationToken ct)
    {
        using var doc = await Http.GetJsonAsync(Base + q, ct);
        if (doc == null) return null;
        var list = doc.RootElement.A("data").ToList();
        if (list.Count == 0) return null;
        var best = list.FirstOrDefault(e => TextUtil.Norm(e.S("name")) == TextUtil.Norm(name));
        if (best.ValueKind == JsonValueKind.Undefined) best = list.OrderBy(e => e.S("name").Length).First();
        return Parse(best, hint);
    }

    static readonly string[] ExtraFrames = { "fusion", "synchro", "xyz", "link" };

    static Card Parse(JsonElement r, PrintHint hint)
    {
        string type = r.S("type");
        string frame = r.S("frameType");
        var c = new Card
        {
            Game = Game.YuGiOh,
            Id = r.S("id"),
            Name = r.S("name"),
            Category = type.Contains("Monster") ? "Monster" : type.StartsWith("Spell") ? "Spell" : "Trap",
            SubType = frame,
            Race = r.S("race"),
            Level = (int)Math.Max(r.D("level"), r.D("linkval")),
            Text = r.S("desc"),
            ImageUrl = r.A("card_images").Select(i => i.S("image_url")).FirstOrDefault() ?? "",
        };
        c.TypeLine = $"{type} / {c.Race}";
        if (r.S("attribute") != "") c.Colors.Add(r.S("attribute"));
        c.IsExtraDeck = ExtraFrames.Any(f => frame == f || frame.StartsWith(f + "_"));

        string ban = r.O("banlist_info").S("ban_tcg").ToLowerInvariant();
        string adv = ban switch { "banned" or "forbidden" => "banned", "limited" => "limited", "semi-limited" => "semi-limited", _ => "legal" };
        c.Legality["advanced"] = adv;
        c.Legality["traditional"] = adv == "banned" ? "limited" : adv;
        c.Legality["unlimited"] = "legal";

        double views = r.A("misc_info").Select(m => m.D("views")).FirstOrDefault();
        c.Quality = Math.Clamp(Math.Log10(views + 1) / 7.0, 0.05, 1);

        // A set code like LOB-EN001 picks one printing, which has its own price.
        var sets = r.A("card_sets").ToList();
        JsonElement set = default;
        if (!string.IsNullOrEmpty(hint?.SetCode))
        {
            var matches = sets.Where(s => TextUtil.Code(s.S("set_code")) == TextUtil.Code(hint.SetCode)).ToList();
            set = matches.FirstOrDefault(s => s.S("set_rarity") == hint.Rarity);
            if (set.ValueKind != JsonValueKind.Object) set = matches.FirstOrDefault();
        }
        if (set.ValueKind == JsonValueKind.Object)
        {
            c.ExactPrinting = true;
            c.SetName = set.S("set_name");
            c.Number = c.SetCode = set.S("set_code");
            c.Rarity = set.S("set_rarity");
            double sp = set.D("set_price");
            if (sp > 0) { c.PriceUsd = sp; c.PriceSource = $"TCGplayer, {c.Rarity}"; }
        }
        else if (sets.Count > 0)
        {
            c.SetName = sets[0].S("set_name");
            c.Number = c.SetCode = sets[0].S("set_code");
        }

        if (c.PriceUsd <= 0)
        {
            // Fallback: one price per card name (usually the cheapest printing).
            var p = r.A("card_prices").FirstOrDefault();
            foreach (var (key, label) in new[] { ("tcgplayer_price", "TCGplayer, cheapest printing"), ("coolstuffinc_price", "CoolStuffInc, cheapest printing") })
            {
                double v = p.D(key);
                if (v <= 0) continue;
                c.PriceUsd = v;
                c.PriceSource = label;
                break;
            }
        }
        c.PriceUpdated = DateTime.Now;
        return c;
    }
}

// ---------------------------------------------------------------- Cyberpunk TCG (CyberpunkDB, unofficial fan API)

public class CyberpunkSource : ICardSource
{
    const string Base = "https://cyberpunkdb.com/api/v1/";
    public Game Game => Game.Cyberpunk;

    static List<Card> _catalogue;
    static DateTime _loaded;

    /// <summary>The whole catalogue is small (about 150 cards), so it is fetched once and matched locally.</summary>
    public static async Task<List<Card>> CatalogueAsync(CancellationToken ct = default)
    {
        if (_catalogue != null && DateTime.Now - _loaded < TimeSpan.FromHours(6)) return _catalogue;
        var list = new List<Card>();
        for (int page = 1; page <= 20; page++)
        {
            using var doc = await Http.GetJsonAsync($"{Base}cards?limit=100&page={page}", ct);
            if (doc == null) break;
            list.AddRange(doc.RootElement.A("data").Select(Parse).Where(c => c != null));
            if (!doc.RootElement.O("pagination").True("hasMore")) break;
        }
        _loaded = DateTime.Now;
        return _catalogue = list;
    }

    public async Task<Card> FindAsync(string name, PrintHint hint, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length < 3) return null;
        var cards = await CatalogueAsync(ct);
        string num = TextUtil.Num(hint?.Number);
        // Match the full name, the name alone, or the subtitle alone (Legends print them on separate lines).
        var best = cards
            .Select(c => (card: c, score: new[]
            {
                TextUtil.Ratio(c.Name, name),
                c.BaseName.Length >= 4 ? TextUtil.Ratio(c.BaseName, name) : 0,
                c.Name.Contains(':') ? TextUtil.Ratio(c.Name[(c.Name.IndexOf(':') + 1)..], name) : 0,
            }.Max() + (num != "" && TextUtil.Num(c.Number) == num ? 0.15 : 0)))
            .OrderByDescending(x => x.score)
            .FirstOrDefault();
        if (best.card == null || best.score < 0.6) return null;
        var found = Clone(best.card);
        found.ExactPrinting = num != "" && TextUtil.Num(found.Number) == num;
        await JustTcg.ApplyAsync(found, ct);
        return found;
    }

    public async Task<Card> RefreshAsync(Card card, CancellationToken ct = default)
    {
        using var doc = await Http.GetJsonAsync(Base + "cards/" + Uri.EscapeDataString(card.Id), ct);
        var c = doc == null ? null : Parse(doc.RootElement.O("data"));
        if (c == null) return null;
        c.ExactPrinting = card.ExactPrinting;
        await JustTcg.ApplyAsync(c, ct);
        return c;
    }

    public async Task<List<Card>> PrintingsAsync(Card card, CancellationToken ct = default)
    {
        var cards = await CatalogueAsync(ct);
        var list = cards.Where(c => c.Name == card.Name).Select(Clone).ToList();
        foreach (var c in list) await JustTcg.ApplyAsync(c, ct);
        return list;
    }

    static Card Clone(Card c) => JsonSerializer.Deserialize<Card>(JsonSerializer.Serialize(c));

    static Card Parse(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) return null;
        string type = r.S("card_type"), color = r.S("color");
        var classes = r.Strs("classifications");
        var c = new Card
        {
            Game = Game.Cyberpunk,
            Id = r.S("id"),
            Name = r.S("display_name") != "" ? r.S("display_name") : r.S("name"),
            BaseName = r.S("name"),
            Category = type,
            ManaValue = r.D("cost"),
            Power = (int)r.D("power"),
            Ram = (int)r.D("ram"),
            Rarity = r.S("rarity"),
            SetName = r.O("set").S("name"),
            SetCode = r.O("set").S("code"),
            Number = r.S("print_number"),
            ImageUrl = r.S("image_url"),
            Text = r.S("rules_text"),
            Sellable = r.True("is_eddiable"),
        };
        if (color != "") c.Colors.Add(color);
        c.TypeLine = $"{type} ({color})" + (classes.Count > 0 ? " " + string.Join(", ", classes) : "");
        c.Legality["standard"] = r.S("legality") == "" ? "legal" : r.S("legality");
        c.Legality["casual"] = "legal";

        // No play-rate data yet, so rating is a heuristic: rarity plus stat efficiency for units.
        double rarity = c.Rarity switch
        {
            "Common" => 0.3, "Uncommon" => 0.4, "Rare" => 0.55, "Epic" => 0.65, "Legendary" => 0.7, "Secret" => 0.7, _ => 0.4
        };
        double efficiency = type == "Unit" && c.Power > 0 ? Math.Min(c.Power / (c.ManaValue + 1), 2.0) / 2.0 * 0.3 : 0.15;
        c.Quality = Math.Clamp(rarity + efficiency + 0.03 * r.Strs("keywords").Count, 0.05, 1);
        c.PriceUpdated = DateTime.Now;
        return c;
    }
}

// ---------------------------------------------------------------- Gundam Card Game (gcg-api, unofficial, ODbL)
// Contains data from gcg-api (https://gcgapi.com), made available under the Open Database License (ODbL) v1.0.

public class GundamSource : ICardSource
{
    const string Base = "https://api.gcgapi.com/v1/";
    public Game Game => Game.Gundam;

    static List<Card> _catalogue;
    static string CacheFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TCG deck scanner", "Gundam cards.json");

    /// <summary>All ~2,200 printings. The source refreshes weekly, so a local copy is kept for 3 days.</summary>
    public static async Task<List<Card>> CatalogueAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _catalogue != null) return _catalogue;
        if (!force && File.Exists(CacheFile) && DateTime.Now - File.GetLastWriteTime(CacheFile) < TimeSpan.FromDays(3))
        {
            try
            {
                _catalogue = JsonSerializer.Deserialize<List<Card>>(await File.ReadAllTextAsync(CacheFile, ct));
                if (_catalogue?.Count > 0) return _catalogue;
            }
            catch { }
        }
        var list = new List<Card>();
        for (int offset = 0; offset < 20000; offset += 250)
        {
            using var doc = await Http.GetJsonAsync($"{Base}cards?limit=250&offset={offset}", ct);
            if (doc == null) break;
            var page = doc.RootElement.A("data").ToList();
            list.AddRange(page.Select(Parse));
            if (page.Count < 250) break;
        }
        if (list.Count == 0) return _catalogue ?? list;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile));
            await File.WriteAllTextAsync(CacheFile, JsonSerializer.Serialize(list), ct);
        }
        catch { }
        return _catalogue = list;
    }

    static bool IsBase(Card c) => !c.Id.Contains("_p");

    /// <summary>Name similarity that also credits a partly read name ("ess Flyer" for "Daughtress Flyer").</summary>
    static double NameScore(string cardName, string read)
    {
        double ratio = TextUtil.Ratio(cardName, read);
        string a = TextUtil.Norm(cardName), b = TextUtil.Norm(read);
        if (b.Length >= 6 && a.Contains(b)) ratio = Math.Max(ratio, 0.6 + 0.4 * b.Length / a.Length);
        return ratio;
    }

    public async Task<Card> FindAsync(string name, PrintHint hint, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length < 3) return null;
        var cards = await CatalogueAsync(false, ct);
        string num = TextUtil.Code(hint?.Number);

        // The card number (like GD01-001) names the card exactly. An exact read is trusted on its own;
        // a read that is one or two characters off must also roughly agree with the name.
        // Digits read cleanly but the set letters garbled (GO01-040 for GD01-040): take the closest real prefix.
        var parts = Regex.Match((hint?.Number ?? "").ToUpperInvariant(), @"^([A-Z0-9]{2,3})(\d{2}-\d{3})$");
        if (parts.Success)
        {
            string prefix = parts.Groups[1].Value, tail = parts.Groups[2].Value;
            var closest = cards.Where(c => c.Category != "Token" && c.Number.EndsWith(tail) && c.Number.Length == prefix.Length + tail.Length)
                .Select(c => c.Number).Distinct()
                .Select(n => (n, dist: n.Take(prefix.Length).Zip(prefix).Count(p => p.First != p.Second)))
                .OrderBy(x => x.dist).ToList();
            if (closest.Count > 0 && closest[0].dist <= 1 && (closest.Count == 1 || closest[1].dist > closest[0].dist))
            {
                var hit = Clone(cards.Where(c => c.Number == closest[0].n).OrderByDescending(IsBase).First());
                hit.ExactPrinting = closest[0].dist == 0;
                await JustTcg.ApplyAsync(hit, ct);
                return hit;
            }
        }

        if (num != "")
        {
            var numbers = cards.Where(c => c.Category != "Token").Select(c => c.Number).Distinct()
                .Select(n => (n, ratio: TextUtil.Ratio(TextUtil.Code(n), num)))
                .Where(x => x.ratio >= 0.75)
                .OrderByDescending(x => x.ratio)
                .Take(3).ToList();
            foreach (var (n, ratio) in numbers)
            {
                var hit = cards.Where(c => c.Number == n).OrderByDescending(IsBase).First();
                if (ratio < 1 && NameScore(hit.Name, name) < 0.6) continue;
                var exact = Clone(hit);
                exact.ExactPrinting = ratio >= 1;
                await JustTcg.ApplyAsync(exact, ct);
                return exact;
            }
        }

        var best = cards
            .Where(c => c.Category != "Token")
            .Select(c => (card: c, score: NameScore(c.Name, name) + (IsBase(c) ? 0.01 : 0)))
            .OrderByDescending(x => x.score)
            .FirstOrDefault();
        if (best.card == null || best.score < 0.7) return null;
        var found = Clone(best.card);
        await JustTcg.ApplyAsync(found, ct);
        return found;
    }

    public async Task<Card> RefreshAsync(Card card, CancellationToken ct = default)
    {
        using var doc = await Http.GetJsonAsync(Base + "cards/" + Uri.EscapeDataString(card.Id), ct);
        var root = doc?.RootElement;
        if (root == null) return null;
        var data = root.Value.O("data").ValueKind == JsonValueKind.Object ? root.Value.O("data") : root.Value;
        if (data.ValueKind != JsonValueKind.Object || data.S("product_id") == "") return null;
        var c = Parse(data);
        c.ExactPrinting = card.ExactPrinting;
        await JustTcg.ApplyAsync(c, ct);
        return c;
    }

    public async Task<List<Card>> PrintingsAsync(Card card, CancellationToken ct = default)
    {
        var cards = await CatalogueAsync(false, ct);
        var list = cards.Where(c => c.Number == card.Number || c.Name == card.Name)
                        .OrderBy(c => c.Number != card.Number).ThenBy(c => c.Id).Select(Clone).ToList();
        foreach (var c in list) await JustTcg.ApplyAsync(c, ct);
        return list;
    }

    static Card Clone(Card c) => JsonSerializer.Deserialize<Card>(JsonSerializer.Serialize(c));

    static readonly string[] StrongKeywords = { "Blocker", "Repair", "Breach", "High-Maneuver", "First Strike", "Support", "Suppression" };

    static Card Parse(JsonElement r)
    {
        string type = r.S("card_type").ToUpperInvariant();
        string category = type switch
        {
            "UNIT" => "Unit",
            "PILOT" => "Pilot",
            "COMMAND" => "Command",
            "BASE" => "Base",
            "RESOURCE" => "Resource",
            _ => "Token",
        };
        string color = r.S("color");
        var traits = r.Strs("traits");
        var c = new Card
        {
            Game = Game.Gundam,
            Id = r.S("product_id"),
            Number = r.S("card_number"),
            Name = r.S("name"),
            SetCode = r.S("set_code"),
            SetName = r.S("set_name"),
            Rarity = r.S("rarity"),
            Category = category,
            Level = (int)r.D("level"),
            ManaValue = r.D("cost"),
            Power = (int)r.D("ap"),
            Hp = (int)r.D("hp"),
            Text = r.S("effect"),
            ImageUrl = r.S("image_url"),
            Race = string.Join(", ", traits),
            LinkRefs = r.Strs("link_refs"),
            UnlimitedCopies = category == "Resource",
        };
        if (color != "") c.Colors.Add(color);
        c.TypeLine = $"{category}" + (color != "" ? $" ({color})" : "") + (c.Level > 0 ? $" Lv.{c.Level}" : "") +
                     (traits.Count > 0 ? " " + string.Join(", ", traits) : "");
        c.Legality["standard"] = category == "Token" ? "not_legal" : "legal";

        // No play-rate data, so rating is a heuristic: rarity, stats for the cost, and strong keywords.
        string baseRarity = c.Rarity.Replace("+", "").Trim();
        double rarity = baseRarity switch
        {
            "C" => 0.3, "U" => 0.4, "R" => 0.55, "LR" => 0.7, "LKR" => 0.6, "LKU" => 0.45, "LKC" => 0.35, "P" => 0.45, _ => 0.4
        };
        double stats = category == "Unit" && c.ManaValue >= 0
            ? Math.Min((c.Power + c.Hp) / (c.ManaValue + 1.0), 3.0) / 3.0 * 0.25
            : 0.12;
        var keywords = r.A("keyword_effects").Select(k => k.S("keyword")).ToList();
        int strong = keywords.Count(k => StrongKeywords.Any(s => k.Contains(s, StringComparison.OrdinalIgnoreCase)));
        c.Quality = Math.Clamp(rarity + stats + 0.04 * strong + (r.Strs("timing_markers").Contains("Burst") ? 0.05 : 0), 0.05, 1);
        c.PriceUpdated = DateTime.Now;
        return c;
    }
}
