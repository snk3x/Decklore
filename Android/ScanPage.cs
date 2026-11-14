using TcgScanner;

namespace TcgMobile;

/// <summary>Take or choose a card photo, or type a name; shows the matched card, its value and printing.</summary>
public class ScanPage : ContentPage
{
	readonly Entry _name = new() { Placeholder = "Or type a card name", ReturnType = ReturnType.Search };
	readonly Label _status = Ui.Note("Fill the photo with one card, held flat, in good light.");
	readonly ActivityIndicator _busy = new() { IsRunning = false, IsVisible = false, Color = Ui.Accent };
	readonly Image _image = new() { HeightRequest = 340, Aspect = Aspect.AspectFit };
	readonly Label _cardName = new() { FontSize = 19, FontAttributes = FontAttributes.Bold };
	readonly Label _cardInfo = Ui.Note();
	readonly Label _cardValue = new Label { FontSize = 16, FontAttributes = FontAttributes.Bold }.GoodText();
	readonly Label _session = Ui.Note();
	readonly Border _result;
	readonly LiveCameraView _camera = new() { HeightRequest = 460 };
	readonly Border _frame = new() { StrokeThickness = 4, BackgroundColor = Colors.Transparent, Margin = new Thickness(28, 18), InputTransparent = true };
	readonly Label _liveStatus = new() { TextColor = Colors.White, FontSize = 15, FontAttributes = FontAttributes.Bold, Padding = new Thickness(10, 6), HorizontalTextAlignment = TextAlignment.Center };
	readonly Button _liveButton;
	readonly AutoScanSession _auto = new();
	bool _liveWanted = true, _permissionAsked;
	bool _sound = Preferences.Default.Get("scan-sound", true);
	bool _autoDetect = true, _addToDeck, _working;
	LibraryEntry _last;
	bool _lastToDeck;
	int _sessionCount;
	double _sessionValue;

