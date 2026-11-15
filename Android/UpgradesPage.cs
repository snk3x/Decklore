using TcgScanner;

namespace TcgMobile;

/// <summary>
/// Stronger cards for the weakest cards in the deck, as a list with prices. Nothing in the deck changes;
/// checked cards can be saved to the collection as wishlist loaners for Loaner picks to test.
/// </summary>
public class UpgradesPage : ContentPage
{
	readonly Label _status = Ui.Note("Looking for stronger cards for the weakest cards in your deck...");
	readonly ActivityIndicator _busy = new() { Color = Ui.Accent, IsRunning = true };
	readonly VerticalStackLayout _list = new() { Spacing = 8 };
	readonly Button _save;
	readonly List<(CheckBox box, UpgradeSuggestion sug, UpgradeOption option)> _rows = new();
	readonly CancellationTokenSource _cts = new();
	bool _loaded;

	public UpgradesPage()
	{
		Title = "Upgrades";
		_save = Ui.Primary("Add checked to wishlist", SaveAsync);
		_save.IsEnabled = false;
		Content = new Grid
		{
			RowDefinitions = { new(GridLength.Star), new(GridLength.Auto) },
			Children =
			{
				new ScrollView
				{
					Content = new VerticalStackLayout
					{
						Padding = 16,
						Spacing = 10,
						Children =
						{
							Ui.Title("Upgrades"),
							Ui.Note("Stronger cards for the weakest cards in your deck, strongest first. Your deck doesn't change. " +
							        "Cards you add to your wishlist are saved as loaners, and Loaner picks shows the deck with them."),
							_busy,
							_status,
							_list,
						}
					}
				},
			}
		};
		var buttons = Ui.Row(_save, Ui.Secondary("Close", CloseAsync));
		buttons.Padding = new Thickness(16, 8, 16, 16);
		((Grid)Content).Add(buttons, 0, 1);
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (_loaded) return;
		_loaded = true;
		try
		{
			var suggestions = await UpgradeAdvisor.SuggestAsync(AppState.Game, AppState.Lib, AppState.Format, AppState.CurrentRules,
				AppState.Source, name => MainThread.BeginInvokeOnMainThread(() => _status.Text = $"Checking {name}..."), _cts.Token);
			foreach (var sug in suggestions)
			{
				var c = sug.Current;
				_list.Children.Add(Ui.Heading($"{sug.Copies}× {c.Name}"));
				_list.Children.Add(Ui.Note($"Rating {(int)Math.Round(c.Quality * 100)}" + (c.PriceUsd > 0 ? $", {Money.Format(c.PriceUsd)} each" : "")));
				foreach (var o in sug.Options)
				{
					var box = new CheckBox { Color = Ui.Accent, VerticalOptions = LayoutOptions.Center };
					box.CheckedChanged += (s, e) => _save.IsEnabled = _rows.Any(r => r.box.IsChecked);
					var name = new Label { Text = o.Label, FontAttributes = FontAttributes.Bold };
					var price = new Label { Text = o.Price > 0 ? Money.Format(o.Price) : "price unknown", FontAttributes = FontAttributes.Bold,
					                        VerticalOptions = LayoutOptions.Center }.GoodText();
					var grid = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
					grid.Add(box, 0);
					grid.Add(new VerticalStackLayout { Children = { name, Ui.Note(o.Why) } }, 1);
					grid.Add(price, 2);
					// Tapping the row toggles its check box.
					var tap = new TapGestureRecognizer();
					tap.Tapped += (s, e) => box.IsChecked = !box.IsChecked;
					grid.GestureRecognizers.Add(tap);
					_list.Children.Add(Ui.Card(grid));
					_rows.Add((box, sug, o));
				}
			}
			int count = _rows.Count;
			_status.Text = count == 0
				? "No stronger cards found for this deck's weakest cards. It may already use the best options in its colors."
				: $"{count} recommended upgrades for {suggestions.Count} of your weakest cards. Prices are per card.";
		}
		catch (OperationCanceledException) { }
		catch (Exception ex) { _status.Text = AppState.FriendlyError(ex); }
		finally { _busy.IsRunning = _busy.IsVisible = false; }
	}

	Task SaveAsync()
	{
		var picks = _rows.Where(r => r.box.IsChecked).ToList();
		if (picks.Count == 0) return Task.CompletedTask;
		foreach (var (box, sug, option) in picks)
		{
			UpgradeAdvisor.AddToWishlist(AppState.Lib, sug, option);
			box.IsChecked = false;
		}
		AppState.Undo = null;
		AppState.NotifyChanged();
		_status.Text = $"Added {picks.Count} card{(picks.Count == 1 ? "" : "s")} to your wishlist. Find them on the Collection tab under Loaners only.";
		return Task.CompletedTask;
	}

	async Task CloseAsync()
	{
		_cts.Cancel();
		await Navigation.PopModalAsync();
	}

	protected override bool OnBackButtonPressed()
	{
		_cts.Cancel();
		return base.OnBackButtonPressed();
	}
}
