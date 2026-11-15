using TcgScanner;

namespace TcgMobile;

/// <summary>The deck for the chosen game and format: check it, optimize it, build a starter deck, share or import a list.</summary>
public class DeckPage : ContentPage
{
	readonly Picker _format = new() { Title = "Format" };
	readonly Label _summary = new() { FontAttributes = FontAttributes.Bold };
	readonly Label _report = new() { FontSize = 13 };
	readonly VerticalStackLayout _deckList = new() { Spacing = 2 };
	readonly Button _starter;
	readonly ActivityIndicator _busy = new() { Color = Ui.Accent, IsVisible = false };
	bool _dirty = true, _updatingFormat, _working;

	public DeckPage()
	{
		Title = "Deck";
		_format.SelectedIndexChanged += (s, e) =>
		{
			if (_updatingFormat || _format.SelectedItem is not string f) return;
			AppState.Format = f;
		};
		AppState.Changed += () => { _dirty = true; if (IsVisible) Refresh(); };
		_starter = Ui.Secondary("Starter decks", StarterDecksAsync);

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = 16,
				Spacing = 10,
				Children =
				{
					Ui.Title("Deck"),
					Ui.GamePicker(),
					_format,
					_summary,
					Ui.Row(Ui.Primary("Optimize", Optimize), Ui.Secondary("Analyze", () => { Analyze(); return Task.CompletedTask; })),
					Ui.Row(Ui.Secondary("Budget swaps", BudgetSwapsAsync), Ui.Secondary("Loaner picks", LoanerPicksAsync)),
					Ui.Row(Ui.Secondary("Undo", UndoAsync), _starter),
					Ui.Row(Ui.Secondary("Share list", ShareList), Ui.Secondary("Import from clipboard", ImportFromClipboard)),
					Ui.Secondary("Clear deck", ClearDeck),
					_busy,
					Ui.Card(_report),
					Ui.Heading("Cards in the deck"),
					_deckList,
				}
			}
		};
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		if (_dirty) Refresh();
	}

	void Refresh()
	{
		_dirty = false;
		var rules = AppState.CurrentRules;
		_updatingFormat = true;
		_format.ItemsSource = rules.Formats;
		_format.SelectedItem = AppState.Format;
		_updatingFormat = false;
		_starter.IsVisible = AppState.Game == Game.Gundam;

		var report = rules.Analyze(AppState.Lib, AppState.Format);
		double value = AppState.Lib.Deck.Sum(e => e.InDeck * e.Card.PriceUsd);
		_summary.Text = $"{AppState.Lib.DeckCount} cards   ·   Value {(value > 0 ? Money.Format(value) : "$0.00")}   ·   Health {report.Health}/100";
		_summary.TextColor = report.Health >= 80 ? Ui.Good : report.Health >= 50 ? Ui.Warn : Ui.Bad;
		if (string.IsNullOrEmpty(_report.Text)) _report.Text = "Tap Analyze to check the deck, or Optimize to build the best deck from your scanned cards.";

		_deckList.Children.Clear();
		foreach (var e in AppState.Lib.Deck.OrderBy(e => e.Card.IsExtraDeck).ThenBy(e => e.Card.Category).ThenBy(e => e.Card.ManaValue).ThenBy(e => e.Card.Name))
		{
			string cost = rules.CostText(e.Card);
			bool loaner = e.InDeck > e.Owned && e.Loaned > 0;
			var label = new Label
			{
				Text = $"{e.InDeck}×  {e.Card.Name}" + (cost != "" ? $"   ({rules.CostHeader} {cost})" : "") + (loaner ? "   (loaner)" : ""),
				FontSize = 14,
			};
			if (loaner) label.TextColor = Ui.Warn;
			_deckList.Children.Add(label);
		}
		if (_deckList.Children.Count == 0) _deckList.Children.Add(Ui.Note("The deck is empty."));
	}

	void SetBusy(bool busy) { _working = busy; _busy.IsRunning = _busy.IsVisible = busy; }

	void Analyze() => _report.Text = AppState.CurrentRules.Analyze(AppState.Lib, AppState.Format).ToString().Trim();

	async Task Optimize()
	{
		if (_working) return;
		if (!AppState.Lib.Entries.Values.Any(e => e.Owned > 0 || e.InDeck > 0))
		{
			_report.Text = "Scan some cards first. The optimizer builds from your collection.";
			return;
		}
		SetBusy(true);
		try
		{
			AppState.SnapshotDeck();
			var log = await AppState.CurrentRules.OptimizeAsync(AppState.Lib, AppState.Format, CancellationToken.None);
			AppState.NotifyChanged();
			_report.Text = "Optimizer changes\n" + string.Join("\n", log.Select(l => "  " + l)) + "\n\n" +
			               AppState.CurrentRules.Analyze(AppState.Lib, AppState.Format).ToString().Trim();
		}
		catch (Exception ex) { _report.Text = AppState.FriendlyError(ex); }
		finally { SetBusy(false); }
	}

	async Task BudgetSwapsAsync()
	{
		if (_working) return;
		if (AppState.Lib.DeckCount == 0) { _report.Text = "Build a deck first. Budget swaps look at the cards in your deck."; return; }
		string input = await DisplayPromptAsync("Budget swaps", "Suggest cheaper cards for deck cards that cost more than this (USD):",
			"Find", "Cancel", initialValue: AppState.Data.BudgetLimit.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
			keyboard: Keyboard.Numeric);
		if (input == null) return;
		if (!double.TryParse(input.Replace("$", "").Replace(",", ".").Trim(), System.Globalization.NumberStyles.Float,
		                     System.Globalization.CultureInfo.InvariantCulture, out double max) || max <= 0)
		{
			_report.Text = "Enter a price like 5 or 2.50.";
			return;
		}
		AppState.Data.BudgetLimit = max;
		AppState.Save();

		var lib = AppState.Lib;
		string format = AppState.Format;
		var rules = AppState.CurrentRules;
		List<BudgetSwap> swaps;
		SetBusy(true);
		try
		{
			swaps = await BudgetAdvisor.SuggestAsync(AppState.Game, lib, format, rules, AppState.Source, max,
				name => MainThread.BeginInvokeOnMainThread(() => _report.Text = $"Checking {name}..."));
		}
		catch (Exception ex) { _report.Text = AppState.FriendlyError(ex); return; }
		finally { SetBusy(false); }

		if (swaps.Count == 0) { _report.Text = $"Every card in the deck is {Money.Format(max)} or less, or has no price yet."; return; }
		var withOptions = swaps.Where(s => s.Options.Count > 0).OrderByDescending(s => s.BestSaving).ToList();
		if (withOptions.Count == 0)
		{
			_report.Text = $"No cheaper options found for: {string.Join(", ", swaps.Select(s => s.Current.Name))}.";
			return;
		}

		// One choice per card. Nothing changes until a replacement is picked.
		var applied = new List<string>();
		double before = lib.Deck.Sum(x => x.InDeck * x.Card.PriceUsd);
		bool snapshotTaken = false;
		foreach (var swap in withOptions)
		{
			var labels = swap.Options.Select((o, n) => $"{n + 1}. {o.Card.Name}" + (o.Card.SetName != "" ? $" [{o.Card.SetName}]" : "") +
			                                           $" ({(o.Price > 0 ? Money.Format(o.Price) : "price unknown")}), {o.Kind.ToLowerInvariant()}").ToArray();
			string title = $"Replace {swap.Copies}× {swap.Current.Name} ({Money.Format(swap.Current.PriceUsd)} each)?";
			string choice = await DisplayActionSheetAsync(title, "Stop here", "Keep this card", labels);
			if (choice == null || choice == "Stop here") break;
			int i = Array.IndexOf(labels, choice);
			if (i < 0) continue;
			if (!snapshotTaken) { AppState.SnapshotDeck(); snapshotTaken = true; }
			var option = swap.Options[i];
			BudgetAdvisor.ApplySwap(lib, swap, option);
			applied.Add($"  {swap.Copies}× {swap.Current.Name} → {option.Card.Name}" + (option.Price > 0 ? $" ({Money.Format(option.Price)})" : ""));
		}
		if (applied.Count == 0) { _report.Text = "No swaps applied. Nothing was changed."; return; }
		double after = lib.Deck.Sum(x => x.InDeck * x.Card.PriceUsd);
		AppState.NotifyChanged();
		_report.Text = $"Budget swaps applied\n{string.Join("\n", applied)}\n\nDeck value {Money.Format(before)} → {Money.Format(after)}\n" +
		               "Cards you don't own were added as loaners. Undo puts the deck back.\n\n" + rules.Analyze(lib, format).ToString().Trim();
	}

	async Task LoanerPicksAsync()
	{
		if (_working) return;
		if (!AppState.Lib.Entries.Values.Any(e => e.Loaned > 0))
		{
			await DisplayAlertAsync("Loaner picks", "You have no loaner cards yet.\n\nTurn on \"Scan as loaner\" on the Scan tab and scan (or add by name) " +
			                                        "the cards you could borrow or buy. Then tap Loaner picks.", "OK");
			return;
		}
		LoanerReport report;
		SetBusy(true);
		try { report = await LoanerAdvisor.RecommendAsync(AppState.Lib, AppState.Format, AppState.CurrentRules); }
		catch (Exception ex) { _report.Text = AppState.FriendlyError(ex); return; }
		finally { SetBusy(false); }

		_report.Text = "Loaner picks\n\n" + LoanerAdvisor.Describe(report);
		if (report.Picks.Count == 0) return;
		if (!await DisplayAlertAsync("Loaner picks", $"Use the deck with these loaners? Health {report.HealthOwnedOnly} → {report.HealthWithLoaners}. " +
		                                             "Your owned cards stay the same, and Undo puts the current deck back.", "Use deck", "Not now")) return;
		SetBusy(true);
		try
		{
			AppState.SnapshotDeck();
			await LoanerAdvisor.UseDeckAsync(AppState.Lib, AppState.Format, AppState.CurrentRules);
			AppState.NotifyChanged();
			_report.Text = "Deck built with loaners (marked \"loaner\"). Undo puts the previous deck back.\n\n" +
			               AppState.CurrentRules.Analyze(AppState.Lib, AppState.Format).ToString().Trim();
		}
		catch (Exception ex) { _report.Text = AppState.FriendlyError(ex); }
		finally { SetBusy(false); }
	}

	Task UndoAsync()
	{
		if (AppState.Undo == null || !AppState.Undo.IsFor(AppState.Lib)) { _report.Text = "Nothing to undo."; return Task.CompletedTask; }
		int removed = AppState.Undo.Restore();
		AppState.Undo = null;
		AppState.NotifyChanged();
		Analyze();
		if (removed > 0) _report.Text = $"Undone. Removed {removed} card{(removed == 1 ? "" : "s")} the last action had added to your collection.\n\n" + _report.Text;
		return Task.CompletedTask;
	}

	async Task StarterDecksAsync()
	{
		if (_working) return;
		var owned = AppState.Lib.Entries.Values.Where(e => e.Owned > 0).Select(e => e.Card.Number).ToHashSet();
		var decks = StarterDecks.Gundam;
		var best = decks.OrderByDescending(d => d.Cards.Keys.Count(owned.Contains)).First();
		var options = decks.Select(d =>
		{
			int m = d.Cards.Keys.Count(owned.Contains);
			return $"{d.Code} {d.Name}" + (m > 0 ? $"  ({m}/{d.Cards.Count} scanned)" : "");
		}).ToArray();
		string title = best.Cards.Keys.Any(owned.Contains) ? $"Which starter deck? Looks like {best.Code}." : "Which starter deck do you have?";
		string choice = await DisplayActionSheetAsync(title, "Cancel", null, options);
		if (choice == null || choice == "Cancel") return;
		var deck = decks[Array.IndexOf(options, choice)];
		bool own = await DisplayAlertAsync(deck.ToString(), "Add this deck's cards to your collection too? Choose No if you're only trying the list.", "Yes, I own it", "No");

		SetBusy(true);
		try
		{
			AppState.SnapshotDeck();
			string summary = await CollectionActions.BuildStarterAsync(AppState.Lib, deck, own, replaceDeck: true);
			AppState.NotifyChanged();
			_report.Text = summary + "\nUndo puts your previous deck back.\n\n" + AppState.CurrentRules.Analyze(AppState.Lib, AppState.Format).ToString().Trim();
		}
		catch (Exception ex) { _report.Text = AppState.FriendlyError(ex); }
		finally { SetBusy(false); }
	}

	async Task ShareList()
	{
		if (AppState.Lib.DeckCount == 0) { _report.Text = "The deck is empty."; return; }
		await Share.Default.RequestAsync(new ShareTextRequest { Title = $"{GameInfo.Short(AppState.Game)} deck", Text = DeckList.Format(AppState.Lib) });
	}

	async Task ImportFromClipboard()
	{
		if (_working) return;
		string text = await Clipboard.Default.GetTextAsync();
		var lines = DeckList.Parse(text);
		if (lines.Count == 0)
		{
			await DisplayAlertAsync("Import", "Copy a decklist first (one card per line, like \"4 Lightning Bolt\"), then tap Import again.", "OK");
			return;
		}
		bool replace = await DisplayAlertAsync("Import", $"Found {lines.Count} lines. Replace the current deck, or add these cards to it?", "Replace", "Add");
		SetBusy(true);
		try
		{
			AppState.SnapshotDeck();
			if (replace) foreach (var e in AppState.Lib.Entries.Values) e.InDeck = 0;
			var missing = new List<string>();
			foreach (var (qty, name, number) in lines)
			{
				var c = await AppState.Source.FindAsync(name, number == null ? null : new PrintHint { Number = number });
				if (c == null) { missing.Add(name); continue; }
				AppState.Lib.Add(c, 0, qty);
			}
			AppState.NotifyChanged();
			_report.Text = (missing.Count > 0 ? "Not found: " + string.Join(", ", missing) + "\n\n" : "") +
			               AppState.CurrentRules.Analyze(AppState.Lib, AppState.Format).ToString().Trim();
		}
		catch (Exception ex) { _report.Text = AppState.FriendlyError(ex); }
		finally { SetBusy(false); }
	}

	async Task ClearDeck()
	{
		if (AppState.Lib.DeckCount == 0) return;
		if (!await DisplayAlertAsync("Clear deck", "Take every card out of the deck? Your collection stays as it is.", "Clear", "Cancel")) return;
		AppState.SnapshotDeck();
		foreach (var e in AppState.Lib.Entries.Values) e.InDeck = 0;
		AppState.NotifyChanged();
		_report.Text = "Deck cleared. Undo brings it back.";
	}
}
