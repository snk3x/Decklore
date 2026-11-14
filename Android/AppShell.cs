namespace TcgMobile;

/// <summary>Bottom tab bar: Scan, Collection, Deck, Learn, Settings.</summary>
public class AppShell : Shell
{
	public AppShell()
	{
		FlyoutBehavior = FlyoutBehavior.Disabled;
		var tabs = new TabBar();
		tabs.Items.Add(Tab("Scan", "tab_scan.png", new ScanPage()));
		tabs.Items.Add(Tab("Collection", "tab_collection.png", new CollectionPage()));
		tabs.Items.Add(Tab("Deck", "tab_deck.png", new DeckPage()));
		tabs.Items.Add(Tab("Learn", "tab_learn.png", new LearnPage()));
		tabs.Items.Add(Tab("Settings", "tab_settings.png", new SettingsPage()));
		Items.Add(tabs);
	}

	static ShellContent Tab(string title, string icon, Page page)
	{
		// Each page shows its own large title, so hide the extra bar at the top.
		Shell.SetNavBarIsVisible(page, false);
		return new() { Title = title, Icon = icon, Content = page };
	}
}
