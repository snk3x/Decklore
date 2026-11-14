using Android.Media;

namespace TcgMobile;

/// <summary>A short beep and a vibration when a card is added, so you know to show the next one.</summary>
public static class AddedFeedback
{
	static ToneGenerator _tone;

	public static void Play(bool sound)
	{
		if (sound)
		{
			try
			{
				// The notification stream follows the phone's notification volume (and stays quiet in silent mode).
				_tone ??= new ToneGenerator(Android.Media.Stream.Notification, 90);
				_tone.StartTone(Tone.PropAck, 180);
			}
			catch { /* no audio available */ }
		}
		try { Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(120)); } catch { /* no vibrator */ }
	}
}
