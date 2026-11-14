using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TcgScanner;

/// <summary>
/// Cyberpunk TCG and Gundam Card Game prices from JustTCG (needs the user's own free API key).
/// The free plan allows 100 requests a day, so each game's prices are fetched in one batch and cached for a day.
/// </summary>
public static class JustTcg
{
    const string Base = "https://api.justtcg.com/v1/";
    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TCG deck scanner");
    static string KeyFile => Path.Combine(Dir, "JustTCG key.dat");
    static string CacheFile(Game g) => Path.Combine(Dir, $"{GameInfo.Short(g)} prices.json");
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TCG deck scanner JustTCG");

    /// <summary>Games priced through JustTCG, with the word used to find each one in JustTCG's game list.</summary>
    public static readonly Dictionary<Game, string> Games = new() { [Game.Cyberpunk] = "Cyberpunk", [Game.Gundam] = "Gundam" };

    public class PriceEntry
    {
        public string Name { get; set; } = "";
        public string Number { get; set; } = "";
        public string SetName { get; set; } = "";
        public double Price { get; set; }
        public string Printing { get; set; } = "";
    }

    public class PriceCache
    {
        public DateTime Fetched { get; set; }
        public string GameId { get; set; } = "";
        public List<PriceEntry> Entries { get; set; } = new();
    }

    static readonly Dictionary<Game, PriceCache> _caches = new();
    static readonly SemaphoreSlim _lock = new(1, 1);

    // ------------------------------------------------------------------ API key

    /// <summary>Where the API key is kept. Windows encrypts it for the signed-in user; Android sets its secure storage.</summary>
    public interface ISecretStore
    {
        string Load();
        void Save(string value);
        void Remove();
    }

    /// <summary>Windows: the key file is encrypted with DPAPI, so only this Windows account can read it.</summary>
    sealed class DpapiSecretStore : ISecretStore
    {
        public string Load()
        {
            try
            {
                if (!OperatingSystem.IsWindows() || !File.Exists(KeyFile)) return null;
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch { return null; }
        }

        public void Save(string value)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Set JustTcg.Secrets for this platform.");
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(KeyFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
        }

        public void Remove()
        {
            if (File.Exists(KeyFile)) File.Delete(KeyFile);
        }
    }

    public static ISecretStore Secrets { get; set; } = new DpapiSecretStore();

    static string _key;
    static bool _keyLoaded;

    public static bool HasKey => !string.IsNullOrEmpty(LoadKey());

    public static string LoadKey()
    {
        if (!_keyLoaded) { _key = Secrets.Load(); _keyLoaded = true; }
        return _key;
    }

    public static void SaveKey(string key)
    {
        Secrets.Save(key.Trim());
        _keyLoaded = false;
        _caches.Clear();
        _gundam = null;
    }

    public static void RemoveKey()
    {
        Secrets.Remove();
        _keyLoaded = false;
        foreach (var g in Games.Keys)
            if (File.Exists(CacheFile(g))) File.Delete(CacheFile(g));
        _caches.Clear();
        _gundam = null;
    }

    // ------------------------------------------------------------------ requests

