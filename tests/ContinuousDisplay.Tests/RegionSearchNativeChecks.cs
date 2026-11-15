using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using ImageManager.App.Controls;
using ImageManager.App.Helpers;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using ImageManager.App.Views;
using ImageManager.Core.Models;
using ImageManager.Core.Services;
using ImageManager.Infrastructure.Caching;
using ImageManager.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

internal static class RegionSearchNativeChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const uint MouseMove = 0x0200;
    private const uint LeftDown = 0x0201;
    private const uint LeftUp = 0x0202;

    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ImageManager-RegionSmoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            CheckMainWindow(folder);
            Console.WriteLine("Region search main-window checks passed.");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void CheckMainWindow(string folder)
    {
        var paths = new[] { "library-a.png", "library-b.png", "library-c.png", "external.png" }
            .Select(name => Path.Combine(folder, name)).ToArray();
        SaveFixtures(paths);
        var libraryPaths = paths.Take(3).ToList();
        var extraPaths = Enumerable.Range(0, 100).Select(i => Path.Combine(folder, $"scroll-{i}.png")).ToArray();
        SaveFixtures(extraPaths);
        libraryPaths.AddRange(extraPaths);
        var external = paths[3];
        var repo = DispatchProxy.Create<IImageMetaRepository, MetadataProxy>();
        var dispatcher = new AvaloniaDispatcher();
        var messenger = new WeakReferenceMessenger();
        var cache = new ThumbnailCacheService(new MediaProcessorFactory(null!), Path.Combine(folder, "cache"));
        var page = new PageManager(cache, null!, metaRepo: repo);
        var search = new FixtureSearch(libraryPaths);
        var wholeImageSearch = new FixtureWholeImageSearch(libraryPaths[1]);
        var settings = new FixtureSettings();
        using var services = new ServiceCollection().AddSingleton(page)
            .AddSingleton<IRegionSearchService>(search).BuildServiceProvider();
        typeof(ImageManager.App.App).GetProperty("Services")!.SetValue(null, services);
        typeof(ImageManager.App.App).GetProperty("UI")!.SetValue(null, dispatcher);
        var tags = new TagSearchEngine(repo, messenger, ImagePaging.Default);
        var vm = new MainWindowViewModel(settings, null!, repo, null!, null!, wholeImageSearch, null!, cache, page,
            tags, null!, null!, null!, messenger, dispatcher, regionSearchService: search);
        vm.AppSettings.SimilaritySearchResultLimit = 1;
        typeof(MainWindowViewModel).GetField("_allFiles", PrivateInstance)!.SetValue(vm, libraryPaths);
        typeof(MainWindowViewModel).GetField("_displayFilteredFiles", PrivateInstance)!.SetValue(vm, libraryPaths);
        var window = new TestMainWindow
        {
            DataContext = vm,
            Width = 1200,
            Height = 700,
            ShowInTaskbar = Environment.GetEnvironmentVariable("IM_REGION_NATIVE_PICKER") == "1",
            Title = "ImageManager Region Diagnostic",
            Topmost = true
        };
        vm.ContinuousDisplayRefreshRequested += (Func<int, Task>)typeof(MainWindow)
            .GetMethod("ApplyDisplayModeAsync", PrivateInstance)!
            .CreateDelegate(typeof(Func<int, Task>), window);

        try
        {
            window.Show();
            Pump(window);
            vm.ReplaceSelectedFiles([libraryPaths[0]]);
            var pane = window.FindControl<RegionSearchPane>("RegionSearchEditor")!;
            var gallery = window.FindControl<ScrollViewer>("ThumbnailScrollViewer")!;
            if (Environment.GetEnvironmentVariable("IM_REGION_NATIVE_PICKER") == "1")
            {
                CheckHoverScrollAfterReplacement(window, vm, pane, gallery, search, external, libraryPaths);
                return;
            }
            CheckToolbarEntry(window, vm, pane, search);
            Wait(window, InvokeWindow(window, "OpenRegionImageSearchAsync", external), "load external image");
            WaitForResult(window, vm, search, 1, "external whole image");
            Require(search.Last is { Region: null, Mode: RegionSearchMode.SimilarContent, Limit: 1 } first
                && first.Path == external, "Unexpected first whole-image query.");
            Require(pane.InputPath == external && gallery.IsEffectivelyVisible &&
                    pane.TranslatePoint(new Point(pane.Bounds.Width, 0), window)!.Value.X
                    <= gallery.TranslatePoint(default, window)!.Value.X + 1,
                "Input editor and result gallery do not occupy independent columns.");
            Require(vm.ActiveFileList.SequenceEqual(libraryPaths.Skip(1).Take(1)) && pane.InputPath == external,
                "Results did not honor the limit or replaced the input.");

            if (OperatingSystem.IsWindows())
            {
                CheckNativeResultClick(window, vm, pane, gallery, libraryPaths[1], external);
                CheckNativeGestures(window, vm, pane, search);
            }
            CaptureIfRequested(window);

            var previous = search.Calls.Count;
            pane.FindControl<Button>("OriginalButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitForResult(window, vm, search, previous + 1, "change search purpose");
            Require(search.Last.Mode == RegionSearchMode.FindOriginal && search.Last.Region == pane.SelectedRegion,
                "Mode change did not keep the input and committed region.");

            if (pane.SelectedRegion is not null)
            {
                pane.FindControl<Button>("ClearButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(pane.IsAwaitingSelection && pane.SelectedRegion is null && !vm.IsRegionSearchBusy &&
                    search.Calls.Count == previous + 1, "Clearing the region did not return to waiting for a box.");
            }

            Wait(window, InvokeWindow(window, "ExitRegionSearchAsync"), "exit after results");
            CheckRestored(window, vm, pane, libraryPaths, "results");
            window.Width = 1100;
            for (var index = 0; index < 2; index++)
            {
                var count = search.Calls.Count;
                Wait(window, InvokeWindow(window, "OpenRegionImageSearchAsync", libraryPaths[0]), $"reopen {index}");
                WaitForResult(window, vm, search, count + 1, $"reopened results {index}");
                Require(ReferenceEquals(pane, window.FindControl<RegionSearchPane>("RegionSearchEditor")) &&
                    pane.IsEffectivelyVisible && !window.OwnedWindows.Any(), "Reopening did not reuse the embedded pane.");
                if (OperatingSystem.IsWindows())
                {
                    var stage = pane.FindControl<RegionSearchCanvas>("Stage")!;
                    var start = StagePoint(window, pane, stage, .2, .2);
                    RequireNativeHit(window, start, $"capture {index}");
                    SendPointer(window, LeftDown, start, pressed: true);
                    SendPointer(window, MouseMove, StagePoint(window, pane, stage, .7, .7), pressed: true);
                    Pump(window);
                    Require(typeof(RegionSearchPane).GetField("_gesture", PrivateInstance)!.GetValue(pane) is not null,
                        "Native pointer did not begin a captured selection before exit.");
                }
                var queriesOnExit = 0;
                void OnQuery(object? sender, RegionSearchQueryEventArgs e) => queriesOnExit++;
                pane.QueryChanged += OnQuery;
                try
                {
                    Wait(window, InvokeWindow(window, "ExitRegionSearchAsync"), $"exit during pointer capture {index}");
                    Require(queriesOnExit == 0, "Exiting during an edited selection started a new search.");
                }
                finally
                {
                    pane.QueryChanged -= OnQuery;
                }
                CheckRestored(window, vm, pane, libraryPaths, $"capture {index}");
            }

            CheckExitDuringLoad(window, vm, pane, search, external, libraryPaths);
            CheckExitDuringInference(window, vm, pane, search, external, libraryPaths);
            CheckDropSearch(window, vm, pane, search, wholeImageSearch, settings, external, libraryPaths);
            CheckHoverScrollAfterReplacement(window, vm, pane, gallery, search, external, libraryPaths);
        }
        finally
        {
            window.Close();
            Pump(window);
        }
    }

    private static void CheckHoverScrollAfterReplacement(TestMainWindow window, MainWindowViewModel vm,
        RegionSearchPane pane, ScrollViewer gallery, FixtureSearch search, string external, List<string> libraryPaths)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (vm.IsShowingSearchResult)
            Wait(window, vm.BackFromSearchCommand.ExecuteAsync(null), "restore before hover scroll");
        vm.ReplaceSelectedFiles([libraryPaths[0]]);
        vm.AppSettings.SimilaritySearchResultLimit = 100;
        var modelsRoot = Environment.GetEnvironmentVariable("IM_REGION_GPU_MODELS_ROOT");
        using var pixai = string.IsNullOrWhiteSpace(modelsRoot) ? null : new PixaiTagService();
        if (pixai is not null)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            search.Pixai = pixai;
            search.ModelsRoot = Path.Combine(modelsRoot!, "pixai");
            Require(File.Exists(Path.Combine(search.ModelsRoot, "model.onnx")) &&
                File.Exists(Path.Combine(search.ModelsRoot, "selected_tags.csv")),
                "GPU regression requires an existing local PixAI model and tags; downloads are not allowed.");
        }
        var host = window.FindControl<Avalonia.Controls.Primitives.Popup>("HoverPreviewHost")!;
        var pickerClosedPreview = false;
        pane.PickerOpening += (_, _) => pickerClosedPreview = !host.IsOpen;
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("IMAGEMANAGER_REGION_SCROLL_ITERATIONS"), out var requested)
            ? Math.Clamp(requested, 1, 100) : 3;
        foreach (var mode in new[] { ImageManager.App.Models.ImageDisplayMode.Paged, ImageManager.App.Models.ImageDisplayMode.Continuous })
        {
            Wait(window, vm.TrySetDisplayModeAsync(mode), $"switch to {mode}");
            Require(vm.DisplayMode == mode, $"Could not switch to {mode}.");
            foreach (var path in new[] { libraryPaths[0], external })
            {
                var previous = search.Calls.Count;
                if (path == external && Environment.GetEnvironmentVariable("IM_REGION_NATIVE_PICKER") == "1")
                {
                    var outside = pane.PointToScreen(new Point(10, 10));
                    SetCursorPos(outside.X, outside.Y);
                    SendPointer(window, MouseMove, outside);
                    Pump(window);
                    AsyncThumbnailImage? PreviousResult() => gallery.GetVisualDescendants().OfType<AsyncThumbnailImage>()
                        .FirstOrDefault(image => image.Name == "GalleryThumbnail" && image.IsEffectivelyVisible && image.Bounds.Width > 0 && image.Bounds.Height > 0 &&
                            image.TranslatePoint(new Point(image.Bounds.Width / 2, image.Bounds.Height / 2), gallery) is { } local &&
                            local.Y > 10 && local.Y < gallery.Viewport.Height - 10);
                    WaitUntil(window, () => PreviousResult() is not null, "previous result layout before picker");
                    var oldResult = PreviousResult()!;
                    var oldCenter = oldResult.PointToScreen(new Point(oldResult.Bounds.Width / 2, oldResult.Bounds.Height / 2));
                    RequireNativeHit(window, oldCenter, "old preview before picker");
                    Require(SetCursorPos(oldCenter.X, oldCenter.Y), "Could not hover the previous result.");
                    SendPointer(window, MouseMove, oldCenter);
                    WaitUntil(window, () => host.IsOpen, "preview before opening file picker");
                    var choose = pane.FindControl<Button>("ChangeButton")!;
                    var point = choose.PointToScreen(new Point(choose.Bounds.Width / 2, choose.Bounds.Height / 2));
                    RequireNativeHit(window, point, "choose replacement input");
                    Console.WriteLine($"NATIVE_PICKER_READY mode={mode} pid={Environment.ProcessId} apartment={Thread.CurrentThread.GetApartmentState()} file={path}");
                    var pickerAutomation = Environment.GetEnvironmentVariable("IM_REGION_PICKER_AUTO") == "1"
                        ? Task.Run(() => SelectNativeFixture(Handle(window), path)) : Task.CompletedTask;
                    Require(SetCursorPos(point.X, point.Y), "Could not move to the Choose button.");
                    SendPointer(window, MouseMove, point);
                    SendPointer(window, LeftDown, point, pressed: true);
                    SendPointer(window, LeftUp, point);
                    if (Environment.GetEnvironmentVariable("IM_REGION_PICKER_AUTO") == "1")
                        Wait(window, pickerAutomation, "native picker automation");
                    WaitUntil(window, () => pane.InputPath == path && search.Calls.Count > previous, "native file picker selection", timeoutSeconds: 180);
                    Require(IsWindowEnabled(Handle(window)) && GetCapture() == 0,
                        "Native file picker left its owner disabled or captured.");
                    Console.WriteLine("NATIVE_PICKER_RETURNED owner enabled, capture released");
                    Require(pickerClosedPreview, "Picker opened before closing the old preview.");
                }
                else
                    Wait(window, InvokeWindow(window, "OpenRegionImageSearchAsync", path), "replace scroll query image");
                WaitUntil(window, () => search.Calls.Count == previous + 1 && vm.HasRegionSearchResults && !vm.IsRegionSearchBusy,
                    "scroll query results", timeoutSeconds: pixai is null ? 10 : 120);
                Require(search.Last.Mode == RegionSearchMode.SimilarContent && vm.RegionSearchResultCount == 100,
                    "Scroll fixture did not return enough similar-content results.");
            }
            WaitUntil(window, () => gallery.Extent.Height > gallery.Viewport.Height + 100, "scrollable result extent");
            if (pixai is not null && Environment.GetEnvironmentVariable("IM_REGION_GPU_WAIT_IDLE") == "1")
            {
                Console.WriteLine("GPU scroll: waiting for idle session disposal before hover");
                WaitUntil(window, () => !pixai.IsModelLoaded, "GPU idle disposal", timeoutSeconds: 100);
                Console.WriteLine("GPU scroll: idle session disposed");
            }
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                // Leave the previous source before re-entering; after a wheel the
                // same screen point can otherwise remain over a recycled item.
                var outside = pane.PointToScreen(new Point(10, 10));
                SetCursorPos(outside.X, outside.Y);
                SendPointer(window, MouseMove, outside);
                Pump(window);
                // Keep stress runs away from the last row so every wheel must move the viewport.
                if (gallery.Offset.Y > gallery.Extent.Height - gallery.Viewport.Height - 500)
                {
                    gallery.Offset = default;
                    Pump(window);
                }
                var result = gallery.GetVisualDescendants().OfType<AsyncThumbnailImage>()
                    .First(image => image.Name == "GalleryThumbnail" && image.IsEffectivelyVisible && image.Bounds.Width > 0 &&
                        image.TranslatePoint(new Point(image.Bounds.Width / 2, image.Bounds.Height / 2), gallery) is { } local &&
                        local.Y > 10 && local.Y < gallery.Viewport.Height - 10);
                var center = result.PointToScreen(new Point(result.Bounds.Width / 2, result.Bounds.Height / 2));
                RequireNativeHit(window, center, "hover before wheel");
                Require(SetCursorPos(center.X, center.Y), "Could not position cursor over the test thumbnail.");
                SendPointer(window, MouseMove, center);
                WaitUntil(window, () => host.IsOpen,
                    $"native hover opens preview ({mode}/{iteration}, {DescribeHit(window.InputHitTest(window.PointToClient(center)))})");
                Require(host.IsUsingOverlayLayer, "Hover preview created a native popup instead of the passive overlay.");
                var preview = window.FindControl<HoverPreviewPopup>("HoverPreviewContent")!;
                preview.RefreshActualScreenRect();
                if (preview.CurrentScreenRect is { } previewRect)
                {
                    var clientStart = window.PointToScreen(default);
                    var clientEnd = window.PointToScreen(new Point(window.ClientSize.Width, window.ClientSize.Height));
                    Require(previewRect.X >= clientStart.X - 1 && previewRect.Y >= clientStart.Y - 1 &&
                        previewRect.Right <= clientEnd.X + 1 && previewRect.Bottom <= clientEnd.Y + 1,
                        "Hover overlay extends outside its owner's client area.");
                    var galleryStart = gallery.PointToScreen(default);
                    var galleryEnd = gallery.PointToScreen(new Point(gallery.Viewport.Width, gallery.Viewport.Height));
                    var left = Math.Max(previewRect.X, galleryStart.X);
                    var top = Math.Max(previewRect.Y, galleryStart.Y);
                    var right = Math.Min(previewRect.Right, galleryEnd.X);
                    var bottom = Math.Min(previewRect.Bottom, galleryEnd.Y);
                    if (right - left > 10 && bottom - top > 10)
                    {
                        center = new PixelPoint(left + (right - left) / 2, top + (bottom - top) / 2);
                        var hit = window.InputHitTest(window.PointToClient(center));
                        Require(hit is Visual visual && (ReferenceEquals(visual, gallery) || visual.GetVisualAncestors().Contains(gallery)),
                            "Hover overlay swallowed hit testing above the results.");
                        SetCursorPos(center.X, center.Y);
                    }
                }
                Console.WriteLine($"Hover scroll {mode}/{iteration}: overlay={host.IsUsingOverlayLayer}, offset={gallery.Offset.Y}");
                var offset = gallery.Offset.Y;
                var coordinates = (nint)((center.Y << 16) | (center.X & 0xffff));
                SendMessage(Handle(window), 0x020A, unchecked((nint)(-120 << 16)), coordinates);
                Console.WriteLine($"Hover scroll {mode}/{iteration}: native wheel returned");
                WaitUntil(window, () => !host.IsOpen && gallery.Offset.Y > offset + 1, "wheel closes preview and scrolls");
                Require(IsWindowEnabled(Handle(window)) && GetCapture() == 0, "Hover wheel left disabled/captured owner.");
            }
            Wait(window, InvokeWindow(window, "ExitRegionSearchAsync"), "exit after hover wheel");
            CheckRestored(window, vm, pane, libraryPaths, $"hover wheel {mode}");
        }
    }

    private static void CheckToolbarEntry(TestMainWindow window, MainWindowViewModel vm, RegionSearchPane pane,
        FixtureSearch search, int queryCount = 0)
    {
        var toolbar = ImageSearchButton(window);
        var flyout = (Flyout)toolbar.Flyout!;
        flyout.ShowAt(toolbar);
        Pump(window);
        Require(flyout.IsOpen, "Image-search flyout did not open.");
        var entry = ((Control)flyout.Content!).GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "框选搜图…"));
        entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitUntil(window, () => vm.IsRegionSearchActive, "toolbar entry");
        Require(!flyout.IsOpen && pane.IsEffectivelyVisible && !window.OwnedWindows.Any(),
            "Toolbar entry did not dismiss its flyout and enter the embedded editor.");
        Require(pane.InputPath is null && search.Calls.Count == queryCount && !vm.IsRegionSearchBusy,
            "Empty toolbar entry started an invalid search.");
        if (OperatingSystem.IsWindows())
            Require(IsWindowEnabled(Handle(window)), "Entering region search disabled the owner HWND.");
    }

    private static void CheckDropSearch(TestMainWindow window, MainWindowViewModel vm, RegionSearchPane pane,
        FixtureSearch search, FixtureWholeImageSearch wholeImageSearch, FixtureSettings settings,
        string external, List<string> libraryPaths)
    {
        var toolbar = ImageSearchButton(window);
        var flyout = (Flyout)toolbar.Flyout!;
        flyout.ShowAt(toolbar);
        Pump(window);
        var content = (Control)flyout.Content!;
        var preference = content.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "ImageDropRegionCheckBox");
        var mode = content.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "ImageSimilarityModeSelector");
        var workspace = window.FindControl<Grid>("WorkspaceGallery")!;
        var stageBorder = pane.FindControl<Border>("StageBorder")!;
        Require(preference.IsChecked == false && mode.IsEffectivelyEnabled && settings.SavedPreferences.IsEmpty,
            "The initial unchecked preference disabled the mode selector or wrote settings.");
        Require(!DragDrop.GetAllowDrop(workspace) && DragDrop.GetAllowDrop(stageBorder),
            "Ordinary gallery took over file drops or the editor canvas stopped accepting them.");
        flyout.Hide();

        // Invoke the shared path dispatcher with an extracted path. Explorer/OLE drops are not simulated.
        var regionCalls = search.Calls.Count;
        foreach (var searchMode in Enum.GetValues<SimilaritySearchMode>())
        {
            vm.SelectSimilarityMode(searchMode);
            Wait(window, InvokeWindow(window, "HandleImageSearchDropAsync", external), $"drop as {searchMode}");
            Require(wholeImageSearch.Calls.Last() == (external, searchMode, 1) &&
                vm.ActiveFileList.SequenceEqual(libraryPaths.Skip(1).Take(1)),
                $"Unchecked drop did not use the selected {searchMode} whole-image mode.");
            Require(!vm.IsRegionSearchActive && search.Calls.Count == regionCalls,
                "Unchecked drop entered region search.");
        }

        Require(wholeImageSearch.Calls.Count == Enum.GetValues<SimilaritySearchMode>().Length,
            "Unchecked drop did not issue one whole-image query per mode.");
        Wait(window, vm.BackFromSearchCommand.ExecuteAsync(null), "restore gallery after whole-image drops");
        vm.ReplaceSelectedFiles([libraryPaths[0]]);
        flyout.ShowAt(toolbar);
        Pump(window);
        var selectedMode = mode.SelectedIndex;
        ClickPreferenceAndCheckSave(window, preference, settings, true);
        Require(vm.UseRegionSearchOnImageDrop && !mode.IsEffectivelyEnabled && mode.SelectedIndex == selectedMode,
            "Checking the preference did not disable the mode selector while preserving its selection.");
        flyout.Hide();
        Pump(window);
        Require(!vm.IsRegionSearchActive && search.Calls.Count == regionCalls, "Changing the preference started a search.");

        var wholeImageCalls = wholeImageSearch.Calls.Count;
        Wait(window, InvokeWindow(window, "HandleImageSearchDropAsync", external), "load dropped image for a selection");
        Require(vm.IsRegionSearchActive && pane.InputPath == external && pane.IsAwaitingSelection &&
            pane.SelectedRegion is null && !vm.IsRegionSearchBusy && search.Calls.Count == regionCalls &&
            wholeImageSearch.Calls.Count == wholeImageCalls,
            "Checked drop did not load the editor and wait for a selection without querying.");
        vm.UseRegionSearchOnImageDrop = false;
        Pump(window);
        Require(vm.IsRegionSearchActive && pane.InputPath == external && pane.IsAwaitingSelection,
            "Changing the preference interrupted an existing region-search session.");
        vm.UseRegionSearchOnImageDrop = true;
        CheckAwaitingSelection(window, vm, pane, search, external, libraryPaths, regionCalls);

        flyout.ShowAt(toolbar);
        Pump(window);
        ClickPreferenceAndCheckSave(window, preference, settings, false);
        Require(!vm.UseRegionSearchOnImageDrop && mode.IsEffectivelyEnabled && mode.SelectedIndex == selectedMode,
            "Unchecking the preference did not restore the previously selected mode.");
        flyout.Hide();
        Pump(window);
        var calls = search.Calls.Count;
        CheckToolbarEntry(window, vm, pane, search, calls);
        ReturnToGallery(window, vm, pane, libraryPaths, "manual empty entry");
        Wait(window, InvokeWindow(window, "HandleImageSearchDropAsync", external), "drop after re-enabling whole-image modes");
        Require(wholeImageSearch.Calls.Count == wholeImageCalls + 1 &&
            wholeImageSearch.Calls.Last() == (external, (SimilaritySearchMode)selectedMode, 1) &&
            !vm.IsRegionSearchActive && search.Calls.Count == calls,
            "Turning off the preference did not restore the earlier whole-image drop route.");

        static void ClickPreferenceAndCheckSave(Window window, CheckBox preference, FixtureSettings settings, bool expected)
        {
            Require(preference.IsChecked != expected, "Preference must actually toggle during native click.");
            if (OperatingSystem.IsWindows())
            {
                var top = TopLevel.GetTopLevel(preference)!;
                var hwnd = top.TryGetPlatformHandle()!.Handle;
                var center = preference.PointToScreen(new Point(preference.Bounds.Width / 2, preference.Bounds.Height / 2));
                var point = new NativePoint { X = center.X, Y = center.Y };
                Require(WindowFromPoint(point) == hwnd, "Checkbox popup is not the native pointer target.");
                Require(ScreenToClient(hwnd, ref point), "Could not map checkbox coordinates.");
                var position = (nint)((point.Y << 16) | (point.X & 0xffff));
                SendMessage(hwnd, MouseMove, 0, position);
                SendMessage(hwnd, LeftDown, 1, position);
                SendMessage(hwnd, LeftUp, 0, position);
            }
            else
            {
                preference.SetCurrentValue(CheckBox.IsCheckedProperty, expected);
                preference.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            Pump(window);
            Require(preference.IsChecked == expected, "Native checkbox click did not change its checked state.");
            WaitUntil(window, () => settings.SavedPreferences.TryPeek(out var saved) && saved == expected,
                $"save drop preference {expected}");
            Require(settings.SavedPreferences.TryDequeue(out _) && settings.SavedPreferences.IsEmpty,
                "The checkbox wrote the preference more than once.");
        }
    }

    private static void CheckAwaitingSelection(TestMainWindow window, MainWindowViewModel vm, RegionSearchPane pane,
        FixtureSearch search, string path, List<string> libraryPaths, int queryCount)
    {
        pane.FindControl<Button>("OriginalButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(pane.Mode == RegionSearchMode.FindOriginal && pane.IsAwaitingSelection, "Search mode did not change.");
        if (OperatingSystem.IsWindows())
        {
            var stage = pane.FindControl<RegionSearchCanvas>("Stage")!;
            var start = StagePoint(window, pane, stage, .25, .25);
            Drag(window, start, start);
            Require(pane.SelectedRegion is null && pane.IsAwaitingSelection, "A click committed an invalid region.");
            RequireNoQuery(window, vm, search, queryCount, "mode change or invalid selection");

            Drag(window, start, StagePoint(window, pane, stage, .65, .75));
            WaitForResult(window, vm, search, ++queryCount, "selection on a dropped image");
            Require(search.Last is { Region: not null, Mode: RegionSearchMode.FindOriginal } &&
                search.Last.Path == path && search.Last.Region == pane.SelectedRegion,
                "The valid selection from the dropped image did not run exactly one region query.");
            pane.FindControl<Button>("ClearButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(pane.IsAwaitingSelection && pane.SelectedRegion is null, "Clearing the drop selection did not reset it.");
            vm.RegionSearchRecursive = !vm.RegionSearchRecursive;
            Wait(window, vm.RefreshRegionSearchSettingsAsync(), "update settings after clearing selection");
        }

        RequireNoQuery(window, vm, search, queryCount, "waiting without a selection");
        pane.FindControl<Button>("SearchButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitForResult(window, vm, search, queryCount + 1, "explicit whole-image search");
        Require(search.Last is { Region: null, Mode: RegionSearchMode.FindOriginal } && search.Last.Path == path,
            "Explicit whole-image search used a stale box or mode.");
        ReturnToGallery(window, vm, pane, libraryPaths, "dropped image");
    }

    private static void RequireNoQuery(Window window, MainWindowViewModel vm, FixtureSearch search, int count,
        string phase)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            Require(!vm.IsRegionSearchBusy && search.Calls.Count == count, $"Unexpected region query during {phase}.");
            Pump(window);
        } while (watch.Elapsed < TimeSpan.FromMilliseconds(350));
        Require(!vm.IsRegionSearchBusy && search.Calls.Count == count, $"Unexpected region query during {phase}.");
    }

    private static void ReturnToGallery(TestMainWindow window, MainWindowViewModel vm, RegionSearchPane pane,
        List<string> libraryPaths, string phase)
    {
        var exit = window.FindControl<Border>("RegionQueryStrip")!.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "返回图库"));
        Require(exit.IsEffectivelyEnabled && exit.IsEffectivelyVisible, "The return button is unavailable.");
        if (OperatingSystem.IsWindows())
        {
            var center = exit.PointToScreen(new Point(exit.Bounds.Width / 2, exit.Bounds.Height / 2));
            RequireNativeHit(window, center, $"return after {phase}");
            SendPointer(window, MouseMove, center);
            SendPointer(window, LeftDown, center, pressed: true);
            SendPointer(window, LeftUp, center);
        }
        else
        {
            exit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        WaitUntil(window, () => !vm.IsRegionSearchActive && vm.CanRestoreRegionSearchScroll && vm.ActiveFileList.SequenceEqual(libraryPaths),
            $"return to gallery after {phase}");
        CheckRestored(window, vm, pane, libraryPaths, phase);
    }

    private static void CheckNativeGestures(TestMainWindow window, MainWindowViewModel vm, RegionSearchPane pane,
        FixtureSearch search)
    {
        var stage = pane.FindControl<RegionSearchCanvas>("Stage")!;
        var start = StagePoint(window, pane, stage, .25, .25);
        var end = StagePoint(window, pane, stage, .65, .75);
        RequireNativeHit(window, start, "draw");
        SendPointer(window, LeftDown, start, pressed: true);
        SendPointer(window, MouseMove, end, pressed: true);
        Pump(window);
        Require(search.Calls.Count == 1 && pane.SelectedRegion is null, "Draft published before native pointer release.");
        SendPointer(window, LeftUp, end);
        WaitForResult(window, vm, search, 2, "draw");
        var drawn = search.Last.Region;
        Require(drawn is not null && Near(drawn.X, 100) && Near(drawn.Y, 25) &&
            Near(drawn.Width, 160) && Near(drawn.Height, 50), $"Unexpected drawn region: {drawn}.");

        Drag(window, StagePoint(window, pane, stage, .45, .5), StagePoint(window, pane, stage, .55, .5));
        WaitForResult(window, vm, search, 3, "move");
        var moved = search.Last.Region;
        Require(moved is not null && Near(moved.X, drawn!.X + 40) &&
            moved.Width == drawn.Width && moved.Height == drawn.Height, $"Unexpected moved region: {moved}.");

        var right = (moved!.X + moved.Width) / 400d;
        var centerY = (moved.Y + moved.Height / 2d) / 100;
        Drag(window, StagePoint(window, pane, stage, right, centerY),
            StagePoint(window, pane, stage, right + .10, centerY));
        WaitForResult(window, vm, search, 4, "resize");
        var resized = search.Last.Region;
        Require(resized is not null && resized.X == moved.X && Near(resized.Width, moved.Width + 40) &&
            resized.Height == moved.Height, $"Unexpected resized region: {resized}.");

        var centerX = (resized!.X + resized.Width / 2d) / 400;
        var origin = StagePoint(window, pane, stage, centerX, centerY);
        var away = StagePoint(window, pane, stage, centerX + .12, centerY);
        RequireNativeHit(window, origin, "drag back");
        SendPointer(window, LeftDown, origin, pressed: true);
        SendPointer(window, MouseMove, away, pressed: true);
        SendPointer(window, MouseMove, origin, pressed: true);
        SendPointer(window, LeftUp, origin);
        WaitForResult(window, vm, search, 5, "drag back to original selection");
        Require(search.Last.Region == resized && pane.SelectedRegion == resized,
            "Dragging back changed the committed selection.");

        IPointer? pointer = null;
        void OnPointerPressed(object? sender, PointerPressedEventArgs e) => pointer = e.Pointer;
        stage.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        try
        {
            RequireNativeHit(window, origin, "edit before capture loss");
            SendPointer(window, LeftDown, origin, pressed: true);
            SendPointer(window, MouseMove, away, pressed: true);
            Require(pointer?.Captured == stage, "Edited selection did not capture the pointer.");
            pointer!.Capture(null);
            SendPointer(window, LeftUp, away);
            WaitForResult(window, vm, search, 6, "restore search after lost pointer capture");
            Require(search.Last.Region == resized && pane.SelectedRegion == resized,
                "Capture loss published the uncommitted selection.");
        }
        finally
        {
            stage.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        }
    }

    private static void CheckNativeResultClick(TestMainWindow window, MainWindowViewModel vm, RegionSearchPane pane,
        ScrollViewer gallery, string resultPath, string inputPath)
    {
        AsyncThumbnailImage? FindResult() => gallery.GetVisualDescendants().OfType<AsyncThumbnailImage>()
            .FirstOrDefault(image => image.Name == "GalleryThumbnail" && image.IsEffectivelyVisible &&
                image.Bounds.Width > 0 && image.Bounds.Height > 0 &&
                image.DataContext is ImageViewItem item && item.FilePath == resultPath &&
                image.TranslatePoint(new Point(image.Bounds.Width / 2, image.Bounds.Height / 2), gallery) is { } center &&
                new Rect(gallery.Viewport).Contains(center));

        var details = window.FindControl<DetailPreviewPane>("DetailsPaneView")!;
        var toggle = window.FindControl<Button>("RegionPreviewToggle")!;

        void Click(Control control, string phase)
        {
            var point = control.PointToScreen(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2));
            RequireNativeHit(window, point, phase);
            SendPointer(window, MouseMove, point);
            SendPointer(window, LeftDown, point, pressed: true);
            SendPointer(window, LeftUp, point);
            Pump(window);
        }

        void ClickResult()
        {
            WaitUntil(window, () => FindResult() is not null, "layout of first search result");
            Click(FindResult()!, "search result thumbnail");
            Require(pane.InputPath == inputPath && !window.OwnedWindows.Any(),
                "Clicking a result replaced the search input or created an owned window.");
        }

        void OpenEmptyPreview()
        {
            Click(toggle, "open region preview");
            WaitUntil(window, () => vm.DetailsPane.IsOpen && details.IsEffectivelyVisible && details.Bounds.Width > 0,
                "layout of manually opened region preview");
            Require(vm.DetailsPane.CurrentPath is null && vm.SelectedFilePaths.SequenceEqual([resultPath]),
                "Opening the preview used the existing result selection or cleared it.");
            CheckDockedPreview();
        }

        void CheckDockedPreview()
        {
            Pump(window);
            var galleryRight = gallery.TranslatePoint(new Point(gallery.Bounds.Width, 0), window)!.Value.X;
            var detailsLeft = details.TranslatePoint(default, window)!.Value.X;
            Require(details.IsEffectivelyVisible && details.Bounds.Width > 0 &&
                    gallery.Bounds.Width > 0 && galleryRight <= detailsLeft + 1,
                "The docked preview is missing or overlaps the results.");
        }

        void ClosePreview()
        {
            var collapse = details.GetVisualDescendants().OfType<Button>()
                .Single(button => button.IsEffectivelyVisible && Equals(button.Tag, "Collapse"));
            Click(collapse, "close region preview");
            WaitUntil(window, () => !vm.DetailsPane.IsOpen && !details.IsEffectivelyVisible, "close result details");
            Require(vm.DetailsPane.CurrentPath is null && vm.SelectedFilePaths.SequenceEqual([resultPath]),
                "Closing the preview did not clear the preview path while preserving selection.");
        }

        var originalWidth = window.Width;
        window.Width = 950;
        WaitUntil(window, () => window.ClientSize.Width < 1000, "narrow region search layout");
        Require(!vm.DetailsPane.IsOpen && vm.DetailsPane.CurrentPath is null,
            "The search results opened or populated the preview automatically.");
        vm.ReplaceSelectedFiles([]);
        ClickResult();
        Require(!vm.DetailsPane.IsOpen && vm.DetailsPane.CurrentPath is null &&
                vm.SelectedFilePaths.SequenceEqual([resultPath]),
            "Clicking with the preview closed did not select only the result.");
        OpenEmptyPreview();
        ClickResult();
        WaitUntil(window, () => vm.DetailsPane.CurrentPath == resultPath, "preview result clicked after opening");
        Require(vm.DetailsPane.IsOpen, "Clicking a result closed the manually opened preview.");
        window.Width = 700;
        WaitUntil(window, () => window.ClientSize.Width <= 720, "narrowest region preview layout");
        CheckDockedPreview();
        Require(vm.DetailsPane.CurrentPath == resultPath, "Resizing changed the previewed result.");
        ClosePreview();
        OpenEmptyPreview();
        ClosePreview();
        window.Width = originalWidth;
        Pump(window);
        Console.WriteLine("Region result selection and manual docked preview checks passed.");
    }

    private static void CheckExitDuringLoad(TestMainWindow window, MainWindowViewModel vm, RegionSearchPane pane,
        FixtureSearch search, string path, List<string> libraryPaths)
    {
        var gate = (SemaphoreSlim)typeof(RegionSearchPane).GetField("_decodeGate", PrivateInstance)!.GetValue(pane)!;
        var count = search.Calls.Count;
        Require(gate.Wait(0), "Preview decoder unexpectedly occupied before load check.");
        Task? open = null;
        var loadStarted = false;
        void OnLoadStarted(object? sender, EventArgs e) => loadStarted = true;
        pane.InputChanging += OnLoadStarted;
        try
        {
            open = InvokeWindow(window, "OpenRegionSearchAsync", path);
            WaitUntil(window, () => loadStarted, "enter while decode gate held");
            Require(!open.IsCompleted, "Preview load completed before the cancellation test.");
            Wait(window, InvokeWindow(window, "ExitRegionSearchAsync"), "exit while reading input");
            Wait(window, open, "finish canceled input task");
            Require(search.Calls.Count == count, "Canceled input load started a search.");
            CheckRestored(window, vm, pane, libraryPaths, "pending preview load");
        }
        finally
        {
            pane.InputChanging -= OnLoadStarted;
            gate.Release();
        }
    }

    private static void CheckExitDuringInference(TestMainWindow window, MainWindowViewModel vm, RegionSearchPane pane,
        FixtureSearch search, string path, List<string> libraryPaths)
    {
        var count = search.Calls.Count;
        var pending = search.HoldNextCall();
        try
        {
            Wait(window, InvokeWindow(window, "OpenRegionImageSearchAsync", path), "load input before gated inference");
            Wait(window, pending.Entered.Task, "enter gated inference");
            Require(vm.IsRegionSearchBusy && search.Calls.Count == count + 1, "Expected one in-flight inference.");
            if (OperatingSystem.IsWindows())
            {
                var stage = pane.FindControl<RegionSearchCanvas>("Stage")!;
                var point = StagePoint(window, pane, stage, .5, .5);
                RequireNativeHit(window, point, "canvas click during inference");
                SendPointer(window, LeftDown, point, pressed: true);
                SendPointer(window, LeftUp, point);
                Pump(window);
                Require(vm.IsRegionSearchBusy && !pending.Entered.Task.Result.IsCancellationRequested &&
                    search.Calls.Count == count + 1, "A canvas click canceled the in-flight whole-image query.");
            }
            Wait(window, InvokeWindow(window, "ExitRegionSearchAsync"), "exit while inference is still in flight");
            Require(pending.Entered.Task.Result.IsCancellationRequested && !pending.Release.Task.IsCompleted,
                "Exit did not cancel the pending query without waiting for inference.");
            CheckRestored(window, vm, pane, libraryPaths, "pending inference");
        }
        finally
        {
            pending.Release.TrySetResult();
            if (pending.Entered.Task.IsCompletedSuccessfully)
                Wait(window, pending.Returned.Task, "return from canceled inference");
        }

        Wait(window, InvokeWindow(window, "OpenRegionImageSearchAsync", path), "reopen after canceled inference");
        WaitForResult(window, vm, search, count + 2, "query after canceled inference");
        Wait(window, InvokeWindow(window, "ExitRegionSearchAsync"), "final exit");
        CheckRestored(window, vm, pane, libraryPaths, "final exit");
    }

    private static void CheckRestored(TestMainWindow window, MainWindowViewModel vm, RegionSearchPane pane,
        List<string> libraryPaths, string phase)
    {
        Pump(window);
        Require(!vm.IsRegionSearchActive && !vm.IsRegionSearchBusy && !vm.HasRegionSearchResults &&
            !pane.IsEffectivelyVisible && pane.InputPath is null && !window.OwnedWindows.Any(),
            $"Search presentation did not exit after {phase}.");
        Require(vm.ActiveFileList.SequenceEqual(libraryPaths) && vm.SelectedFilePaths.SequenceEqual(libraryPaths.Take(1)),
            $"Original gallery and selection did not restore after {phase}.");
        if (!OperatingSystem.IsWindows())
            return;

        var hwnd = Handle(window);
        Require(IsWindowEnabled(hwnd) && GetCapture() == 0, $"Owner HWND or capture did not recover after {phase}.");
        var button = ImageSearchButton(window);
        var flyout = (Flyout)button.Flyout!;
        var center = new Point(button.Bounds.Width / 2, button.Bounds.Height / 2);
        var point = button.PointToScreen(center);
        RequireNativeHit(window, point, $"toolbar after {phase}");
        var hit = window.InputHitTest(button.TranslatePoint(center, window)!.Value);
        var events = new List<string>();
        void OnPointer(object? sender, PointerEventArgs e) =>
            events.Add($"{e.RoutedEvent?.Name} source={DescribeHit(e.Source)} handled={e.Handled}");
        void OnClick(object? sender, RoutedEventArgs e) => events.Add("Button.Click");
        void OnOpened(object? sender, EventArgs e) => events.Add("Flyout.Opened");
        void OnClosed(object? sender, EventArgs e) => events.Add("Flyout.Closed");

        window.AddHandler(InputElement.PointerPressedEvent, OnPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerReleasedEvent, OnPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        button.Click += OnClick;
        flyout.Opened += OnOpened;
        flyout.Closed += OnClosed;
        try
        {
            SendPointer(window, MouseMove, point);
            SendPointer(window, LeftDown, point, pressed: true);
            SendPointer(window, LeftUp, point);
            try
            {
                WaitUntil(window, () => flyout.IsOpen, $"owner toolbar native click after {phase}");
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"{ex.Message} Button={button.Name}, Avalonia hit={DescribeHit(hit)}, " +
                    $"enabled={button.IsEffectivelyEnabled}, native capture={GetCapture()}, events=[{string.Join(", ", events)}].",
                    ex);
            }
        }
        finally
        {
            window.RemoveHandler(InputElement.PointerPressedEvent, OnPointer);
            window.RemoveHandler(InputElement.PointerReleasedEvent, OnPointer);
            button.Click -= OnClick;
            flyout.Opened -= OnOpened;
            flyout.Closed -= OnClosed;
            flyout.Hide();
            Pump(window);
        }
    }

    private static string DescribeHit(object? source) => source is StyledElement element
        ? $"{source.GetType().Name}#{element.Name}"
        : source?.GetType().Name ?? "none";

    private static Button ImageSearchButton(Window window) => window.GetVisualDescendants().OfType<Button>()
        .First(button => button.IsEffectivelyVisible && Equals(button.Content, "以图搜图"));

    private static PixelPoint StagePoint(Window window, RegionSearchPane pane, Control stage, double x, double y)
    {
        Pump(window);
        var bounds = (Rect)typeof(RegionSearchPane).GetProperty("ImageBounds", PrivateInstance)!.GetValue(pane)!;
        Require(bounds.Width > 0 && bounds.Height > 0, "Source image has no visible canvas bounds.");
        return stage.PointToScreen(new Point(bounds.X + x * bounds.Width, bounds.Y + y * bounds.Height));
    }

    private static void Drag(Window window, PixelPoint start, PixelPoint end)
    {
        RequireNativeHit(window, start, "selection drag");
        SendPointer(window, LeftDown, start, pressed: true);
        SendPointer(window, MouseMove, end, pressed: true);
        SendPointer(window, LeftUp, end);
    }

    private static bool Near(int value, int expected) => Math.Abs(value - expected) <= 3;
    private static nint Handle(Window window) => window.TryGetPlatformHandle()!.Handle;

    private static void RequireNativeHit(Window window, PixelPoint point, string phase)
    {
        window.Activate();
        Pump(window);
        var expected = Handle(window);
        var hit = WindowFromPoint(new NativePoint { X = point.X, Y = point.Y });
        Require(hit == expected,
            $"Native hit testing failed for {phase} at {point}. Expected owner {DescribeWindow(expected)}, " +
            $"hit {DescribeWindow(hit)}, foreground {DescribeWindow(GetForegroundWindow())}.");
    }

    private static string DescribeWindow(nint hwnd)
    {
        if (hwnd == 0)
            return "HWND=0";

        var className = new StringBuilder(256);
        GetClassName(hwnd, className, className.Capacity);
        GetWindowThreadProcessId(hwnd, out var pid);
        return $"HWND=0x{hwnd:X} class={className} PID={pid}";
    }

    private static void SendPointer(Window window, uint message, PixelPoint point, bool pressed = false)
    {
        var hwnd = Handle(window);
        var coordinates = new NativePoint { X = point.X, Y = point.Y };
        Require(ScreenToClient(hwnd, ref coordinates), "Could not map native pointer coordinates.");
        var lParam = (nint)((coordinates.Y << 16) | (coordinates.X & 0xffff));
        SendMessage(hwnd, message, pressed ? 1 : 0, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    private delegate bool EnumerateWindow(nint hwnd, nint state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumerateWindow callback, nint state);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumerateWindow callback, nint state);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeout(nint hwnd, uint message, nint wParam, string text, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);

    // This driver only targets a dialog owned by this disposable test process.
    private static void SelectNativeFixture(nint owner, string path)
    {
        nint dialog = 0;
        var watch = Stopwatch.StartNew();
        while (dialog == 0 && watch.Elapsed < TimeSpan.FromSeconds(8))
        {
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != Environment.ProcessId) return true;
                var title = new StringBuilder(256);
                var className = new StringBuilder(256);
                GetWindowText(hwnd, title, title.Capacity);
                GetClassName(hwnd, className, className.Capacity);
                if (className.ToString() == "#32770" && title.ToString() == "选择输入图片") dialog = hwnd;
                return true;
            }, 0);
            if (dialog == 0) Thread.Sleep(50);
        }
        Require(dialog != 0, "No native file dialog appeared in the isolated test process.");
        Console.WriteLine($"NATIVE_PICKER_FOUND hwnd={dialog} ownerEnabled={IsWindowEnabled(owner)}");
        try
        {
        Thread.Sleep(500); // Let IFileDialog finish initializing its edit control.
        nint filename = 0;
        EnumChildWindows(dialog, (hwnd, _) =>
        {
            if (GetDlgCtrlID(hwnd) == 1148) filename = hwnd;
            return true;
        }, 0);
        Require(filename != 0, "Native picker has no filename control (1148).");
        nint edit = 0;
        var filenameClass = new StringBuilder(256);
        GetClassName(filename, filenameClass, filenameClass.Capacity);
        if (filenameClass.ToString() == "Edit") edit = filename;
        EnumChildWindows(filename, (hwnd, _) =>
        {
            var className = new StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() == "Edit") edit = hwnd;
            return true;
        }, 0);
        Require(edit != 0, "Native picker has no filename edit child.");
        GetWindowThreadProcessId(edit, out var editPid);
        Require(editPid == Environment.ProcessId && File.Exists(path), "Picker target is outside this test.");
        Require(SendMessageTimeout(edit, 0x000C, 0, path, 2, 2000, out _) != 0, "Could not fill native picker filename.");
        nint openButton = 0;
        EnumChildWindows(dialog, (hwnd, _) =>
        {
            var className = new StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);
            if (GetDlgCtrlID(hwnd) == 1 && className.ToString() == "Button") openButton = hwnd;
            return true;
        }, 0);
        Require(openButton != 0, "Native picker has no Open button.");
        Require(PostMessage(openButton, 0x00F5, 0, 0), "Could not click native picker Open button.");
        Console.WriteLine("NATIVE_PICKER_SUBMITTED");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"NATIVE_PICKER_DRIVER_FAILED {ex}");
            PostMessage(dialog, 0x0010, 0, 0);
            throw;
        }
    }

    private static void SaveFixtures(IEnumerable<string> paths)
    {
        var portrait = Environment.GetEnvironmentVariable("IM_REGION_PORTRAIT_FIXTURE") == "1" &&
            Environment.GetEnvironmentVariable("IM_REGION_NATIVE_PICKER") == "1";
        using var bitmap = new SKBitmap(portrait ? 800 : 400, portrait ? 1200 : 100);
        bitmap.Erase(SKColors.Blue);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 90);
        foreach (var path in paths)
        {
            using var stream = File.Create(path);
            png.SaveTo(stream);
        }
    }

    private static void CaptureIfRequested(Window window)
    {
        if (Environment.GetEnvironmentVariable("IMAGEMANAGER_REGION_NATIVE_CAPTURE") != "1")
            return;

        Pump(window);
        var path = Path.Combine(Path.GetTempPath(), "IM-RegionInline-preview.png");
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)));
        bitmap.Render(window);
        bitmap.Save(path);
        Console.WriteLine($"Region search preview saved to {path}");
    }

    private static Task InvokeWindow(MainWindow window, string name, params object?[] args) =>
        (Task)typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(window, args)!;

    private static void Pump(Window window)
    {
        var dispatcher = Dispatcher.UIThread;
        // SetupWithoutStarting has no main loop. RunJobs alone cannot service
        // the native window messages Windows uses to detect a hung HWND.
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Background,
            dispatcher, (_, _) => frame.Continue = false);
        try
        {
            dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }

        if (window.IsVisible)
            window.UpdateLayout();
        dispatcher.RunJobs();
    }

    private static void Wait(Window window, Task task, string phase)
    {
        WaitUntil(window, () => task.IsCompleted, phase);
        task.GetAwaiter().GetResult();
        Pump(window);
    }

    private static void WaitForResult(Window window, MainWindowViewModel vm, FixtureSearch search, int count,
        string phase)
    {
        WaitUntil(window, () => search.Calls.Count >= count && vm.HasRegionSearchResults && !vm.IsRegionSearchBusy,
            $"{phase} ({vm.RegionSearchStatus})");
        Require(search.Calls.Count == count && vm.RegionSearchResultCount == 1,
            $"Expected one completed query for {phase}: {vm.RegionSearchStatus}.");
    }

    private static void WaitUntil(Window window, Func<bool> complete, string phase, int timeoutSeconds = 10)
    {
        var watch = Stopwatch.StartNew();
        while (!complete() && watch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
            Pump(window);
        Require(complete(), $"Timed out during {phase}.");
        Pump(window);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record Query(string Path, ImageRegion? Region, RegionSearchMode Mode, int Limit);

    private sealed class FixtureSettings : ISettingsRepository
    {
        public ConcurrentQueue<bool> SavedPreferences { get; } = new();

        public Task<AppSettings> LoadAsync() => Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings)
        {
            SavedPreferences.Enqueue(settings.UseRegionSearchOnImageDrop);
            return Task.CompletedTask;
        }
    }

    private sealed class FixtureWholeImageSearch(string resultPath) : ISimilarImageService
    {
        public List<(string Path, SimilaritySearchMode Mode, int Limit)> Calls { get; } = new();

        public void InvalidateVectorSnapshots() { }
        public void InvalidatePerceptualSnapshot() { }

        public Task<List<SimilaritySearchResult>> SearchByImageAsync(string baseFilePath, IEnumerable<string> candidates,
            SimilaritySearchMode mode, int limit = 50, CancellationToken ct = default)
        {
            Calls.Add((baseFilePath, mode, limit));
            return Task.FromResult<List<SimilaritySearchResult>>([new(resultPath, .9f)]);
        }

        public Task<List<SimilaritySearchResult>> SearchByTextAsync(string query, IEnumerable<string> candidates,
            int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class HeldQuery
    {
        public TaskCompletionSource<CancellationToken> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FixtureSearch(List<string> paths) : IRegionSearchService
    {
        public PixaiTagService? Pixai { get; set; }
        public string ModelsRoot { get; set; } = "";
        private HeldQuery? _next;
        public ConcurrentQueue<Query> Calls { get; } = new();
        public Query Last => Calls.Last();

        public HeldQuery HoldNextCall()
        {
            var pending = new HeldQuery();
            Interlocked.Exchange(ref _next, pending);
            return pending;
        }

        public async Task<RegionSearchResponse> SearchAsync(string imagePath, ImageRegion? region,
            IReadOnlyCollection<string> candidates, RegionSearchMode mode, int limit, CancellationToken ct = default)
        {
            Require(candidates.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(paths),
                "Search candidate scope changed when using an external image.");
            Calls.Enqueue(new Query(imagePath, region, mode, limit));
            if (Pixai is not null)
            {
                var watch = Stopwatch.StartNew();
                if (!Pixai.IsModelLoaded) await Pixai.LoadModelAsync(ModelsRoot, ct);
                var vector = await Pixai.GetEmbeddingAsync(imagePath);
                Require(vector is { Length: > 0 }, "Real PixAI inference returned no embedding.");
                Console.WriteLine($"GPU scroll: real PixAI inference {watch.ElapsedMilliseconds}ms, embedding={vector!.Length}; ranking remains synthetic");
            }
            var pending = Interlocked.Exchange(ref _next, null);
            if (pending is not null)
            {
                pending.Entered.TrySetResult(ct);
                // A native inference call may return only after UI cancellation.
                await pending.Release.Task.ConfigureAwait(false);
                pending.Returned.TrySetResult();
            }

            var results = paths.Skip(1).Take(limit).Select(path => new SimilaritySearchResult(path, .8f)).ToArray();
            return new RegionSearchResponse(results, paths.Count, candidates.Count);
        }
    }
}
