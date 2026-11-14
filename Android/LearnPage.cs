using TcgScanner;

namespace TcgMobile;

/// <summary>How to play the chosen game: the same guides as the Windows app.</summary>
public class LearnPage : ContentPage
{
	readonly VerticalStackLayout _body = new() { Spacing = 6 };
	readonly Button _official;
	bool _dirty = true;

	public LearnPage()
	{
		Title = "Learn";
		_official = Ui.Secondary("Official rules", () => Ui.OpenUrl(HowToPlay.For(AppState.Game).OfficialUrl));
		AppState.Changed += () => { _dirty = true; if (IsVisible) Render(); };
		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = 16,
				Spacing = 10,
				Children = { Ui.GamePicker(), _official, _body }
			}
		};
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		if (_dirty) Render();
	}

	void Render()
	{
		_dirty = false;
		var guide = HowToPlay.For(AppState.Game);
		_official.Text = guide.OfficialLabel;
		_body.Children.Clear();
		_body.Children.Add(Ui.Title(guide.Title));
		_body.Children.Add(new Label { Text = guide.Intro });
		foreach (var (heading, lines) in guide.Sections)
		{
			_body.Children.Add(new Label { Text = heading, FontSize = 18, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 10, 0, 0) }.AccentText());
			foreach (var line in lines)
			{
				bool bullet = line.StartsWith("• ");
				var text = new FormattedString();
				if (bullet) text.Spans.Add(new Span { Text = "•  " });
				var parts = (bullet ? line[2..] : line).Split("**");
				for (int i = 0; i < parts.Length; i++)
					text.Spans.Add(new Span { Text = parts[i], FontAttributes = i % 2 == 1 ? FontAttributes.Bold : FontAttributes.None });
				_body.Children.Add(new Label { FormattedText = text, Margin = new Thickness(bullet ? 6 : 0, 0, 0, 0) });
			}
		}
		_body.Children.Add(Ui.Note("This guide is a summary for learning. The official rules have the full details and the final word."));
	}
}
