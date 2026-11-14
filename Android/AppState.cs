using TcgScanner;

namespace TcgMobile;

/// <summary>App-wide state shared by every tab: the saved collection, the chosen game and format, card sources and rules.</summary>
public static class AppState
{
	public static readonly string[] GameNames = { "Magic: The Gathering", "Pokémon TCG", "Yu-Gi-Oh!", "Cyberpunk TCG", "Gundam Card Game" };

	public static readonly Dictionary<Game, ICardSource> Sources = new()
	{
		[Game.Magic] = new ScryfallSource(),
		[Game.Pokemon] = new TcgdexSource(),
		[Game.YuGiOh] = new YgoSource(),
		[Game.Cyberpunk] = new CyberpunkSource(),
		[Game.Gundam] = new GundamSource(),
	};

	public static readonly Dictionary<Game, DeckRules> Rules = new()
	{
		[Game.Magic] = new MagicRules((ScryfallSource)Sources[Game.Magic]),
		[Game.Pokemon] = new PokemonRules((TcgdexSource)Sources[Game.Pokemon]),
		[Game.YuGiOh] = new YugiohRules(),
		[Game.Cyberpunk] = new CyberpunkRules(),
		[Game.Gundam] = new GundamRules(),
	};

	public static AppData Data { get; private set; } = new();

	/// <summary>Raised whenever the collection, deck, game or format changes, so every tab can refresh.</summary>
	public static event Action Changed;

	/// <summary>The deck before the last Optimize, Starter deck, Import or Clear, so it can be put back.</summary>
	public static Dictionary<LibraryEntry, int> Undo;

	public static void Init() => Data = Storage.Load();

	public static Game Game
	{
		get => Data.LastGame;
		set
		{
			if (Data.LastGame == value) return;
			Data.LastGame = value;
			Undo = null;
			NotifyChanged();
		}
	}

	public static Library Lib => Data.Get(Game);
	public static DeckRules CurrentRules => Rules[Game];
	public static ICardSource Source => Sources[Game];

	public static string Format
	{
		get => Data.Formats.TryGetValue(Game, out var f) && CurrentRules.Formats.Contains(f) ? f : CurrentRules.Formats[0];
		set
		{
			Data.Formats[Game] = value;
			NotifyChanged();
		}
	}

	public static void SnapshotDeck() => Undo = Lib.Entries.Values.ToDictionary(e => e, e => e.InDeck);

	public static void Save()
	{
		try { Storage.Save(Data); } catch { /* storage full or unavailable; keep running */ }
	}

	public static void NotifyChanged()
	{
		Save();
		Changed?.Invoke();
	}

	public static string FriendlyError(Exception ex) => ex switch
	{
		HttpRequestException or TaskCanceledException => "Couldn't reach the card database. Check your internet connection and try again.",
		_ => ex.Message
	};
}

/// <summary>Keeps the JustTCG API key in Android's encrypted secure storage.</summary>
public sealed class SecureSecretStore : JustTcg.ISecretStore
{
	const string KeyName = "justtcg-key";
	string _value;

	public static SecureSecretStore Create()
	{
		var store = new SecureSecretStore();
		try { store._value = Task.Run(() => SecureStorage.Default.GetAsync(KeyName)).GetAwaiter().GetResult(); }
		catch { store._value = null; }
		return store;
	}

	public string Load() => _value;

	public void Save(string value)
	{
		_value = value;
		_ = SecureStorage.Default.SetAsync(KeyName, value);
	}

	public void Remove()
	{
		_value = null;
		SecureStorage.Default.Remove(KeyName);
	}
}
