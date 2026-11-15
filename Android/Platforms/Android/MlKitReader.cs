using Android.Gms.Tasks;
using Android.Graphics;
using Android.Media;
using Android.Runtime;
using TcgScanner;
using Xamarin.Google.MLKit.Vision.Common;
using Xamarin.Google.MLKit.Vision.Text;
using Xamarin.Google.MLKit.Vision.Text.Latin;
using MlText = Xamarin.Google.MLKit.Vision.Text.Text;

namespace TcgMobile;

/// <summary>
/// Reads card photos with Google ML Kit, on the phone and without internet. The text it finds is split into the same
/// regions the Windows app reads (top band, lower part, bottom strip) and handed to the shared CardText.
/// </summary>
public static class MlKitReader
{
	static ITextRecognizer _recognizer;
	static ITextRecognizer Recognizer => _recognizer ??= TextRecognition.GetClient(TextRecognizerOptions.DefaultOptions);

	public static async System.Threading.Tasks.Task<ScanResult> ReadAsync(string photoPath)
	{
		using var bitmap = LoadUpright(photoPath, 2600);
		return ToScan(await RecognizeAsync(InputImage.FromBitmap(bitmap, 0)));
	}

	/// <summary>Reads one live camera frame (already rotated upright by ML Kit using the frame's rotation).</summary>
	public static async System.Threading.Tasks.Task<ScanResult> ReadFrameAsync(Android.Media.Image frame, int rotationDegrees)
	{
		var lines = await RecognizeAsync(InputImage.FromMediaImage(frame, rotationDegrees));
		// Only text inside the card-shaped guide frame (centered, 63 x 88 portrait) counts; ignore labels and screens
		// around it. The region is a little larger than the frame, because the preview crops the camera image slightly.
		bool sideways = rotationDegrees is 90 or 270;
		double w = sideways ? frame.Height : frame.Width, h = sideways ? frame.Width : frame.Height;
		double gh = h * 0.95, gw = gh * 63 / 88 * 1.15;
		if (gw > w * 0.95) { gw = w * 0.95; gh = Math.Min(h * 0.95, gw / 1.15 * 88 / 63); }
		double left = (w - gw) / 2, top = (h - gh) / 2;
		var inside = lines.Where(l =>
		{
			if (l.Words.Count == 0) return false;
			double cx = l.Words.Average(x => x.X + x.Width / 2), cy = l.Words.Average(x => x.Y + x.Height / 2);
			return cx > left && cx < left + gw && cy > top && cy < top + gh;
		}).ToList();
		return ToScan(inside);
	}

	/// <summary>Splits the text into the regions the shared CardText expects, using the text's own outline as the card.</summary>
	static ScanResult ToScan(List<TextLine> lines)
	{
		if (lines.Count == 0) return CardText.Analyze(lines, lines, lines, lines);
		double top = lines.Min(l => l.Top);
		double bottom = lines.Max(l => l.Words.Count == 0 ? l.Top : l.Words.Max(w => w.Y + w.Height));
		double h = Math.Max(1, bottom - top);
		var topBand = lines.Where(l => l.Top < top + 0.22 * h).ToList();
		var lower = lines.Where(l => l.Top > top + 0.62 * h).ToList();
		var strip = lines.Where(l => l.Top > top + 0.86 * h).ToList();
		return CardText.Analyze(lines, topBand, lower, strip);
	}

	static async System.Threading.Tasks.Task<List<TextLine>> RecognizeAsync(InputImage image)
	{
		var listener = new TaskListener();
		Recognizer.Process(image).AddOnSuccessListener(listener).AddOnFailureListener(listener);
		var result = (await listener.Task).JavaCast<MlText>();

		var lines = new List<TextLine>();
		foreach (var block in result.TextBlocks)
			foreach (var line in block.Lines)
			{
				var words = line.Elements
					.Select(e => new TextWord(e.Text, e.BoundingBox?.Left ?? 0, e.BoundingBox?.Top ?? 0,
					                          e.BoundingBox?.Width() ?? 0, e.BoundingBox?.Height() ?? 0))
					.ToList();
				lines.Add(new TextLine(line.Text, words));
			}
		return lines;
	}

	/// <summary>Loads a photo at a sensible size and turns it upright (phone cameras store rotation separately).</summary>
	static Bitmap LoadUpright(string path, int maxSide)
	{
		var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
		BitmapFactory.DecodeFile(path, bounds);
		int sample = 1;
		while (Math.Max(bounds.OutWidth, bounds.OutHeight) / sample > maxSide * 2) sample *= 2;
		var bitmap = BitmapFactory.DecodeFile(path, new BitmapFactory.Options { InSampleSize = sample })
		             ?? throw new InvalidOperationException("Couldn't open that photo.");

		int degrees = 0;
		try
		{
			var exif = new ExifInterface(path);
			degrees = exif.GetAttributeInt(ExifInterface.TagOrientation, 1) switch { 6 => 90, 3 => 180, 8 => 270, _ => 0 };
		}
		catch { /* no orientation info */ }

		float scale = Math.Min(1f, (float)maxSide / Math.Max(bitmap.Width, bitmap.Height));
		if (degrees == 0 && scale >= 1f) return bitmap;
		var matrix = new Matrix();
		matrix.PostScale(scale, scale);
		matrix.PostRotate(degrees);
		var upright = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, matrix, true);
		if (!ReferenceEquals(upright, bitmap)) bitmap.Recycle();
		return upright;
	}

	/// <summary>Bridges ML Kit's Java callbacks to an awaitable task.</summary>
	sealed class TaskListener : Java.Lang.Object, IOnSuccessListener, IOnFailureListener
	{
		readonly TaskCompletionSource<Java.Lang.Object> _tcs = new();
		public System.Threading.Tasks.Task<Java.Lang.Object> Task => _tcs.Task;
		public void OnSuccess(Java.Lang.Object result) => _tcs.TrySetResult(result);
		public void OnFailure(Java.Lang.Exception e) => _tcs.TrySetException(new InvalidOperationException("Text recognition failed: " + e.Message));
	}
}
