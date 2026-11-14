using TcgScanner;

namespace TcgMobile;

/// <summary>Every scanned card for the chosen game, with counts and values. Tap a card for actions.</summary>
public class CollectionPage : ContentPage
{
	public record Row(LibraryEntry Entry, string Name, string Detail, string Price, Color LegalColor);

	readonly SearchBar _search = new() { Placeholder = "Filter by name or type" };
	readonly Label _summary = new() { FontAttributes = FontAttributes.Bold };
	readonly CollectionView _list;
	bool _dirty = true;

	public CollectionPage()
	{
		Title = "Collection";
		_search.TextChanged += (s, e) => Refresh();
		AppState.Changed += () => { _dirty = true; if (IsVisible) Refresh(); };

		_list = new CollectionView
		{
			SelectionMode = SelectionMode.None,
			EmptyView = new Label { Text = "No cards yet. Scan some on the Scan tab.", Margin = 20 }.MutedText(),
			ItemTemplate = new DataTemplate(() =>
			{
				var name = new Label { FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.TailTruncation };
				name.SetBinding(Label.TextProperty, nameof(Row.Name));
				var detail = new Label { FontSize = 12 }.MutedText();
				detail.SetBinding(Label.TextProperty, nameof(Row.Detail));
				var price = new Label { FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center }.GoodText();
				price.SetBinding(Label.TextProperty, nameof(Row.Price));
				var grid = new Grid
				{
					Padding = new Thickness(16, 8),
					ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) },
					Children = { new VerticalStackLayout { Children = { name, detail } } },
				};
				grid.Add(price, 1);
				var tap = new TapGestureRecognizer();
				tap.Tapped += async (s, e) => { if (((BindableObject)s).BindingContext is Row r) await Actions(r.Entry); };
				grid.GestureRecognizers.Add(tap);
				return grid;
			}),
		};

		var header = new VerticalStackLayout
		{
			Padding = new Thickness(16, 16, 16, 4),
			Spacing = 8,
			Children =
			{
				Ui.Title("Collection"),
				Ui.GamePicker(),
				_search,
				_summary,
				Ui.Row(Ui.Secondary("Refresh prices", RefreshPrices), Ui.Secondary("Share as CSV", ShareCsv)),
			}
		};
		var grid = new Grid { RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) } };
		grid.Add(header, 0, 0);
		grid.Add(_list, 0, 1);
		Content = grid;
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
		string f = _search.Text?.Trim() ?? "";
		_list.ItemsSource = AppState.Lib.Entries.Values
			.Where(e => f == "" || e.Card.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || e.Card.TypeLine.Contains(f, StringComparison.OrdinalIgnoreCase))
			.OrderBy(e => e.Card.Name)
			.Select(e =>
			{
				string legal = rules.LegalityLabel(e.Card, AppState.Format);
				string detail = $"{e.Card.TypeLine}\n{e.Card.SetName} {e.Card.Number}   ·   Owned {e.Owned}   ·   In deck {e.InDeck}" +
				                (legal is "Legal" ? "" : $"   ·   {legal}");
				return new Row(e, e.Card.Name, detail, e.Card.PriceUsd > 0 ? Money.Format(e.Card.PriceUsd) : "", Ui.Good);
			})
			.ToList();

		var owned = AppState.Lib.Entries.Values.Where(e => e.Owned > 0).ToList();
		double value = owned.Sum(e => e.Owned * e.Card.PriceUsd);
		_summary.Text = $"{owned.Sum(e => e.Owned)} cards owned   ·   Value {(value > 0 ? Money.Format(value) : "$0.00")}" +
		                (JustTcg.Games.ContainsKey(AppState.Game) && !JustTcg.HasKey ? "\nAdd a free JustTCG key in Settings for prices." : "");
	}

	async Task Actions(LibraryEntry e)
	{
		string choice = await DisplayActionSheetAsync(e.Card.Name, "Cancel", "Delete from collection",
			"Add 1 to deck", "Take 1 out of deck", "I own one more", "I own one less", "Pick printing");
		switch (choice)
		{
			case "Add 1 to deck": e.InDeck++; break;
			case "Take 1 out of deck": e.InDeck = Math.Max(0, e.InDeck - 1); break;
			case "I own one more": e.Owned++; break;
			case "I own one less": e.Owned = Math.Max(0, e.Owned - 1); break;
			case "Pick printing": await Printing.PickAsync(this, e); return;
			case "Delete from collection":
				if (!await DisplayAlertAsync("Delete card", $"Delete {e.Card.Name} from your collection? This can't be undone.", "Delete", "Cancel")) return;
				AppState.Lib.Remove(e);
				break;
			default: return;
		}
		AppState.NotifyChanged();
	}

	async Task RefreshPrices()
	{
		var entries = AppState.Lib.Entries.Values.ToList();
		if (entries.Count == 0) return;
		int done = 0, failed = 0;
		try
		{
			if (AppState.Game == Game.Cyberpunk && JustTcg.HasKey) await JustTcg.PricesAsync(Game.Cyberpunk, force: true);
			if (AppState.Game == Game.Gundam && JustTcg.HasKey) JustTcg.ExpireGundamPrices();
			if (AppState.Game == Game.Gundam) await GundamSource.CatalogueAsync(force: true);
			foreach (var e in entries)
			{
				_summary.Text = $"Updating prices ({++done} of {entries.Count})...";
				var fresh = await AppState.Source.RefreshAsync(e.Card);
				if (fresh == null) { failed++; continue; }
				e.Card = fresh;
			}
			AppState.NotifyChanged();
			await DisplayAlertAsync("Prices updated", $"Updated {done - failed} cards." + (failed > 0 ? $" {failed} couldn't be found." : ""), "OK");
		}
		catch (Exception ex)
		{
			AppState.NotifyChanged();
			await DisplayAlertAsync("Refresh prices", AppState.FriendlyError(ex), "OK");
		}
	}

	async Task ShareCsv()
	{
		if (!AppState.Lib.Entries.Values.Any(e => e.Owned > 0)) return;
		string path = Path.Combine(FileSystem.CacheDirectory, $"{GameInfo.Short(AppState.Game)} collection.csv");
		File.WriteAllText(path, DeckList.Csv(AppState.Lib), new System.Text.UTF8Encoding(true));
		await Share.Default.RequestAsync(new ShareFileRequest { Title = "Collection", File = new ShareFile(path, "text/csv") });
	}
}
