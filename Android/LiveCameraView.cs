using TcgScanner;

namespace TcgMobile;

/// <summary>
/// A live camera preview that reads cards continuously. Each readable frame is turned into a scan result and
/// handed to <see cref="FrameScanned"/>; the next frame is only read after that finishes, so lookups never pile up.
/// </summary>
public class LiveCameraView : View
{
	public static readonly BindableProperty IsActiveProperty =
		BindableProperty.Create(nameof(IsActive), typeof(bool), typeof(LiveCameraView), false);

	/// <summary>True while the camera should run. Turn it off when the page is hidden to save battery.</summary>
	public bool IsActive
	{
		get => (bool)GetValue(IsActiveProperty);
		set => SetValue(IsActiveProperty, value);
	}

	/// <summary>Called on the main thread for each frame that was read.</summary>
	public Func<ScanResult, Task> FrameScanned { get; set; }

	/// <summary>Called on the main thread if the camera can't start.</summary>
	public Action<string> CameraError { get; set; }
}