    static async Task<JsonDocument> GetAsync(string key, string url, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Base + url);
            req.Headers.Add("x-api-key", key);
            using var resp = await Http.Client.SendAsync(req, ct);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException("JustTCG did not accept the API key. Check it in Settings.");
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (attempt >= 2) throw new InvalidOperationException("JustTCG's free-plan limit was reached. Prices will update again later.");
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
                await Task.Delay(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait, ct);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(s, cancellationToken: ct);
        }
    }

    /// <summary>Checks a key and returns a short status line, for the Settings dialog.</summary>
    public static async Task<string> TestKeyAsync(string key, CancellationToken ct = default)
    {
        using var doc = await GetAsync(key.Trim(), "games", ct);
        var names = doc.RootElement.A("data").Select(g => g.S("name")).ToList();
        var have = Games.Where(kv => names.Any(n => n.Contains(kv.Value, StringComparison.OrdinalIgnoreCase))).Select(kv => GameInfo.Short(kv.Key)).ToList();
        var missing = Games.Keys.Select(GameInfo.Short).Except(have).ToList();
        string left = doc.RootElement.O("_metadata").S("apiRequestsRemaining");
        return "Key works. " +
               (have.Count > 0 ? $"Prices available for: {string.Join(", ", have)}. " : "") +
               (missing.Count > 0 ? $"JustTCG doesn't list {string.Join(" or ", missing)} yet. " : "") +
               (left != "" ? $"Requests left this month: {left}." : "");
    }

    /// <summary>All prices for one game: from memory, then the disk cache (under a day old), then JustTCG.</summary>
    public static async Task<PriceCache> PricesAsync(Game game, bool force, CancellationToken ct = default)
    {
        if (!Games.TryGetValue(game, out var keyword)) return null;
        var key = LoadKey();
        if (string.IsNullOrEmpty(key)) return null;
        await _lock.WaitAsync(ct);
        try
        {
            _caches.TryGetValue(game, out var cache);
            if (cache == null && File.Exists(CacheFile(game)))
                try { cache = JsonSerializer.Deserialize<PriceCache>(File.ReadAllText(CacheFile(game))); } catch { }
            if (cache != null) _caches[game] = cache;
            if (!force && cache != null && DateTime.Now - cache.Fetched < TimeSpan.FromDays(1)) return cache;

            string gameId = cache?.GameId;
            if (string.IsNullOrEmpty(gameId))
            {
                using var games = await GetAsync(key, "games", ct);
                var g = games.RootElement.A("data").FirstOrDefault(x => x.S("name").Contains(keyword, StringComparison.OrdinalIgnoreCase));
                if (g.ValueKind != JsonValueKind.Object) return cache;
                gameId = g.S("id");
            }

            // Cyberpunk is small (~150 cards), so the whole price list fits in about 8 requests.
            var entries = new List<PriceEntry>();
            for (int offset = 0, page = 0; page < 12; page++, offset += 20)
            {
                using var doc = await GetAsync(key, $"cards?game={Uri.EscapeDataString(gameId)}&limit=20&offset={offset}&condition=NM&language=English", ct);
                entries.AddRange(ReadEntries(doc));
                if (!doc.RootElement.O("meta").True("hasMore")) break;
                await Task.Delay(700, ct); // stay under 10 requests a minute
            }

            cache = new PriceCache { Fetched = DateTime.Now, GameId = gameId, Entries = entries };
            _caches[game] = cache;
            Directory.CreateDirectory(Dir);
            File.WriteAllText(CacheFile(game), JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
            return cache;
        }
        finally { _lock.Release(); }
    }

    static IEnumerable<PriceEntry> ReadEntries(JsonDocument doc)
    {
        foreach (var card in doc.RootElement.A("data"))
        {
            var variants = card.A("variants").Where(v => v.D("price") > 0).ToList();
            if (variants.Count == 0) continue;
            var pick = variants.FirstOrDefault(v => v.S("printing").Equals("Normal", StringComparison.OrdinalIgnoreCase));
            if (pick.ValueKind != JsonValueKind.Object) pick = variants.OrderBy(v => v.D("price")).First();
            yield return new PriceEntry
            {
                Name = card.S("name"),
                Number = card.S("number"),
                SetName = card.S("set_name"),
                Price = pick.D("price"),
                Printing = pick.S("printing"),
            };
        }
    }

    // ------------------------------------------------------------------ Gundam: priced one card number at a time

    public class NumberCache
    {
        public string GameId { get; set; } = "";
        public Dictionary<string, PriceCache> Numbers { get; set; } = new();
    }

    static NumberCache _gundam;

    /// <summary>Makes the next Gundam lookups fetch fresh prices (used by Refresh prices).</summary>
    public static void ExpireGundamPrices()
    {
        if (_gundam == null) return;
        foreach (var c in _gundam.Numbers.Values) c.Fetched = DateTime.MinValue;
    }

    /// <summary>Price entries for one Gundam card number (all its printings), fetched at most once a day.</summary>
    static async Task<PriceCache> GundamNumberAsync(string number, CancellationToken ct)
    {
        var key = LoadKey();
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(number)) return null;
        await _lock.WaitAsync(ct);
        try
        {
            if (_gundam == null && File.Exists(CacheFile(Game.Gundam)))
                try { _gundam = JsonSerializer.Deserialize<NumberCache>(File.ReadAllText(CacheFile(Game.Gundam))); } catch { }
            _gundam ??= new NumberCache();
            if (_gundam.Numbers.TryGetValue(number, out var cached) && DateTime.Now - cached.Fetched < TimeSpan.FromDays(1)) return cached;

            if (string.IsNullOrEmpty(_gundam.GameId))
            {
                using var games = await GetAsync(key, "games", ct);
                var g = games.RootElement.A("data").FirstOrDefault(x => x.S("name").Contains("Gundam", StringComparison.OrdinalIgnoreCase));
                if (g.ValueKind != JsonValueKind.Object) return null;
                _gundam.GameId = g.S("id");
            }

            using var doc = await GetAsync(key, $"cards?game={Uri.EscapeDataString(_gundam.GameId)}&number={Uri.EscapeDataString(number)}&limit=20&condition=NM&language=English", ct);
            var result = new PriceCache { Fetched = DateTime.Now, GameId = _gundam.GameId, Entries = ReadEntries(doc).ToList() };
            _gundam.Numbers[number] = result;
            Directory.CreateDirectory(Dir);
            File.WriteAllText(CacheFile(Game.Gundam), JsonSerializer.Serialize(_gundam, new JsonSerializerOptions { WriteIndented = true }));
            return result;
        }
        finally { _lock.Release(); }
    }

    static string Clean(string s) => TextUtil.Norm(Regex.Replace(s ?? "", @"\(.*?\)|[^\p{L}\p{N} ]", " "));

    /// <summary>Puts the JustTCG price on a card. Silently does nothing without a key or for other games.</summary>
    public static async Task ApplyAsync(Card card, CancellationToken ct = default)
    {
        if (!Games.ContainsKey(card.Game) || !HasKey) return;
        PriceCache cache;
        try
        {
            cache = card.Game == Game.Gundam
                ? await GundamNumberAsync(card.Number, ct)
                : await PricesAsync(card.Game, false, ct);
        }
        catch { return; } // prices are optional; a bad key or limit must not block scanning
        if (cache == null) return;
        var match = Match(card, cache.Entries);
        if (match == null) return;
        card.PriceUsd = match.Price;
        card.PriceSource = "TCGplayer near mint (via JustTCG)" + (match.Printing is "" or "Normal" ? "" : $", {match.Printing}");
        card.PriceUpdated = cache.Fetched;
    }

    /// <summary>
    /// Best price entry for a card by name (punctuation and "(...)" notes ignored), helped by the card number.
    /// When several printings tie (an alternate art shares its number), base printings take the lowest price
    /// and alternate arts ("_p" ids) the highest.
    /// </summary>
    public static PriceEntry Match(Card card, List<PriceEntry> entries)
    {
        string name = Clean(card.Name);
        string num = TextUtil.Code(card.Number);
        var scored = entries
            .Select(e =>
            {
                string en = TextUtil.Code(e.Number);
                double bonus = num != "" && en != "" ? (en == num ? 0.3 : en.StartsWith(num) || num.StartsWith(en) ? 0.1 : -0.2) : 0;
                return (e, score: TextUtil.Ratio(Clean(e.Name), name) + bonus);
            })
            .Where(x => x.score >= 0.85)
            .OrderByDescending(x => x.score)
            .ToList();
        if (scored.Count == 0) return null;
        var top = scored.Where(x => x.score >= scored[0].score - 0.02).Select(x => x.e).ToList();
        bool altArt = card.Id.Contains("_p");
        return altArt ? top.OrderByDescending(e => e.Price).First() : top.OrderBy(e => e.Price).First();
    }
}
