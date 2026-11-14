using TcgScanner;

namespace TcgMobile;

/// <summary>Small building blocks shared by the pages.</summary>
public static class Ui
{
	public static readonly Color Accent = Color.FromArgb("#283C8C");
	public static readonly Color Muted = Color.FromArgb("#6B7280");
	public static readonly Color Good = Color.FromArgb("#15803D");
	public static readonly Color Warn = Color.FromArgb("#B45309");
	public static readonly Color Bad = Color.FromArgb("#B91C1C");

	/// <summary>A game picker that stays in sync with every other page.</summary>
	public static Picker GamePicker()
	{
		var picker = new Picker { Title = "Game", ItemsSource = AppState.GameNames, SelectedIndex = (int)AppState.Game };
		bool updating = false;
		picker.SelectedIndexChanged += (s, e) =>
		{
			if (updating || picker.SelectedIndex < 0) return;
			AppState.Game = (Game)picker.SelectedIndex;
		};
		AppState.Changed += () =>
		{
			if (picker.SelectedIndex == (int)AppState.Game) return;
			updating = true;
			picker.SelectedIndex = (int)AppState.Game;
			updating = false;
		};
		return picker;
	}

	public static Label Title(string text) => new() { Text = text, FontSize = 22, FontAttributes = FontAttributes.Bold, TextColor = Accent };
	public static Label Heading(string text) => new() { Text = text, FontSize = 17, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 8, 0, 0) };
	public static Label Note(string text = "") => new() { Text = text, FontSize = 13, TextColor = Muted };

	public static Button Primary(string text, Func<Task> onClick)
	{
		var b = new Button { Text = text, BackgroundColor = Accent, TextColor = Colors.White, FontAttributes = FontAttributes.Bold };
		b.Clicked += async (s, e) => await onClick();
		return b;
	}

	public static Button Secondary(string text, Func<Task> onClick)
	{
		var b = new Button { Text = text, BackgroundColor = Color.FromArgb("#E5E7EB"), TextColor = Color.FromArgb("#111827") };
		b.Clicked += async (s, e) => await onClick();
		return b;
	}

	/// <summary>Two buttons side by side.</summary>
	public static Grid Row(View left, View right)
	{
		var g = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 8 };
		g.Add(left, 0);
		g.Add(right, 1);
		return g;
	}

	public static Border Card(View content) => new()
	{
		Content = content,
		Padding = 12,
		StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
		Stroke = Color.FromArgb("#D1D5DB"),
		BackgroundColor = Colors.White,
	};

	public static HorizontalStackLayout Toggle(string text, bool on, Action<bool> changed)
	{
		var sw = new Switch { IsToggled = on };
		sw.Toggled += (s, e) => changed(e.Value);
		return new HorizontalStackLayout { Spacing = 8, Children = { sw, new Label { Text = text, VerticalOptions = LayoutOptions.Center } } };
	}

	public static Task OpenUrl(string url) => Launcher.Default.OpenAsync(new Uri(url));
}