	public ScanPage()
	{
		Title = "Scan";
		_name.Completed += async (s, e) => await AddByName();

		_result = Ui.Card(new VerticalStackLayout
		{
			Spacing = 6,
			Children =
			{
				_image, _cardName, _cardInfo, _cardValue,
				Ui.Row(Ui.Secondary("Undo", UndoLast), Ui.Secondary("Pick printing", PickPrinting)),
			}
		});
		_result.IsVisible = false;

		// Live scan: the camera preview with a guide frame and a status line on top of it.
		_camera.FrameScanned = OnFrameScanned;
		_camera.CameraError = msg => _liveStatus.Text = msg;
		_liveStatus.Text = "Point the camera at a card.";
		SetFrameColor("#4ADE80");
		var liveArea = new Grid { HeightRequest = 460, BackgroundColor = Colors.Black };
		liveArea.Add(_camera);
		liveArea.Add(_frame);
		liveArea.Add(new Border
		{
			Content = _liveStatus, BackgroundColor = Color.FromArgb("#99000000"), StrokeThickness = 0,
			VerticalOptions = LayoutOptions.End, Margin = new Thickness(0, 0, 0, 10), HorizontalOptions = LayoutOptions.Center,
			StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 }, InputTransparent = true,
		});
		_liveButton = Ui.Secondary("Pause camera", ToggleLive);

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = 16,
				Spacing = 12,
				Children =
				{
					Ui.Title("Scan cards"),
					Ui.GamePicker(),
					liveArea,
					Ui.Note("Hold one card flat inside the frame. It's added automatically; swap in the next card when you hear the beep."),
					_liveButton,
					Ui.Toggle("Beep when a card is added", _sound, v =>
					{
						_sound = v;
						Preferences.Default.Set("scan-sound", v);
						if (v) AddedFeedback.Play(true); // let the user hear it right away
					}),
					Ui.Row(Ui.Secondary("Take photo", () => Scan(fromCamera: true)), Ui.Secondary("Choose photo", () => Scan(fromCamera: false))),
					new Grid
					{
						ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8,
						Children = { _name, WithColumn(Ui.Secondary("Add", AddByName), 1) }
					},
					Ui.Toggle("Detect the game automatically", true, v => _autoDetect = v),
					Ui.Toggle("Also add scanned cards to the deck", false, v => _addToDeck = v),
					new Grid
					{
						ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star) }, ColumnSpacing = 8,
						Children = { _busy, WithColumn(_status, 1) }
					},
					_result,
					_session,
				}
			}
		};
	}

	static View WithColumn(View v, int col) { Grid.SetColumn(v, col); return v; }

	// ------------------------------------------------------------------ live scan

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (_liveWanted) await StartLive();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_camera.IsActive = false; // stop the camera when leaving the tab, to save battery
	}

	async Task StartLive()
	{
		var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
		if (status != PermissionStatus.Granted && !_permissionAsked)
		{
			_permissionAsked = true;
			status = await Permissions.RequestAsync<Permissions.Camera>();
		}
		if (status != PermissionStatus.Granted)
		{
			_liveStatus.Text = "Camera permission is off. Allow it in Android Settings > Apps > TCG deck scanner.";
			return;
		}
		_camera.IsActive = true;
		_liveButton.Text = "Pause camera";
	}

	async Task ToggleLive()
	{
		_liveWanted = !_camera.IsActive;
		if (_liveWanted) await StartLive();
		else
		{
			_camera.IsActive = false;
			_liveButton.Text = "Resume camera";
			_liveStatus.Text = "Camera paused.";
		}
	}

	void SetFrameColor(string hex) =>
		_frame.Stroke = new SolidColorBrush(Color.FromArgb(hex));

	/// <summary>Runs for each live frame: decides via the shared AutoScanSession whether a card should be added.</summary>
	async Task OnFrameScanned(ScanResult scan)
	{
		if (_working || !_camera.IsActive) return;
		try
		{
			var card = await _auto.ProcessAsync(scan, FindCardAsync, DateTime.Now);
			_liveStatus.Text = _auto.Status;
			if (card != null)
			{
				AddCard(card);
				AddedFeedback.Play(_sound);
				SetFrameColor("#00FF78");
				_frame.StrokeThickness = 9;
				await Task.Delay(700);
				_frame.StrokeThickness = 4;
			}
			SetFrameColor(_auto.Status.StartsWith("Checking") ? "#FBBF24" : "#4ADE80");
		}
		catch (Exception ex) { _liveStatus.Text = AppState.FriendlyError(ex); }
	}

	/// <summary>Finds the card a scan shows, trying the likely games in order.</summary>
	async Task<Card> FindCardAsync(ScanResult scan)
	{
		var games = !_autoDetect ? new List<Game> { AppState.Game }
			: scan.GameConfident ? scan.GameGuesses.Take(1).ToList()
			: new[] { AppState.Game }.Concat(scan.GameGuesses.Where(g => g != AppState.Game)).ToList();
		foreach (var g in games)
		{
			var found = await CardMatcher.FindBestAsync(AppState.Sources[g], scan, g);
			if (found != null) return found;
		}
		return null;
	}

	void SetBusy(bool busy, string message = null)
	{
		_working = busy;
		_busy.IsRunning = _busy.IsVisible = busy;
		if (message != null) _status.Text = message;
	}

	async Task Scan(bool fromCamera)
	{
		if (_working) return;
		try
		{
			if (fromCamera && !MediaPicker.Default.IsCaptureSupported)
			{
				await DisplayAlertAsync("No camera", "This device has no camera. Choose a photo instead.", "OK");
				return;
			}
			var photo = fromCamera
				? await MediaPicker.Default.CapturePhotoAsync()
				: (await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { SelectionLimit = 1 }))?.FirstOrDefault();
			if (photo == null) return;

			SetBusy(true, "Reading the card...");
			string path = photo.FullPath;
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
			{
				// Picked photos may only be readable as a stream; copy them somewhere ML Kit can open.
				path = Path.Combine(FileSystem.CacheDirectory, "scan.jpg");
				await using var src = await photo.OpenReadAsync();
				await using var dst = File.Create(path);
				await src.CopyToAsync(dst);
			}

			var scan = await MlKitReader.ReadAsync(path);
			if (scan.NameCandidates.Count == 0)
			{
				SetBusy(false, "No text found. Try more light, less glare, and fill the photo with the card.");
				return;
			}

			var found = await FindCardAsync(scan);
			if (found == null)
			{
				SetBusy(false, "No matching card. Try again in better light, or type the name.");
				return;
			}
			AddCard(found);
		}
		catch (PermissionException)
		{
			SetBusy(false, "Camera permission is needed to scan. Allow it in Android Settings > Apps > TCG deck scanner.");
		}
		catch (Exception ex)
		{
			SetBusy(false, AppState.FriendlyError(ex));
		}
	}

	async Task AddByName()
	{
		string name = _name.Text?.Trim();
		if (string.IsNullOrEmpty(name) || name.Length < 2 || _working) return;
		SetBusy(true, $"Looking up \"{name}\"...");
		try
		{
			var card = await AppState.Source.FindAsync(name, null);
			if (card == null) { SetBusy(false, $"No {GameInfo.Short(AppState.Game)} card found for \"{name}\"."); return; }
			_name.Text = "";
			AddCard(card);
		}
		catch (Exception ex) { SetBusy(false, AppState.FriendlyError(ex)); }
	}

	void AddCard(Card c)
	{
		if (c.Game != AppState.Game) AppState.Game = c.Game;
		var e = AppState.Lib.Add(c, 1, _addToDeck ? 1 : 0);
		_last = e;
		_lastToDeck = _addToDeck;
		_sessionCount++;
		_sessionValue += c.PriceUsd;
		AppState.NotifyChanged();
		ShowCard(e);
		string price = c.PriceUsd > 0 ? Money.Format(c.PriceUsd) : "no price";
		SetBusy(false, c.ExactPrinting ? $"Added {c.Name} ({price})." : $"Added {c.Name} ({price}). Tap Pick printing if this isn't your version.");
	}

	void ShowCard(LibraryEntry e)
	{
		var c = e.Card;
		_result.IsVisible = true;
		_image.Source = string.IsNullOrEmpty(c.ImageUrl) ? null : ImageSource.FromUri(new Uri(c.ImageUrl));
		_cardName.Text = c.Name;
		string printing = c.ExactPrinting ? "matched from the card" : "best guess";
		_cardInfo.Text = $"{c.TypeLine}\n{c.SetName} {(c.Number != "" ? "#" + c.Number : "")} ({printing})\n" +
		                 $"{AppState.Format}: {AppState.CurrentRules.LegalityLabel(c, AppState.Format)}   ·   Owned: {e.Owned}";
		_cardValue.Text = c.PriceUsd > 0 ? $"{Money.Format(c.PriceUsd)}  ({c.PriceSource})" : "No price listed";
		_session.Text = SessionText();
	}

	string SessionText() =>
		$"This session: {_sessionCount} card{(_sessionCount == 1 ? "" : "s")} scanned, worth {(_sessionValue > 0 ? Money.Format(_sessionValue) : "$0.00")}";

	Task UndoLast()
	{
		_auto.Reset();
		var e = _last;
		var lib = AppState.Data.Libraries.Values.FirstOrDefault(l => e != null && l.Entries.ContainsValue(e));
		if (lib == null) { _status.Text = "Nothing to undo."; return Task.CompletedTask; }
		e.Owned = Math.Max(0, e.Owned - 1);
		if (_lastToDeck) e.InDeck = Math.Max(0, e.InDeck - 1);
		if (e.Owned == 0 && e.InDeck == 0) lib.Remove(e);
		_sessionCount = Math.Max(0, _sessionCount - 1);
		_sessionValue = Math.Max(0, _sessionValue - e.Card.PriceUsd);
		_last = null;
		_result.IsVisible = false;
		_session.Text = SessionText();
		_status.Text = $"Removed {e.Card.Name}.";
		AppState.NotifyChanged();
		return Task.CompletedTask;
	}

	async Task PickPrinting()
	{
		if (_last == null || _working) return;
		var moved = await Printing.PickAsync(this, _last);
		if (moved == null) return;
		_sessionValue = Math.Max(0, _sessionValue - _last.Card.PriceUsd + moved.Card.PriceUsd);
		_last = moved;
		ShowCard(moved);
	}
}

