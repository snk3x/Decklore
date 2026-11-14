namespace TcgScanner;

/// <summary>
/// Hands-free scanning: the camera is read continuously and this decides when a card is really there.
/// A card is added once it is recognized in two frames in a row, and not again while it stays in view.
/// Showing nothing (or a different card) for a moment allows the same card to be added again, for duplicates.
/// Lookups are cached by what was read, so a card sitting in front of the camera isn't looked up every frame.
/// </summary>
public class AutoScanSession
{
    /// <summary>How long the view must be empty before the same card can be added again.</summary>
    public TimeSpan ClearTime { get; set; } = TimeSpan.FromSeconds(1.2);

    /// <summary>How many frames in a row must agree before a card is added.</summary>
    public int FramesToConfirm { get; set; } = 2;

    /// <summary>Card-game clues a frame needs before it's treated as a card (ordinary text has 0 or 1).</summary>
    public int MinCardEvidence { get; set; } = 2;

    /// <summary>How closely the card's name must match what was read, unless its set code or number confirmed it.</summary>
    public double MinNameScore { get; set; } = 0.75;

    readonly Dictionary<string, Card> _lookupCache = new();
    readonly Queue<string> _cacheOrder = new();
    string _pendingKey;
    int _pendingCount;
    string _lastAddedKey;
    DateTime _emptySince = DateTime.MinValue;

    /// <summary>What the last frame showed, for on-screen feedback.</summary>
    public string Status { get; private set; } = "Show a card to the camera.";

    static string KeyOf(Card c) => $"{c.Game}|{c.Id}|{c.Number}";

    /// <summary>
    /// Processes one camera frame that has already been read into text. Returns a card to add, or null.
    /// </summary>
    public async Task<Card> ProcessAsync(ScanResult scan, Func<ScanResult, Task<Card>> lookup, DateTime now)
    {
        // Text without card-game clues (a bottle label, a poster, a screen) is treated as "no card in view".
        if (scan == null || scan.NameCandidates.Count == 0 || scan.CardEvidence < MinCardEvidence)
        {
            if (_emptySince == DateTime.MinValue) _emptySince = now;
            if (now - _emptySince >= ClearTime) { _lastAddedKey = null; _pendingKey = null; _pendingCount = 0; }
            Status = "Show a card to the camera.";
            return null;
        }
        _emptySince = DateTime.MinValue;

        // The same text read again means the same card: reuse the earlier lookup.
        string signature = string.Join("|", scan.NameCandidates.Take(3).Select(TextUtil.Norm)) + "#" +
                           scan.GundamHint.Number + scan.PokemonHint.Number + scan.MagicHint.SetCode + scan.YugiohHint.SetCode;
        if (!_lookupCache.TryGetValue(signature, out var card))
        {
            Status = "Reading the card...";
            card = await lookup(scan);
            // Hands-free adds need a close name match, or a printed set code or number that confirmed the card.
            if (card != null && !card.ExactPrinting && CardMatcher.Score(card, scan.NameCandidates) < MinNameScore) card = null;
            _lookupCache[signature] = card;
            _cacheOrder.Enqueue(signature);
            while (_cacheOrder.Count > 200) _lookupCache.Remove(_cacheOrder.Dequeue());
        }
        if (card == null)
        {
            _pendingKey = null;
            _pendingCount = 0;
            Status = "Can't make out this card yet. Hold it flat, closer, in good light.";
            return null;
        }

        string key = KeyOf(card);
        if (key == _lastAddedKey)
        {
            Status = $"{card.Name} added. Show the next card.";
            return null;
        }
        if (key == _pendingKey) _pendingCount++;
        else { _pendingKey = key; _pendingCount = 1; }

        if (_pendingCount < FramesToConfirm)
        {
            Status = $"Checking: {card.Name}...";
            return null;
        }
        _lastAddedKey = key;
        _pendingKey = null;
        _pendingCount = 0;
        Status = $"{card.Name} added. Show the next card.";
        return card;
    }

    /// <summary>Forget the last card, so it can be added again right away (for example after Undo).</summary>
    public void Reset()
    {
        _lastAddedKey = null;
        _pendingKey = null;
        _pendingCount = 0;
    }
}
