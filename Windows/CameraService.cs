using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace TcgScanner;

/// <summary>Grabs webcam frames on a background thread.</summary>
public sealed class CameraService : IDisposable
{
    VideoCapture _cap;
    CancellationTokenSource _cts;
    Task _loop;
    readonly object _lock = new();
    Bitmap _latest;

    /// <summary>Raised on a background thread. The handler owns (and must dispose) the bitmap.</summary>
    public event Action<Bitmap> FrameReady;

    public bool Running => _loop != null && !_loop.IsCompleted;

    public void Start(int index)
    {
        Stop();
        var cap = new VideoCapture(index, VideoCaptureAPIs.DSHOW);
        if (!cap.IsOpened())
        {
            cap.Dispose();
            throw new InvalidOperationException($"Camera {index + 1} could not be opened. Check that it is connected and not used by another app.");
        }
        cap.Set(VideoCaptureProperties.FrameWidth, 1920);
        cap.Set(VideoCaptureProperties.FrameHeight, 1080);
        _cap = cap;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() =>
        {
            using var mat = new Mat();
            while (!token.IsCancellationRequested)
            {
                if (!cap.Read(mat) || mat.Empty()) { Thread.Sleep(30); continue; }
                var bmp = BitmapConverter.ToBitmap(mat);
                lock (_lock)
                {
                    _latest?.Dispose();
                    _latest = new Bitmap(bmp);
                }
                var handler = FrameReady;
                if (handler != null) handler(bmp); else bmp.Dispose();
            }
        }, token);
    }

    /// <summary>A copy of the most recent frame, or null.</summary>
    public Bitmap Snapshot()
    {
        lock (_lock) return _latest == null ? null : new Bitmap(_latest);
    }

    public void Stop()
    {
        if (_cts == null) return;
        _cts.Cancel();
        try { _loop?.Wait(1500); } catch { }
        _cap?.Release();
        _cap?.Dispose();
        _cap = null;
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    public void Dispose()
    {
        Stop();
        lock (_lock) { _latest?.Dispose(); _latest = null; }
    }
}
