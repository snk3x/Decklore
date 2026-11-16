using TcgScanner;

namespace TcgMobile;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		AppState.Init();
		// The JustTCG key lives in Android's secure storage instead of a file.
		JustTcg.Secrets = SecureSecretStore.Create();
	}

	protected override Window CreateWindow(IActivationState activationState) => new(new AppShell()) { Title = "Decklore" };
}