/// <summary>Lets the user choose which printing of a card they have.</summary>
public static class Printing
{
	public static async Task<LibraryEntry> PickAsync(Page page, LibraryEntry entry)
	{
		try
		{
			var source = AppState.Sources[entry.Card.Game];
			var prints = await source.PrintingsAsync(entry.Card);
			if (prints.Count == 0) { await page.DisplayAlertAsync("Pick printing", "No other printings found.", "OK"); return null; }
			var options = prints.Take(60)
				.Select((p, i) => $"{i + 1}. {p.SetName} {p.Number} {p.Rarity} {Money.Format(p.PriceUsd)}".Trim())
				.ToArray();
			string choice = await page.DisplayActionSheetAsync($"Which {entry.Card.Name} do you have?", "Cancel", null, options);
			if (choice == null || choice == "Cancel") return null;
			int index = int.Parse(choice[..choice.IndexOf('.')]) - 1;
			var full = await source.RefreshAsync(prints[index]) ?? prints[index];
			var lib = AppState.Data.Get(entry.Card.Game);
			var moved = CollectionActions.ChangePrinting(lib, entry, full);
			AppState.NotifyChanged();
			return moved;
		}
		catch (Exception ex)
		{
			await page.DisplayAlertAsync("Pick printing", AppState.FriendlyError(ex), "OK");
			return null;
		}
	}
}
