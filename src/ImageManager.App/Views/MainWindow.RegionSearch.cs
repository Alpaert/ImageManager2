using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ImageManager.App.Controls;
using ImageManager.App.ViewModels;
using ImageManager.Common.Constants;
using ImageManager.Common.Helpers;

namespace ImageManager.App.Views;

public partial class MainWindow
{
    private MainWindowViewModel? _regionViewModel;
    private bool _regionUiWasActive;
    private bool _regionDetailsWasOpen;
    private int _regionOpenVersion;

    private void InitializeRegionSearch()
    {
        RegionSearchEditor.PickerOpening += (_, _) =>
        {
            AppLogger.Info($"[RegionSearch.PickerPreparing] hoverOpen={HoverPreviewHost.IsOpen}");
            CloseHoverPreview();
            StopWheelAnimation();
            AppLogger.Info("[RegionSearch.PickerPrepared]");
        };
        RegionSearchEditor.InputChanging += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm && vm.IsRegionSearchActive)
                vm.InvalidateRegionSearchInput();
        };
        RegionSearchEditor.InputStateChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.UpdateRegionSearchInputState(RegionSearchEditor.InputPath, RegionSearchEditor.SelectedRegion,
                    RegionSearchEditor.Mode, RegionSearchEditor.IsAwaitingSelection);
        };
        RegionSearchEditor.QueryChanged += RegionQueryChanged;
        DataContextChanged += (_, _) =>
        {
            if (_regionViewModel is not null)
                _regionViewModel.PropertyChanged -= OnRegionViewModelPropertyChanged;
            _regionViewModel = DataContext as MainWindowViewModel;
            if (_regionViewModel is not null)
                _regionViewModel.PropertyChanged += OnRegionViewModelPropertyChanged;
        };
        Closed += (_, _) =>
        {
            if (_regionViewModel is not null)
                _regionViewModel.PropertyChanged -= OnRegionViewModelPropertyChanged;
            RegionSearchEditor.Dispose();
        };
    }

    private async void RegionQueryChanged(object? sender, RegionSearchQueryEventArgs e)
    {
        if (_windowCloseStarted || DataContext is not MainWindowViewModel vm || !vm.IsRegionSearchActive)
            return;
        await vm.QueueRegionSearchAsync(e.Path, e.Region, e.Mode);
    }

    private void OnRegionViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainWindowViewModel.IsRegionSearchActive)
            or nameof(MainWindowViewModel.HasRegionSearchResults))) return;
        UpdateRegionSearchPresentation();
    }

    private void UpdateRegionSearchPresentation()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var active = vm.IsRegionSearchActive;
        if (active != _regionUiWasActive)
        {
            _regionUiWasActive = active;
            CloseHoverPreview();
            StopWheelAnimation();
            if (active)
            {
                _regionDetailsWasOpen = vm.DetailsPane.IsOpen;
                vm.SetRegionDetailsPaneOpen(false);
            }
            else
            {
                RegionSearchEditor.ClearInput();
                vm.SetRegionDetailsPaneOpen(false);
                vm.DetailsPane.IsOpen = _regionDetailsWasOpen;
            }
        }
        WorkspaceSidebar.IsVisible = WorkspaceSearchBar.IsVisible = WorkspaceHeader.IsVisible = !active;
        ThumbnailScrollViewer.IsVisible = !active || vm.HasRegionSearchResults;
        RegionEmptyState.IsVisible = active && !vm.HasRegionSearchResults;
        ApplyWorkspaceGeometry(ClientSize.Width, ClientSize.Height);
    }

    private Task OpenRegionSearchAsync(string? path) =>
        OpenRegionSearchCoreAsync(path, RegionSearchInputBehavior.AwaitSelection);

    private Task OpenRegionImageSearchAsync(string path) =>
        OpenRegionSearchCoreAsync(path, RegionSearchInputBehavior.SearchWholeImage);

    private async Task OpenRegionSearchCoreAsync(string? path, RegionSearchInputBehavior behavior)
    {
        if (path is not null && !FileTypeConstants.IsImageFile(path)) return;
        var version = ++_regionOpenVersion;
        var folder = Vm.CurrentFolder;
        ImageSearchButtonA.Flyout?.Hide();
        CloseHoverPreview();
        // Let an existing tag search finish writing its own result list before
        // taking a snapshot. Awaiting it leaves the dispatcher responsive.
        if (Vm.SearchByTagCommand.ExecutionTask is { IsCompleted: false } tagSearch)
        {
            try { await tagSearch; }
            catch (Exception ex) { AppLogger.Warn($"等待标签搜索完成: {ex.Message}"); }
        }
        await Dispatcher.Yield(DispatcherPriority.Background);
        if (_windowCloseStarted || !IsVisible || version != _regionOpenVersion || folder != Vm.CurrentFolder) return;
        RegionSearchEditor.CancelInteraction();
        // Switching entry intent must not submit the previous input while setting its mode.
        if (RegionSearchEditor.InputBehavior != behavior || behavior == RegionSearchInputBehavior.SearchWholeImage)
            RegionSearchEditor.ClearInput();
        RegionSearchEditor.InputBehavior = behavior;
        if (!Vm.IsRegionSearchActive)
        {
            Vm.BeginRegionSearchSession(ThumbnailScrollViewer.Offset.Y);
            UpdateRegionSearchPresentation();
            RegionSearchEditor.Mode = ImageManager.Core.Models.RegionSearchMode.SimilarContent;
        }
        if (behavior == RegionSearchInputBehavior.SearchWholeImage)
            RegionSearchEditor.Mode = ImageManager.Core.Models.RegionSearchMode.SimilarContent;
        if (path is not null) await RegionSearchEditor.LoadImageAsync(path);
        else RegionSearchEditor.Focus();
    }

    private async Task ExitRegionSearchAsync()
    {
        if (!Vm.IsRegionSearchActive) return;
        var version = ++_regionOpenVersion;
        RegionSearchEditor.CancelInteraction();
        await Vm.ExitRegionSearchSessionAsync();
        UpdateRegionSearchPresentation();
        if (!Vm.CanRestoreRegionSearchScroll) return;
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        if (_windowCloseStarted || Vm.IsRegionSearchActive || version != _regionOpenVersion || !Vm.CanRestoreRegionSearchScroll) return;
        _continuousResizeAnchor = null;
        StopWheelAnimation();
        ThumbnailScrollViewer.Offset = new Vector(0, Vm.RegionSearchRestoreOffset);
        RootGrid.Focus();
    }

    private async void RegionExit_Click(object? sender, RoutedEventArgs e) => await ExitRegionSearchAsync();
    private void RegionStop_Click(object? sender, RoutedEventArgs e)
    {
        RegionSearchEditor.CancelInteraction();
        Vm.StopRegionSearch();
    }

    private void RegionLimitFlyout_Opened(object? sender, EventArgs e) =>
        RegionResultLimitInput.Value = Math.Clamp(Vm.AppSettings.SimilaritySearchResultLimit, 1, 500);

    private async void RegionLimitApply_Click(object? sender, RoutedEventArgs e)
    {
        if (RegionResultLimitInput.Value is not { } value || value != decimal.Truncate(value)) return;
        Vm.AppSettings.SimilaritySearchResultLimit = (int)Math.Clamp(value, 1, 500);
        RegionLimitButton.Flyout?.Hide();
        try
        {
            await Vm.SaveSettingsAsync();
            await Vm.RefreshRegionSearchSettingsAsync();
        }
        catch (Exception ex) { Vm.RegionSearchStatus = $"无法保存搜索设置：{ex.Message}"; }
    }

    private async void ImageDropRegionOption_Click(object? sender, RoutedEventArgs e) =>
        await Vm.SaveImageDropPreferenceAsync();

    private Task HandleImageSearchDropAsync(string path)
    {
        if (!FileTypeConstants.IsImageFile(path) || !File.Exists(path)) return Task.CompletedTask;
        // Snapshot intent synchronously. Toggling the preference only affects the next drop.
        var useRegion = Vm.UseRegionSearchOnImageDrop;
        var mode = (ImageManager.Core.Models.SimilaritySearchMode)Math.Clamp(Vm.SelectedSimilarityMode, 0, 3);
        return useRegion ? OpenRegionSearchAsync(path) : SearchSimilarForFileAsync(path, mode);
    }

    private void SetRegionDetailsPaneOpen(bool open)
    {
        CloseHoverPreview();
        Vm.SetRegionDetailsPaneOpen(open);
        ApplyWorkspaceGeometry(ClientSize.Width, ClientSize.Height);
    }
}
