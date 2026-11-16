using TcgScanner;

namespace TcgMobile;

/// <summary>JustTCG key for Cyberpunk and Gundam prices, plus credits.</summary>
public class SettingsPage : ContentPage
{
	readonly Entry _key = new() { IsPassword = true, Placeholder = "Paste your API key (starts with tcg_)" };
	readonly Label _result = Ui.Note();

	public SettingsPage()
	{
		Title = "Settings";
		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = 16,
				Spacing = 10,
				Children =
				{
					Ui.Title("Settings"),
					Ui.Heading("Cyberpunk and Gundam prices"),
					new Label { Text = "Magic, Pokémon and Yu-Gi-Oh! prices need no setup. Cyberpunk and Gundam prices come from JustTCG, which needs your own free API key. The free plan is for personal use and allows 100 requests a day." },
					Ui.Secondary("Get a free key at justtcg.com", () => Ui.OpenUrl("https://justtcg.com/pricing")),
					_key,
					Ui.Row(Ui.Primary("Test and save", SaveKey), Ui.Secondary("Remove key", RemoveKey)),
					_result,
					Ui.Heading("About"),
					new Label { Text = $"Decklore {AppInfo.Current.VersionString}. An unofficial fan project, not affiliated with the companies behind these games." },
					new Label { Text = "Thanks to Levi for field testing the app and suggesting improvements." },
					Ui.Note("Card data: Scryfall, TCGdex, YGOPRODeck, CyberpunkDB, and gcg-api (https://gcgapi.com, Open Database License). Prices: TCGplayer via those services and JustTCG. Text recognition runs on your phone with Google ML Kit."),
					Ui.Secondary("Project on GitHub", () => Ui.OpenUrl("https://github.com/snk3x/Decklore")),
				}
			}
		};
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		_key.Placeholder = JustTcg.HasKey ? "A key is saved. Paste a new one to replace it." : "Paste your API key (starts with tcg_)";
	}

	async Task SaveKey()
	{
		string key = _key.Text?.Trim() ?? "";
		if (key.Length < 8) { _result.Text = "Paste your JustTCG API key first."; return; }
		_result.Text = "Checking the key...";
		try
		{
			_result.Text = await JustTcg.TestKeyAsync(key) + " Saved.";
			JustTcg.SaveKey(key);
			_key.Text = "";
			_key.Placeholder = "A key is saved. Paste a new one to replace it.";
			AppState.NotifyChanged();
		}
		catch (Exception ex) { _result.Text = AppState.FriendlyError(ex); }
	}

	Task RemoveKey()
	{
		JustTcg.RemoveKey();
		_result.Text = "Key removed.";
		_key.Placeholder = "Paste your API key (starts with tcg_)";
		AppState.NotifyChanged();
		return Task.CompletedTask;
	}
}
