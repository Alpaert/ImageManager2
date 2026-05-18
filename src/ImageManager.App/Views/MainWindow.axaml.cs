using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using System.Runtime.InteropServices;
using ImageManager.App.Helpers;
using ImageManager.App.ViewModels;
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

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(InputElement.KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        ThumbnailScrollViewer.AddHandler(ScrollViewer.PointerWheelChangedEvent,
            OnThumbnailScrollWheel, RoutingStrategies.Tunnel);
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

        // Set position BEFORE Show so window appears at the right place from frame 0
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        if (pv.SavedLeft >= 0)
            win.Position = new PixelPoint((int)pv.SavedLeft, (int)pv.SavedTop);

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

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath)) continue;
            try
            {
                File.Delete(item.FilePath);
                var repo = App.Services.GetRequiredService<Core.Services.IImageMetaRepository>();
                await repo.DeleteByPathAsync(item.FilePath);
                thumbCache.DeleteFromDiskCache(item.FilePath);
                deletedPaths.Add(item.FilePath);
                successCount++;
            }
            catch { }
        }

        if (deletedPaths.Count > 0)
            Vm.RemoveFilesFromView(deletedPaths);

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

        Vm.ScrollRestoreRequested += () =>
        {
            ThumbnailScrollViewer.Offset = new Vector(0, Vm.PreSearchScrollOffset);
        };

        // Restore startup size
        if (Vm.AppSettings.StartupWidth > 0) Width = Vm.AppSettings.StartupWidth;
        if (Vm.AppSettings.StartupHeight > 0) Height = Vm.AppSettings.StartupHeight;

        ApplyWallpaper();
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
        e.Handled = true;

        _scrollAnimCts?.Cancel();
        _scrollAnimCts = new CancellationTokenSource();
        var ct = _scrollAnimCts.Token;

        var sv = ThumbnailScrollViewer;
        double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        if (maxY <= 0) return;

        double scrollAmount = e.Delta.Y * 100;
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
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is Core.Models.FolderInfo folder)
        {
            await OpenFolderOrRelocateAsync(folder);
        }
    }

    private async Task OpenFolderOrRelocateAsync(Core.Models.FolderInfo folder)
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
                await Vm.RelocateFolderAsync(folder.Id, result[0].Path.LocalPath);
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

            // Get target folder from selected or hovered list item
            string? targetFolder = null;
            if (sender is ListBox lb)
            {
                // Try selected item first, then hit-test
                targetFolder = (lb.SelectedItem as Core.Models.FolderInfo)?.Path;
                if (string.IsNullOrEmpty(targetFolder))
                {
                    var pos = e.GetPosition(lb);
                    var element = lb.InputHitTest(pos) as Avalonia.Visual;
                    while (element != null)
                    {
                        if (element is ListBoxItem lbi && lbi.DataContext is Core.Models.FolderInfo fi)
                        {
                            targetFolder = fi.Path;
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

            if (string.Equals(Path.GetFullPath(targetFolder).TrimEnd('\\', '/'),
                    Path.GetFullPath(Vm.CurrentFolder).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase))
                await Vm.SyncCurrentFolderAsync();

            Vm.StatusText = $"已复制 {filePaths.Count} 个文件到目标文件夹";
        }
        catch { }
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
            // Clear others, select this one
            foreach (var img in Vm.Images)
            {
                if (img.IsSelected) img.IsSelected = false;
            }
            item.IsSelected = true;
            _lastSelectedIndex = Vm.Images.IndexOf(item);
        }
    }

    // ==================== Drag Thumbnail to Explorer / QQ / Browser ====================

    // Store PointerPressedEventArgs for DoDragDropAsync
    private Avalonia.Input.PointerPressedEventArgs? _dragPressArgs;
    private Point? _lastClickScreenPos;
    private ImageViewItem? _lastHoveredItem;
    private int _lastSelectedIndex = -1;

    private async void Thumbnail_PointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (sender is not Avalonia.Controls.Border border) return;
        if (border.DataContext is not ImageViewItem item) return;

        _lastHoveredItem = item;

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
        if (sender is Avalonia.Controls.Border border &&
            border.DataContext is ImageViewItem item &&
            File.Exists(item.FilePath))
        {
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
        var item = GetCtxItem(sender);
        if (item != null)
            await EditTagForItemAsync(item);
    }

    private async Task<bool> EditTagForItemAsync(ImageViewItem item)
    {
        await Vm.RefreshTagCountsAsync();
        var allTags = Vm.GetAllTagCounts();

        var tagVm = new TagEditViewModel(
            string.Join(", ", item.Tags),
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
            await Vm.RefreshTagCountsAsync();
            await Vm.SaveSettingsAsync();
        }

        return result;
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
        var exts = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

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
        while (element != null && element is not ListBoxItem)
            element = element.Parent as Control;

        if (element is not ListBoxItem { DataContext: Core.Models.FolderInfo fi })
            return;

        if (pt.Properties.IsRightButtonPressed)
        {
            LstFolders.SelectedItem = fi;
            return;
        }

        // Left-click on a folder that needs relocation — trigger dialog even if already selected
        if (pt.Properties.IsLeftButtonPressed && Vm.NeedsRelocation(fi))
        {
            LstFolders.SelectedItem = fi;
            _ = OpenFolderOrRelocateAsync(fi);
        }
    }

    private Core.Models.FolderInfo? GetContextMenuFolder(object? sender)
    {
        return LstFolders.SelectedItem as Core.Models.FolderInfo;
    }

    private async void MenuRenameFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folder = GetContextMenuFolder(sender);
        if (folder == null) return;

        var alias = await ShowFolderAliasDialogAsync(
            folder.Alias ?? System.IO.Path.GetFileName(folder.Path.TrimEnd('\\', '/')));
        if (alias == null) return; // cancelled

        await Vm.UpdateFolderAliasAsync(folder.Path, string.IsNullOrWhiteSpace(alias) ? null! : alias);
    }

    private async void MenuClearFolderAlias_Click(object? sender, RoutedEventArgs e)
    {
        var folder = GetContextMenuFolder(sender);
        if (folder == null) return;

        await Vm.UpdateFolderAliasAsync(folder.Path, null);
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

    // ==================== Window Closing ====================

    private void Window_Closing(object? sender, Avalonia.Controls.WindowClosingEventArgs e)
    {
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
            Vm.AppSettings.ThumbnailCacheMaxMB,
            Vm.AppSettings.DiskCacheDirectory,
            () => App.Services.GetRequiredService<Infrastructure.Caching.ThumbnailCacheService>().EstimatedMemoryBytes,
            path => new Infrastructure.Caching.DiskThumbnailCache(path).EstimateDiskUsage(),
            pathChanged =>
            {
                Vm.AppSettings.ThumbnailCacheMaxMB = memVm!.MaxCacheMB;
                Vm.AppSettings.DiskCacheDirectory = memVm.CachePath;
                var cache = App.Services.GetRequiredService<Infrastructure.Caching.ThumbnailCacheService>();
                cache.CacheDirectory = memVm.CachePath;
                OnlineSearchHelper.SetTempDir(Path.Combine(memVm.CachePath, "search_temp"));
                _ = cache.ClearAsync();
                _ = Vm.SaveSettingsAsync();
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
