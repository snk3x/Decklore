using AndroidX.Camera.Core;
using AndroidX.Camera.Core.ResolutionSelector;
using AndroidX.Camera.Lifecycle;
using AndroidX.Camera.View;
using AndroidX.Core.Content;
using AndroidX.Lifecycle;
using Java.Util.Concurrent;
using Microsoft.Maui.Handlers;

namespace TcgMobile;

/// <summary>Android side of <see cref="LiveCameraView"/>: CameraX preview plus a frame analyzer feeding ML Kit.</summary>
public class LiveCameraHandler : ViewHandler<LiveCameraView, PreviewView>
{
	public static readonly IPropertyMapper<LiveCameraView, LiveCameraHandler> Mapper =
		new PropertyMapper<LiveCameraView, LiveCameraHandler>(ViewMapper)
		{
			[nameof(LiveCameraView.IsActive)] = (h, v) => h.UpdateActive(),
		};

	ProcessCameraProvider _provider;
	IExecutorService _executor;
	bool _bound;

	public LiveCameraHandler() : base(Mapper) { }

	protected override PreviewView CreatePlatformView() => new(Context);

	protected override void DisconnectHandler(PreviewView platformView)
	{
		Stop();
		_executor?.Shutdown();
		_executor = null;
		base.DisconnectHandler(platformView);
	}

	void UpdateActive()
	{
		if (VirtualView?.IsActive == true) Start();
		else Stop();
	}

	void Start()
	{
		if (_bound || PlatformView == null) return;
		var future = ProcessCameraProvider.GetInstance(Context);
		future.AddListener(new Java.Lang.Runnable(() =>
		{
			try
			{
				_provider = (ProcessCameraProvider)future.Get();
				Bind();
			}
			catch (Exception ex)
			{
				VirtualView?.CameraError?.Invoke("Couldn't start the camera: " + ex.Message);
			}
		}), ContextCompat.GetMainExecutor(Context));
	}

	void Bind()
	{
		if (_provider == null || VirtualView?.IsActive != true) return;
		_provider.UnbindAll();

		var preview = new Preview.Builder().Build();
		preview.SetSurfaceProvider(ContextCompat.GetMainExecutor(Context), PlatformView.SurfaceProvider);

		// Card text is small, so analyze frames at a fairly high resolution.
		var resolution = new ResolutionSelector.Builder()
			.SetResolutionStrategy(new ResolutionStrategy(new Android.Util.Size(1600, 1200), ResolutionStrategy.FallbackRuleClosestHigherThenLower))
			.Build();
		var analysis = new ImageAnalysis.Builder()
			.SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)
			.SetResolutionSelector(resolution)
			.Build();
		_executor ??= Executors.NewSingleThreadExecutor();
		analysis.SetAnalyzer(_executor, new FrameAnalyzer(this));

		var owner = (ILifecycleOwner)Platform.CurrentActivity;
		_provider.BindToLifecycle(owner, CameraSelector.DefaultBackCamera, preview, analysis);
		_bound = true;
	}

	void Stop()
	{
		if (!_bound) return;
		_provider?.UnbindAll();
		_bound = false;
	}

	/// <summary>Reads one frame at a time, at most every 0.6 s; skips frames while the previous one is still being handled.</summary>
	sealed class FrameAnalyzer : Java.Lang.Object, ImageAnalysis.IAnalyzer
	{
		readonly LiveCameraHandler _owner;
		bool _busy;
		DateTime _last = DateTime.MinValue;

		public FrameAnalyzer(LiveCameraHandler owner) => _owner = owner;

		// Android asks the analyzer for these optional settings; answer with "no preference".
		Android.Util.Size ImageAnalysis.IAnalyzer.DefaultTargetResolution => null;
		int ImageAnalysis.IAnalyzer.TargetCoordinateSystem => ImageAnalysis.CoordinateSystemOriginal;
		void ImageAnalysis.IAnalyzer.UpdateTransform(Android.Graphics.Matrix matrix) { }

		public void Analyze(IImageProxy proxy)
		{
			if (_busy || DateTime.Now - _last < TimeSpan.FromMilliseconds(600) || proxy.Image == null)
			{
				proxy.Close();
				return;
			}
			_busy = true;
			_last = DateTime.Now;
			_ = Handle(proxy);
		}

		async Task Handle(IImageProxy proxy)
		{
			try
			{
				TcgScanner.ScanResult scan;
				try { scan = await MlKitReader.ReadFrameAsync(proxy.Image, proxy.ImageInfo.RotationDegrees); }
				finally { proxy.Close(); } // free the camera buffer before any slow card lookup
				var callback = _owner.VirtualView?.FrameScanned;
				if (callback != null) await MainThread.InvokeOnMainThreadAsync(() => callback(scan));
			}
			catch { /* one bad frame is not worth stopping the camera for */ }
			finally { _busy = false; }
		}
	}
}
