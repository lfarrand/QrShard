using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QrShard.Display;

/// <summary>Shows a folder of stills 1:1 on one monitor, as a slideshow or flipbook.</summary>
public partial class MainWindow : Window
{
    private readonly string _folder;
    private readonly bool _flipbookMode;
    private readonly long _memoryCapBytes;
    private readonly bool _playOnce;
    private readonly Color _terminationColor;
    private double _fps;
    private List<string> _imagePaths = [];
    private bool _paused;
    private bool _terminated;

    private readonly System.Drawing.Rectangle _screenBounds;
    private DpiScale _dpi;
    private int _screenPixelWidth;
    private int _screenPixelHeight;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);
    private const uint SwpNoZOrder = 0x0004;

    private readonly DispatcherTimer _timer;
    private int _currentIndex = -1;
    private Task<BitmapSource?>? _preloadTask;
    private int _preloadIndex = -1;

    private BitmapSource?[] _frames = [];
    private string?[] _frameErrors = [];
    private readonly Stopwatch _clock = new();
    private double _clockOffsetSeconds;
    private TimeSpan _lastRenderTime = TimeSpan.MinValue;
    private int _lastShownFrame = -1;
    private long _lastRawIndex = -1;

    /// <summary>Initializes a new instance of the <see cref="MainWindow"/> class.</summary>
    /// <param name="folder">The folder of stills to play.</param>
    /// <param name="fps">The playback rate in frames per second.</param>
    /// <param name="memoryCapBytes">The flipbook pre-decode ceiling in bytes.</param>
    /// <param name="playOnce"><see langword="true" /> to hold the termination colour after one pass; otherwise, <see langword="false" />.</param>
    /// <param name="borderColor">The colour around images smaller than the monitor.</param>
    /// <param name="terminationColor">The full-screen stop colour after a single pass.</param>
    /// <param name="screenBounds">The nominated monitor bounds in physical pixels.</param>
    public MainWindow(string folder, double fps, long memoryCapBytes, bool playOnce,
        Color borderColor, Color terminationColor, System.Drawing.Rectangle screenBounds)
    {
        InitializeComponent();

        _folder = folder;
        _fps = fps;
        _memoryCapBytes = memoryCapBytes;
        _playOnce = playOnce;
        _terminationColor = terminationColor;
        _screenBounds = screenBounds;
        _flipbookMode = DisplayPlayback.IsFlipbook(fps);
        Background = new SolidColorBrush(borderColor);

        SourceInitialized += (_, _) =>
        {
            nint hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, 0, _screenBounds.X, _screenBounds.Y,
                _screenBounds.Width, _screenBounds.Height, SwpNoZOrder);
            WindowState = WindowState.Maximized;
        };
        DpiChanged += (_, e) => _dpi = e.NewDpi;

        _timer = new DispatcherTimer();
        _timer.Tick += (_, _) => _ = ShowNextAsync();

        Loaded += async (_, _) => await StartAsync();
    }

    private double CurrentSeconds => _clockOffsetSeconds + _clock.Elapsed.TotalSeconds;

    private async Task StartAsync()
    {
        _dpi = VisualTreeHelper.GetDpi(this);
        _screenPixelWidth = _screenBounds.Width;
        _screenPixelHeight = _screenBounds.Height;

        _imagePaths = ImageSequence.Enumerate(_folder);

        if (_imagePaths.Count == 0)
        {
            MessageBox.Show($"No images found in {_folder}",
                "QrShard.Display", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        if (_flipbookMode)
        {
            await StartFlipbookAsync();
        }
        else
        {
            await ShowNextAsync();
            _timer.Interval = TimeSpan.FromSeconds(1.0 / _fps);
            _timer.Start();
        }
    }

    /// <summary>
    /// Shows <paramref name="bitmap"/> mapped 1:1 onto device pixels, or an error
    /// message when the frame is rejected. A null bitmap with no error (undecodable
    /// file) leaves the previous frame on screen.
    /// </summary>
    private void DisplayFrame(BitmapSource? bitmap, string? error)
    {
        if (error is not null)
        {
            PhotoImage.Visibility = Visibility.Collapsed;
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (bitmap is null) return;

        ErrorText.Visibility = Visibility.Collapsed;
        PhotoImage.Width = bitmap.PixelWidth / _dpi.DpiScaleX;
        PhotoImage.Height = bitmap.PixelHeight / _dpi.DpiScaleY;
        PhotoImage.Source = bitmap;
        PhotoImage.Visibility = Visibility.Visible;
    }

    private string OversizeError(string path, int width, int height) =>
        $"{Path.GetFileName(path)} ({width}×{height}) exceeds the monitor display resolution " +
        $"({_screenPixelWidth}×{_screenPixelHeight}) — image not displayed.";

    private async Task StartFlipbookAsync()
    {
        LoadingText.Visibility = Visibility.Visible;
        LoadingText.Text = "Scanning frames…";

        var errors = new string?[_imagePaths.Count];
        long estimatedBytes = await Task.Run(() => ScanFrames(errors));

        if (estimatedBytes > _memoryCapBytes)
        {
            MessageBox.Show(
                $"Decoding all {_imagePaths.Count} frames at full resolution needs about " +
                $"{ByteSizeFormat.Format(estimatedBytes)}, which exceeds the {ByteSizeFormat.Format(_memoryCapBytes)} memory cap.\n\n" +
                "Images are never shrunk, so either raise the cap, e.g.\n" +
                $"    QrShard.Display \"{_folder}\" {_fps} {Math.Ceiling(estimatedBytes / (double)ByteSizeFormat.Gibibyte)}GB\n" +
                "or point the app at a smaller frame set.",
                "QrShard.Display", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        var frames = new BitmapSource?[_imagePaths.Count];
        int decoded = 0;
        var progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        progressTimer.Tick += (_, _) =>
            LoadingText.Text = $"Decoding frames… {Volatile.Read(ref decoded)} / {frames.Length}";
        progressTimer.Start();

        await Task.Run(() =>
            Parallel.For(0, frames.Length, i =>
            {
                if (errors[i] is null)
                {
                    frames[i] = DecodeFrame(_imagePaths[i]);
                }
                Interlocked.Increment(ref decoded);
            }));

        progressTimer.Stop();
        LoadingText.Visibility = Visibility.Collapsed;

        if (frames.All(f => f is null) && errors.All(e => e is null))
        {
            MessageBox.Show($"None of the images in {_folder} could be decoded.",
                "QrShard.Display", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        _frames = frames;
        _frameErrors = errors;
        ShowStatus($"{_frames.Length} frames @ {_fps:0.###} fps");

        CompositionTarget.Rendering += OnRendering;
        _clock.Start();
    }

    private long ScanFrames(string?[] errors)
    {
        long total = 0;
        Parallel.For(0, _imagePaths.Count, i =>
        {
            try
            {
                using var stream = File.OpenRead(_imagePaths[i]);
                var decoder = BitmapDecoder.Create(stream,
                    BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                if (frame.PixelWidth > _screenPixelWidth || frame.PixelHeight > _screenPixelHeight)
                {
                    errors[i] = OversizeError(_imagePaths[i], frame.PixelWidth, frame.PixelHeight);
                }
                else
                {
                    Interlocked.Add(ref total, 4L * frame.PixelWidth * frame.PixelHeight);
                }
            }
            catch (Exception)
            {
                // Unreadable header — the frame will be skipped during decoding as well.
            }
        });
        return total;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs args)
        {
            if (args.RenderingTime == _lastRenderTime) return;
            _lastRenderTime = args.RenderingTime;
        }

        long timeIndex = (long)(CurrentSeconds * _fps);
        long rawIndex = FlipbookIndexer.Advance(timeIndex, _lastRawIndex);
        _lastRawIndex = rawIndex;

        if (_playOnce && FlipbookIndexer.OncePastLast(rawIndex, _frames.Length))
        {
            ShowTerminationScreen();
            return;
        }

        int index = (int)(rawIndex % _frames.Length);
        if (index == _lastShownFrame) return;
        _lastShownFrame = index;

        DisplayFrame(_frames[index], _frameErrors[index]);
    }

    private void RebaseClock(double framePosition)
    {
        _clockOffsetSeconds = framePosition / _fps;
        _lastRawIndex = (long)framePosition - 1;
        _clock.Reset();
        if (!_paused) _clock.Start();
    }

    private void SetFlipbookPaused(bool paused)
    {
        _paused = paused;
        if (paused)
        {
            _clockOffsetSeconds = CurrentSeconds;
            _clock.Reset();
        }
        else
        {
            _clock.Start();
        }
    }

    private void StepFrames(int delta)
    {
        double position = CurrentSeconds * _fps + delta;
        RebaseClock(Math.Floor(position) + 0.5);
        OnRendering(this, EventArgs.Empty);
    }

    private void ChangeSpeed(double factor)
    {
        double position = CurrentSeconds * _fps;
        _fps = Math.Clamp(_fps * factor, 0.1, DisplayPlayback.MaxFps);
        RebaseClock(position);
        ShowStatus($"{_fps:0.###} fps");
    }

    private async Task ShowNextAsync()
    {
        if (_playOnce && _currentIndex == _imagePaths.Count - 1)
        {
            ShowTerminationScreen();
            return;
        }
        await ShowImageAsync((_currentIndex + 1) % _imagePaths.Count);
    }

    private async Task ShowPreviousAsync() =>
        await ShowImageAsync((_currentIndex - 1 + _imagePaths.Count) % _imagePaths.Count);

    private async Task ShowImageAsync(int index)
    {
        BitmapSource? bitmap;
        if (_preloadTask is not null && _preloadIndex == index)
        {
            bitmap = await _preloadTask;
        }
        else
        {
            string path = _imagePaths[index];
            bitmap = await Task.Run(() => DecodeFrame(path));
        }

        _currentIndex = index;

        if (bitmap is not null)
        {
            if (bitmap.PixelWidth > _screenPixelWidth || bitmap.PixelHeight > _screenPixelHeight)
            {
                DisplayFrame(null, OversizeError(_imagePaths[index], bitmap.PixelWidth, bitmap.PixelHeight));
            }
            else
            {
                DisplayFrame(bitmap, null);
            }
        }

        _preloadIndex = (_currentIndex + 1) % _imagePaths.Count;
        string preloadPath = _imagePaths[_preloadIndex];
        _preloadTask = Task.Run(() => DecodeFrame(preloadPath));
    }

    private void ShowTerminationScreen()
    {
        if (_terminated) return;
        _terminated = true;

        CompositionTarget.Rendering -= OnRendering;
        _timer.Stop();
        _clock.Reset();

        PhotoImage.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
        LoadingText.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        Background = new SolidColorBrush(_terminationColor);
    }

    private static BitmapSource? DecodeFrame(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_terminated && e.Key != Key.Escape) return;

        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;

            case Key.Space:
                if (_flipbookMode)
                {
                    SetFlipbookPaused(!_paused);
                }
                else
                {
                    _paused = !_paused;
                    if (_paused) _timer.Stop(); else _timer.Start();
                }
                ShowStatus(_paused ? "Paused" : "Playing");
                break;

            case Key.Right:
                if (_flipbookMode) StepFrames(1);
                else { _ = ShowNextAsync(); RestartTimerIfRunning(); }
                break;

            case Key.Left:
                if (_flipbookMode) StepFrames(-1);
                else { _ = ShowPreviousAsync(); RestartTimerIfRunning(); }
                break;

            case Key.Up:
                AdjustSpeed(2.0);
                break;

            case Key.Down:
                AdjustSpeed(0.5);
                break;
        }
    }

    private void AdjustSpeed(double factor)
    {
        if (_flipbookMode)
        {
            ChangeSpeed(factor);
        }
        else
        {
            _fps = Math.Clamp(_fps * factor, 0.01, DisplayPlayback.FlipbookThresholdFps);
            _timer.Interval = TimeSpan.FromSeconds(1.0 / _fps);
            ShowStatus($"{_fps:0.###} fps");
        }
    }

    private void RestartTimerIfRunning()
    {
        if (_paused) return;
        _timer.Stop();
        _timer.Start();
    }

    private async void ShowStatus(string message)
    {
        if (_terminated) return;
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
        await Task.Delay(1500);
        if (StatusText.Text == message)
        {
            StatusText.Visibility = Visibility.Collapsed;
        }
    }
}
