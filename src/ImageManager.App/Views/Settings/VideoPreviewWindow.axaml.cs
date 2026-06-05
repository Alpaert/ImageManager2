using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ImageManager.App.ViewModels;
using ImageManager.Common.Helpers;
using ImageManager.Infrastructure.Imaging;
using LibVLCSharp.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace ImageManager.App.Views.Settings;

public partial class VideoPreviewWindow : Window
{
    private VideoPreviewViewModel Vm => (VideoPreviewViewModel)DataContext!;

    private MediaPlayer? _mediaPlayer;
    private DispatcherTimer _videoTimer;
    private DispatcherTimer? _replayTimer;
    private DispatcherTimer? _autoHideTimer;
    private bool _videoSliderDragging;
    private string? _currentFilePath;
    private readonly ImageManager.Core.Services.IVideoService _videoService;

    // Subtitle state
    private int _activeEmbeddedTrack = -1;
    private string? _activeExternalPath;
    private bool _subtitleEnabled;

    public VideoPreviewWindow() : this(App.Services.GetRequiredService<ImageManager.Core.Services.IVideoService>()) { }

    public VideoPreviewWindow(ImageManager.Core.Services.IVideoService videoService)
    {
        _videoService = videoService;
        InitializeComponent();
        KeyDown += OnPreviewKeyDown;
        _videoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _videoTimer.Tick += VideoTimer_Tick;

        // Tunnel handlers fire before the Slider's internal Thumb consumes the events
        VideoSlider.AddHandler(InputElement.PointerPressedEvent,
            (object? s, PointerPressedEventArgs e) => _videoSliderDragging = true,
            RoutingStrategies.Tunnel);
        VideoPlayer.DoubleTapped += (_, _) => ToggleFullScreen();

        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _autoHideTimer.Tick += (_, _) => { _autoHideTimer.Stop(); ControlBar.IsVisible = false; };

        AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (WindowState != WindowState.FullScreen) return;
            var pos = e.GetPosition(this);
            if (pos.Y > Bounds.Height - 80)
            {
                ControlBar.IsVisible = true;
                _autoHideTimer.Stop();
                _autoHideTimer.Start();
            }
        }, RoutingStrategies.Tunnel);

        VideoSlider.AddHandler(InputElement.PointerReleasedEvent,
            (object? s, PointerReleasedEventArgs e) =>
            {
                _videoSliderDragging = false;
                if (_mediaPlayer != null && _mediaPlayer.Length > 0)
                    _mediaPlayer.Time = (long)(VideoSlider.Value * _mediaPlayer.Length);
            },
            RoutingStrategies.Tunnel);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Vm.SavedLeft = Position.X;
        Vm.SavedTop = Position.Y;
        try { StopVideo(); } catch (Exception ex) { AppLogger.Warn($"StopVideo on close failed: {ex.Message}"); }
        base.OnClosing(e);
    }

    public static VideoPreviewWindow Create(List<string> videoPaths, int startIndex,
        ImageManager.Core.Services.IVideoService videoService)
    {
        var win = new VideoPreviewWindow(videoService);
        var vm = new VideoPreviewViewModel
        {
            ImagePaths = videoPaths,
            CurrentIndex = startIndex,
            HasPrev = startIndex > 0,
            HasNext = startIndex < videoPaths.Count - 1
        };
        win.DataContext = vm;
        if (videoPaths.Count == 0) return win;
        if (startIndex >= videoPaths.Count) startIndex = videoPaths.Count - 1;
        win.LoadVideo(videoPaths[startIndex]);
        return win;
    }

    private void LoadVideo(string filePath)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            StopVideo();

            _currentFilePath = filePath;
            var fi = new FileInfo(filePath);
            Vm.VideoFilePath = filePath;
            Vm.FileSizeBytes = fi.Length;
            Vm.Title = Path.GetFileName(filePath);
            Vm.Position = 0;
            Vm.DurationMs = 0;
            Vm.IsPlaying = false;
            Vm.UpdateInfo();

            var libVlc = ImageManager.App.Services.VideoService.GetOrCreateLibVLC();
            var media = new Media(libVlc, new Uri(filePath));
            _mediaPlayer = new MediaPlayer(media);

            _mediaPlayer.Playing += OnMediaPlaying;
            _mediaPlayer.EndReached += OnMediaEndReached;
            _mediaPlayer.EncounteredError += OnMediaError;

            // Assign MediaPlayer after window is loaded to avoid access violation
            VideoPlayer.Loaded += OnVideoPlayerLoaded;
            if (VideoPlayer.IsLoaded)
            {
                VideoPlayer.MediaPlayer = _mediaPlayer;
                _mediaPlayer.Play();
            }

            _videoTimer.Start();
            BuildSubtitleMenu();
        }
        catch (Exception ex)
        {
            Vm.InfoText = "Video load failed: " + ex.Message;
        }
    }

    private void OnVideoPlayerLoaded(object? s, RoutedEventArgs e)
    {
        if (_mediaPlayer != null)
        {
            VideoPlayer.MediaPlayer = _mediaPlayer;
            _mediaPlayer.Play();
        }
    }

    private void OnMediaPlaying(object? s, EventArgs e)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            Vm.IsPlaying = true;
            BtnPlayPause.Content = "\u23F8";
            Vm.DurationMs = _mediaPlayer?.Length ?? 0;
            Vm.UpdateInfo();
        });
    }

    private void OnMediaEndReached(object? s, EventArgs e)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            Vm.IsPlaying = false;
            BtnPlayPause.Content = "\u25B6";
            Vm.Position = 0;

            if (_replayTimer == null)
            {
                _replayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _replayTimer.Tick += (_, _) =>
                {
                    _replayTimer.Stop();
                    _mediaPlayer?.Play();
                };
            }
            _replayTimer.Start();
        });
    }

    private void OnMediaError(object? s, EventArgs e)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            Vm.InfoText = "Video failed: " + Path.GetFileName(_currentFilePath ?? "");
        });
    }

    private void VideoTimer_Tick(object? sender, EventArgs e)
    {
        if (_mediaPlayer == null || !_mediaPlayer.IsPlaying || _videoSliderDragging) return;
        var length = _mediaPlayer.Length;
        if (length > 0)
        {
            Vm.DurationMs = length;
            Vm.Position = (double)_mediaPlayer.Time / length;
        }
    }

    private void BtnPlayPause_Click(object? sender, RoutedEventArgs e)
    {
        if (_mediaPlayer == null) return;
        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
            Vm.IsPlaying = false;
            BtnPlayPause.Content = "\u25B6";
        }
        else
        {
            _mediaPlayer.Play();
            Vm.IsPlaying = true;
            BtnPlayPause.Content = "\u23F8";
        }
    }

    private void StopVideo()
    {
        _videoTimer.Stop();
        _replayTimer?.Stop();
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Playing -= OnMediaPlaying;
            _mediaPlayer.EndReached -= OnMediaEndReached;
            _mediaPlayer.EncounteredError -= OnMediaError;
            VideoPlayer.Loaded -= OnVideoPlayerLoaded;
            try { VideoPlayer.MediaPlayer = null; } catch (Exception ex) { AppLogger.Warn($"Detach VideoPlayer failed: {ex.Message}"); }
            try { _mediaPlayer.Stop(); } catch (Exception ex) { AppLogger.Warn($"MediaPlayer Stop failed: {ex.Message}"); }
            try { _mediaPlayer.Dispose(); } catch (Exception ex) { AppLogger.Warn($"MediaPlayer Dispose failed: {ex.Message}"); }
            _mediaPlayer = null;
        }
        Vm.IsPlaying = false;
    }

    // ==================== Navigation ====================

    private void NavigateTo(string filePath)
    {
        LoadVideo(filePath);
    }

    private void ArrowLeft_Click(object? sender, PointerPressedEventArgs e)
    {
        var path = Vm.Navigate(-1);
        if (path != null) NavigateTo(path);
        e.Handled = true;
    }

    private void ArrowRight_Click(object? sender, PointerPressedEventArgs e)
    {
        var path = Vm.Navigate(1);
        if (path != null) NavigateTo(path);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                if (_mediaPlayer != null && _mediaPlayer.Length > 0)
                {
                    var newTime = Math.Max(0, _mediaPlayer.Time - 5000);
                    _mediaPlayer.Time = newTime;
                }
                e.Handled = true;
                break;
            case Key.Right:
                if (_mediaPlayer != null && _mediaPlayer.Length > 0)
                {
                    var newTime = Math.Min(_mediaPlayer.Length, _mediaPlayer.Time + 5000);
                    _mediaPlayer.Time = newTime;
                }
                e.Handled = true;
                break;
            case Key.Space:
                BtnPlayPause_Click(null, null!);
                e.Handled = true;
                break;
            case Key.Escape:
                if (WindowState == WindowState.FullScreen)
                {
                    ToggleFullScreen();
                }
                else
                {
                    Close();
                }
                e.Handled = true;
                break;
            case Key.Back:
                if (WindowState != WindowState.FullScreen)
                    Close();
                e.Handled = true;
                break;
        }
    }

    private void BtnSubtitle_Click(object? sender, RoutedEventArgs e)
    {
        BuildSubtitleMenu();
        RootGrid.ContextMenu?.Open(RootGrid);
    }

    private void BtnFullScreen_Click(object? sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = WindowState.Normal;
            ControlBar.IsVisible = true;
            _autoHideTimer?.Stop();
        }
        else
        {
            WindowState = WindowState.FullScreen;
            ControlBar.IsVisible = false;
        }
    }

    // ==================== Subtitle ====================

    private static readonly string[] SubtitleExtensions =
        { ".ass", ".srt", ".sub", ".ssa", ".vtt", ".smi", ".txt" };

    private void BuildSubtitleMenu()
    {
        var menu = new ContextMenu();
        var selectSub = new MenuItem { Header = "选择字幕" };
        bool hasAny = false;

        // Embedded tracks
        if (_mediaPlayer?.Media != null)
        {
            int spuIdx = 0;
            foreach (var track in _mediaPlayer.Media.Tracks)
            {
                if (track.TrackType == TrackType.Text)
                {
                    hasAny = true;
                    int idx = spuIdx++;
                    var label = string.IsNullOrEmpty(track.Description)
                        ? $"内封轨道 {idx + 1}"
                        : track.Description;
                    var item = new MenuItem { Header = label };
                    item.Click += (_, _) => SelectEmbeddedTrack(idx);
                    selectSub.Items.Add(item);
                }
            }
        }

        // External subtitle files
        var extFiles = ScanExternalSubtitles();
        if (hasAny && extFiles.Count > 0)
            selectSub.Items.Add(new Separator());

        foreach (var ext in extFiles)
        {
            hasAny = true;
            var item = new MenuItem { Header = $"[外挂] {Path.GetFileName(ext)}" };
            var path = ext;
            item.Click += (_, _) => SelectExternalSub(path);
            selectSub.Items.Add(item);
        }

        if (!hasAny)
            selectSub.Items.Add(new MenuItem { Header = "(无可用字幕)", IsEnabled = false });

        var toggleSub = new MenuItem { Header = "显示字幕" };
        toggleSub.Click += (_, _) =>
        {
            _subtitleEnabled = !_subtitleEnabled;
            if (_subtitleEnabled)
            {
                if (_activeExternalPath != null)
                    ReloadWithExternalSub(_activeExternalPath);
                else if (_activeEmbeddedTrack >= 0)
                    _mediaPlayer?.SetSpu(_activeEmbeddedTrack);
                else if (selectSub.Items.Count > 0 && selectSub.Items[0] is MenuItem first
                    && first.IsEnabled && !first.Header!.ToString()!.Contains("外挂"))
                    SelectEmbeddedTrack(0);
                else if (extFiles.Count > 0)
                    SelectExternalSub(extFiles[0]);
            }
            else
            {
                _mediaPlayer?.SetSpu(-1);
            }
        };

        var subMenuItem = new MenuItem { Header = "字幕" };
        subMenuItem.Items.Add(selectSub);
        menu.Items.Add(subMenuItem);
        menu.Items.Add(toggleSub);
        RootGrid.ContextMenu = menu;
    }

    private List<string> ScanExternalSubtitles()
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(_currentFilePath)) return result;
        var dir = Path.GetDirectoryName(_currentFilePath);
        if (string.IsNullOrEmpty(dir)) return result;
        var baseName = Path.GetFileNameWithoutExtension(_currentFilePath);
        foreach (var ext in SubtitleExtensions)
        {
            foreach (var file in Directory.EnumerateFiles(dir, $"{baseName}*{ext}"))
                result.Add(file);
        }
        return result;
    }

    private void SelectEmbeddedTrack(int spuIdx)
    {
        _activeEmbeddedTrack = spuIdx;
        _activeExternalPath = null;
        _subtitleEnabled = true;
        _mediaPlayer?.SetSpu(spuIdx);
    }

    private void SelectExternalSub(string path)
    {
        _activeExternalPath = path;
        _activeEmbeddedTrack = -1;
        _subtitleEnabled = true;
        ReloadWithExternalSub(path);
    }

    private void ReloadWithExternalSub(string subPath)
    {
        if (string.IsNullOrEmpty(_currentFilePath)) return;
        var wasPlaying = _mediaPlayer?.IsPlaying ?? false;
        var savedTime = _mediaPlayer?.Time ?? 0;
        StopVideo();

        var libVlc = ImageManager.App.Services.VideoService.GetOrCreateLibVLC();
        var media = new Media(libVlc, new Uri(_currentFilePath));
        media.AddOption($":sub-file={subPath}");
        _mediaPlayer = new MediaPlayer(media);
        _mediaPlayer.Playing += OnMediaPlaying;
        _mediaPlayer.EndReached += OnMediaEndReached;
        _mediaPlayer.EncounteredError += OnMediaError;

        VideoPlayer.Loaded += OnVideoPlayerLoaded;
        if (VideoPlayer.IsLoaded)
        {
            VideoPlayer.MediaPlayer = _mediaPlayer;
            _mediaPlayer.Play();
            if (savedTime > 0) { _mediaPlayer.Time = savedTime; if (!wasPlaying) _mediaPlayer.Pause(); }
            else if (!wasPlaying) _mediaPlayer.Pause();
        }
        _videoTimer.Start();
        Vm.IsPlaying = wasPlaying;
        BtnPlayPause.Content = wasPlaying ? "⏸" : "▶";
    }
}
