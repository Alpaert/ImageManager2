using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Messaging;
using ImageManager.Core.Messages;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using System.Runtime.InteropServices;
using ImageManager.App.Controls;
using ImageManager.App.Helpers;
using ImageManager.App.Models;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Imaging;
using ImageManager.Infrastructure.Services;
using ImageManager.Infrastructure.Video;
using Microsoft.Extensions.DependencyInjection;

namespace ImageManager.App.Views;

public partial class MainWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    private CancellationTokenSource? _scrollAnimCts;
    private CancellationTokenSource? _scrollActivityCts;
    private CancellationTokenSource? _continuousModeCts;
    private CancellationTokenSource? _continuousReflowCts;
    private readonly DispatcherTimer _continuousResizeTimer;
    private VirtualizingWaterfallPanel? _continuousWaterfallPanel;
    private ImageDisplayRange _continuousVisibleRange;
    private ContinuousScrollDirection _continuousScrollDirection;
    private int _continuousResizeAnchorIndex;
    private double _continuousResizeRequestedWidth;
    private int _scrollAnimationId;
    private int _autoTagRunVersion;
    private bool _lastAutoTagRecursive;
    // Accessed only on the UI thread; reserve before the first await and release in finally.
    private FolderOperationScope? _autoTagScope;
    private FolderOperationScope? _characterMatchScope;
    private FolderOperationScope? _clearTagsScope;
    private string? _videoOriginalFrameTargetName;
    private string? _videoOriginalFrameTargetPath;
    private int _videoOriginalFrameBatchRunning;
    private CancellationTokenSource? _videoOriginalFrameBatchCts;

    public MainWindow()
    {
        InitializeComponent();
        _continuousResizeTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Background,
            OnContinuousResizeTimerTick);
        SizeChanged += OnWindowSizeChanged;
        _hoverPreviewTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(600),
            DispatcherPriority.Background,
            OnHoverPreviewTimerTick);
        CreateHoverPreviewCloseTimer();
        AddHandler(InputElement.KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        ThumbnailScrollViewer.AddHandler(ScrollViewer.PointerWheelChangedEvent,
            OnThumbnailScrollWheel, RoutingStrategies.Tunnel);
        ContinuousItemsImages.LayoutUpdated += OnContinuousItemsImagesLayoutUpdated;
        LstFolders.AddHandler(TreeViewItem.ExpandedEvent,
            (_, e) =>
            {
                if (e.Source is TreeViewItem tvi && tvi.DataContext is ViewModels.FolderTreeNode node)
                    node.IsExpanded = true;
            },
            RoutingStrategies.Bubble);
        LstFolders.AddHandler(TreeViewItem.CollapsedEvent,
            (_, e) =>
            {
                if (e.Source is TreeViewItem tvi && tvi.DataContext is ViewModels.FolderTreeNode node)
                    node.IsExpanded = false;
            },
            RoutingStrategies.Bubble);
        LstFolders.AddHandler(InputElement.PointerPressedEvent,
            (_, e) =>
            {
                if (!e.GetCurrentPoint(LstFolders).Properties.IsRightButtonPressed) return;
                e.Handled = true;
                var el = e.Source as Control;
                while (el != null && el is not TreeViewItem)
                    el = el.Parent as Control;
                _rightClickedFolder = (el as TreeViewItem)?.DataContext as ViewModels.FolderTreeNode;
            },
            RoutingStrategies.Tunnel);
    }

    private async Task OpenPreviewForFileAsync(string filePath)
    {
        var fileList = Vm.ActiveFileList;
        int index = fileList.FindIndex(f => string.Equals(f, filePath, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = 0;

        var win = Settings.PreviewWindow.Create(fileList, index);
        if (Vm.AppSettings.PreviewWidth > 0) win.Width = Vm.AppSettings.PreviewWidth;
        if (Vm.AppSettings.PreviewHeight > 0) win.Height = Vm.AppSettings.PreviewHeight;

        win.Closed += (_, _) =>
        {
            var pv = (PreviewViewModel)win.DataContext!;
            Vm.AppSettings.PreviewWidth = win.Width;
            Vm.AppSettings.PreviewHeight = win.Height;
            // Position saved during OnClosing (win.Position is stale in Closed)
            Vm.AppSettings.PreviewLeft = pv.SavedLeft;
            Vm.AppSettings.PreviewTop = pv.SavedTop;
            _ = Vm.SaveSettingsAsync();
        };

        // Set saved position on VM for OnClosing to update
        var pv = (PreviewViewModel)win.DataContext!;
        pv.SavedLeft = Vm.AppSettings.PreviewLeft;
        pv.SavedTop = Vm.AppSettings.PreviewTop;

        win.WindowStartupLocation = WindowStartupLocation.Manual;
        if (!double.IsNaN(pv.SavedLeft))
        {
            win.Position = new PixelPoint((int)pv.SavedLeft, (int)pv.SavedTop);
        }
        else
        {
            var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
            var bounds = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            win.Position = new PixelPoint(
                bounds.X + (bounds.Width - (int)win.Width) / 2,
                bounds.Y + (bounds.Height - (int)win.Height) / 2);
        }

        // Use Show instead of ShowDialog to avoid Avalonia bug #19255
        // where ShowDialog ignores Window.Position on Windows.
        win.Show(this);
        var tcs = new TaskCompletionSource<bool>();
        win.Closed += (_, _) => tcs.TrySetResult(true);
        await tcs.Task;
    }

    private async Task DeleteSelectedFilesAsync(IEnumerable<string> filePaths)
    {
        var deletedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int successCount = 0;
        var thumbCache = App.Services.GetRequiredService<Infrastructure.Caching.ThumbnailCacheService>();

        Vm.SuppressDeletedEvent();
        foreach (var filePath in filePaths.Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(filePath)) continue;
            try
            {
                File.Delete(filePath);
                thumbCache.InvalidateThumbnail(filePath);
                deletedPaths.Add(filePath);
                successCount++;
            }
            catch { }
        }
        Vm.RestoreDeletedEvent();

        if (deletedPaths.Count > 0)
            await Vm.RemoveFilesFromViewAsync(deletedPaths);

        Vm.StatusText = $"已删除 {successCount} 个文件";
    }

    private async Task CopySelectedImagesToClipboardAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var selectedPaths = GetSelectedFilePaths();
        if (selectedPaths.Count == 0) return;

        try
        {
            var storageFiles = new List<Avalonia.Platform.Storage.IStorageFile>();
            foreach (var filePath in selectedPaths)
            {
                var sf = await topLevel.StorageProvider.TryGetFileFromPathAsync(filePath);
                if (sf != null)
                    storageFiles.Add(sf);
            }

            if (storageFiles.Count > 0)
                await topLevel.Clipboard.SetFilesAsync(storageFiles);
        }
        catch { }
    }

    private async Task CopyImageToClipboardAsync(string filePath)
    {
        if (!File.Exists(filePath)) return;
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var storageFile = await topLevel.StorageProvider.TryGetFileFromPathAsync(filePath);
            if (storageFile == null) return;

            await topLevel.Clipboard.SetFileAsync(storageFile);
        }
        catch { }
    }

    private static void OpenInExplorer(string filePath)
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
        }
        catch { }
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Vm.PropertyChanged += OnViewModelPropertyChanged;
        Vm.ContinuousDisplayRefreshRequested += ApplyDisplayModeAsync;
        await Vm.InitializeAsync();

        OnlineSearchHelper.SetTempDir(Path.Combine(Vm.AppSettings.DiskCacheDirectory, "search_temp"));
        OnlineSearchHelper.CleanupOldTempFiles();

        Vm.ScrollRestoreRequested += OnScrollRestore;
        Vm.ScrollToSelectedRequested += OnScrollToSelected;
        Vm.ScrollSearchResultsToTopRequested += OnScrollSearchResultsToTop;
        Vm.TreeScrollToNodeRequested += OnTreeScrollToNode;
        AttachContinuousWaterfallPanelWhenAvailable();

        // Restore startup size
        if (Vm.AppSettings.StartupWidth > 0) Width = Vm.AppSettings.StartupWidth;
        if (Vm.AppSettings.StartupHeight > 0) Height = Vm.AppSettings.StartupHeight;

        ApplyWallpaper();

        // UI 心跳日志：检测 UI 线程是否被阻塞
        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            var elapsed = sw.ElapsedMilliseconds;
            sw.Restart();
            if (elapsed > 2500)
                PerfLogger.Log($"[HEARTBEAT] UI THREAD BLOCKED! gap={elapsed}ms (expected ~2000ms)");
        });
        timer.Start();
    }

    // ==================== Keyboard Shortcuts ====================

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            CloseHoverPreview();

        // Don't intercept shortcuts when user is typing in a text field
        if (e.Source is TextBox)
            return;

        // Ctrl+C — copy selected images to clipboard
        if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            await CopySelectedImagesToClipboardAsync();
            return;
        }

        // Delete key — delete selected images
        if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            var selectedPaths = GetSelectedFilePaths();
            if (selectedPaths.Count > 0)
                await DeleteSelectedFilesAsync(selectedPaths);
            return;
        }

        // Ctrl+A — select all images on current page
        if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            // Preserve paged-mode behavior: Ctrl+A selects the current page only.
            Vm.ReplaceSelectedFiles(Vm.Images.Select(img => img.FilePath));
            return;
        }

        // Ctrl+Shift+G — force GC + LOH compaction + show memory stats (debug)
        if (e.Key == Key.G && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            var memBefore = GC.GetTotalMemory(false);
            var privBefore = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0;
            Infrastructure.Helpers.MemoryPressureMonitor.CompactLoh();
            var memAfter = GC.GetTotalMemory(false);
            var privAfter = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0;
            var freedMB = (memBefore - memAfter) / 1024.0 / 1024.0;
            var privFreed = privBefore - privAfter;
            Vm.StatusText = $"GC: Heap {memBefore/1048576:F0}→{memAfter/1048576:F0}MB | " +
                $"Private {privBefore:F0}→{privAfter:F0}MB | " +
                $"FragScore {Infrastructure.Helpers.MemoryPressureMonitor.FragmentationScore:F1} " +
                $"({Infrastructure.Helpers.MemoryPressureMonitor.Current})";
            return;
        }

        // Only handle modifier+key combos (Ctrl+X, Ctrl+Shift+X, etc.) for IME compatibility
        if (e.KeyModifiers == KeyModifiers.None)
            return;

        var gesture = KeyGestureHelper.KeyEventArgsToGesture(e);
        var bindings = Vm.AppSettings.ShortcutBindings;
        var configured = bindings?.GetValueOrDefault("EditTag", "Ctrl+T");
        if (!string.Equals(gesture, configured, StringComparison.OrdinalIgnoreCase))
            return;

        e.Handled = true;

        // Find target image: hovered > first selected
        var target = _lastHoveredItem;
        if (target == null)
            target = CreateTemporaryImageItems(GetSelectedFilePaths()).FirstOrDefault();
        if (target == null)
            return;

        await EditTagForItemAsync(target);
    }

    private async void OnThumbnailScrollWheel(object? sender, PointerWheelEventArgs e)
    {
        CloseHoverPreview();
        BeginScrollActivity();
        // Ctrl+Wheel = zoom
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            double step = e.Delta.Y > 0 ? 0.5 : -0.5;
            Vm.ZoomTick = Math.Clamp(Vm.ZoomTick + step, 1, 10);
            return;
        }

        e.Handled = true;

        _scrollAnimCts?.Cancel();
        _scrollAnimCts?.Dispose();
        _scrollAnimCts = new CancellationTokenSource();
        var ct = _scrollAnimCts.Token;

        var sv = ThumbnailScrollViewer;
        double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        if (maxY <= 0) return;

        double scrollAmount = e.Delta.Y * 400;
        double startY = sv.Offset.Y;
        double targetY = Math.Clamp(startY - scrollAmount, 0, maxY);

        if (Math.Abs(targetY - startY) < 0.5) return;

        const int duration = 120;
        const int steps = 10;
        const int interval = duration / steps;
        int animationId = ++_scrollAnimationId;
        int executedSteps = 0;
        int late20 = 0;
        int late33 = 0;
        int lateLogCount = 0;
        long worstGapMs = 0;
        long totalStepGapMs = 0;
        bool completed = false;
        var animationStopwatch = Stopwatch.StartNew();
        long previousStepTimestamp = animationStopwatch.ElapsedMilliseconds;

        try
        {
            for (int i = 1; i <= steps; i++)
            {
                if (ct.IsCancellationRequested) break;

                long currentTimestamp = animationStopwatch.ElapsedMilliseconds;
                long gapMs = currentTimestamp - previousStepTimestamp;
                previousStepTimestamp = currentTimestamp;
                worstGapMs = Math.Max(worstGapMs, gapMs);
                if (i > 1)
                {
                    totalStepGapMs += gapMs;
                    if (gapMs > 20)
                    {
                        late20++;
                        if (gapMs > 33) late33++;
                        if (lateLogCount++ < 2)
                            ScrollDiagnosticsLogger.Log($"ScrollAnim.Late id={animationId} step={i} gapMs={gapMs}");
                    }
                }

                double t = EaseOutQuad((double)i / steps);
                sv.Offset = new Vector(sv.Offset.X, startY + (targetY - startY) * t);
                executedSteps++;
                await Task.Delay(interval, ct);
            }
            completed = !ct.IsCancellationRequested && executedSteps == steps;
        }
        catch (OperationCanceledException) { }
        finally
        {
            long elapsedMs = animationStopwatch.ElapsedMilliseconds;
            double averageStepMs = executedSteps > 1 ? (double)totalStepGapMs / (executedSteps - 1) : 0;
            ScrollDiagnosticsLogger.Log(
                $"ScrollAnim.End id={animationId} status={(completed ? "completed" : "canceled")} " +
                $"steps={executedSteps}/{steps} elapsedMs={elapsedMs} avgStepMs={averageStepMs:F1} " +
                $"worstStepMs={worstGapMs} late20={late20} late33={late33} " +
                $"offset={startY:F0}->{sv.Offset.Y:F0} target={targetY:F0} " +
                $"viewport={sv.Viewport.Width:F0}x{sv.Viewport.Height:F0} " +
                $"extent={sv.Extent.Width:F0}x{sv.Extent.Height:F0}");
        }
    }

    private void BeginScrollActivity()
    {
        _scrollActivityCts?.Cancel();
        _scrollActivityCts?.Dispose();
        _scrollActivityCts = new CancellationTokenSource();
        var ct = _scrollActivityCts.Token;
        App.Services.GetRequiredService<PageManager>().SetScrollActivity(true);
        _ = EndScrollActivityWhenIdleAsync(ct);
    }

    private async Task EndScrollActivityWhenIdleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(350, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                        App.Services.GetRequiredService<PageManager>().SetScrollActivity(false);
                },
                DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { }
    }

    private static double EaseOutQuad(double t) => 1 - (1 - t) * (1 - t);

    // ==================== Folder Panel ====================

    private async void BtnAddFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择要加入列表的图片文件夹",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            await Vm.AddFolderCommand.ExecuteAsync(folders[0].Path.LocalPath);
        }
    }

    private async void BtnRemoveFolder_Click(object? sender, RoutedEventArgs e)
    {
        await Vm.RemoveFolderCommand.ExecuteAsync(null);
    }

    private async void LstFolders_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        CloseHoverPreview();
        if (Vm._isProgrammaticFolderSelection) return;
        Vm.ClearSearchHighlight();
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is ViewModels.FolderTreeNode folder)
        {
            await OpenFolderOrRelocateAsync(folder);
        }
    }

    private async Task OpenFolderOrRelocateAsync(ViewModels.FolderTreeNode folder)
    {
        if (Vm.NeedsRelocation(folder))
        {
            var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"文件夹路径已变更，请定位到 \"{folder.DisplayName}\" 的新位置",
                AllowMultiple = false
            });
            if (result.Count > 0)
            {
                await Vm.RelocateFolderAsync(folder.DbId, result[0].Path.LocalPath);
                await Vm.SelectFolderAsync(folder);
            }
            else
            {
                Vm.StatusText = $"文件夹路径已变更: {folder.Path}";
            }
            return;
        }
        await Vm.SelectFolderAsync(folder);
    }

    // ==================== Folder Search ====================

    private void TxtFolderSearch_GotFocus(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(Vm.FolderSearchText))
            Vm.IsFolderSearchPopupOpen = Vm.FolderSearchSuggestions.Count > 0;
    }

    private void FolderSuggestion_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is FolderTreeNode folder)
            Vm.SelectFolderSuggestionCommand.Execute(folder);
    }

    private void LstFolders_DragOver(object? sender, Avalonia.Input.DragEventArgs e)
    {
        e.DragEffects = Avalonia.Input.DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void LstFolders_Drop(object? sender, Avalonia.Input.DragEventArgs e)
    {
        try
        {
            var filePaths = ExtractAllFilePaths(e);
            if (filePaths.Count == 0) return;

            // Get target folder from selected or hovered tree item
            string? targetFolder = null;
            if (sender is TreeView tv)
            {
                targetFolder = (tv.SelectedItem as ViewModels.FolderTreeNode)?.Path;
                if (string.IsNullOrEmpty(targetFolder))
                {
                    var pos = e.GetPosition(tv);
                    var element = tv.InputHitTest(pos) as Avalonia.Visual;
                    while (element != null)
                    {
                        if (element is TreeViewItem tvi && tvi.DataContext is ViewModels.FolderTreeNode fn)
                        {
                            targetFolder = fn.Path;
                            break;
                        }
                        element = element.GetVisualParent();
                    }
                }
            }

            if (string.IsNullOrEmpty(targetFolder) || !Directory.Exists(targetFolder)) return;

            await Task.Run(() =>
            {
                Directory.CreateDirectory(targetFolder);
                foreach (var sourcePath in filePaths)
                {
                    if (!File.Exists(sourcePath)) continue;
                    try
                    {
                        var destPath = Common.Helpers.PathHelper.GetNonConflictingPath(
                            Path.Combine(targetFolder, Path.GetFileName(sourcePath)));
                        File.Copy(sourcePath, destPath);
                    }
                    catch { }
                }
            });

            if (IsFolderWithinCurrentView(targetFolder))
                await Vm.RefreshCurrentFolderFromDiskAsync("folder-drop");

            Vm.StatusText = $"已复制 {filePaths.Count} 个文件到目标文件夹";
        }
        catch { }
    }

    private bool IsFolderWithinCurrentView(string targetFolder)
    {
        if (string.IsNullOrWhiteSpace(Vm.CurrentFolder)) return false;
        try
        {
            var target = Path.GetFullPath(targetFolder).TrimEnd('\\', '/');
            var current = Path.GetFullPath(Vm.CurrentFolder).TrimEnd('\\', '/');
            if (string.Equals(target, current, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!Vm.ShowAllSubfolders)
                return false;
            return target.StartsWith(current + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || target.StartsWith(current + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Extract ALL file paths from DragEventArgs (not just images)</summary>
    private static List<string> ExtractAllFilePaths(Avalonia.Input.DragEventArgs e)
    {
        var result = new List<string>();
        var dt = e.DataTransfer;
        if (dt == null) return result;

        foreach (var item in dt.Items)
        {
            try
            {
                var m = item.GetType().GetMethod("GetText");
                if (m != null)
                {
                    var text = m.Invoke(item, null) as string;
                    if (!string.IsNullOrEmpty(text))
                    {
                        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var p = line.Trim();
                            if (File.Exists(p)) result.Add(p);
                        }
                    }
                }
            }
            catch { }
            try
            {
                foreach (var fmt in item.Formats)
                {
                    try
                    {
                        var raw = item.TryGetRaw(fmt);
                        if (raw is string s && File.Exists(s)) result.Add(s);
                        if (raw is System.Collections.IEnumerable en)
                        {
                            foreach (var obj in en)
                            {
                                if (obj is string ps && File.Exists(ps)) result.Add(ps);
                                try
                                {
                                    var pathProp = obj?.GetType().GetProperty("Path");
                                    if (pathProp != null)
                                    {
                                        var uri = pathProp.GetValue(obj);
                                        var localPath = uri?.GetType().GetProperty("LocalPath")?.GetValue(uri) as string;
                                        if (localPath != null && File.Exists(localPath)) result.Add(localPath);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
        return result;
    }

    // ==================== Toolbar Buttons ====================

    private async void BtnSelectFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择图片文件夹",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            await Vm.LoadFolderAsync(folders[0].Path.LocalPath);
        }
    }

    private async void BtnSaveData_Click(object? sender, RoutedEventArgs e)
    {
        _ = Vm.SaveSettingsAsync();
    }

    private async void MenuDetectDuplicates_Click(object? sender, RoutedEventArgs e)
    {
        AppLogger.Info("Menu.Tool.DetectDuplicates.Open");
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择重复图片存放的目标文件夹",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            var target = folders[0].Path.LocalPath;
            AppLogger.Info($"Menu.Tool.DetectDuplicates.Target path={target}");
            try
            {
                await Vm.DetectDuplicatesCommand.ExecuteAsync(target);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Menu.Tool.DetectDuplicates.Error type={ex.GetType().FullName} message={ex.Message}");
                throw;
            }
        }
    }

    // ==================== Menu Handlers ====================

    private async void MenuSetMainSize_Click(object? sender, RoutedEventArgs e)
    {
        var vm = new SizeSettingViewModel(
            Vm.AppSettings.StartupWidth > 0 ? Vm.AppSettings.StartupWidth : Width,
            Vm.AppSettings.StartupHeight > 0 ? Vm.AppSettings.StartupHeight : Height,
            "设置主界面默认大小",
            (w, h) =>
            {
                Vm.AppSettings.StartupWidth = w;
                Vm.AppSettings.StartupHeight = h;
                _ = Vm.SaveSettingsAsync();
            });

        var win = new Settings.SizeSettingWindow { DataContext = vm };
        await win.ShowDialog(this);
    }

    private async void MenuSetPreviewSize_Click(object? sender, RoutedEventArgs e)
    {
        var vm = new SizeSettingViewModel(
            Vm.AppSettings.PreviewWidth > 0 ? Vm.AppSettings.PreviewWidth : 800,
            Vm.AppSettings.PreviewHeight > 0 ? Vm.AppSettings.PreviewHeight : 600,
            "设置预览窗口默认大小",
            (w, h) =>
            {
                Vm.AppSettings.PreviewWidth = w;
                Vm.AppSettings.PreviewHeight = h;
                _ = Vm.SaveSettingsAsync();
            });

        var win = new Settings.SizeSettingWindow { DataContext = vm };
        await win.ShowDialog(this);
    }

    private void MenuAiRecommend_Click(object? sender, RoutedEventArgs e)
    {
        var recommendService = App.Services.GetRequiredService<Infrastructure.Services.DeepSeekRecommendService>();
        recommendService.SetApiKey(Vm.AppSettings.DeepSeekApiKey);

        var tagMappingRepo = App.Services.GetRequiredService<Core.Services.ITagMappingRepository>();
        var vm = new AiRecommendViewModel(recommendService, tagMappingRepo);
        var win = new Settings.AiRecommendWindow { DataContext = vm };
        win.Show(this);
    }

    private int FindActiveFileIndex(string filePath) =>
        Vm.ActiveFileList.FindIndex(path => string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase));

    private void SetLastSelectedIndex(string filePath)
    {
        var index = FindActiveFileIndex(filePath);
        if (index >= 0)
            _lastSelectedIndex = index;
    }

    private IEnumerable<(ImageViewItem Item, Control Container)> GetVisibleThumbnailContainers()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in Vm.Images)
        {
            if (ItemsImages.ContainerFromItem(item) is Control container && paths.Add(item.FilePath))
                yield return (item, container);
        }

        // Inspect realized controls only. The lazy continuous source must never be enumerated.
        foreach (var control in ContinuousItemsImages.GetVisualDescendants().OfType<Control>())
        {
            if (control.DataContext is not ImageViewItem item ||
                ContinuousItemsImages.ContainerFromItem(item) is not Control container ||
                !ReferenceEquals(control, container) ||
                !paths.Add(item.FilePath))
            {
                continue;
            }

            yield return (item, container);
        }
    }

    // ==================== Box Selection ====================

    private bool _isDraggingSelection;
    private Point _selectionStartPoint;

    private void ImagePanelHost_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        CloseHoverPreview();
        // Only start box selection if clicked on empty area (not on a thumbnail Border)
        var source = e.Source as Avalonia.Controls.Control;
        while (source != null)
        {
            if (source is Avalonia.Controls.Border b && b.DataContext is ImageViewItem)
                return; // Clicked on a thumbnail — handled by Thumbnail_PointerPressed
            source = source.Parent as Avalonia.Controls.Control;
        }

        // Dismiss tag search popup when clicking on blank area
        Vm.IsTagSearchPopupOpen = false;
        RootGrid.Focus();

        var point = e.GetCurrentPoint(ImagePanelHost);
        if (!point.Properties.IsLeftButtonPressed) return;

        _isDraggingSelection = true;
        e.Pointer.Capture(ImagePanelHost);
        _selectionStartPoint = e.GetPosition(ImagePanelHost);

        // Clear the canonical selection; recycled continuous items project this on realization.
        Vm.ReplaceSelectedFiles(Array.Empty<string>());

        SelectionRectangle.IsVisible = true;
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;
        SelectionRectangle.Margin = new Avalonia.Thickness(_selectionStartPoint.X, _selectionStartPoint.Y, 0, 0);
        e.Handled = true;
    }

    private void ImagePanelHost_PointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (!_isDraggingSelection) return;

        var current = e.GetPosition(ImagePanelHost);
        double x = Math.Min(_selectionStartPoint.X, current.X);
        double y = Math.Min(_selectionStartPoint.Y, current.Y);
        double w = Math.Abs(current.X - _selectionStartPoint.X);
        double h = Math.Abs(current.Y - _selectionStartPoint.Y);

        SelectionRectangle.Margin = new Avalonia.Thickness(x, y, 0, 0);
        SelectionRectangle.Width = w;
        SelectionRectangle.Height = h;

        var selRect = new Rect(x, y, w, h);

        // Hit-test only realized containers. Do not enumerate the continuous item source.
        var selectedPaths = new List<string>();
        foreach (var (img, container) in GetVisibleThumbnailContainers())
        {
            try
            {
                var transform = container.TransformToVisual(ImagePanelHost);
                if (transform == null) continue;
                var topLeft = transform.Value.Transform(new Point(0, 0));
                var itemRect = new Rect(topLeft, new Size(container.Bounds.Width, container.Bounds.Height));
                bool intersect = selRect.Intersects(itemRect);
                if (intersect)
                    selectedPaths.Add(img.FilePath);
            }
            catch { }
        }
        Vm.ReplaceSelectedFiles(selectedPaths);

        e.Handled = true;
    }

    private void ImagePanelHost_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (!_isDraggingSelection) return;
        _isDraggingSelection = false;
        e.Pointer.Capture(null);
        SelectionRectangle.IsVisible = false;
        e.Handled = true;
    }

    // ==================== Thumbnail Click / Multi-Select ====================

    private async void Thumbnail_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Border border) return;
        if (border.DataContext is not ImageViewItem item) return;

        CloseHoverPreview();

        // Dismiss tag search popup when clicking on an image
        Vm.IsTagSearchPopupOpen = false;
        RootGrid.Focus();

        // Store for potential drag-drop initiation
        _dragPressArgs = e;
        _lastClickScreenPos = e.GetPosition(this); // Relative to window for TagEdit positioning

        var point = e.GetCurrentPoint(border);

        // Middle mouse button: open tag editor for this image
        if (point.Properties.IsMiddleButtonPressed)
        {
            var selectedPaths = GetSelectedFilePaths();
            if (selectedPaths.Count > 1 && Vm.IsFileSelected(item.FilePath))
                await EditTagsForItemsAsync(await CreateTemporaryImageItemsAsync(selectedPaths, loadTags: true));
            else
                await EditTagForItemAsync(item);
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        bool ctrl = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift);

        if (ctrl)
        {
            // Toggle selection
            Vm.SetFileSelected(item.FilePath, !Vm.IsFileSelected(item.FilePath));
            SetLastSelectedIndex(item.FilePath);
        }
        else if (shift && _lastSelectedIndex >= 0)
        {
            // Shift range selection from anchor to clicked item
            int clickedIdx = FindActiveFileIndex(item.FilePath);
            if (clickedIdx < 0) return;
            int start = Math.Min(_lastSelectedIndex, clickedIdx);
            int end = Math.Max(_lastSelectedIndex, clickedIdx);
            Vm.ReplaceSelectedFiles(Vm.ActiveFileList.Skip(start).Take(end - start + 1));
        }
        else
        {
            bool multiSelected = Vm.SelectedFilePaths.Count > 1;
            if (multiSelected && Vm.IsFileSelected(item.FilePath))
            {
                // Defer single-select to pointer release (allow drag to cancel)
                _pendingClick = item;
            }
            else
            {
                Vm.ReplaceSelectedFiles(new[] { item.FilePath });
                SetLastSelectedIndex(item.FilePath);
            }
        }
    }

    private void Thumbnail_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (_pendingClick == null) return;
        if (sender is not Avalonia.Controls.Border border) return;
        if (border.DataContext is not ImageViewItem item) return;
        if (item != _pendingClick) return;

        Vm.ReplaceSelectedFiles(new[] { item.FilePath });
        SetLastSelectedIndex(item.FilePath);
        _pendingClick = null;
    }

    // ==================== Drag Thumbnail to Explorer / QQ / Browser ====================

    // Store PointerPressedEventArgs for DoDragDropAsync
    private Avalonia.Input.PointerPressedEventArgs? _dragPressArgs;
    private Point? _lastClickScreenPos;
    private ImageViewItem? _lastHoveredItem;
    private ImageViewItem? _pendingClick;
    private int _lastSelectedIndex = -1;

    // Hover preview state is UI-thread confined. The request version makes a
    // completed background decode harmless after the pointer has moved away.
    private readonly DispatcherTimer _hoverPreviewTimer;
    private DispatcherTimer _hoverPreviewCloseTimer = null!;
    private HoverPreviewPopup? _hoverPreview;
    private CancellationTokenSource? _hoverPreviewCts;
    private ImageViewItem? _hoverPreviewCandidate;
    private Border? _hoverPreviewSource;
    private PixelRect? _hoverPreviewSourceRect;
    private PixelPoint _hoverPreviewAnchor;
    private PixelPoint? _hoverPreviewLastPointerScreen;
    private int _hoverPreviewRequestVersion;
    private int _hoverPreviewCloseGeneration;

    private HoverPreviewPopup CreateHoverPreviewPopup()
    {
        var popup = new HoverPreviewPopup();
        popup.PointerEnteredPreview += OnHoverPreviewPointerEntered;
        popup.PointerExitedPreview += OnHoverPreviewPointerExited;
        return popup;
    }

    private void ReleaseHoverPreviewPopup()
    {
        var popup = _hoverPreview;
        if (popup == null)
            return;

        popup.PointerEnteredPreview -= OnHoverPreviewPointerEntered;
        popup.PointerExitedPreview -= OnHoverPreviewPointerExited;
        popup.Close();
        _hoverPreview = null;
    }

    private void CreateHoverPreviewCloseTimer()
    {
        var generation = ++_hoverPreviewCloseGeneration;
        _hoverPreviewCloseTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(220),
            DispatcherPriority.Background,
            (_, _) =>
            {
                if (generation == _hoverPreviewCloseGeneration)
                    CloseHoverPreviewIfPointerLeftHoverSession();
            });
    }

    private void StopHoverPreviewCloseTimer()
    {
        _hoverPreviewCloseGeneration++;
        _hoverPreviewCloseTimer.Stop();
    }

    private int GetHoverPreviewMaxSize() =>
        Vm.AppSettings.HoverPreviewMaxSize > 0
            ? Vm.AppSettings.HoverPreviewMaxSize
            : HoverPreviewPopup.DefaultMaxPreviewWidth;

    private void Thumbnail_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not ImageViewItem item)
            return;
        if (!FileTypeConstants.IsImageFile(item.FilePath) || !File.Exists(item.FilePath))
            return;

        var pointer = this.PointToScreen(e.GetPosition(this));
        _hoverPreviewLastPointerScreen = pointer;
        BeginHoverPreview(border, item, pointer);
    }

    private void Thumbnail_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Border border && border.DataContext == _hoverPreviewCandidate)
            ScheduleHoverPreviewClose();
    }

    private void BeginHoverPreview(Border source, ImageViewItem item, PixelPoint anchor)
    {
        StopHoverPreviewCloseTimer();
        if (_hoverPreviewCandidate == item && ReferenceEquals(_hoverPreviewSource, source))
            return;

        CloseHoverPreview();
        _hoverPreviewCandidate = item;
        _hoverPreviewSource = source;
        _hoverPreviewSourceRect = GetScreenRect(source);
        _hoverPreviewAnchor = anchor;
        _hoverPreviewLastPointerScreen = anchor;
        _hoverPreviewTimer.Start();
    }

    private void UpdatePendingHoverPreviewAnchor(Border source, ImageViewItem item, PixelPoint anchor)
    {
        if (_hoverPreviewCandidate != item || !ReferenceEquals(_hoverPreviewSource, source) ||
            _hoverPreview?.IsVisible == true)
            return;

        _hoverPreviewAnchor = anchor;
        _hoverPreviewLastPointerScreen = anchor;
        _hoverPreviewSourceRect = GetScreenRect(source);
        _hoverPreviewTimer.Stop();

        // Movement during an in-flight decode starts a fresh stillness session.
        // The completed old request is discarded by the request version check.
        var cts = _hoverPreviewCts;
        _hoverPreviewCts = null;
        if (cts != null)
        {
            _hoverPreviewRequestVersion++;
            cts.Cancel();
        }

        _hoverPreviewTimer.Start();
    }

    private void OnHoverPreviewTimerTick(object? sender, EventArgs e)
    {
        _hoverPreviewTimer.Stop();
        if (_hoverPreviewCandidate is not { } item || !FileTypeConstants.IsImageFile(item.FilePath))
            return;

        var cts = new CancellationTokenSource();
        _hoverPreviewCts = cts;
        var requestVersion = ++_hoverPreviewRequestVersion;
        _ = ShowHoverPreviewAsync(item, _hoverPreviewAnchor, requestVersion, cts);
    }

    private async Task ShowHoverPreviewAsync(
        ImageViewItem item,
        PixelPoint anchor,
        int requestVersion,
        CancellationTokenSource cts)
    {
        try
        {
            var maxPreviewSize = GetHoverPreviewMaxSize();
            var result = await Task.Run(() =>
            {
                cts.Token.ThrowIfCancellationRequested();
                var (width, height) = ThumbnailGenerator.GetDimensions(item.FilePath);
                if (width <= 0 || height <= 0)
                    return (Pixels: (byte[]?)null, DecodedWidth: 0, DecodedHeight: 0, OriginalWidth: 0, OriginalHeight: 0, FileSize: 0L);

                var scale = Math.Min(1d, Math.Min(maxPreviewSize / (double)width, maxPreviewSize / (double)height));
                var decodeWidth = Math.Max(1, (int)Math.Round(width * scale));
                var (pixels, decodedWidth, decodedHeight) = ThumbnailGenerator.DecodeRawPixels(item.FilePath, decodeWidth);
                cts.Token.ThrowIfCancellationRequested();
                long fileSize = new FileInfo(item.FilePath).Length;
                return (Pixels: pixels, DecodedWidth: decodedWidth, DecodedHeight: decodedHeight,
                    OriginalWidth: width, OriginalHeight: height, FileSize: fileSize);
            }, cts.Token);

            if (cts.Token.IsCancellationRequested || requestVersion != _hoverPreviewRequestVersion ||
                _hoverPreviewCandidate != item || result.Pixels == null)
                return;

            ReleaseHoverPreviewPopup();
            var popup = CreateHoverPreviewPopup();
            _hoverPreview = popup;

            popup.MaxPreviewWidth = maxPreviewSize;
            popup.MaxPreviewHeight = maxPreviewSize;
            var sourceRect = GetScreenRect(_hoverPreviewSource!);
            _hoverPreviewSourceRect = sourceRect;
            popup.Present(
                result.Pixels,
                result.DecodedWidth,
                result.DecodedHeight,
                item.FileName,
                result.OriginalWidth,
                result.OriginalHeight,
                result.FileSize,
                this,
                anchor,
                sourceRect);
        }
        catch (OperationCanceledException)
        {
            // Pointer movement and view changes intentionally cancel pending reads.
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"悬停预览加载失败: {Path.GetFileName(item.FilePath)}: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_hoverPreviewCts, cts))
                _hoverPreviewCts = null;
            cts.Dispose();
        }
    }

    private void OnHoverPreviewPointerEntered(object? sender, EventArgs e)
    {
        StopHoverPreviewCloseTimer();
    }

    private void OnHoverPreviewPointerExited(object? sender, EventArgs e)
    {
        ScheduleHoverPreviewClose();
    }

    private PixelRect GetScreenRect(Border source)
    {
        var topLeft = source.PointToScreen(new Point(0, 0));
        var bottomRight = source.PointToScreen(new Point(source.Bounds.Width, source.Bounds.Height));
        return new PixelRect(
            topLeft.X,
            topLeft.Y,
            Math.Max(1, bottomRight.X - topLeft.X),
            Math.Max(1, bottomRight.Y - topLeft.Y));
    }

    private bool TryGetCursorScreenPoint(out PixelPoint point)
    {
        if (OperatingSystem.IsWindows() && GetCursorPos(out var nativePoint))
        {
            point = new PixelPoint(nativePoint.X, nativePoint.Y);
            _hoverPreviewLastPointerScreen = point;
            return true;
        }

        if (_hoverPreviewLastPointerScreen is { } lastPoint)
        {
            point = lastPoint;
            return true;
        }

        point = default;
        return false;
    }

    private bool IsCursorInActiveHoverSession()
    {
        if (_hoverPreviewCandidate == null || !TryGetCursorScreenPoint(out var cursor))
            return false;

        var sourceRect = _hoverPreviewSource != null
            ? GetScreenRect(_hoverPreviewSource)
            : _hoverPreviewSourceRect;
        if (sourceRect is { } source && ContainsExpanded(source, cursor, 8))
            return true;

        if (_hoverPreview?.CurrentScreenRect is { } popup && ContainsExpanded(popup, cursor, 8))
            return true;

        return false;
    }

    private static bool ContainsExpanded(PixelRect rect, PixelPoint point, int padding)
    {
        return point.X >= rect.X - padding && point.X <= rect.Right + padding &&
               point.Y >= rect.Y - padding && point.Y <= rect.Bottom + padding;
    }

    private void ScheduleHoverPreviewClose()
    {
        _hoverPreviewTimer.Stop();
        StopHoverPreviewCloseTimer();
        CreateHoverPreviewCloseTimer();
        _hoverPreviewCloseTimer.Start();
    }

    private void CloseHoverPreviewIfPointerLeftHoverSession()
    {
        if (IsCursorInActiveHoverSession())
        {
            StopHoverPreviewCloseTimer();
            return;
        }

        CloseHoverPreview();
    }

    private void CloseHoverPreview()
    {
        _hoverPreviewTimer.Stop();
        StopHoverPreviewCloseTimer();
        _hoverPreviewCandidate = null;
        _hoverPreviewSource = null;
        _hoverPreviewSourceRect = null;
        _hoverPreviewLastPointerScreen = null;
        _hoverPreviewRequestVersion++;

        var cts = _hoverPreviewCts;
        _hoverPreviewCts = null;
        if (cts != null)
        {
            cts.Cancel();
        }
        _hoverPreview?.ResetPreviewSurface();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.Images))
            CloseHoverPreview();

        if (e.PropertyName == nameof(MainWindowViewModel.ContinuousDisplayGeometrySnapshot))
            AttachContinuousWaterfallPanelWhenAvailable();

        if (e.PropertyName == nameof(MainWindowViewModel.DisplayMode) && Vm.IsPagedDisplay)
            _ = ApplyDisplayModeAsync(0);

        if (Vm.DisplayMode == ImageDisplayMode.Continuous &&
            e.PropertyName is nameof(MainWindowViewModel.WaterfallMode) or nameof(MainWindowViewModel.ThumbnailBaseWidth))
            _ = ApplyDisplayModeAsync(GetContinuousAnchorIndex());
    }

    private int GetContinuousAnchorIndex() => Math.Max(0, _continuousVisibleRange.StartIndex);

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel ||
            viewModel.DisplayMode != ImageDisplayMode.Continuous)
        {
            return;
        }

        var width = GetContinuousContainerWidth();
        if (width <= 0 || (viewModel.ContinuousDisplayGeometryIndex is { } geometry &&
            Math.Abs(width - geometry.Options.ContainerWidth) < 1))
            return;

        // Keep the current leading item visible. This is a fixed-rate throttle,
        // not a trailing debounce, so reflow stays visually coupled to dragging.
        _continuousResizeAnchorIndex = GetContinuousAnchorIndex();
        _continuousResizeRequestedWidth = width;
        if (!_continuousResizeTimer.IsEnabled)
            _continuousResizeTimer.Start();
    }

    private void OnContinuousResizeTimerTick(object? sender, EventArgs e)
    {
        _continuousResizeTimer.Stop();
        if (DataContext is not MainWindowViewModel viewModel ||
            viewModel.DisplayMode != ImageDisplayMode.Continuous)
        {
            return;
        }

        var width = _continuousResizeRequestedWidth;
        if (width <= 0)
            width = GetContinuousContainerWidth();
        if (width <= 0)
            return;

        if (viewModel.ContinuousDisplayGeometryIndex is { } geometry)
        {
            if (Math.Abs(width - geometry.Options.ContainerWidth) >= 1)
            {
                // Do not cancel work that is already arranging the previous frame.
                // Constant cancellation while the user drags makes the view appear
                // frozen; when it completes, the latest requested width is queued.
                if (_continuousReflowCts is not null)
                    return;

                _continuousReflowCts = new CancellationTokenSource();
                _ = ReflowContinuousDisplayAsync(width, _continuousResizeAnchorIndex, _continuousReflowCts);
            }
            return;
        }

        // A resize before first geometry publication still needs the full initial
        // build; all later resize work follows the CPU-only reflow path above.
        _ = ApplyDisplayModeAsync(_continuousResizeAnchorIndex);
    }

    private double GetContinuousContainerWidth()
    {
        var width = ThumbnailScrollViewer.Viewport.Width;
        return width > 0 ? width : ImagePanelHost.Bounds.Width;
    }

    private async Task ReflowContinuousDisplayAsync(
        double width,
        int preferredIndex,
        CancellationTokenSource cts)
    {
        try
        {
            if (!await Vm.ReflowContinuousDisplayGeometryAsync(width, cts.Token) ||
                cts.IsCancellationRequested ||
                Vm.DisplayMode != ImageDisplayMode.Continuous)
            {
                return;
            }

            // Reflow keeps the old source and thumbnail cache alive. Updating layout
            // before scrolling publishes the new extent and prevents offset clamping.
            ContinuousItemsImages.UpdateLayout();
            _continuousWaterfallPanel?.ScrollToIndex(Math.Clamp(preferredIndex, 0, Vm.ActiveFileList.Count - 1));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // A later resize superseded this calculation.
        }
        finally
        {
            var completedCurrentReflow = ReferenceEquals(_continuousReflowCts, cts);
            var wasCancelled = cts.IsCancellationRequested;
            if (completedCurrentReflow)
                _continuousReflowCts = null;
            cts.Dispose();

            // Coalesce all size notifications that arrived while this CPU-only
            // layout was running into one follow-up frame at the newest width.
            if (completedCurrentReflow && !wasCancelled &&
                Vm.DisplayMode == ImageDisplayMode.Continuous &&
                Vm.ContinuousDisplayGeometryIndex is { } geometry &&
                Math.Abs(GetContinuousContainerWidth() - geometry.Options.ContainerWidth) >= 1 &&
                !_continuousResizeTimer.IsEnabled)
            {
                _continuousResizeRequestedWidth = GetContinuousContainerWidth();
                _continuousResizeTimer.Start();
            }
        }
    }

    private async Task ApplyDisplayModeAsync(int preferredIndex)
    {
        _continuousReflowCts?.Cancel();
        _continuousReflowCts = null;
        _continuousModeCts?.Cancel();
        _continuousModeCts?.Dispose();
        _continuousModeCts = new CancellationTokenSource();
        var token = _continuousModeCts.Token;

        if (Vm.DisplayMode == ImageDisplayMode.Paged)
        {
            _continuousResizeTimer.Stop();
            ContinuousItemsImages.IsVisible = false;
            ItemsImages.IsVisible = true;
            return;
        }

        ItemsImages.IsVisible = false;
        ContinuousItemsImages.IsVisible = true;
        await Dispatcher.UIThread.InvokeAsync(() => ContinuousItemsImages.UpdateLayout());
        if (token.IsCancellationRequested || Vm.DisplayMode != ImageDisplayMode.Continuous)
            return;
        var width = GetContinuousContainerWidth();
        if (!await Vm.PrepareContinuousDisplayGeometryAsync(width, token) || token.IsCancellationRequested)
            return;

        AttachContinuousWaterfallPanelWhenAvailable();
        // Publish the new scroll extent before setting Offset, otherwise the
        // ScrollViewer clamps a requested anchor to the previous (often zero) extent.
        ContinuousItemsImages.UpdateLayout();
        _continuousWaterfallPanel?.ScrollToIndex(Math.Clamp(preferredIndex, 0, Vm.ActiveFileList.Count - 1));
    }

    private void AttachContinuousWaterfallPanelWhenAvailable()
    {
        if (_continuousWaterfallPanel != null)
            return;

        if (ContinuousItemsImages.ItemsPanelRoot is not VirtualizingWaterfallPanel panel)
            return;

        _continuousWaterfallPanel = panel;
        panel.VisibleRangeChanged += OnContinuousVisibleRangeChanged;
        panel.RetainedRangeChanged += OnContinuousRetainedRangeChanged;
        ContinuousItemsImages.LayoutUpdated -= OnContinuousItemsImagesLayoutUpdated;
        _continuousVisibleRange = panel.CurrentVisibleRange;
        Vm.UpdateContinuousDisplayViewport(
            panel.CurrentVisibleRange,
            panel.CurrentRetainedRange);
    }

    private void OnContinuousItemsImagesLayoutUpdated(object? sender, EventArgs e) =>
        AttachContinuousWaterfallPanelWhenAvailable();

    private void OnContinuousVisibleRangeChanged(object? sender, ImageDisplayRange range)
    {
        _continuousVisibleRange = range;
        _continuousScrollDirection = _continuousWaterfallPanel?.CurrentScrollDirection
            ?? ContinuousScrollDirection.None;
    }

    private void OnContinuousRetainedRangeChanged(object? sender, ImageDisplayRange range)
    {
        Vm.UpdateContinuousDisplayViewport(_continuousVisibleRange, range, _continuousScrollDirection);
    }

    private void ThumbnailContextMenu_Opened(object? sender, RoutedEventArgs e) => CloseHoverPreview();

    private async void Thumbnail_PointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (sender is not Avalonia.Controls.Border border) return;
        if (border.DataContext is not ImageViewItem item) return;

        _lastHoveredItem = item;
        _pendingClick = null; // movement cancels deferred single-select
        var point = e.GetCurrentPoint(border);
        var pointer = this.PointToScreen(e.GetPosition(this));
        if (!point.Properties.IsLeftButtonPressed)
        {
            if (_hoverPreviewCandidate != item || !ReferenceEquals(_hoverPreviewSource, border))
                BeginHoverPreview(border, item, pointer);
            else
                UpdatePendingHoverPreviewAnchor(border, item, pointer);
        }

        if (!point.Properties.IsLeftButtonPressed) return;
        CloseHoverPreview();
        if (_dragPressArgs == null) return;

        // Ensure the dragged item is selected
        if (!Vm.IsFileSelected(item.FilePath))
        {
            Vm.ReplaceSelectedFiles(new[] { item.FilePath });
        }

        var filePaths = GetSelectedFilePaths().Where(File.Exists).ToList();
        if (filePaths.Count == 0) return;

        try
        {
            var dt = new Avalonia.Input.DataTransfer();
            var topLevel = TopLevel.GetTopLevel(border);
            if (topLevel?.StorageProvider != null)
            {
                // Batch-resolve all selected files to IStorageFile
                var storageFiles = new List<IStorageItem>();
                foreach (var path in filePaths)
                {
                    try
                    {
                        var file = await topLevel.StorageProvider.TryGetFileFromPathAsync(path);
                        if (file != null) storageFiles.Add(file);
                    }
                    catch { }
                }

                if (storageFiles.Count == 0) return;

                // One DataTransferItem per file (for external apps: Explorer, QQ, browser upload)
                foreach (var file in storageFiles)
                {
                    var dropItem = new Avalonia.Input.DataTransferItem();
                    dropItem.SetFile(file);
                    dt.Add(dropItem);
                }

                // One text item with all paths (for internal handlers: search zone, folder list)
                var textItem = new Avalonia.Input.DataTransferItem();
                textItem.SetText(string.Join("\r\n", filePaths));
                dt.Add(textItem);
            }

            if (dt.Items.Count == 0) return;

            await Avalonia.Input.DragDrop.DoDragDropAsync(
                _dragPressArgs, dt,
                Avalonia.Input.DragDropEffects.Copy);
        }
        catch { }
        finally { _dragPressArgs = null; }
    }

    // ==================== Thumbnail Interaction ====================

    private async void Thumbnail_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        CloseHoverPreview();
        if (sender is not Avalonia.Controls.Border border) return;
        if (border.DataContext is not ImageViewItem item) return;
        if (!File.Exists(item.FilePath)) return;

        // Video: open with system default player
        if (FileTypeConstants.IsVideoFile(item.FilePath))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = item.FilePath,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Failed to open video: {ex.Message}");
            }
        }
        else
        {
            // Image: use built-in preview window
            await OpenPreviewForFileAsync(item.FilePath);
        }
    }

    // ==================== Context Menu Handlers ====================

    /// <summary>Get ImageViewItem from MenuItem.DataContext (Avalonia inherits from PlacementTarget)</summary>
    private static ImageViewItem? GetCtxItem(object? sender) =>
        (sender as Avalonia.Controls.MenuItem)?.DataContext as ImageViewItem;

    private List<string> GetSelectedFilePaths() =>
        Vm.SelectedFilePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Creates lightweight items only for APIs that still require ImageViewItem.
    /// This deliberately never walks ContinuousImageItemSource, whose uncached
    /// entries must remain unmaterialized in continuous mode.
    /// </summary>
    private List<ImageViewItem> CreateTemporaryImageItems(IEnumerable<string> filePaths) =>
        filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new ImageViewItem
            {
                FilePath = path,
                FileName = Path.GetFileName(path)
            })
            .ToList();

    private async Task<List<ImageViewItem>> CreateTemporaryImageItemsAsync(
        IEnumerable<string> filePaths, bool loadTags)
    {
        var items = CreateTemporaryImageItems(filePaths);
        if (!loadTags || items.Count == 0)
            return items;

        var tagsByPath = await Vm.EnsureTagsLoadedAsync(items.Select(item => item.FilePath).ToList());
        foreach (var item in items)
        {
            if (tagsByPath.TryGetValue(item.FilePath, out var tags))
                item.Tags = new List<string>(tags);
        }

        return items;
    }

    /// <summary>If the right-clicked item is in a multi-selection, operate on all selected paths; otherwise just the clicked path.</summary>
    private List<string> GetTargetFilePathsForContextMenu(ImageViewItem clickedItem)
    {
        var selectedPaths = GetSelectedFilePaths();
        return selectedPaths.Count > 1 && Vm.IsFileSelected(clickedItem.FilePath)
            ? selectedPaths
            : new List<string> { clickedItem.FilePath };
    }

    private async void MenuEditTag_Click(object? sender, RoutedEventArgs e)
    {
        var clicked = GetCtxItem(sender);
        if (clicked == null)
            return;

        var targetPaths = GetTargetFilePathsForContextMenu(clicked);
        if (targetPaths.Count <= 1)
        {
            await EditTagForItemAsync(clicked);
        }
        else
        {
            await EditTagsForItemsAsync(await CreateTemporaryImageItemsAsync(targetPaths, loadTags: true));
        }
    }

    private async void MenuRegenerateThumbnail_Click(object? sender, RoutedEventArgs e)
    {
        var clicked = GetCtxItem(sender);
        if (clicked == null) return;

        var items = CreateTemporaryImageItems(GetTargetFilePathsForContextMenu(clicked));
        await App.Services.GetRequiredService<PageManager>().RegenerateThumbnailsAsync(items);
        Vm.StatusText = $"已重新生成 {items.Count} 个缩略图";
    }

    private async Task<bool> EditTagForItemAsync(ImageViewItem item)
    {
        var allTags = Vm.GetAllTagCountsSnapshot();

        // ✅ 如果 Tags 为空，主动从数据库加载
        var currentTags = item.Tags;
        if (currentTags.Count == 0)
        {
            var tagsFromDb = await Vm.GetTagsForFileAsync(item.FilePath);
            if (tagsFromDb.Count > 0)
            {
                item.Tags = tagsFromDb;
                item.NotifyAll();
                currentTags = tagsFromDb;
            }
        }

        var tagVm = new TagEditViewModel(
            string.Join(", ", currentTags),
            allTags,
            Vm.AppSettings.FavoriteTags,
            Vm.AppSettings.MaxTagSuggestionCount,
            onRenameTag: (oldName, newName) => Vm.RenameTagAsync(oldName, newName),
            onMergeTags: (oldName, newName) => Vm.MergeTagsAsync(oldName, newName));

        var win = new Settings.TagEditWindow { DataContext = tagVm };
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        PixelPoint screenPos;
        if (_lastClickScreenPos.HasValue)
        {
            screenPos = this.PointToScreen(_lastClickScreenPos.Value);
        }
        else
        {
            // Get current mouse cursor position on screen via Win32 API
            POINT pt;
            GetCursorPos(out pt);
            screenPos = new PixelPoint(pt.X, pt.Y);
        }
        // Use the screen that contains the cursor, not always primary
        var screen = Screens.ScreenFromPoint(screenPos) ?? Screens.Primary;
        var bounds = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        int x = (int)screenPos.X;
        int y = (int)screenPos.Y;
        int w = (int)win.Width;
        int h = (int)win.Height;
        if (x + w > bounds.X + bounds.Width)  x = bounds.X + bounds.Width - w;
        if (y + h > bounds.Y + bounds.Height) y = bounds.Y + bounds.Height - h;
        if (x < bounds.X) x = bounds.X;
        if (y < bounds.Y) y = bounds.Y;
        win.Position = new PixelPoint(x, y);
        var result = await win.ShowDialog<bool>(this);

        if (result)
        {
            var newTags = tagVm.ResultText
                .Split(',')
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            await Vm.SetImageTagsAsync(item.FilePath, newTags);
            item.Tags = newTags;
            item.NotifyAll();
            await Vm.RefreshTagCountsAsync(forceRefresh: true);
            await Vm.SaveSettingsAsync();
        }

        return result;
    }

    private async Task EditTagsForItemsAsync(List<ImageViewItem> items)
    {
        var allTags = Vm.GetAllTagCountsSnapshot();

        // ✅ 批量从数据库加载 Tags（如果缓存未命中）
        var filePaths = items.Select(i => i.FilePath).ToList();
        var tagsDict = await Vm.EnsureTagsLoadedAsync(filePaths);

        // 更新 ImageViewItem
        foreach (var item in items)
        {
            if (item.Tags.Count == 0 && tagsDict.TryGetValue(item.FilePath, out var tags) && tags.Count > 0)
            {
                item.Tags = tags;
                item.NotifyAll();
            }
        }

        // 计算交集：所有选中图片共有的 tag
        var tagSets = items.Select(i => new HashSet<string>(i.Tags, StringComparer.OrdinalIgnoreCase)).ToList();
        var intersection = new List<string>();
        if (tagSets.Count > 0)
        {
            foreach (var tag in tagSets[0])
            {
                if (tagSets.Skip(1).All(s => s.Contains(tag)))
                    intersection.Add(tag);
            }
        }

        var tagVm = new TagEditViewModel(
            intersection,
            allTags,
            Vm.AppSettings.FavoriteTags,
            Vm.AppSettings.MaxTagSuggestionCount,
            onAddTagToAll: async tag =>
            {
                foreach (var item in items)
                {
                    if (!item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                        item.Tags.Add(tag);
                }
                await Vm.AddTagToImagesBatchAsync(
                    items.Select(i => i.FilePath).ToList(), tag);
            },
            onRemoveTagFromAll: async tag =>
            {
                foreach (var item in items)
                    item.Tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                await Vm.RemoveTagFromImagesBatchAsync(
                    items.Select(i => i.FilePath).ToList(), tag);
            },
            onClearAllTags: async () =>
            {
                foreach (var item in items)
                    item.Tags.Clear();
                await Vm.ClearTagsFromImagesBatchAsync(
                    items.Select(i => i.FilePath).ToList());
            });

        var win = new Settings.TagEditWindow { DataContext = tagVm };
        win.Title = $"编辑 Tag — {items.Count} 张图片";
        await win.ShowDialog<bool>(this);

        foreach (var item in items)
            item.NotifyAll();
        await Vm.RefreshTagCountsAsync(forceRefresh: true);
    }

    private async void MenuClearSelectedTags_Click(object? sender, RoutedEventArgs e)
    {
        var selectedPaths = GetSelectedFilePaths();
        if (selectedPaths.Count == 0) { Vm.StatusText = "未选中图片"; return; }

        var ok = await ShowConfirmDialogAsync($"确定要清空 {selectedPaths.Count} 张选中图片的所有标签？");
        if (!ok) return;
        AppLogger.Warn($"ClearSelectedTags confirmed count={selectedPaths.Count}");
        await Vm.ClearTagsFromImagesBatchAsync(selectedPaths);
        AppLogger.Info($"ClearSelectedTags tag counts refreshed count={selectedPaths.Count}");
        Vm.StatusText = $"已清空 {selectedPaths.Count} 张图片的标签";
    }

    private async void MenuAutoTag_Click(object? sender, RoutedEventArgs e)
    {
        var ctxItem = GetCtxItem(sender);
        var filePaths = ctxItem is null
            ? GetSelectedFilePaths()
            : GetTargetFilePathsForContextMenu(ctxItem);
        if (filePaths.Count == 0)
        {
            Vm.StatusText = "未找到图片";
            return;
        }

        await RunAutoTagAsync(null, filePaths, false);
    }

    private async void MenuCopyImage_Click(object? sender, RoutedEventArgs e)
    {
        var item = GetCtxItem(sender);
        if (item != null)
            await CopyImageToClipboardAsync(item.FilePath);
    }

    private async void MenuFindSimilar_Click(object? sender, RoutedEventArgs e)
    {
        var baseItem = GetCtxItem(sender);
        if (baseItem == null) return;

        if (int.TryParse((sender as MenuItem)?.Tag?.ToString(), out var mode) &&
            Enum.IsDefined(typeof(SimilaritySearchMode), mode))
        {
            Vm.SelectSimilarityMode((SimilaritySearchMode)mode);
        }

        Vm.StatusText = "正在全文件夹搜索相似图片...";
        Vm.PreSearchScrollOffset = ThumbnailScrollViewer.Offset.Y;
        // Run on background thread to avoid UI freeze
        await Vm.SearchSimilarCommand.ExecuteAsync(baseItem.FilePath);
    }

    private async void MenuVectorIndexSettings_Click(object? sender, RoutedEventArgs e)
    {
        var indexService = App.Services.GetRequiredService<IVectorIndexService>();
        var clipService = App.Services.GetRequiredService<ImageManager.Infrastructure.Services.ChineseClipService>();
        var viewModel = new VectorIndexViewModel(indexService, clipService);
        var window = new Settings.VectorIndexWindow { DataContext = viewModel };
        await window.ShowDialog(this);
    }

    private async void MenuSearchOnline_Click(object? sender, RoutedEventArgs e)
    {
        var item = GetCtxItem(sender);
        if (item == null || !File.Exists(item.FilePath)) return;

        var tag = (sender as MenuItem)?.Tag as string;
        if (string.IsNullOrEmpty(tag)) return;

        Vm.StatusText = "正在上传搜图...";
        var ok = await OnlineSearchHelper.SearchAsync(item.FilePath, tag);
        if (!ok)
        {
            OnlineSearchHelper.OpenHomePage(tag);
            Vm.StatusText = "已打开搜图页面，请手动上传图片";
        }
        else
        {
            Vm.StatusText = "搜图结果已打开";
        }
    }

    private void MenuOpenInExplorer_Click(object? sender, RoutedEventArgs e)
    {
        var item = GetCtxItem(sender);
        if (item != null && File.Exists(item.FilePath))
            OpenInExplorer(item.FilePath);
    }

    private async void MenuDeleteFile_Click(object? sender, RoutedEventArgs e)
    {
        var clicked = GetCtxItem(sender);
        if (clicked == null) return;
        var filePaths = GetTargetFilePathsForContextMenu(clicked);
        if (filePaths.Count > 0)
            await DeleteSelectedFilesAsync(filePaths);
    }

    private async void MenuCopyFileToFolder_Click(object? sender, RoutedEventArgs e)
    {
        var clicked = GetCtxItem(sender);
        if (clicked == null) return;
        var filePaths = GetTargetFilePathsForContextMenu(clicked);

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择要复制到的目标文件夹", AllowMultiple = false
        });
        if (folders.Count == 0) return;

        var targetDir = folders[0].Path.LocalPath;
        Vm.StatusText = "正在复制...";
        int success = 0;
        await Task.Run(() =>
        {
            Directory.CreateDirectory(targetDir);
            foreach (var filePath in filePaths)
            {
                if (!File.Exists(filePath)) continue;
                try
                {
                    var destPath = Common.Helpers.PathHelper.GetNonConflictingPath(
                        Path.Combine(targetDir, Path.GetFileName(filePath)));
                    File.Copy(filePath, destPath);
                    Interlocked.Increment(ref success);
                }
                catch { }
            }
        });

        if (string.Equals(Path.GetFullPath(targetDir).TrimEnd('\\', '/'),
                Path.GetFullPath(Vm.CurrentFolder).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
            await Vm.LoadFolderAsync(Vm.CurrentFolder);

        Vm.StatusText = $"已复制 {success} 个文件到: {targetDir}";
    }

    private async void MenuMoveFileToFolder_Click(object? sender, RoutedEventArgs e)
    {
        var clicked = GetCtxItem(sender);
        if (clicked == null) return;
        var filePaths = GetTargetFilePathsForContextMenu(clicked);

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择要移动到的目标文件夹（剪贴）", AllowMultiple = false
        });
        if (folders.Count == 0) return;

        var targetDir = folders[0].Path.LocalPath;
        Vm.StatusText = "正在移动...";
        int success = 0;

        await Task.Run(() =>
        {
            var thumbCache = App.Services.GetRequiredService<Infrastructure.Caching.ThumbnailCacheService>();
            Directory.CreateDirectory(targetDir);
            foreach (var filePath in filePaths)
            {
                if (!File.Exists(filePath)) continue;
                var srcDir = Path.GetDirectoryName(filePath) ?? "";
                if (string.Equals(Path.GetFullPath(srcDir).TrimEnd('\\', '/'),
                        Path.GetFullPath(targetDir).TrimEnd('\\', '/'),
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    var destPath = Common.Helpers.PathHelper.GetNonConflictingPath(
                        Path.Combine(targetDir, Path.GetFileName(filePath)));
                    var oldPath = filePath;
                    File.Move(oldPath, destPath);
                    thumbCache.MoveDiskCache(oldPath, destPath);
                    Interlocked.Increment(ref success);
                }
                catch { }
            }
        });

        await Vm.LoadFolderAsync(Vm.CurrentFolder);
        Vm.StatusText = $"已移动 {success} 个文件到: {targetDir}";
    }

    private async void MenuRefreshFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm.ShowAllSubfolders)
            await Vm.RebuildFileListAsync();
        else
            await Vm.SyncCurrentFolderAsync();
    }

    private async void MenuWallpaperSettings_Click(object? sender, RoutedEventArgs e)
    {
        var vm = new WallpaperSettingViewModel(Vm.AppSettings, ApplyWallpaper);
        var win = new Settings.WallpaperSettingWindow { DataContext = vm };
        await win.ShowDialog(this);
    }

    private void ApplyWallpaper()
    {
        var path = Vm.AppSettings.WallpaperPath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                if (Background is Avalonia.Media.ImageBrush { Source: IDisposable oldBitmap })
                    oldBitmap.Dispose();
                var brush = new Avalonia.Media.ImageBrush(
                    new Avalonia.Media.Imaging.Bitmap(path));
                brush.Opacity = Vm.AppSettings.WallpaperOpacity;
                brush.Stretch = Vm.AppSettings.WallpaperStretch switch
                {
                    "None" => Avalonia.Media.Stretch.None,
                    "Fill" => Avalonia.Media.Stretch.Fill,
                    "UniformToFill" => Avalonia.Media.Stretch.UniformToFill,
                    _ => Avalonia.Media.Stretch.Uniform
                };
                // Set alignment
                brush.AlignmentX = Vm.AppSettings.WallpaperAlignment switch
                {
                    "TopLeft" or "BottomLeft" => Avalonia.Media.AlignmentX.Left,
                    "TopRight" or "BottomRight" => Avalonia.Media.AlignmentX.Right,
                    _ => Avalonia.Media.AlignmentX.Center
                };
                brush.AlignmentY = Vm.AppSettings.WallpaperAlignment switch
                {
                    "TopLeft" or "TopRight" => Avalonia.Media.AlignmentY.Top,
                    "BottomLeft" or "BottomRight" => Avalonia.Media.AlignmentY.Bottom,
                    _ => Avalonia.Media.AlignmentY.Center
                };
                Background = brush;
            }
            catch { }
        }
        else
        {
            ClearValue(BackgroundProperty);
        }
        _ = Vm.SaveSettingsAsync();
    }
    // ==================== Drag External Image into Search Zone ====================

    private void DragSearchZone_DragOver(object? sender, Avalonia.Input.DragEventArgs e)
    {
        e.DragEffects = Avalonia.Input.DragDropEffects.Copy;
        e.Handled = true;
    }

    /// <summary>Extract the first image file path from DragEventArgs</summary>
    private static string? ExtractFirstImagePath(Avalonia.Input.DragEventArgs e)
    {
        var dt = e.DataTransfer;
        if (dt == null) return null;
        var exts = FileTypeConstants.AllMediaExtensions.ToArray();

        foreach (var item in dt.Items)
        {
            // Method 1: GetText (works for internal drags with SetText)
            try
            {
                var m = item.GetType().GetMethod("GetText");
                if (m != null)
                {
                    var text = m.Invoke(item, null) as string;
                    if (!string.IsNullOrEmpty(text))
                    {
                        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var t = line.Trim();
                            if (exts.Contains(Path.GetExtension(t).ToLower()) && File.Exists(t))
                                return t;
                        }
                    }
                }
            }
            catch { }

            // Method 2: Try all formats for file paths
            try
            {
                foreach (var fmt in item.Formats)
                {
                    try
                    {
                        var raw = item.TryGetRaw(fmt);
                        if (raw == null) continue;

                        // Single string path
                        if (raw is string s && !string.IsNullOrEmpty(s))
                        {
                            foreach (var line in s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                var t = line.Trim();
                                if (exts.Contains(Path.GetExtension(t).ToLower()) && File.Exists(t))
                                    return t;
                            }
                        }
                        // IEnumerable of strings/paths
                        if (raw is System.Collections.IEnumerable en)
                        {
                            foreach (var obj in en)
                            {
                                var p = obj?.ToString();
                                if (p != null)
                                {
                                    // Could be IStorageItem with Path property
                                    var pathProp = obj.GetType().GetProperty("Path");
                                    if (pathProp != null)
                                    {
                                        var uri = pathProp.GetValue(obj);
                                        var localPath = uri?.GetType().GetProperty("LocalPath")?.GetValue(uri) as string;
                                        if (localPath != null && exts.Contains(Path.GetExtension(localPath).ToLower()) && File.Exists(localPath))
                                            return localPath;
                                    }
                                    // Plain string path
                                    if (exts.Contains(Path.GetExtension(p).ToLower()) && File.Exists(p))
                                        return p;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
        return null;
    }

    private async void DragSearchZone_Drop(object? sender, Avalonia.Input.DragEventArgs e)
    {
        var firstImage = ExtractFirstImagePath(e);
        if (firstImage != null)
        {
            Vm.PreSearchScrollOffset = ThumbnailScrollViewer.Offset.Y;
            await Vm.SearchSimilarCommand.ExecuteAsync(firstImage);
        }
    }

    // Drag-to-folder handled by ListBox DragDrop events below

    private async void TagSuggestion_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Core.Models.TagCount tag)
        {
            Vm.PreSearchScrollOffset = ThumbnailScrollViewer.Offset.Y;
            var wasCoTagMode = Vm.IsSuggestionCoTagMode;
            await Vm.SelectTagSuggestionCommand.ExecuteAsync(tag);

            // Update border color only for co-tag cycling; prefix-mode click already removed this button
            if (wasCoTagMode && btn.Content is Border border)
            {
                var state = Vm.GetCoTagState(tag.Name);
                border.Background = state switch
                {
                    1 => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#86D9B0")), // green: AND
                    2 => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8CB8E8")), // blue: AND-each
                    3 => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E8A0A0")), // red: NOT
                    _ => null // default
                };
            }

            // Return focus to search box so Enter triggers search, not another cycle
            var searchBox = Vm.IsSearchToolbarLayoutC ? TxtTagSearchC : TxtTagSearchA;
            searchBox.Focus();
            searchBox.CaretIndex = searchBox.Text?.Length ?? 0;
        }
    }

    private void TxtTagSearch_GotFocus(object? sender, RoutedEventArgs e)
    {
        Vm.OnTagSearchGotFocus();
    }

    private async void TxtTagSearch_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Vm.PreSearchScrollOffset = ThumbnailScrollViewer.Offset.Y;
            await Vm.SearchByTagCommand.ExecuteAsync(null);
            RootGrid.Focus();
            e.Handled = true;
        }
    }

    private void BtnStopSearch_Click(object? sender, RoutedEventArgs e)
    {
        Vm.StopSearchCommand.Execute(null);
    }

    // ==================== Folder Context Menu ====================

    private void LstFolders_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(LstFolders);

        var element = e.Source as Control;
        while (element != null && element is not TreeViewItem)
            element = element.Parent as Control;

        if (element is not TreeViewItem { DataContext: ViewModels.FolderTreeNode fi })
            return;

        if (pt.Properties.IsRightButtonPressed) return; // handled in Tunnel

        if (pt.Properties.IsLeftButtonPressed) _rightClickedFolder = null;

        // Left-click on a folder that needs relocation — trigger dialog even if already selected
        if (pt.Properties.IsLeftButtonPressed && Vm.NeedsRelocation(fi))
        {
            LstFolders.SelectedItem = fi;
            _ = OpenFolderOrRelocateAsync(fi);
        }
    }

    private ViewModels.FolderTreeNode? _rightClickedFolder;

    private ViewModels.FolderTreeNode? GetContextMenuFolder(object? sender)
    {
        return _rightClickedFolder ?? LstFolders.SelectedItem as ViewModels.FolderTreeNode;
    }

    private FolderTreeNode? CaptureContextMenuFolder(object? sender)
    {
        var folder = GetContextMenuFolder(sender);
        return folder == null ? null : new FolderTreeNode
        {
            Path = folder.Path, DisplayName = folder.DisplayName, Alias = folder.Alias, DbId = folder.DbId
        };
    }

    private void FolderContextMenu_Opened(object? sender, RoutedEventArgs e) => RefreshFolderMenuState();

    private void RefreshFolderMenuState()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        FolderAutoTagMenu.IsEnabled = !vm.IsAutoTagRunning;
        FolderCharacterMenu.IsEnabled = _characterMatchScope == null;
        FolderClearTagsMenu.IsEnabled = _clearTagsScope == null;
        var videoRunning = Volatile.Read(ref _videoOriginalFrameBatchRunning) != 0;
        FolderVideoMenu.IsEnabled = !videoRunning;
        FolderStopVideoMenu.IsVisible = videoRunning;
        FolderStopVideoMenu.IsEnabled = _videoOriginalFrameBatchCts?.IsCancellationRequested == false;
        FolderStopVideoMenu.Header = $"停止视频原始帧生成：{_videoOriginalFrameTargetName}";
        ToolTip.SetTip(FolderStopVideoMenu, _videoOriginalFrameTargetPath);
    }

    private bool IsBlockedByTagClear(FolderOperationScope scope)
    {
        if (_clearTagsScope?.Overlaps(scope) != true) return false;
        Vm.StatusText = "目标范围正在清空标签，请等待完成后再开始。";
        return true;
    }

    private async void MenuRenameFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folder = CaptureContextMenuFolder(sender);
        if (folder == null) return;
        var alias = await Settings.FolderActionDialog.ShowAliasAsync(
            this, folder.DisplayName, folder.Path, folder.Alias);
        if (alias == null) return;
        try
        {
            await Vm.UpdateFolderAliasAsync(folder.Path, string.IsNullOrWhiteSpace(alias) ? null : alias);
        }
        catch (Exception ex)
        {
            Vm.StatusText = $"保存显示名称失败：{ex.Message}";
            AppLogger.Error($"Folder alias failed: {ex}");
        }
    }

    private async void MenuArchiveFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folder = GetContextMenuFolder(sender);
        if (folder == null) return;
        if (!await Settings.FolderActionDialog.ShowConfirmAsync(
            this, "归档文件夹", folder.DisplayName, folder.Path,
            "归档后文件夹不会显示在左侧目录中，数据仍会保留，可在设置中恢复显示。", "归档"))
            return;

        LstFolders.SelectedItem = folder;
        await Vm.ArchiveFolderCommand.ExecuteAsync(null);
    }

    private async void MenuArchivedFolders_Click(object? sender, RoutedEventArgs e)
    {
        var repo = App.Services.GetRequiredService<IFolderRepository>();
        var window = new Settings.ArchivedFoldersWindow
        {
            DataContext = new ArchivedFoldersViewModel(repo)
        };
        await window.ShowDialog(this);
        await Vm.RefreshFolderTreeAsync();
    }

    private async void MenuComputeAutoTags_Click(object? sender, RoutedEventArgs e)
    {
        var folder = CaptureContextMenuFolder(sender);
        if (folder == null || Vm.IsAutoTagRunning) return;
        var recursive = await Settings.FolderActionDialog.ShowScopeAsync(
            this, "自动打标", folder.DisplayName, folder.Path,
            "为所选范围内的图片计算标签，自动跳过已打标图片。", "开始打标", _lastAutoTagRecursive);
        if (recursive == null) return;
        _lastAutoTagRecursive = recursive.Value;
        await RunAutoTagAsync(folder, null, recursive.Value);
    }

    private async void MenuComputeHashes_Click(object? sender, RoutedEventArgs e)
    {
        var folder = CaptureContextMenuFolder(sender);
        if (folder == null) return;
        var failedOnly = await Settings.FolderActionDialog.ShowHashModeAsync(this, folder.DisplayName, folder.Path);
        if (failedOnly == null) return;
        try
        {
            if (failedOnly.Value) await Vm.RecomputeFailedHashesForFolderAsync(folder);
            else await Vm.ComputeHashesIncrementallyForFolderAsync(folder);
        }
        catch (Exception ex)
        {
            Vm.StatusText = $"计算图片指纹失败：{ex.Message}";
            AppLogger.Error($"Folder fingerprints failed: {ex}");
        }
    }

    private async void MenuClearFolderTags_Click(object? sender, RoutedEventArgs e)
    {
        var folder = CaptureContextMenuFolder(sender);
        if (folder == null || _clearTagsScope != null) return;
        var recursive = await Settings.FolderActionDialog.ShowScopeAsync(
            this, "清空标签", folder.DisplayName, folder.Path,
            "将删除所选范围内的全部标签（包括手动和自动标签），并重置打标状态。图片和视频文件不会被删除。此操作无法撤销。",
            "清空标签");
        if (recursive == null) return;
        var scope = FolderOperationScope.ForFolder(folder.Path, recursive.Value);
        if (_clearTagsScope != null || _autoTagScope?.Overlaps(scope) == true || _characterMatchScope?.Overlaps(scope) == true)
        {
            Vm.StatusText = "目标范围正在打标、匹配角色标签或清空标签，请等待完成后重试。";
            return;
        }
        _clearTagsScope = scope;
        RefreshFolderMenuState();
        try
        {
            var files = recursive.Value
                ? await GetImageFilesRecursiveAsync(folder.Path)
                : await GetImageFilesInFolderAsync(folder.Path);
            if (files.Count == 0) { Vm.StatusText = "所选范围内没有图片或视频。"; return; }
            Vm.StatusText = $"正在清空 {files.Count} 个文件的标签…";
            var repository = App.Services.GetRequiredService<IImageMetaRepository>();
            await Task.Run(() => repository.ClearTagsAndStatusBatchAsync(files));
            Vm.InvalidatePageCache();
            await Vm.RefreshTagsAfterExternalWriteAsync(files);
            AppLogger.Info($"ClearFolderTags: folder={folder.Path}, recursive={recursive}, files={files.Count}");
            Vm.StatusText = $"已清空 {files.Count} 个文件的标签。";
        }
        catch (Exception ex)
        {
            Vm.StatusText = $"清空标签失败：{ex.Message}";
            AppLogger.Error($"ClearFolderTags failed: {ex}");
        }
        finally
        {
            _clearTagsScope = null;
            RefreshFolderMenuState();
        }
    }

    private async void MenuMatchCharacterEmbeddingsRecursive_Click(object? sender, RoutedEventArgs e)
    {
        var folder = CaptureContextMenuFolder(sender);
        if (folder == null || _characterMatchScope != null) return;
        if (!await Settings.FolderActionDialog.ShowConfirmAsync(
            this, "匹配角色标签", folder.DisplayName, folder.Path,
            "将处理当前文件夹及所有子文件夹，使用已有图片向量与角色库匹配并添加角色标签。缺少向量的图片会跳过；请先准备图片向量和角色库。",
            "开始匹配")) return;
        var scope = FolderOperationScope.ForFolder(folder.Path, true);
        if (_characterMatchScope != null || IsBlockedByTagClear(scope)) return;
        _characterMatchScope = scope;
        RefreshFolderMenuState();
        try { await RunCharacterEmbeddingMatchAsync(folder.Path); }
        finally
        {
            _characterMatchScope = null;
            RefreshFolderMenuState();
        }
    }

    private void MenuStopVideoOriginalFrames_Click(object? sender, RoutedEventArgs e)
    {
        var cts = _videoOriginalFrameBatchCts;
        if (_videoOriginalFrameBatchRunning == 0 || cts == null) return;
        cts.Cancel();
        Vm.StatusText = $"正在停止视频原始帧生成：{_videoOriginalFrameTargetName}…";
        RefreshFolderMenuState();
    }

    private async void MenuComputeVideoOriginalFramesRecursive_Click(object? sender, RoutedEventArgs e)
    {
        var folder = CaptureContextMenuFolder(sender);
        if (folder == null || _videoOriginalFrameBatchRunning != 0) return;
        if (!await Settings.FolderActionDialog.ShowConfirmAsync(
            this, "生成视频原始帧", folder.DisplayName, folder.Path,
            "将处理当前文件夹及所有子文件夹中的视频，跳过已有原始帧缓存的视频。运行中可在文件夹菜单中停止。", "开始生成")) return;
        if (Interlocked.CompareExchange(ref _videoOriginalFrameBatchRunning, 1, 0) != 0) return;
        var batchCts = new CancellationTokenSource();
        _videoOriginalFrameBatchCts = batchCts;
        _videoOriginalFrameTargetName = folder.DisplayName;
        _videoOriginalFrameTargetPath = folder.Path;
        RefreshFolderMenuState();
        await RunVideoOriginalFrameBatchAsync(folder.Path, batchCts);
    }

    private async Task RunVideoOriginalFrameBatchAsync(string rootPath, CancellationTokenSource batchCts)
    {
        var token = batchCts.Token;

        try
        {
            await App.UI.InvokeAsync(() =>
            {
                Vm.StatusText = "正在扫描视频文件...";
                Vm.BackgroundStatusText = "";
            });

            var videos = await GetVideoFilesRecursiveAsync(rootPath);
            token.ThrowIfCancellationRequested();

            if (videos.Count == 0)
            {
                await App.UI.InvokeAsync(() =>
                {
                    Vm.StatusText = "文件夹及子文件夹无视频";
                    Vm.BackgroundStatusText = "";
                });
                return;
            }

            var originalFrames = App.Services.GetRequiredService<VideoOriginalFrameCacheService>();
            var existing = 0;
            var missingVideos = new List<string>();

            await App.UI.InvokeAsync(() =>
            {
                Vm.StatusText = $"正在检查 {videos.Count} 个视频的原始帧缓存...";
                Vm.BackgroundStatusText = $"视频原始帧检查 0/{videos.Count}";
            });

            for (var i = 0; i < videos.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                if (originalFrames.Exists(videos[i]))
                    existing++;
                else
                    missingVideos.Add(videos[i]);

                if ((i + 1) % 100 == 0 || i + 1 == videos.Count)
                {
                    var checkedCount = i + 1;
                    var missingCount = missingVideos.Count;
                    await App.UI.InvokeAsync(() =>
                    {
                        Vm.BackgroundStatusText =
                            $"视频原始帧检查 {checkedCount}/{videos.Count} | 已有 {existing} / 缺失 {missingCount}";
                    });
                }
            }

            var missing = missingVideos.Count;
            await App.UI.InvokeAsync(() =>
            {
                Vm.StatusText = $"视频原始帧: 总数 {videos.Count}, 已有 {existing}, 缺失 {missing}";
                Vm.BackgroundStatusText = missing == 0
                    ? ""
                    : $"视频原始帧生成 0/{missing} | 已有 {existing} / 新增 0 / 失败 0";
            });

            if (missing == 0)
                return;

            var generated = 0;
            var failed = 0;

            for (var i = 0; i < missingVideos.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var path = missingVideos[i];
                var index = i + 1;

                await App.UI.InvokeAsync(() =>
                {
                    Vm.BackgroundStatusText =
                        $"视频原始帧生成 {index}/{missing} | 已有 {existing} / 新增 {generated} / 失败 {failed}";
                });

                VideoOriginalFrameResult result;
                try
                {
                    result = await originalFrames.EnsureAsync(
                        path,
                        token,
                        cancelExtractionOnCancellation: true);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result = new VideoOriginalFrameResult(false, false, null, ex.Message);
                }

                if (result.Success)
                {
                    if (!result.AlreadyExisted)
                        generated++;
                }
                else
                {
                    failed++;
                    AppLogger.Warn($"VideoOriginalFrames batch failed file={path} error={result.Error}");
                }
            }

            await App.UI.InvokeAsync(() =>
            {
                Vm.StatusText = $"视频原始帧完成: 总数 {videos.Count}, 已有 {existing}, 新增 {generated}, 失败 {failed}";
                Vm.BackgroundStatusText = "";
            });
        }
        catch (OperationCanceledException)
        {
            await App.UI.InvokeAsync(() =>
            {
                Vm.StatusText = "视频原始帧计算已停止";
                Vm.BackgroundStatusText = "";
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error($"VideoOriginalFrames batch error: {ex}");
            await App.UI.InvokeAsync(() =>
            {
                Vm.StatusText = $"视频原始帧计算失败: {ex.Message}";
                Vm.BackgroundStatusText = "";
            });
        }
        finally
        {
            await App.UI.InvokeAsync(() =>
            {
                _videoOriginalFrameBatchRunning = 0;
                _videoOriginalFrameBatchCts = null;
                _videoOriginalFrameTargetName = null;
                _videoOriginalFrameTargetPath = null;
                batchCts.Dispose();
                RefreshFolderMenuState();
            });
        }
    }

    private async Task RunCharacterEmbeddingMatchAsync(string rootPath)
    {
        const string modelKey = "pixai";
        const string modelVersion = "v0.9";
        const string source = "CharacterEmbedding";

        Vm.StatusText = "正在匹配人物 Embedding...";

        try
        {
            var imageFiles = (await GetImageFilesRecursiveAsync(rootPath))
                .Where(FileTypeConstants.IsImageFile)
                .ToList();
            if (imageFiles.Count == 0)
            {
                Vm.StatusText = "文件夹及子文件夹无图片";
                return;
            }

            var embeddingRepo = App.Services.GetRequiredService<IImageEmbeddingRepository>();
            var characterStore = App.Services.GetRequiredService<CharacterEmbeddingStore>();
            if (characterStore.Count == 0)
            {
                Vm.StatusText = "角色 Embedding 库为空，请先构建角色库";
                return;
            }

            var settings = Vm.AppSettings;
            var embeddings = await embeddingRepo.GetByFolderPrefixAsync(rootPath, modelKey, modelVersion);
            var imageSet = new HashSet<string>(imageFiles, StringComparer.OrdinalIgnoreCase);
            embeddings = embeddings
                .Where(e => imageSet.Contains(e.FilePath))
                .ToList();

            var missing = imageFiles.Count - embeddings.Count;
            var matched = 0;
            var written = 0;
            var pending = new List<(long ImageMetaId, string TagName)>(100);
            var pendingPaths = new List<string>(100);

            for (var i = 0; i < embeddings.Count; i++)
            {
                var item = embeddings[i];
                var matches = characterStore.SearchTop(
                    item.Embedding,
                    settings.CharacterMatchThreshold,
                    Math.Clamp(settings.CharacterMaxMatchesPerImage, 1, 5));

                foreach (var match in matches)
                {
                    pending.Add((item.ImageMetaId, match.CharacterName));
                    pendingPaths.Add(item.FilePath);
                }

                if (matches.Count > 0)
                    matched++;

                if (pending.Count >= 100)
                {
                    written += await embeddingRepo.AddCharacterEmbeddingTagsAsync(pending, source);
                    await Vm.RefreshTagsAfterExternalWriteAsync(pendingPaths, refreshCounts: false);
                    pending.Clear();
                    pendingPaths.Clear();
                }

                if ((i + 1) % 200 == 0 || i + 1 == embeddings.Count)
                {
                    Vm.BackgroundStatusText =
                        $"人物 Embedding 匹配 {i + 1}/{embeddings.Count} | 命中 {matched} / 缺失 {missing}";
                }
            }

            if (pending.Count > 0)
            {
                written += await embeddingRepo.AddCharacterEmbeddingTagsAsync(pending, source);
                await Vm.RefreshTagsAfterExternalWriteAsync(pendingPaths, refreshCounts: false);
            }

            Vm.BackgroundStatusText = "";
            Vm.StatusText =
                $"人物 Embedding 匹配完成: 图片 {imageFiles.Count}, 有向量 {embeddings.Count}, 缺失 {missing}, 命中 {matched}, 写入 {written}";
            await Vm.RefreshTagsAfterExternalWriteAsync(Array.Empty<string>());
        }
        catch (Exception ex)
        {
            Vm.BackgroundStatusText = "";
            Vm.StatusText = $"人物 Embedding 匹配失败: {ex.Message}";
            AppLogger.Error($"Character embedding match failed: {ex}");
        }
    }

    private static async Task<List<string>> GetImageFilesInFolderAsync(string path)
    {
        try
        {
            return await Task.Run(() =>
                Directory.EnumerateFiles(path)
                    .Where(f => FileTypeConstants.IsMediaFile(f))
                    .ToList());
        }
        catch { return new List<string>(); }
    }

    private static async Task<List<string>> GetImageFilesRecursiveAsync(string root)
    {
        try
        {
            return await Task.Run(() =>
            {
                var files = new List<string>();
                var dirs = new Queue<string>();
                dirs.Enqueue(root);
                while (dirs.Count > 0)
                {
                    var dir = dirs.Dequeue();
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(dir))
                            if (FileTypeConstants.IsMediaFile(f))
                                files.Add(f);
                        foreach (var sub in Directory.EnumerateDirectories(dir))
                            dirs.Enqueue(sub);
                    }
                    catch { }
                }
                return files;
            });
        }
        catch { return new List<string>(); }
    }

    private static async Task<List<string>> GetVideoFilesRecursiveAsync(string root)
    {
        try
        {
            return await Task.Run(() =>
            {
                var files = new List<string>();
                var dirs = new Queue<string>();
                dirs.Enqueue(root);
                while (dirs.Count > 0)
                {
                    var dir = dirs.Dequeue();
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(dir))
                            if (FileTypeConstants.IsVideoFile(f))
                                files.Add(f);
                        foreach (var sub in Directory.EnumerateDirectories(dir))
                            dirs.Enqueue(sub);
                    }
                    catch { }
                }
                return files;
            });
        }
        catch { return new List<string>(); }
    }

    private async Task RunAutoTagAsync(ViewModels.FolderTreeNode? folder, List<string>? filePaths, bool recursive)
    {
        if (Vm.IsAutoTagRunning)
        {
            Vm.StatusText = "自动打标正在进行中";
            return;
        }

        var scope = folder != null
            ? FolderOperationScope.ForFolder(folder.Path, recursive)
            : FolderOperationScope.ForFiles(filePaths ?? []);
        if (IsBlockedByTagClear(scope)) return;

        var controller = App.Services.GetRequiredService<ImageManager.Infrastructure.Services.AutoTagOrchestrator>();

        var settings = Vm.AppSettings;
        controller.Configure(
            (Core.Services.TagMode)settings.TagMode,
            settings.SingleModelMinConfidence, 75,
            settings.EnsemblePixaiMinConfidence,
            settings.ArtistMatchThreshold,
            settings.EnableCharacterRecognition,
            settings.CharacterMatchThreshold,
            settings.CharacterMaxMatchesPerImage,
            settings.DeepSeekApiKey);

        var messenger = App.Services.GetRequiredService<IMessenger>();
        // 用唯一 token 替代 this，避免旧 Task.Run 的 finally 误删新 handler
        var msgToken = new object();
        var terminalMessage = 0;
        messenger.Register<AutoTagProgressMessage>(msgToken, (r, m) =>
        {
            if (m.Phase is "Done" or "Stopped" or "Error")
                Interlocked.Exchange(ref terminalMessage, 1);
            App.UI.Post(() =>
            {
                Vm.StatusText = m.StatusText;
            });
        });

        var runVersion = Interlocked.Increment(ref _autoTagRunVersion);
        Vm.IsAutoTagRunning = true;
        _autoTagScope = scope;
        RefreshFolderMenuState();
        Vm.StatusText = "正在准备自动打标...";
        try
        {
            if (folder != null)
            {
                await controller.RunFolderAsync(folder.DbId, folder.Path, recursive);
            }
            else
            {
                await controller.RunSelectedImagesAsync(filePaths ?? new List<string>());
            }

            var processed = controller.LastProcessedPaths?.ToList() ?? new List<string>();
            await App.UI.InvokeAsync(async () =>
            {
                if (processed.Count > 0)
                {
                    await Vm.RefreshTagsAfterExternalWriteAsync(processed);
                    AppLogger.Info($"AutoTag tag counts refreshed processed={processed.Count}");
                }

                if (Volatile.Read(ref terminalMessage) == 0)
                {
                    var total = (filePaths?.Count ?? 0);
                    Vm.StatusText = controller.LastRunCancelled
                        ? "自动打标已停止"
                        : processed.Count > 0
                            ? $"打标完成 ({processed.Count}/{(total > 0 ? total : processed.Count)} 张)"
                            : controller.LastPreparationFailed > 0
                                ? $"打标完成（准备失败 {controller.LastPreparationFailed} 张，跳过 {controller.LastSkippedCount} 张）"
                                : $"打标完成（跳过 {controller.LastSkippedCount} 张）";
                }
            });
        }
        catch (Exception ex)
        {
            await App.UI.InvokeAsync(() =>
            {
                Vm.StatusText = $"打标失败: {ex.Message}";
                AppLogger.Error($"AutoTag UI failed: {ex}");
            });
        }
        finally
        {
            try { messenger.Unregister<AutoTagProgressMessage>(msgToken); }
            catch (Exception ex) { AppLogger.Warn($"AutoTag unregister failed: {ex.Message}"); }

            try
            {
                await App.UI.InvokeAsync(() =>
                {
                    if (runVersion == Volatile.Read(ref _autoTagRunVersion))
                    {
                        Vm.IsAutoTagRunning = false;
                        _autoTagScope = null;
                        RefreshFolderMenuState();
                    }
                });
            }
            catch (Exception ex) { AppLogger.Warn($"AutoTag reset IsRunning failed: {ex.Message}"); }
        }
    }

    private async Task ShowInfoDialogAsync(string message)
    {
        var dialog = new Window
        {
            Title = "诊断信息",
            Width = 500, Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel { Margin = new Avalonia.Thickness(16) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var btn = new Button { Content = "确定", Width = 80, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Margin = new Avalonia.Thickness(0, 12, 0, 0) };
        btn.Click += (_, _) => dialog.Close();
        panel.Children.Add(btn);
        dialog.Content = panel;
        await dialog.ShowDialog(this);
    }

    private async Task<bool> ShowConfirmDialogAsync(string message)
    {
        var tcs = new TaskCompletionSource<bool>();
        var dialog = new Window
        {
            Title = "确认",
            Width = 400, Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var panel = new StackPanel { Margin = new Avalonia.Thickness(16) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var btnPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Margin = new Avalonia.Thickness(0, 12, 0, 0) };
        var yesBtn = new Button { Content = "是", Width = 80, Margin = new Avalonia.Thickness(4) };
        var noBtn = new Button { Content = "否", Width = 80, Margin = new Avalonia.Thickness(4) };
        yesBtn.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        noBtn.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        btnPanel.Children.Add(yesBtn);
        btnPanel.Children.Add(noBtn);
        panel.Children.Add(btnPanel);
        dialog.Content = panel;

        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    // ==================== Tag Settings ====================

    private void MenuTagSettings_Click(object? sender, RoutedEventArgs e)
    {
        // Simple: increment suggestion count or reset
        int current = Vm.AppSettings.MaxTagSuggestionCount;
        if (current <= 0) current = 30;
        int newVal = current >= 100 ? 10 : current + 10;
        Vm.AppSettings.MaxTagSuggestionCount = newVal;
        _ = Vm.SaveSettingsAsync();
    }

    private async void MenuAppearanceSettings_Click(object? sender, RoutedEventArgs e)
    {
        var vm = new AppearanceSettingViewModel(
            Vm.AppSettings.ThemeVariant,
            Vm.AppSettings.SearchToolbarLayout,
            Vm.DisplayMode,
            (theme, layout, displayMode) =>
            {
                Vm.AppSettings.ThemeVariant = theme;
                Vm.SetSearchToolbarLayout(layout);
                App.ApplyColors(!string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase));
                _ = ApplyAppearanceDisplayModeAsync(displayMode);
            });

        var win = new Settings.AppearanceSettingWindow { DataContext = vm };
        await win.ShowDialog(this);
    }

    private async Task ApplyAppearanceDisplayModeAsync(ImageDisplayMode displayMode)
    {
        await Vm.TrySetDisplayModeAsync(displayMode);
        Vm.AppSettings.ImageDisplayMode = Vm.DisplayMode.ToString();
        await Vm.SaveSettingsAsync();
    }

    private async void MenuSearchSettings_Click(object? sender, RoutedEventArgs e)
    {
        var viewModel = new SearchSettingViewModel(
            Vm.AppSettings.PerceptualSearchResultMode,
            Vm.AppSettings.SimilaritySearchResultLimit,
            async (mode, resultLimit) =>
            {
                Vm.AppSettings.PerceptualSearchResultMode = mode;
                Vm.AppSettings.SimilaritySearchResultLimit = resultLimit;
                await Vm.SaveSettingsAsync();
            });
        var window = new Settings.SearchSettingWindow { DataContext = viewModel };
        await window.ShowDialog(this);
    }

    private void OnScrollRestore()
    {
        ThumbnailScrollViewer.Offset = new Vector(0, Vm.PreSearchScrollOffset);
    }

    private void OnScrollSearchResultsToTop()
    {
        ThumbnailScrollViewer.Offset = new Vector(0, 0);
    }

    private async void OnScrollToSelected()
    {
        if (Vm.DisplayMode == ImageDisplayMode.Continuous)
        {
            var selectedPath = Vm.SelectedFilePaths.FirstOrDefault();
            var index = selectedPath is null ? -1 : Vm.ActiveFileList.FindIndex(path =>
                string.Equals(path, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && _continuousWaterfallPanel?.ScrollToIndex(index) == true)
                return;

            System.Diagnostics.Debug.WriteLine("[ImageScroll] FAILED continuous target unavailable");
            return;
        }

        // SmartWaterfallPanel 对视口外 child 跳过 Arrange，Bounds 为空，BringIntoView 失效。
        // 改为先从 Panel 查询目标行 Y、直接驱动 ScrollViewer.Offset，再用 BringIntoView 微调。
        // 跨文件夹首次跳转时 Images 集合刚被替换为新 ObservableCollection，ItemsControl 需要
        // 至少一次 layout pass 才能生成 child 容器。重试覆盖 ~2 秒，含缩略图异步解码窗口。
        const int maxAttempts = 40;
        const int delayMs = 50;

        string? failReason = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var scrolled = await App.UI.InvokeAsync(() =>
            {
                var selected = Vm.GetSelectedRealizedItems().FirstOrDefault();
                if (selected == null) { failReason = "no-selected"; return true; }

                // 强制 ItemsControl 与 Panel 完整跑一次 Measure
                ItemsImages.InvalidateMeasure();
                if (ItemsImages.ItemsPanelRoot is SmartWaterfallPanel p0)
                    p0.InvalidateMeasure();
                ItemsImages.UpdateLayout();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

                if (ItemsImages.ContainerFromItem(selected) is not Control container)
                { failReason = "no-container"; return false; }
                if (ItemsImages.ItemsPanelRoot is not SmartWaterfallPanel panel)
                { failReason = "no-panel"; return false; }
                if (!panel.TryGetItemY(container, out var y, out var h))
                { failReason = "no-layout"; return false; }

                var viewportH = ThumbnailScrollViewer.Viewport.Height;
                var extentH = ThumbnailScrollViewer.Extent.Height;
                if (viewportH <= 0) { failReason = "no-viewport"; return false; }

                var maxOffset = Math.Max(0, extentH - viewportH);
                var target = Math.Max(0, Math.Min(maxOffset, y - (viewportH - h) / 2));
                ThumbnailScrollViewer.Offset = new Vector(0, target);
                System.Diagnostics.Debug.WriteLine(
                    $"[ImageScroll] OK attempt={attempt} y={y:F1} h={h:F1} target={target:F1} ext={extentH:F1} vp={viewportH:F1}");
                return true;
            });

            if (scrolled)
            {
                await Task.Delay(delayMs);
                await App.UI.InvokeAsync(() =>
                {
                    var selected = Vm.GetSelectedRealizedItems().FirstOrDefault();
                    if (selected != null && ItemsImages.ContainerFromItem(selected) is Control c)
                        c.BringIntoView();
                });
                return;
            }

            await Task.Delay(delayMs);
        }

        System.Diagnostics.Debug.WriteLine($"[ImageScroll] FAILED reason={failReason ?? "unknown"}");
    }

    private async void OnTreeScrollToNode(ViewModels.FolderTreeNode node)
    {
        // Retry with increasing delays. After ExpandAndHighlightFolderAsync expands
        // the path, TreeViewItem containers take time to materialize (especially for
        // large trees where virtualization may defer container creation).
        const int maxAttempts = 10;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var scrolled = await App.UI.InvokeAsync(() =>
            {
                LstFolders.UpdateLayout();
                var container = LstFolders.TreeContainerFromItem(node);
                if (container == null) return false;
                container.BringIntoView();
                return true;
            });
            if (scrolled) return;
            await Task.Delay(Math.Min(100 * (attempt + 1), 400));
        }
        AppLogger.Warn($"[TreeScroll] 滚动失败: {node.Path}");
    }

    // ==================== Window Closing ====================

    private void Window_Closing(object? sender, Avalonia.Controls.WindowClosingEventArgs e)
    {
        _scrollActivityCts?.Cancel();
        _scrollActivityCts?.Dispose();
        _scrollActivityCts = null;
        _continuousModeCts?.Cancel();
        _continuousModeCts?.Dispose();
        _continuousModeCts = null;
        _continuousReflowCts?.Cancel();
        _continuousReflowCts = null;
        _continuousResizeTimer.Stop();
        SizeChanged -= OnWindowSizeChanged;
        App.Services.GetRequiredService<PageManager>().SetScrollActivity(false);
        Vm.PropertyChanged -= OnViewModelPropertyChanged;
        Vm.ContinuousDisplayRefreshRequested -= ApplyDisplayModeAsync;
        ContinuousItemsImages.LayoutUpdated -= OnContinuousItemsImagesLayoutUpdated;
        if (_continuousWaterfallPanel != null)
        {
            _continuousWaterfallPanel.VisibleRangeChanged -= OnContinuousVisibleRangeChanged;
            _continuousWaterfallPanel.RetainedRangeChanged -= OnContinuousRetainedRangeChanged;
            _continuousWaterfallPanel = null;
            _continuousVisibleRange = default;
        }
        CloseHoverPreview();
        ReleaseHoverPreviewPopup();
        Vm.ScrollRestoreRequested -= OnScrollRestore;
        Vm.ScrollToSelectedRequested -= OnScrollToSelected;
        Vm.ScrollSearchResultsToTopRequested -= OnScrollSearchResultsToTop;
        Vm.TreeScrollToNodeRequested -= OnTreeScrollToNode;
        Vm.ShutdownBackgroundWork();
        App.ReleaseInferenceSessions();
        _ = Vm.SaveSettingsAsync();
    }
    private async void MenuThumbnailSettings_Click(object? sender, RoutedEventArgs e)
    {
        var vm = new ThumbnailSettingViewModel(Vm.AppSettings, () =>
        {
            _ = Vm.SaveSettingsAsync();
            Vm.SyncUISettingsFromAppData();
            var cache = App.Services.GetRequiredService<Infrastructure.Caching.ThumbnailCacheService>();
            _ = cache.ClearAsync();
            _ = Vm.ShowPageAsync(Vm.CurrentPage);
        });

        var win = new Settings.ThumbnailSettingWindow { DataContext = vm };
        await win.ShowDialog(this);
    }

    private async void MenuMemorySettings_Click(object? sender, RoutedEventArgs e)
    {
        MemorySettingViewModel? memVm = null;
        memVm = new MemorySettingViewModel(
            Vm.AppSettings.DiskCacheDirectory,
            Vm.AppSettings.DeepSeekApiKey,
            Vm.AppSettings.TagMode,
            Vm.AppSettings.EnsembleMaxTagsPerImage,
            Vm.AppSettings.EnsemblePixaiMinConfidence,
            Vm.AppSettings.ArtistMatchThreshold,
            Vm.AppSettings.EnableCharacterRecognition,
            Vm.AppSettings.CharacterMatchThreshold,
            Vm.AppSettings.CharacterMaxMatchesPerImage,
            Vm.AppSettings.SingleModelMinConfidence,
            path => new Infrastructure.Caching.DiskThumbnailCache(path).EstimateDiskUsage(),
            pathChanged =>
            {
                if (memVm == null) return;

                var oldPath = Vm.AppSettings.DiskCacheDirectory;
                var newPath = Path.GetFullPath(memVm.CachePath);
                if (!StartupCacheConfig.TryValidateWritableDirectory(newPath, out var validationError))
                {
                    _ = ShowInfoDialogAsync(validationError);
                    return;
                }

                Vm.AppSettings.DiskCacheDirectory = newPath;
                Vm.AppSettings.DeepSeekApiKey = memVm.DeepSeekApiKey;
                Vm.AppSettings.TagMode = memVm.TagMode;
                Vm.AppSettings.EnsembleMaxTagsPerImage = memVm.EnsembleMaxTags;
                Vm.AppSettings.EnsemblePixaiMinConfidence = memVm.PixaiMinConfidence;
                Vm.AppSettings.ArtistMatchThreshold = memVm.ArtistMatchThreshold;
                Vm.AppSettings.EnableCharacterRecognition = memVm.EnableCharacterRecognition;
                Vm.AppSettings.CharacterMatchThreshold = memVm.CharacterMatchThreshold;
                Vm.AppSettings.CharacterMaxMatchesPerImage = Math.Clamp(memVm.CharacterMaxMatches, 1, 5);
                Vm.AppSettings.SingleModelMinConfidence = memVm.SingleModelMinConfidence;
                var cache = App.Services.GetRequiredService<Infrastructure.Caching.ThumbnailCacheService>();
                cache.SwitchCacheDirectory(newPath);
                OnlineSearchHelper.SetTempDir(Path.Combine(newPath, "search_temp"));
                _ = Vm.SaveSettingsAsync();

                // Update boot config with previous path for startup DB migration
                var startupConfig = StartupCacheConfig.Load();
                startupConfig.PreviousCacheDirectory = oldPath;
                startupConfig.CacheDirectory = newPath;
                startupConfig.CachePromptShown = true;
                startupConfig.Save();

                if (pathChanged)
                {
                    _ = ShowInfoDialogAsync(
                        "缓存位置已更新。缩略图和临时文件会立即使用新目录，数据库会在下次启动时迁移到新目录。");
                }
            });

        var win = new Settings.MemorySettingWindow { DataContext = memVm };
        await win.ShowDialog(this);
    }

    private async void MenuShortcutSettings_Click(object? sender, RoutedEventArgs e)
    {
        var vm = new ShortcutSettingViewModel(
            Vm.AppSettings.ShortcutBindings ?? new Dictionary<string, string>(),
            async bindings =>
            {
                Vm.AppSettings.ShortcutBindings = bindings;
                await Vm.SaveSettingsAsync();
            });

        var win = new Settings.ShortcutSettingWindow { DataContext = vm };
        await win.ShowDialog(this);
    }

    private async void MenuArtistDbBuilder_Click(object? sender, RoutedEventArgs e)
    {
        var controller = App.Services.GetRequiredService<ImageManager.Infrastructure.Services.AutoTagOrchestrator>();

        if (!controller.IsModelLoaded)
        {
            Vm.StatusText = "正在加载打标模型...";
            try { await controller.LoadModelAsync(); }
            catch (Exception ex) { Vm.StatusText = $"模型加载失败: {ex.Message}"; return; }
        }

        var vm = new ViewModels.ArtistDbBuilderViewModel();

        vm.OnSelectFolder = async _ =>
        {
            var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择画师参考图根目录（子文件夹名为画师名）",
                AllowMultiple = false
            });
            if (result.Count > 0)
                vm.ReferenceDir = result[0].Path.LocalPath;
        };

        vm.OnBuildAsync = dir =>
        {
            return Task.Run(async () =>
            {
                var pixai = App.Services.GetRequiredService<ImageManager.Infrastructure.Services.PixaiTagService>();
                var store = App.Services.GetRequiredService<ImageManager.Infrastructure.Services.ArtistEmbeddingStore>();

                // Phase 1: 扫描所有画师文件夹，比对图片数量
                var artistDirs = Directory.GetDirectories(dir);
                if (artistDirs.Length == 0)
                {
                    await App.UI.InvokeAsync(() => vm.StatusText = "未找到子文件夹");
                    return;
                }

                var toBuild = new List<(string name, string dir, int imgCount)>();
                int skipped = 0;
                foreach (var artistDir in artistDirs)
                {
                    var artistName = Path.GetFileName(artistDir);
                    var images = Directory.GetFiles(artistDir)
                        .Where(f => FileTypeConstants.IsMediaFile(f))
                        .ToList();
                    int currentCount = images.Count;
                    int storedCount = store.GetImageCount(artistName);

                    if (storedCount == currentCount && storedCount > 0)
                    {
                        skipped++;
                        continue;
                    }
                    toBuild.Add((artistName, artistDir, currentCount));
                }

                if (toBuild.Count == 0)
                {
                    await App.UI.InvokeAsync(() =>
                        vm.StatusText = $"全部 {skipped} 位画师无需更新");
                    return;
                }

                // Phase 2: 批量推理需要重建的画师
                int built = 0;
                foreach (var (artistName, artistDir, imgCount) in toBuild)
                {
                    built++;
                    var label = store.GetImageCount(artistName) > 0 ? $"重建 {artistName}" : $"新增 {artistName}";
                    var hint = $"跳过{skipped} / 处理{built}/{toBuild.Count}: {label}";
                    await App.UI.InvokeAsync(() =>
                    {
                        vm.ReportProgress(built, toBuild.Count, $"{hint} ({imgCount}张)");
                    });

                    var images = Directory.GetFiles(artistDir)
                        .Where(f => FileTypeConstants.IsMediaFile(f))
                        .ToList();

                    if (images.Count == 0) continue;

                    float[]? sumEmb = null;
                    int valid = 0;
                    var embeddings = await ExtractEmbeddingsForLibraryAsync(pixai, images, artistName);
                    foreach (var emb in embeddings)
                    {
                        if (sumEmb == null) sumEmb = new float[emb.Length];
                        for (int j = 0; j < emb.Length; j++) sumEmb[j] += emb[j];
                        valid++;
                    }

                    if (sumEmb != null && valid > 0)
                    {
                        for (int j = 0; j < sumEmb.Length; j++) sumEmb[j] /= valid;
                        controller.RegisterArtistWithEmbeddingAsync(artistName, sumEmb, valid);
                    }
                }

                await App.UI.InvokeAsync(() =>
                    vm.ReportProgress(toBuild.Count, toBuild.Count,
                        $"完成！跳过 {skipped} / 重建+新增 {toBuild.Count}，共 {controller.GetArtistStoreCount()} 位画师"));
            });
        };

        var win = new Settings.ArtistDbBuilderWindow { DataContext = vm };
        await win.ShowDialog(this);
    }

    private async void MenuCharacterDbBuilder_Click(object? sender, RoutedEventArgs e)
    {
        var controller = App.Services.GetRequiredService<ImageManager.Infrastructure.Services.AutoTagOrchestrator>();

        if (!controller.IsModelLoaded)
        {
            Vm.StatusText = "正在加载打标模型...";
            try { await controller.LoadModelAsync(); }
            catch (Exception ex) { Vm.StatusText = $"模型加载失败: {ex.Message}"; return; }
        }

        var vm = new ViewModels.ArtistDbBuilderViewModel
        {
            StatusText = "就绪",
            ReferenceDescription = "每个子文件夹 = 一个角色，文件夹名 = 角色名，文件夹内为该角色的参考图",
            LibraryTitle = "当前角色库",
            MeanEmbeddingHint = "每个角色取所有参考图的嵌入均值",
            RecommendedCountHint = "建议每个角色 20-50 张代表图",
            IncrementalHint = "新角色可随时添加，自动与已有库合并"
        };

        vm.OnSelectFolder = async _ =>
        {
            var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择角色参考图根目录（子文件夹名为角色名）",
                AllowMultiple = false
            });
            if (result.Count > 0)
                vm.ReferenceDir = result[0].Path.LocalPath;
        };

        vm.OnBuildAsync = dir =>
        {
            return Task.Run(async () =>
            {
                var pixai = App.Services.GetRequiredService<ImageManager.Infrastructure.Services.PixaiTagService>();
                var store = App.Services.GetRequiredService<ImageManager.Infrastructure.Services.CharacterEmbeddingStore>();

                var characterDirs = Directory.GetDirectories(dir);
                if (characterDirs.Length == 0)
                {
                    await App.UI.InvokeAsync(() => vm.StatusText = "未找到子文件夹");
                    return;
                }

                var toBuild = new List<(string name, string dir, int imgCount)>();
                int skipped = 0;
                foreach (var characterDir in characterDirs)
                {
                    var characterName = Path.GetFileName(characterDir);
                    var images = Directory.GetFiles(characterDir)
                        .Where(f => FileTypeConstants.IsMediaFile(f))
                        .ToList();
                    int currentCount = images.Count;
                    int storedCount = store.GetImageCount(characterName);

                    if (storedCount == currentCount && storedCount > 0)
                    {
                        skipped++;
                        continue;
                    }
                    toBuild.Add((characterName, characterDir, currentCount));
                }

                if (toBuild.Count == 0)
                {
                    await App.UI.InvokeAsync(() =>
                        vm.StatusText = $"全部 {skipped} 个角色无需更新");
                    return;
                }

                int built = 0;
                foreach (var (characterName, characterDir, imgCount) in toBuild)
                {
                    built++;
                    var label = store.GetImageCount(characterName) > 0 ? $"重建 {characterName}" : $"新增 {characterName}";
                    var hint = $"跳过{skipped} / 处理{built}/{toBuild.Count}: {label}";
                    await App.UI.InvokeAsync(() =>
                    {
                        vm.ReportProgress(built, toBuild.Count, $"{hint} ({imgCount}张)");
                    });

                    var images = Directory.GetFiles(characterDir)
                        .Where(f => FileTypeConstants.IsMediaFile(f))
                        .ToList();

                    if (images.Count == 0) continue;

                    float[]? sumEmb = null;
                    int valid = 0;
                    var embeddings = await ExtractEmbeddingsForLibraryAsync(pixai, images, characterName);
                    foreach (var emb in embeddings)
                    {
                        if (sumEmb == null) sumEmb = new float[emb.Length];
                        for (int j = 0; j < emb.Length; j++) sumEmb[j] += emb[j];
                        valid++;
                    }

                    if (sumEmb != null && valid > 0)
                    {
                        for (int j = 0; j < sumEmb.Length; j++) sumEmb[j] /= valid;
                        controller.RegisterCharacterWithEmbedding(characterName, sumEmb, valid);
                    }
                }

                await App.UI.InvokeAsync(() =>
                    vm.ReportProgress(toBuild.Count, toBuild.Count,
                        $"完成！跳过 {skipped} / 重建+新增 {toBuild.Count}，共 {controller.GetCharacterStoreCount()} 个角色"));
            });
        };

        var win = new Settings.ArtistDbBuilderWindow
        {
            DataContext = vm,
            Title = "角色嵌入库构建工具"
        };
        await win.ShowDialog(this);
    }

    private static async Task<List<float[]>> ExtractEmbeddingsForLibraryAsync(
        PixaiTagService pixai,
        List<string> images,
        string libraryItemName)
    {
        var result = new List<float[]>();
        int batchSize = 2;
        int index = 0;

        while (index < images.Count)
        {
            var actualBatchSize = Math.Min(batchSize, images.Count - index);
            var batch = images.Skip(index).Take(actualBatchSize).ToList();

            try
            {
                var embs = await pixai.GetEmbeddingsBatchAsync(batch);
                if (embs != null)
                    result.AddRange(embs);

                index += actualBatchSize;
            }
            catch (Exception ex) when (IsOnnxMemoryException(ex))
            {
                AppLogger.Warn(
                    $"Embedding library batch memory pressure item={libraryItemName} batch={actualBatchSize}: {ex.Message}");
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                if (actualBatchSize > 1)
                {
                    batchSize = Math.Max(1, actualBatchSize / 2);
                    continue;
                }

                try
                {
                    var emb = await pixai.GetEmbeddingAsync(batch[0]);
                    if (emb != null)
                        result.Add(emb);
                }
                catch (Exception singleEx)
                {
                    AppLogger.Warn(
                        $"Embedding library skipped image item={libraryItemName} file={Path.GetFileName(batch[0])}: {singleEx.Message}");
                }

                index++;
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    $"Embedding library batch failed item={libraryItemName} batch={actualBatchSize}: {ex.Message}");

                if (actualBatchSize > 1)
                {
                    batchSize = 1;
                    continue;
                }

                index++;
            }
        }

        return result;
    }

    private static bool IsOnnxMemoryException(Exception ex)
    {
        return ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException &&
               (ex.Message.Contains("Available memory", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("AllocateRawInternal", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("BFCArena", StringComparison.OrdinalIgnoreCase));
    }

    private async void MenuTagManage_Click(object? sender, RoutedEventArgs e)
    {
        await Vm.RefreshTagCountsAsync(forceRefresh: true);
        var allTags = Vm.GetAllTagCounts();
        var tagVm = new ViewModels.TagManageViewModel(
            allTags,
            onRename: (oldName, newName) => Vm.RenameTagAsync(oldName, newName),
            onMerge: (oldName, newName) => Vm.MergeTagsAsync(oldName, newName),
            onDelete: async (tagName) => await Vm.DeleteTagFromAllImagesAsync(tagName));

        var win = new Settings.TagManageWindow { DataContext = tagVm };
        await win.ShowDialog(this);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private async void MenuHelp_Click(object? sender, RoutedEventArgs e)
    {
        var win = new Settings.HelpWindow();
        await win.ShowDialog(this);
    }

    // ==================== Sort Menu Handlers ====================

    private async void SortFileNameAsc_Click(object? sender, RoutedEventArgs e) =>
        await Vm.SortImagesAsync(ImageSortOrder.FileNameAsc);
    private async void SortFileNameDesc_Click(object? sender, RoutedEventArgs e) =>
        await Vm.SortImagesAsync(ImageSortOrder.FileNameDesc);
    private async void SortModifiedAsc_Click(object? sender, RoutedEventArgs e) =>
        await Vm.SortImagesAsync(ImageSortOrder.ModifiedAsc);
    private async void SortModifiedDesc_Click(object? sender, RoutedEventArgs e) =>
        await Vm.SortImagesAsync(ImageSortOrder.ModifiedDesc);
    private async void SortFileSizeAsc_Click(object? sender, RoutedEventArgs e) =>
        await Vm.SortImagesAsync(ImageSortOrder.FileSizeAsc);
    private async void SortFileSizeDesc_Click(object? sender, RoutedEventArgs e) =>
        await Vm.SortImagesAsync(ImageSortOrder.FileSizeDesc);
    private async void SortResolutionAsc_Click(object? sender, RoutedEventArgs e) =>
        await Vm.SortImagesAsync(ImageSortOrder.ResolutionAsc);
    private async void SortResolutionDesc_Click(object? sender, RoutedEventArgs e) =>
        await Vm.SortImagesAsync(ImageSortOrder.ResolutionDesc);
}
