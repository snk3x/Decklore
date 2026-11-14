using TcgScanner;

namespace TcgMobile;

/// <summary>Small building blocks shared by the pages.</summary>
public static class Ui
{
	// Colors come in light and dark pairs so the app reads well in either phone theme.
	static bool Dark => Application.Current?.RequestedTheme == AppTheme.Dark;
	static Color Pick(string light, string dark) => Color.FromArgb(Dark ? dark : light);

	public static Color Accent => Pick("#283C8C", "#9DB0FF");
	public static Color Muted => Pick("#6B7280", "#A1A1AA");
	public static Color Good => Pick("#15803D", "#4ADE80");
	public static Color Warn => Pick("#B45309", "#FBBF24");
	public static Color Bad => Pick("#B91C1C", "#F87171");

	/// <summary>Sets a color that switches automatically when the phone changes between light and dark mode.</summary>
	public static T Themed<T>(this T view, BindableProperty property, string light, string dark) where T : BindableObject
	{
		view.SetAppThemeColor(property, Color.FromArgb(light), Color.FromArgb(dark));
		return view;
	}

	public static Label AccentText(this Label l) => l.Themed(Label.TextColorProperty, "#283C8C", "#9DB0FF");
	public static Label MutedText(this Label l) => l.Themed(Label.TextColorProperty, "#6B7280", "#A1A1AA");
	public static Label GoodText(this Label l) => l.Themed(Label.TextColorProperty, "#15803D", "#4ADE80");

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

	public static Label Title(string text) => new Label { Text = text, FontSize = 24, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 8, 0, 0) }.AccentText();
	public static Label Heading(string text) => new() { Text = text, FontSize = 17, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 8, 0, 0) };
	public static Label Note(string text = "") => new Label { Text = text, FontSize = 13 }.MutedText();

	public static Button Primary(string text, Func<Task> onClick)
	{
		var b = new Button { Text = text, TextColor = Colors.White, FontAttributes = FontAttributes.Bold }
			.Themed(VisualElement.BackgroundColorProperty, "#283C8C", "#3D56B8");
		b.Clicked += async (s, e) => await onClick();
		return b;
	}

	public static Button Secondary(string text, Func<Task> onClick)
	{
		var b = new Button { Text = text }
			.Themed(VisualElement.BackgroundColorProperty, "#E5E7EB", "#3A3A3C")
			.Themed(Button.TextColorProperty, "#111827", "#F3F4F6");
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

	public static Border Card(View content)
	{
		var border = new Border
		{
			Content = content,
			Padding = 12,
			StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
		}.Themed(VisualElement.BackgroundColorProperty, "#FFFFFF", "#2A2A2C");
		border.SetAppTheme<Brush>(Border.StrokeProperty, new SolidColorBrush(Color.FromArgb("#D1D5DB")), new SolidColorBrush(Color.FromArgb("#48484A")));
		return border;
	}

	public static HorizontalStackLayout Toggle(string text, bool on, Action<bool> changed)
	{
		var sw = new Switch { IsToggled = on };
		sw.Toggled += (s, e) => changed(e.Value);
		return new HorizontalStackLayout { Spacing = 8, Children = { sw, new Label { Text = text, VerticalOptions = LayoutOptions.Center } } };
	}

	public static Task OpenUrl(string url) => Launcher.Default.OpenAsync(new Uri(url));
}
