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
using System.Runtime.InteropServices;
using ImageManager.App.Controls;
using ImageManager.App.Helpers;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;
using ImageManager.Infrastructure.Data;
using ImageManager.Infrastructure.Imaging;
using ImageManager.Infrastructure.Services;
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
    private int _autoTagRunVersion;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(InputElement.KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        ThumbnailScrollViewer.AddHandler(ScrollViewer.PointerWheelChangedEvent,
            OnThumbnailScrollWheel, RoutingStrategies.Tunnel);
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

    private async Task DeleteSelectedFilesAsync(List<ImageViewItem> items)
    {
        var deletedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int successCount = 0;
        var thumbCache = App.Services.GetRequiredService<Infrastructure.Caching.ThumbnailCacheService>();

        Vm.SuppressDeletedEvent();
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath)) continue;
            try
            {
                File.Delete(item.FilePath);
                thumbCache.DeleteFromDiskCache(item.FilePath);
                deletedPaths.Add(item.FilePath);
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

        var selected = Vm.Images.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        try
        {
            var storageFiles = new List<Avalonia.Platform.Storage.IStorageFile>();
            foreach (var item in selected)
            {
                var sf = await topLevel.StorageProvider.TryGetFileFromPathAsync(item.FilePath);
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
        await Vm.InitializeAsync();

        OnlineSearchHelper.SetTempDir(Path.Combine(Vm.AppSettings.DiskCacheDirectory, "search_temp"));
        OnlineSearchHelper.CleanupOldTempFiles();

        Vm.ScrollRestoreRequested += OnScrollRestore;
        Vm.ScrollToSelectedRequested += OnScrollToSelected;
        Vm.TreeScrollToNodeRequested += OnTreeScrollToNode;

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
            var selected = Vm.Images.Where(i => i.IsSelected).ToList();
            if (selected.Count > 0)
                await DeleteSelectedFilesAsync(selected);
            return;
        }

        // Ctrl+A — select all images on current page
        if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            foreach (var img in Vm.Images)
                img.IsSelected = true;
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
            target = Vm.Images.FirstOrDefault(i => i.IsSelected);
        if (target == null)
            return;

        await EditTagForItemAsync(target);
    }

    private async void OnThumbnailScrollWheel(object? sender, PointerWheelEventArgs e)
    {
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

        try
        {
            for (int i = 1; i <= steps; i++)
            {
                if (ct.IsCancellationRequested) return;
                double t = EaseOutQuad((double)i / steps);
                sv.Offset = new Vector(sv.Offset.X, startY + (targetY - startY) * t);
                await Task.Delay(interval, ct);
            }
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

    private async void BtnDetectDuplicates_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择重复图片存放的目标文件夹",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            await Vm.DetectDuplicatesCommand.ExecuteAsync(folders[0].Path.LocalPath);
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

    // ==================== Box Selection ====================

    private bool _isDraggingSelection;
    private Point _selectionStartPoint;

    private void ImagePanelHost_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
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

        // Clear existing selection
        foreach (var img in Vm.Images)
            img.IsSelected = false;

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

        // Hit-test each visible thumbnail
        foreach (var img in Vm.Images)
        {
            var container = ItemsImages.ContainerFromItem(img) as Avalonia.Controls.Control;
            if (container == null) continue;

            try
            {
                var transform = container.TransformToVisual(ImagePanelHost);
                if (transform == null) continue;
                var topLeft = transform.Value.Transform(new Point(0, 0));
                var itemRect = new Rect(topLeft, new Size(container.Bounds.Width, container.Bounds.Height));
                bool intersect = selRect.Intersects(itemRect);
                if (img.IsSelected != intersect)
                    img.IsSelected = intersect;
            }
            catch { }
        }

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
            await EditTagForItemAsync(item);
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        bool ctrl = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift);

        if (ctrl)
        {
            // Toggle selection
            item.IsSelected = !item.IsSelected;
            _lastSelectedIndex = Vm.Images.IndexOf(item);
        }
        else if (shift && _lastSelectedIndex >= 0)
        {
            // Shift range selection from anchor to clicked item
            int clickedIdx = Vm.Images.IndexOf(item);
            if (clickedIdx < 0) return;
            int start = Math.Min(_lastSelectedIndex, clickedIdx);
            int end = Math.Max(_lastSelectedIndex, clickedIdx);
            for (int i = 0; i < Vm.Images.Count; i++)
                Vm.Images[i].IsSelected = i >= start && i <= end;
        }
        else
        {
            bool multiSelected = Vm.Images.Count(i => i.IsSelected) > 1;
            if (multiSelected && item.IsSelected)
            {
                // Defer single-select to pointer release (allow drag to cancel)
                _pendingClick = item;
            }
            else
            {
                foreach (var img in Vm.Images)
                    img.IsSelected = false;
                item.IsSelected = true;
                _lastSelectedIndex = Vm.Images.IndexOf(item);
            }
        }
    }

    private void Thumbnail_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (_pendingClick == null) return;
        if (sender is not Avalonia.Controls.Border border) return;
        if (border.DataContext is not ImageViewItem item) return;
        if (item != _pendingClick) return;

        foreach (var img in Vm.Images)
            img.IsSelected = img == item;
        _lastSelectedIndex = Vm.Images.IndexOf(item);
        _pendingClick = null;
    }

    // ==================== Drag Thumbnail to Explorer / QQ / Browser ====================

    // Store PointerPressedEventArgs for DoDragDropAsync
    private Avalonia.Input.PointerPressedEventArgs? _dragPressArgs;
    private Point? _lastClickScreenPos;
    private ImageViewItem? _lastHoveredItem;
    private ImageViewItem? _pendingClick;
    private int _lastSelectedIndex = -1;

    private async void Thumbnail_PointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (sender is not Avalonia.Controls.Border border) return;
        if (border.DataContext is not ImageViewItem item) return;

        _lastHoveredItem = item;
        _pendingClick = null; // movement cancels deferred single-select

        var point = e.GetCurrentPoint(border);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (_dragPressArgs == null) return;

        // Ensure the dragged item is selected
        var selected = Vm.Images.Where(i => i.IsSelected).ToList();
        if (!selected.Contains(item))
        {
            foreach (var img in Vm.Images) img.IsSelected = false;
            item.IsSelected = true;
            selected = new List<ImageViewItem> { item };
        }

        var filePaths = selected.Select(i => i.FilePath).Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

    /// <summary>If the right-clicked item is in a multi-selection, operate on all selected; otherwise just the clicked one</summary>
    private List<ImageViewItem> GetTargetItemsForContextMenu(ImageViewItem clickedItem)
    {
        var selected = Vm.Images.Where(i => i.IsSelected).ToList();
        return selected.Count > 1 && selected.Contains(clickedItem) ? selected : new List<ImageViewItem> { clickedItem };
    }

    private async void MenuEditTag_Click(object? sender, RoutedEventArgs e)
    {
        var selected = Vm.Images.Where(i => i.IsSelected).ToList();
        if (selected.Count <= 1)
        {
            var item = GetCtxItem(sender);
            if (item != null) await EditTagForItemAsync(item);
        }
        else
        {
            await EditTagsForItemsAsync(selected);
        }
    }

    private async void MenuRegenerateThumbnail_Click(object? sender, RoutedEventArgs e)
    {
        var clicked = GetCtxItem(sender);
        if (clicked == null) return;

        var items = GetTargetItemsForContextMenu(clicked);
        await App.Services.GetRequiredService<PageManager>().RegenerateThumbnailsAsync(items);
        Vm.StatusText = $"已重新生成 {items.Count} 个缩略图";
    }

    private async Task<bool> EditTagForItemAsync(ImageViewItem item)
    {
        var allTags = Vm.GetAllTagCounts();

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
        var allTags = Vm.GetAllTagCounts();

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
        var selected = Vm.Images.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) { Vm.StatusText = "未选中图片"; return; }

        var ok = await ShowConfirmDialogAsync($"确定要清空 {selected.Count} 张选中图片的所有标签？");
        if (!ok) return;
        AppLogger.Warn($"ClearSelectedTags confirmed count={selected.Count}");

        var repo = App.Services.GetRequiredService<Core.Services.IImageMetaRepository>();
        await Task.Run(async () =>
        {
            foreach (var item in selected)
            {
                try
                {
                    var meta = await repo.GetByPathAsync(item.FilePath);
                    if (meta != null)
                    {
                        await repo.SetTagsAsync(meta.Id, new List<string>());
                        await repo.SetAutoTagStatusByPathAsync(item.FilePath, 0);
                    }
                }
                catch { }
            }
        });

        foreach (var item in selected)
        {
            Vm.ClearTagCacheForPath(item.FilePath);
            item.Tags.Clear();
            item.NotifyAll();
        }
        await Vm.RefreshTagCountsAsync(forceRefresh: true);
        AppLogger.Info($"ClearSelectedTags tag counts refreshed count={selected.Count}");
        Vm.StatusText = $"已清空 {selected.Count} 张图片的标签";
    }

    private async void MenuAutoTag_Click(object? sender, RoutedEventArgs e)
    {
        // 获取选中的图片（含右键点击的那张）
        var selected = Vm.Images.Where(i => i.IsSelected).ToList();
        var ctxItem = GetCtxItem(sender);
        if (selected.Count == 0 && ctxItem != null)
            selected.Add(ctxItem);

        if (selected.Count == 0)
        {
            Vm.StatusText = "未找到图片";
            return;
        }

        var controller = App.Services.GetRequiredService<ImageManager.Infrastructure.Services.AutoTagOrchestrator>();
        if (!controller.IsModelLoaded)
        {
            Vm.StatusText = "正在加载打标模型...";
            try { await controller.LoadModelAsync(); }
            catch (Exception ex) { Vm.StatusText = $"模型加载失败: {ex.Message}"; return; }
        }

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

        var filePaths = selected.Select(i => i.FilePath).Distinct().ToList();
        Vm.StatusText = "正在准备...";

        _ = Task.Run(async () =>
        {
            var repo = App.Services.GetRequiredService<Core.Services.IImageMetaRepository>();
            var actualPaths = new List<string>();
            foreach (var p in filePaths)
            {
                var m = await repo.GetByPathAsync(p);
                if (m?.AutoTagStatus == 1) continue;
                actualPaths.Add(p);
            }
            if (actualPaths.Count == 0)
            {
                await App.UI.InvokeAsync(() =>
                    Vm.StatusText = "所选图片均已打标，跳过");
                return;
            }

            await App.UI.InvokeAsync(() =>
                Vm.StatusText = $"正在推理 {actualPaths.Count} 张图片...");

            try
            {
                for (int idx = 0; idx < actualPaths.Count; idx++)
                {
                    var path = actualPaths[idx];
                    await App.UI.InvokeAsync(() =>
                        Vm.StatusText = $"推理中 ({idx + 1}/{actualPaths.Count}): {System.IO.Path.GetFileName(path)}");

                    var items = await controller.RunSingleImageAsync(path);
                    if (items.Count > 0)
                        await controller.SaveMappingsAndTagsAsync(path, items);
                    await repo.SetAutoTagStatusByPathAsync(path, 1);
                }

                await App.UI.InvokeAsync(async () =>
                {
                    foreach (var path in actualPaths)
                        await Vm.RefreshImageTagsAsync(path);
                    await Vm.RefreshTagCountsAsync(forceRefresh: true);
                    AppLogger.Info($"SelectedAutoTag tag counts refreshed count={actualPaths.Count}");
                    Vm.StatusText = $"打标完成 ({actualPaths.Count} 张)";
                });
            }
            catch (Exception ex)
            {
                await App.UI.InvokeAsync(() =>
                    Vm.StatusText = $"打标失败: {ex.Message}");
            }
        });
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

        Vm.StatusText = "正在全文件夹搜索相似图片...";
        Vm.PreSearchScrollOffset = ThumbnailScrollViewer.Offset.Y;
        // Run on background thread to avoid UI freeze
        await Vm.SearchSimilarCommand.ExecuteAsync(baseItem.FilePath);
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
        var items = GetTargetItemsForContextMenu(clicked);
        if (items.Count > 0)
            await DeleteSelectedFilesAsync(items);
    }

    private async void MenuCopyFileToFolder_Click(object? sender, RoutedEventArgs e)
    {
        var clicked = GetCtxItem(sender);
        if (clicked == null) return;
        var items = GetTargetItemsForContextMenu(clicked);

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
            foreach (var item in items)
            {
                if (!File.Exists(item.FilePath)) continue;
                try
                {
                    var destPath = Common.Helpers.PathHelper.GetNonConflictingPath(
                        Path.Combine(targetDir, Path.GetFileName(item.FilePath)));
                    File.Copy(item.FilePath, destPath);
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
        var items = GetTargetItemsForContextMenu(clicked);

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
            Directory.CreateDirectory(targetDir);
            foreach (var item in items)
            {
                if (!File.Exists(item.FilePath)) continue;
                var srcDir = Path.GetDirectoryName(item.FilePath) ?? "";
                if (string.Equals(Path.GetFullPath(srcDir).TrimEnd('\\', '/'),
                        Path.GetFullPath(targetDir).TrimEnd('\\', '/'),
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    var destPath = Common.Helpers.PathHelper.GetNonConflictingPath(
                        Path.Combine(targetDir, Path.GetFileName(item.FilePath)));
                    File.Move(item.FilePath, destPath);
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
            TxtTagSearch.Focus();
            TxtTagSearch.CaretIndex = TxtTagSearch.Text?.Length ?? 0;
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

    private async void MenuRenameFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folder = GetContextMenuFolder(sender);
        if (folder == null) return;

        var alias = await ShowFolderAliasDialogAsync(folder.DisplayName);
        if (alias == null) return; // cancelled

        await Vm.UpdateFolderAliasAsync(folder.Path, string.IsNullOrWhiteSpace(alias) ? null! : alias);
    }

    private async void MenuClearFolderAlias_Click(object? sender, RoutedEventArgs e)
    {
        var folder = GetContextMenuFolder(sender);
        if (folder == null) return;

        await Vm.UpdateFolderAliasAsync(folder.Path, null);
    }

    private async void MenuComputeAutoTags_Click(object? sender, RoutedEventArgs e)
    {
        var folder = GetContextMenuFolder(sender);
        if (folder == null) return;
        var files = await GetImageFilesInFolderAsync(folder.Path);
        if (files.Count == 0) { Vm.StatusText = "文件夹无图片"; return; }
        await RunAutoTagAsync(folder, files);
    }

    private async void MenuRecomputeFailedHashes_Click(object? sender, RoutedEventArgs e)
    {
        var folder = GetContextMenuFolder(sender);
        if (folder == null) return;
        await Vm.RecomputeFailedHashesForFolderAsync(folder);
    }

    private async void MenuClearFolderTags_Click(object? sender, RoutedEventArgs e)
    {
        var folder = GetContextMenuFolder(sender);
        if (folder == null) { Vm.StatusText = "未找到文件夹"; return; }
        AppLogger.Info($"ClearTags: folder={folder.Path}");

        var result = await ShowClearTagsDialogAsync();
        if (result == null) { AppLogger.Info("ClearTags: cancelled"); return; }

        bool recursive = result.Value;
        AppLogger.Info($"ClearTags: recursive={recursive}");
        var files = recursive
            ? await GetImageFilesRecursiveAsync(folder.Path)
            : await GetImageFilesInFolderAsync(folder.Path);
        AppLogger.Info($"ClearTags: found {files.Count} files");
        if (files.Count == 0) { Vm.StatusText = "文件夹无图片"; return; }

        Vm.StatusText = $"正在清空 {files.Count} 张图片的标签...";
        var clearRepo = App.Services.GetRequiredService<Core.Services.IImageMetaRepository>();
        await Task.Run(() =>
            clearRepo.ClearTagsAndStatusBatchAsync(files));
        AppLogger.Info($"ClearTags: done cleared={files.Count}");
        foreach (var path in files)
            Vm.ClearTagCacheForPath(path);
        if (string.Equals(Vm.CurrentFolder, folder.Path, StringComparison.OrdinalIgnoreCase))
        {
            Vm.InvalidatePageCache();
            await Vm.ShowPageAsync(Vm.CurrentPage);
        }
        await Vm.RefreshTagCountsAsync(forceRefresh: true);
        AppLogger.Info($"ClearFolderTags tag counts refreshed count={files.Count} recursive={recursive}");
        Vm.StatusText = $"已清空 {files.Count} 张图片的标签";
    }

    private Task<bool?> ShowClearTagsDialogAsync()
    {
        var tcs = new TaskCompletionSource<bool?>();
        var dialog = new Window
        {
            Title = "清空图片标签",
            Width = 400, Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel { Margin = new Avalonia.Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "选择清空范围：", TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 12)
        });
        var btnPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Spacing = 10
        };
        var currentBtn = new Button { Content = "仅当前文件夹", Width = 140 };
        var recursiveBtn = new Button { Content = "包含所有子文件夹", Width = 140 };
        var cancelBtn = new Button { Content = "取消", Width = 80 };

        currentBtn.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        recursiveBtn.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        cancelBtn.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);

        btnPanel.Children.Add(currentBtn);
        btnPanel.Children.Add(recursiveBtn);
        panel.Children.Add(btnPanel);
        panel.Children.Add(cancelBtn);
        dialog.Content = panel;
        _ = dialog.ShowDialog(this);
        return tcs.Task;
    }

    private async void MenuComputeAutoTagsRecursive_Click(object? sender, RoutedEventArgs e)
    {
        var folder = GetContextMenuFolder(sender);
        if (folder == null) return;
        var files = await GetImageFilesRecursiveAsync(folder.Path);
        if (files.Count == 0) { Vm.StatusText = "文件夹及子文件夹无图片"; return; }
        await RunAutoTagAsync(folder, files);
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

    private async Task RunAutoTagAsync(ViewModels.FolderTreeNode folder, List<string> filePaths)
    {
        if (Vm.IsAutoTagRunning)
        {
            Vm.StatusText = "自动打标正在进行中";
            return;
        }

        var controller = App.Services.GetRequiredService<ImageManager.Infrastructure.Services.AutoTagOrchestrator>();

        if (!controller.IsModelLoaded)
        {
            Vm.StatusText = "正在加载打标模型...";
            try { await controller.LoadModelAsync(); }
            catch (Exception ex)
            {
                var fullDetail = ex.ToString();
                while (ex.InnerException != null) { ex = ex.InnerException; }
                AppLogger.Error($"模型加载失败: {fullDetail}");
                Vm.StatusText = $"模型加载失败: {ex.Message}";
                return;
            }
        }

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
        messenger.Register<AutoTagProgressMessage>(msgToken, (r, m) =>
        {
            App.UI.Post(() =>
                Vm.StatusText = $"[{m.Phase}] {m.StatusText}");
        });

        var runVersion = Interlocked.Increment(ref _autoTagRunVersion);
        Vm.IsAutoTagRunning = true;
        Vm.StatusText = $"正在推理 {filePaths.Count} 张图片...";
        _ = Task.Run(async () =>
        {
            try
            {
                await controller.RunPipelineAsync(folder.DbId, folder.Path, filePaths, "Start");
                var processed = controller.LastProcessedPaths;
                await App.UI.InvokeAsync(async () =>
                {
                    if (!Vm.StatusText.Contains("已停止"))
                        Vm.StatusText = processed.Count > 0
                            ? $"打标完成 ({processed.Count}/{filePaths.Count} 张)"
                            : $"打标完成 ({filePaths.Count} 张，全部已跳过)";
                    foreach (var path in processed)
                        await Vm.RefreshImageTagsAsync(path);
                    if (processed.Count > 0)
                    {
                        await Vm.RefreshTagCountsAsync(forceRefresh: true);
                        AppLogger.Info($"AutoTag tag counts refreshed processed={processed.Count}");
                    }
                });
            }
            catch (Exception ex)
            {
                await App.UI.InvokeAsync(() =>
                    Vm.StatusText = $"打标失败: {ex.Message}");
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
                            Vm.IsAutoTagRunning = false;
                    });
                }
                catch (Exception ex) { AppLogger.Warn($"AutoTag reset IsRunning failed: {ex.Message}"); }
            }
        });
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

    private async Task<string?> ShowFolderAliasDialogAsync(string currentText)
    {
        var tcs = new TaskCompletionSource<string?>();

        var dialog = new Window
        {
            Title = "重命名显示名称",
            Width = 420,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };

        var textBox = new TextBox
        {
            Text = currentText,
            Margin = new Thickness(14, 14, 14, 8)
        };

        var btnPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Thickness(14, 0),
            Spacing = 10
        };
        var cancelBtn = new Button { Content = "取消", Width = 80 };
        var okBtn = new Button { Content = "确定", Width = 80 };

        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(okBtn);

        var panel = new StackPanel();
        panel.Children.Add(textBox);
        panel.Children.Add(btnPanel);

        dialog.Content = panel;

        cancelBtn.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
        okBtn.Click += (_, _) => { tcs.TrySetResult(textBox.Text); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);

        if (this.IsVisible)
            await dialog.ShowDialog(this);
        else
            dialog.Show();

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
            theme =>
            {
                Vm.AppSettings.ThemeVariant = theme;
                App.ApplyColors(!string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase));
                _ = Vm.SaveSettingsAsync();
            });

        var win = new Settings.AppearanceSettingWindow { DataContext = vm };
        await win.ShowDialog(this);
    }

    private void OnScrollRestore()
    {
        ThumbnailScrollViewer.Offset = new Vector(0, Vm.PreSearchScrollOffset);
    }

    private async void OnScrollToSelected()
    {
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
                var selected = Vm.Images.FirstOrDefault(i => i.IsSelected);
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
                    var selected = Vm.Images.FirstOrDefault(i => i.IsSelected);
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
        Vm.ScrollRestoreRequested -= OnScrollRestore;
        Vm.ScrollToSelectedRequested -= OnScrollToSelected;
        Vm.TreeScrollToNodeRequested -= OnTreeScrollToNode;
        Vm.ShutdownBackgroundWork();
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
