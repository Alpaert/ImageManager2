using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ImageManager.App.Controls;
using ImageManager.App.Models;
using ImageManager.App.Services;
using ImageManager.App.ViewModels;
using ImageManager.App.Views;
using ImageManager.Core.Services;
using ImageManager.Core.Models;
using ImageManager.Infrastructure.Caching;
using ImageManager.Infrastructure.Services;
using CommunityToolkit.Mvvm.Messaging;
using System.Reflection;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Avalonia.Interactivity;

AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
Application.Current!.Styles.Add(new FluentTheme());
ThumbnailDecodeChecks.Run();
if (typeof(Window).IsAssignableFrom(typeof(HoverPreviewPopup)))
    throw new InvalidOperationException("Hover preview must not create a secondary Window/TopLevel.");
var fixtureDirectory = Path.Combine(Path.GetTempPath(), "ImageManager-ContinuousTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureDirectory);
using var bitmap = new SkiaSharp.SKBitmap(100, 100);
using (var canvas = new SkiaSharp.SKCanvas(bitmap)) canvas.Clear(SkiaSharp.SKColors.Red);
using var encoded = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
var paths = Enumerable.Range(0, 10000).Select(i => Path.Combine(fixtureDirectory, $"test-{i}.png")).ToArray();
foreach (var path in paths) { using var file = File.Create(path); encoded.SaveTo(file); }
var sizes = paths.Select(_ => new ImageDisplayItemSize(100, 100)).ToArray();
var prefetchSource = new ContinuousImageItemSource(paths, sizes, _ => new());
var prefetchItems = prefetchSource.GetOrCreateItems(new ImageDisplayRange(100, 8));
if (prefetchItems.Count != 8 || prefetchSource.CachedItemCount != 8)
    throw new InvalidOperationException("Continuous prefetch must materialize only its requested bounded range.");
prefetchSource.UpdateRetainedRange(new ImageDisplayRange(100, 8));
if (prefetchSource.GetCachedItems(new ImageDisplayRange(100, 8)).Count != 8)
    throw new InvalidOperationException("Continuous prefetch items must survive while inside the retained range.");
prefetchSource.UpdateRetainedRange(default);
if (prefetchSource.CachedItemCount != 0)
    throw new InvalidOperationException("Continuous prefetch items must be released outside the retained range.");
var source = new ContinuousImageItemSource(paths, sizes, _ => new());
var geometry = ImageDisplayGeometryIndex.Build(sizes,
    new ImageDisplayGeometryOptions(ImageDisplayLayoutMode.None, 800, 160, 1));
var panel = new VirtualizingWaterfallPanel { GeometryIndex = geometry };
var items = new ItemsControl
{
    ItemsSource = source,
    ItemsPanel = new FuncTemplate<Panel?>(() => panel),
    ItemTemplate = new FuncDataTemplate<ImageViewItem>((item, _) => new Border
    {
        Width = 150, Height = 150, Background = Brushes.Red,
        Child = new TextBlock { Text = item!.FileName }
    })
};
var viewer = new ScrollViewer { Content = new Grid { Children = { items } } };
var window = new Window { Width = 800, Height = 600, Content = viewer, ShowInTaskbar = false };
try
{
    window.Show();
    for (var i = 0; i < 5; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    Console.WriteLine($"items={items.ItemCount} root={items.ItemsPanelRoot?.GetType().Name} geometry={panel.GeometryIndex?.Count} bounds={panel.Bounds} viewport={viewer.Viewport} realized={panel.RealizedCount} cache={source.CachedItemCount}");
    Console.WriteLine($"borders={panel.GetVisualDescendants().OfType<Border>().Count()} texts={panel.GetVisualDescendants().OfType<TextBlock>().Count()}");
    if (panel.RealizedCount == 0 || !panel.GetVisualDescendants().OfType<TextBlock>().Any())
        throw new InvalidOperationException("Continuous first viewport has no item visuals.");
}
finally { window.Close(); }

var repo = DispatchProxy.Create<IImageMetaRepository, MetadataProxy>();
var metadataProxy = (MetadataProxy)(object)repo;
var messenger = new WeakReferenceMessenger();
var cache = new ThumbnailCacheService(new MediaProcessorFactory(null!), Path.Combine(fixtureDirectory, "cache"));
var page = new PageManager(cache, null!);
var dispatcher = new ImageManager.App.Helpers.AvaloniaDispatcher();
typeof(ImageManager.App.App).GetProperty("Services")!.SetValue(null, new ServiceCollection().AddSingleton(page).BuildServiceProvider());
typeof(ImageManager.App.App).GetProperty("UI")!.SetValue(null, dispatcher);
var tagSearchEngine = new TagSearchEngine(repo, messenger, ImagePaging.Default);
var vm = new MainWindowViewModel(null!, null!, repo, null!, null!, null!, null!, cache, page,
    tagSearchEngine, null!, null!, null!, messenger, dispatcher);
vm.WaterfallMode = "Horizontal";
typeof(MainWindowViewModel).GetField("_displayFilteredFiles", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, paths.ToList());
var main = new TestMainWindow { DataContext = vm, ShowInTaskbar = false };
ComparePagedLayouts();
VerifyThumbnailCardRecycling();
var handler = (PropertyChangedEventHandler)typeof(MainWindow).GetMethod("OnViewModelPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate(typeof(PropertyChangedEventHandler), main);
vm.PropertyChanged += handler;
vm.ContinuousDisplayRefreshRequested += (Func<int, Task>)typeof(MainWindow).GetMethod("ApplyDisplayModeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate(typeof(Func<int, Task>), main);
try
{
    main.Show();
    Pump();
    var hoverPreview = main.FindControl<HoverPreviewPopup>("HoverPreviewContent")!;
    var hoverHost = main.FindControl<Avalonia.Controls.Primitives.Popup>("HoverPreviewHost")!;
    var tagPopupA = main.FindControl<Avalonia.Controls.Primitives.Popup>("TagSearchSuggestionsPopupA")!;
    var tagPopupC = main.FindControl<Avalonia.Controls.Primitives.Popup>("TagSearchSuggestionsPopupC")!;
    if (hoverHost.TakesFocusFromNativeControl || tagPopupA.TakesFocusFromNativeControl ||
        tagPopupC.TakesFocusFromNativeControl)
        throw new InvalidOperationException("Tag and hover popups must not take native focus when opened.");
    var hoverAnchor = main.PointToScreen(new Point(600, 350));
    var hoverPosition = hoverPreview.Present(
        new byte[4 * 4 * 4], 4, 4, "preview.png", 4, 4, 64,
        main, hoverAnchor, new PixelRect(hoverAnchor.X - 40, hoverAnchor.Y - 40, 30, 30));
    if (hoverPosition is not { } previewPosition)
        throw new InvalidOperationException("Hover preview could not calculate popup placement.");
    hoverHost.PlacementRect = new Rect(main.FindControl<Grid>("RootGrid")!.PointToClient(previewPosition), new Size(1, 1));
    hoverHost.IsOpen = true;
    Pump();
    hoverPreview.RefreshActualScreenRect();
    if (hoverPreview.CurrentScreenRect is not { Width: > 0, Height: > 0 })
        throw new InvalidOperationException("Hover popup opened without a usable screen rectangle.");
    hoverHost.IsOpen = false;
    hoverPreview.ResetPreviewSurface();
    var tagSearchBox = main.FindControl<TextBox>("TxtTagSearchA")!;
    var tagSearchPresenter = tagSearchBox.GetVisualDescendants()
        .OfType<Avalonia.Controls.Presenters.TextPresenter>()
        .First();
    tagSearchPresenter.PreeditText = "ni";
    var isImeComposing = (bool)typeof(MainWindow)
        .GetMethod("IsImeComposing", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, new object[] { tagSearchBox })!;
    tagSearchPresenter.PreeditText = null;
    if (!isImeComposing)
        throw new InvalidOperationException("Tag search Enter guard did not detect active IME preedit text.");
    Console.WriteLine("PASS hover preview is not a Window and IME preedit state is detected");

    SetFiles(paths);
    var rootGrid = main.FindControl<Grid>("RootGrid")!;
    rootGrid.Focusable = true;
    rootGrid.Focus();
    Pump();
    vm.GetAllTagCounts().Clear();
    vm.GetAllTagCounts().Add(new TagCount { Name = "red", Count = 2 });
    vm.GetAllTagCounts().Add(new TagCount { Name = "blue sky", Count = 1 });
    var callsBeforeFocus = metadataProxy.CoTagCalls;
    tagSearchBox.Focus();
    WaitUntil(() => vm.TagSearchSuggestions.Count == 2);
    if (metadataProxy.CoTagCalls != callsBeforeFocus)
        throw new InvalidOperationException("Opening the Tag input must not query co-tags.");
    WaitForTask(vm.OnTagSearchGotFocusAsync(preserveCurrentMode: true));
    if (metadataProxy.CoTagCalls != callsBeforeFocus)
        throw new InvalidOperationException("Re-focusing an open Tag suggestions popup must not query co-tags.");
    vm.TagSearchText = "bl";
    WaitUntil(() => vm.TagSearchSuggestions.Count == 1 && vm.TagSearchSuggestions[0].Name == "blue sky");
    if (metadataProxy.CoTagCalls != callsBeforeFocus)
        throw new InvalidOperationException("Typing a Tag prefix must use in-memory suggestions only.");
    vm.DismissTagSearchSuggestions();
    rootGrid.Focus();
    Pump();

    // Simulate a completed search-result co-tag snapshot. Filtering this snapshot
    // must remain local and must not issue another repository query.
    typeof(TagSearchEngine)
        .GetField("_coTagMode", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(tagSearchEngine, true);
    var suggestionModeField = typeof(MainWindowViewModel)
        .GetField("_tagSuggestionMode", BindingFlags.Instance | BindingFlags.NonPublic)!;
    suggestionModeField.SetValue(vm, Enum.Parse(suggestionModeField.FieldType, "SearchResultCoTags"));
    typeof(MainWindowViewModel)
        .GetField("_searchResultSuggestionVersion", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(vm, (int)typeof(MainWindowViewModel)
            .GetField("_tagSuggestionVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!);
    messenger.Send(new ImageManager.Core.Messages.TagSearchSuggestionsChangedMessage(
        new List<TagCount>
        {
            new() { Name = "red", Count = 2 },
            new() { Name = "blue sky", Count = 1 }
        }));
    Pump();
    var cachedCoTagCalls = metadataProxy.CoTagCalls;
    vm.CoTagFilterText = "blue";
    WaitUntil(() => vm.TagSearchSuggestions.Count == 1 && vm.TagSearchSuggestions[0].Name == "blue sky");
    if (metadataProxy.CoTagCalls != cachedCoTagCalls)
        throw new InvalidOperationException("Filtering cached co-tags must not issue a repository query.");
    vm.CoTagFilterText = string.Empty;
    WaitUntil(() => vm.TagSearchSuggestions.Select(tag => tag.Name)
        .SequenceEqual(new[] { "red", "blue sky" }));
    if (metadataProxy.CoTagCalls != cachedCoTagCalls)
        throw new InvalidOperationException("Clearing the co-tag filter must use the cached snapshot.");
    typeof(TagSearchEngine)
        .GetField("_coTagMode", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(tagSearchEngine, false);
    vm.GetAllTagCounts().Clear();
    vm.GetAllTagCounts().Add(new TagCount { Name = "cat", Count = 5 });
    vm.TagSearchText = "ca";
    if (vm.IsSuggestionCoTagMode || vm.TagSearchSuggestions.SingleOrDefault()?.Name != "cat")
        throw new InvalidOperationException("Editing the main Tag input did not switch to typed suggestions.");
    vm.DismissTagSearchSuggestions();
    Console.WriteLine("PASS focus co-tags, in-memory typing/filtering, clear restore, and no duplicate queries");
    var dialog = new ImageManager.App.Views.Settings.AppearanceSettingWindow
    {
        DataContext = new AppearanceSettingViewModel("Dark", "Expanded", ImageManager.App.Models.ImageDisplayMode.Paged, (_, _, _) => { })
    };
    _ = dialog.ShowDialog(main);
    var switchTask = vm.TrySetDisplayModeAsync(ImageManager.App.Models.ImageDisplayMode.Continuous);
    WaitForTask(switchTask);
    dialog.Close();
    Pump();
    var actualItems = main.FindControl<ItemsControl>("ContinuousItemsImages")!;
    var actualPanel = (VirtualizingWaterfallPanel)actualItems.ItemsPanelRoot!;
    WaitForImages(actualPanel);
    Console.WriteLine($"MAIN active={vm.ActiveFileList.Count} source={vm.ContinuousImageItemSource?.Count} controlItems={actualItems.ItemCount} visible={actualItems.IsVisible} geometry={actualPanel.GeometryIndex?.Count} bounds={actualPanel.Bounds} realized={actualPanel.RealizedCount}");
    Console.WriteLine($"MAIN images={actualItems.GetVisualDescendants().OfType<Image>().Count()} texts={actualItems.GetVisualDescendants().OfType<TextBlock>().Count()}");
    foreach (var img in actualItems.GetVisualDescendants().OfType<Image>().Take(3))
        Console.WriteLine($"IMAGE source={img.Source?.Size} bounds={img.Bounds} desired={img.DesiredSize} visible={img.IsEffectivelyVisible}");
    if (actualPanel.RealizedCount == 0)
        throw new InvalidOperationException("Real MainWindow continuous switch is blank.");
    if (actualPanel.RealizedCount >= 200)
        throw new InvalidOperationException("Continuous navigation realizes more than a viewport of containers.");
    if (!actualItems.GetVisualDescendants().OfType<Image>().Any(i => i.Source != null && i.Bounds.Width > 0 && i.Bounds.Height > 0))
        throw new InvalidOperationException("Continuous thumbnails have no visible decoded image.");

    // Folder/filter updates discard the old snapshot before refreshing the display.
    typeof(MainWindowViewModel).GetMethod("InvalidateContinuousDisplayGeometry", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
    var refresh = vm.RefreshImageDisplayAsync(0);
    WaitForTask(refresh);
    WaitForImages(actualPanel);
    Console.WriteLine($"REFRESH source={vm.ContinuousImageItemSource?.Count} geometry={actualPanel.GeometryIndex?.Count} realized={actualPanel.RealizedCount}");
    if (actualPanel.RealizedCount == 0)
        throw new InvalidOperationException("Continuous folder/filter refresh leaves the window blank.");

    foreach (var mode in new[] { "None", "Vertical", "Horizontal" })
    {
        vm.WaterfallMode = mode;
        WaitUntil(() => !vm.IsContinuousGeometryBuilding && actualPanel.GeometryIndex?.Options.Mode.ToString() == mode);
        WaitForImages(actualPanel);
        foreach (var index in new[] { 5000, 9999, 0 })
        {
            actualPanel.ScrollToIndex(index);
            Pump();
            WaitForImages(actualPanel);
            if (actualPanel.RealizedCount >= 200 || vm.ContinuousImageItemSource!.CachedItemCount >= 200)
                throw new InvalidOperationException("Scrolling materializes the full result list.");
            if (index < actualPanel.CurrentVisibleRange.StartIndex || index >= actualPanel.CurrentVisibleRange.EndExclusive)
                throw new InvalidOperationException($"Navigation failed: target={index}, visible={actualPanel.CurrentVisibleRange}");
        }
        Console.WriteLine($"PASS {mode}: first/middle/last viewport contains decoded images; realized={actualPanel.RealizedCount}");
    }

    // Search navigation stores its target as a path. Continuous mode must be
    // able to locate that target before an ImageViewItem is materialized.
    vm.ReplaceSelectedFiles(new[] { paths[5000] });
    typeof(MainWindow).GetMethod("OnScrollToSelected", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(main, null);
    WaitUntil(() => 5000 >= actualPanel.CurrentVisibleRange.StartIndex &&
                    5000 < actualPanel.CurrentVisibleRange.EndExclusive);
    if (!vm.IsFileSelected(paths[5000]))
        throw new InvalidOperationException("Continuous path-based search navigation lost its selected target.");
    Console.WriteLine("PASS continuous path-based search navigation");

    // Scrollbar thumb dragging produces a burst of offset changes. The panel
    // may coalesce intermediate positions, but must realize the final viewport.
    var continuousViewer = main.FindControl<ScrollViewer>("ThumbnailScrollViewer")!;
    var fastDragTargetY = Math.Max(0, continuousViewer.Extent.Height - continuousViewer.Viewport.Height - 1200);
    for (var step = 1; step <= 40; step++)
    {
        continuousViewer.Offset = new Vector(
            continuousViewer.Offset.X,
            fastDragTargetY * step / 40d);
        main.UpdateLayout();
    }

    var expectedFastDragRange = actualPanel.GeometryIndex!.QueryViewport(
        fastDragTargetY,
        continuousViewer.Viewport.Height);
    WaitUntil(() => actualPanel.CurrentVisibleRange == expectedFastDragRange);
    WaitForImages(actualPanel);
    if (actualPanel.RealizedCount >= 200 || vm.ContinuousImageItemSource!.CachedItemCount >= 200)
        throw new InvalidOperationException("Fast scrollbar dragging exceeds the bounded continuous window.");
    Console.WriteLine($"PASS fast scrollbar drag: final={actualPanel.CurrentVisibleRange} realized={actualPanel.RealizedCount}");

    actualPanel.ScrollToIndex(0);
    Pump();
    WaitForImages(actualPanel);
    var geometryWidthBeforeResize = actualPanel.GeometryIndex!.Options.ContainerWidth;
    var sourceBeforeResize = vm.ContinuousImageItemSource!;
    var retainedVisualBeforeResize = actualPanel.GetVisualDescendants().OfType<Border>().First();
    WaitForTask(vm.ReflowContinuousDisplayGeometryAsync(geometryWidthBeforeResize + 240));
    WaitUntil(() => actualPanel.GeometryIndex!.Options.ContainerWidth > geometryWidthBeforeResize + 100);
    WaitForImages(actualPanel);
    if (!ReferenceEquals(sourceBeforeResize, vm.ContinuousImageItemSource) ||
        !actualPanel.GetVisualDescendants().OfType<Border>().Any(border => ReferenceEquals(border, retainedVisualBeforeResize)))
    {
        throw new InvalidOperationException("Continuous resize must retain its source and realized thumbnail visuals.");
    }
    Console.WriteLine($"PASS continuous resize geometry rebuild: {geometryWidthBeforeResize:F0} -> {actualPanel.GeometryIndex!.Options.ContainerWidth:F0}");

    vm.WaterfallMode = "Vertical";
    WaitUntil(() => !vm.IsContinuousGeometryBuilding &&
                    actualPanel.GeometryIndex?.Options.Mode == ImageDisplayLayoutMode.Vertical);
    WaitForImages(actualPanel);

    // A deletion outside the viewport must use the same incremental contract as
    // paged mode: keep the source and already-realized thumbnail controls alive.
    actualPanel.ScrollToIndex(0);
    Pump();
    WaitForImages(actualPanel);
    var sourceBeforeDelete = vm.ContinuousImageItemSource!;
    var retainedVisualBeforeDelete = actualPanel.GetVisualDescendants().OfType<Border>().First();
    var deletedPath = paths[8000];
    WaitForTask(vm.RemoveFilesFromViewAsync(new HashSet<string>([deletedPath], StringComparer.OrdinalIgnoreCase)));
    WaitUntil(() => vm.ContinuousImageItemSource?.Count == paths.Length - 1 &&
                    actualPanel.GeometryIndex?.Count == paths.Length - 1);
    WaitForImages(actualPanel);
    if (!ReferenceEquals(sourceBeforeDelete, vm.ContinuousImageItemSource) ||
        !actualPanel.GetVisualDescendants().OfType<Border>().Any(border => ReferenceEquals(border, retainedVisualBeforeDelete)) ||
        !actualItems.GetVisualDescendants().OfType<Image>().Any(image => image.Source != null))
    {
        throw new InvalidOperationException("Continuous deletion must retain visible containers and decoded thumbnails.");
    }
    Console.WriteLine("PASS continuous deletion retains visible containers and thumbnails");

    var shiftedContainerBeforeDelete = actualPanel.GetVisualChildren()
        .OfType<Control>()
        .First(control => (control.DataContext as ImageViewItem)?.FilePath == paths[2]);
    WaitForTask(vm.RemoveFilesFromViewAsync(new HashSet<string>([paths[1]], StringComparer.OrdinalIgnoreCase)));
    WaitUntil(() => vm.ContinuousImageItemSource?.Count == paths.Length - 2 &&
                    actualPanel.GeometryIndex?.Count == paths.Length - 2);
    WaitForImages(actualPanel);
    if (!actualPanel.GetVisualChildren().Any(control => ReferenceEquals(control, shiftedContainerBeforeDelete)) ||
        (shiftedContainerBeforeDelete.DataContext as ImageViewItem)?.FilePath != paths[2])
    {
        throw new InvalidOperationException("Deleting a visible item must retain and reindex its neighboring containers.");
    }
    var visibleThumbnailImages = actualPanel.GetVisualDescendants()
        .OfType<Image>()
        .Where(image => image.Bounds.Width > 0 && image.Bounds.Height > 0)
        .ToArray();
    if (visibleThumbnailImages.Length == 0 || visibleThumbnailImages.Any(image => image.Source is null))
    {
        throw new InvalidOperationException("Continuous deletion leaves a visible thumbnail card without an image source.");
    }
    var realizedItemControls = actualPanel.GetVisualChildren()
        .OfType<Control>()
        .Where(control => control.DataContext is ImageViewItem)
        .ToArray();
    var realizedPaths = realizedItemControls
        .Select(control => ((ImageViewItem)control.DataContext!).FilePath)
        .ToArray();
    if (realizedItemControls.Length != actualPanel.RealizedCount ||
        realizedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != realizedPaths.Length)
    {
        throw new InvalidOperationException("Continuous deletion leaves orphaned or duplicate realized thumbnail containers.");
    }
    Console.WriteLine("PASS continuous visible deletion reindexes neighboring containers without reset");

    // Drive the real filter -> invalidation -> ShowPage -> continuous refresh chain.
    SetFiles(paths.Take(7));
    WaitForTask(vm.ResetDisplayFilterAsync());
    WaitForImages(actualPanel);
    if (actualItems.ItemCount != 7) throw new InvalidOperationException("Folder/filter replacement did not reach the control.");

    SetFiles(paths.Take(7).Append(Path.Combine(fixtureDirectory, "filtered-out.mp4")));
    var imageFilter = new DisplayFilterOptions("image");
    WaitForTask(vm.ApplyDisplayFilterAsync(imageFilter));
    WaitUntil(() => actualPanel.GeometryIndex?.Count == 7);
    WaitForImages(actualPanel);
    var filteredSourceBeforeDelete = vm.ContinuousImageItemSource;
    var retainedFilteredContainer = actualPanel.GetVisualChildren().OfType<Control>()
        .First(control => (control.DataContext as ImageViewItem)?.FilePath == paths[2]);
    var retainedFilteredItem = (ImageViewItem)retainedFilteredContainer.DataContext!;
    var retainedFilteredBytes = retainedFilteredItem.ThumbnailData;

    WaitForTask(vm.RemoveFilesFromViewAsync(new HashSet<string>([paths[1]], StringComparer.OrdinalIgnoreCase)));
    WaitUntil(() => actualPanel.GeometryIndex?.Count == 6 && actualItems.ItemCount == 6);
    WaitForImages(actualPanel);
    if (vm.DisplayFilter != imageFilter || vm.DisplayFilterSourceCount != 7 ||
        !ReferenceEquals(filteredSourceBeforeDelete, vm.ContinuousImageItemSource) ||
        !actualPanel.GetVisualChildren().Any(control => ReferenceEquals(control, retainedFilteredContainer)) ||
        !ReferenceEquals(retainedFilteredItem, retainedFilteredContainer.DataContext) ||
        retainedFilteredBytes is not { Length: > 0 } ||
        !ReferenceEquals(retainedFilteredBytes, retainedFilteredItem.ThumbnailData))
    {
        throw new InvalidOperationException("Deleting a filtered item must retain continuous containers and thumbnail bytes.");
    }
    Console.WriteLine("PASS filtered continuous deletion retains neighboring containers and thumbnails");

    SetFiles(Array.Empty<string>());
    WaitForTask(vm.ResetDisplayFilterAsync());
    Pump();
    if (actualPanel.RealizedCount != 0) throw new InvalidOperationException("Empty results retain stale images.");
    SetFiles(paths);
    WaitForTask(vm.ResetDisplayFilterAsync());
    WaitForImages(actualPanel);
    page.UseViewportLoading = true;
    page.PageSize = ImagePaging.Max;
    WaitForTask(vm.TrySetDisplayModeAsync(ImageManager.App.Models.ImageDisplayMode.Paged));
    Pump();
    var pagedItems = main.FindControl<ItemsControl>("ItemsImages")!;
    var pagedPanel = (VirtualizingWaterfallPanel)pagedItems.ItemsPanelRoot!;
    if (actualItems.IsVisible || !pagedItems.IsVisible)
        throw new InvalidOperationException("Returning to paged mode failed.");
    WaitForPagedImages();
    if (vm.Images.Count != ImagePaging.Max || pagedPanel.RealizedCount >= ImagePaging.Max / 2 ||
        vm.Images.Any(item => !pagedPanel.PagedRetainedItems.Contains(item) &&
            !pagedPanel.PagedPrefetchItems.Contains(item) && item.ThumbnailData is not null))
        throw new InvalidOperationException("Paged display did not bound controls and bytes to its viewport and prefetch window.");

    var pagedScroll = main.FindControl<ScrollViewer>("ThumbnailScrollViewer")!;
    pagedScroll.Offset = default;
    Pump();
    var wheelPointer = new Avalonia.Input.Pointer(1, PointerType.Mouse, true);
    SendWheel(-1);
    SendWheel(1);
    WaitForNextWheelFrame();
    if (pagedScroll.Offset.Y != 0)
        throw new InvalidOperationException("An opposite wheel event at the top failed to cancel the queued frame.");
    SendWheel(-1);
    SendWheel(-1);
    SendWheel(0, deltaX: 1);
    try { WaitUntil(() => pagedScroll.Offset.Y == 800); }
    catch (TimeoutException exception)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var motion = (SmoothScrollMotion)typeof(MainWindow).GetField("_wheelMotion", fields)!.GetValue(main)!;
        var pending = typeof(MainWindow).GetField("_wheelFramePending", fields)!.GetValue(main);
        throw new InvalidOperationException(
            $"Wheel did not settle at 800 DIP. Offset={pagedScroll.Offset.Y:R}, position={motion.Position:R}, " +
            $"target={motion.Target:R}, velocity={motion.Velocity:R}, needsFrame={motion.NeedsFrame}, pending={pending}.",
            exception);
    }
    SendWheel(-1);
    pagedScroll.Offset = new Vector(0, 250);
    WaitForNextWheelFrame();
    if (pagedScroll.Offset.Y != 250)
        throw new InvalidOperationException("Wheel animation overrode a newer external scroll offset.");
    pagedScroll.Offset = default;
    WaitForPagedImages();
    Console.WriteLine("PASS wheel boundary cancellation, accumulated distance, zero Y input and external offset takeover");

    void SendWheel(double delta, double deltaX = 0) => pagedScroll.RaiseEvent(new PointerWheelEventArgs(
        pagedScroll, wheelPointer, main, default, 0, new PointerPointProperties(), KeyModifiers.None, new Vector(deltaX, delta))
    {
        RoutedEvent = InputElement.PointerWheelChangedEvent
    });

    void WaitForNextWheelFrame()
    {
        var frameObserved = false;
        main.RequestAnimationFrame(_ => frameObserved = true);
        WaitUntil(() => frameObserved);
    }

    var middleIndex = vm.Images.Count / 2;
    if (!pagedPanel.ScrollToIndex(middleIndex))
        throw new InvalidOperationException("Could not navigate to the paged prefetch fixture.");
    WaitUntil(() => pagedPanel.CurrentScrollDirection == ContinuousScrollDirection.Down &&
        pagedPanel.PagedPrefetchItems.FirstOrDefault()?.ThumbnailData is { Length: > 0 });
    var forwardItem = pagedPanel.PagedPrefetchItems[0];
    if (pagedPanel.PagedPrefetchItems.Count > 48 ||
        pagedPanel.PagedPrefetchItems.Any(item => pagedItems.ContainerFromItem(item) is not null) ||
        pagedPanel.RealizedCount != pagedPanel.PagedRetainedItems.Count)
        throw new InvalidOperationException("Forward paged prefetch exceeded its data-only budget.");

    if (!pagedPanel.ScrollToIndex(middleIndex - 24))
        throw new InvalidOperationException("Could not reverse the paged prefetch fixture.");
    WaitUntil(() => pagedPanel.CurrentScrollDirection == ContinuousScrollDirection.Up &&
        pagedPanel.PagedPrefetchItems.FirstOrDefault()?.ThumbnailData is { Length: > 0 } &&
        forwardItem.ThumbnailData is null);
    if (pagedPanel.PagedPrefetchItems.Count > 48 ||
        pagedPanel.PagedPrefetchItems.Any(item => pagedItems.ContainerFromItem(item) is not null))
        throw new InvalidOperationException("Reverse paged prefetch expanded the realized controls.");
    pagedScroll.Offset = default;
    WaitForPagedImages();
    Console.WriteLine("PASS paged data-only prefetch, direction reversal and former prefetch eviction");

    var firstPage = vm.Images.ToHashSet();
    WaitForTask(vm.ShowPageAsync(1));
    WaitForPagedImages();
    if (vm.Images.Count != firstPage.Count || pagedPanel.PagedVisibleItems.Any(firstPage.Contains))
        throw new InvalidOperationException("Replacing a page with the same count must realize its new items.");

    var survivor = pagedPanel.PagedVisibleItems[0];
    var deleted = pagedPanel.PagedVisibleItems[1];
    var survivorContainer = pagedItems.ContainerFromItem(survivor);
    var survivorBytes = survivor.ThumbnailData;
    WaitForTask(vm.RemoveFilesFromViewAsync(new HashSet<string>([deleted.FilePath], StringComparer.OrdinalIgnoreCase)));
    WaitForPagedImages();
    if (vm.Images.Count != ImagePaging.Max || vm.Images.Contains(deleted) ||
        !ReferenceEquals(survivorContainer, pagedItems.ContainerFromItem(survivor)) ||
        !ReferenceEquals(survivorBytes, survivor.ThumbnailData))
        throw new InvalidOperationException("Deletion and same-count refill lost a surviving paged card or its thumbnail.");

    var distantItem = vm.Images[^1];
    if (!pagedPanel.ScrollToIndex(vm.Images.Count - 1))
        throw new InvalidOperationException("Scrolling to an unrealized paged item failed.");
    WaitUntil(() => pagedPanel.PagedVisibleItems.Contains(distantItem) && distantItem.IsLoaded &&
        !pagedPanel.PagedRetainedItems.Contains(survivor) && survivor.ThumbnailData is null);
    Console.WriteLine("PASS paged viewport bounds, same-count replacement, deletion refill, distant navigation and eviction");

    WaitForTask(vm.TrySetDisplayModeAsync(ImageManager.App.Models.ImageDisplayMode.Continuous));
    WaitForImages(actualPanel);

    void WaitForPagedImages()
    {
        WaitUntil(() => pagedPanel.PagedVisibleItems.Count > 1 &&
            pagedPanel.PagedVisibleItems.All(item => item.IsLoaded && item.ThumbnailData is { Length: > 0 }));
        Pump();
    }

    // Deleting the final result follows the incremental continuous-delete path,
    // unlike filter replacement. It must remove every realized card immediately.
    SetFiles(new[] { paths[0] });
    WaitForTask(vm.ResetDisplayFilterAsync());
    WaitUntil(() => vm.ContinuousImageItemSource?.Count == 1 &&
                    actualPanel.GeometryIndex?.Count == 1);
    WaitForImages(actualPanel);
    WaitForTask(vm.RemoveFilesFromViewAsync(new HashSet<string>([paths[0]], StringComparer.OrdinalIgnoreCase)));
    WaitUntil(() => vm.ContinuousImageItemSource?.Count == 0 &&
                    actualPanel.GeometryIndex?.Count == 0);
    Pump();
    if (actualItems.ItemCount != 0 || actualPanel.RealizedCount != 0 ||
        actualItems.GetVisualDescendants().Any(control => control.DataContext is ImageViewItem))
    {
        throw new InvalidOperationException("Deleting the final continuous result leaves stale thumbnail cards.");
    }
    Console.WriteLine("PASS deleting final continuous result clears all realized cards");

    SetFiles(paths);
    WaitForTask(vm.ResetDisplayFilterAsync());
    WaitForImages(actualPanel);
    var capturePath = Path.Combine(Path.GetTempPath(), "ImageManager-continuous-regression.png");
    using (var capture = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)main.Bounds.Width, (int)main.Bounds.Height)))
    {
        capture.Render(main);
        capture.Save(capturePath);
    }
    using var rendered = SkiaSharp.SKBitmap.Decode(capturePath);
    if (rendered.Pixels.Count(p => p.Red > 200 && p.Green < 40 && p.Blue < 40) < 1000)
        throw new InvalidOperationException("Window render contains no visible fixture images.");
    Console.WriteLine($"PASS filter replacement, empty recovery, paged roundtrip and rendered image pixels. Capture: {capturePath}");
}
finally
{
    typeof(MainWindowViewModel).GetMethod("InvalidateContinuousDisplayGeometry", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
    page.Dispose();
    main.Close();
    // Only files generated in this test's unique temporary directory are removed.
    try { Directory.Delete(fixtureDirectory, recursive: true); } catch (IOException) { }
}

void Pump() { main.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
void WaitUntil(Func<bool> condition)
{
    var timer = System.Diagnostics.Stopwatch.StartNew();
    do
    {
        Pump();
        if (condition()) return;
        Thread.Sleep(10);
    } while (timer.Elapsed < TimeSpan.FromSeconds(15));
    throw new TimeoutException("Continuous UI did not reach the expected state.");
}
void WaitForTask(Task task) { WaitUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); Pump(); }
void WaitForImages(VirtualizingWaterfallPanel actualPanel)
{
    WaitUntil(() => actualPanel.CurrentVisibleRange.Count > 0 &&
        vm.ContinuousImageItemSource!.GetCachedItems(actualPanel.CurrentVisibleRange) is { Count: > 0 } visible &&
        visible.All(i => i.IsLoaded));
    Pump();
    if (!actualPanel.GetVisualDescendants().OfType<Image>().Any(i => i.Source != null && i.Bounds.Width > 0 && i.Bounds.Height > 0))
        throw new InvalidOperationException("No decoded thumbnail has visible layout bounds.");
}
void SetFiles(IEnumerable<string> files) => typeof(MainWindowViewModel)
    .GetField("_allFiles", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, files.ToList());

void VerifyThumbnailCardRecycling()
{
    var fixtures = Enumerable.Range(0, 12).Select(index => new ImageViewItem
    {
        FilePath = Path.Combine(fixtureDirectory, $"recycled-{index}.png"),
        FileName = $"recycled-{index}.png",
        Width = 100,
        Height = 100
    }).ToArray();
    var firstItem = fixtures[0];
    firstItem.ThumbnailData = encoded.ToArray();
    firstItem.IsLoaded = true;
    firstItem.IsSelected = true;
    var lastItem = fixtures[^1];
    lastItem.FilePath = Path.Combine(fixtureDirectory, "recycled-audio.mp3");
    lastItem.FileName = "recycled-audio.mp3";
    using var audioCover = new SkiaSharp.SKBitmap(47, 53);
    using (var canvas = new SkiaSharp.SKCanvas(audioCover)) canvas.Clear(SkiaSharp.SKColors.Blue);
    using var coverBytes = audioCover.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
    lastItem.ThumbnailData = coverBytes.ToArray();
    lastItem.IsLoaded = true;

    var panel = new VirtualizingWaterfallPanel
    {
        ViewportBuffer = 0,
        GeometryIndex = ImageDisplayGeometryIndex.Build(
            fixtures.Select(item => new ImageDisplayItemSize(item.Width, item.Height)).ToArray(),
            new ImageDisplayGeometryOptions(ImageDisplayLayoutMode.None, 400, 300, 1))
    };
    var gallery = new ThumbnailItemsControl
    {
        ItemsSource = fixtures,
        ItemTemplate = (IDataTemplate)main.Resources["ThumbnailItemTemplate"]!,
        ItemsPanel = new FuncTemplate<Panel?>(() => panel)
    };
    var window = new Window
    {
        Width = 400, Height = 185, DataContext = vm, ShowInTaskbar = false,
        Content = new ScrollViewer { Content = gallery }
    };

    try
    {
        window.Show();
        WaitForCard(() => panel.RealizedCount == 1 && gallery.ContainerFromIndex(0) is Decorator { Child: Border });
        var container = (Decorator)gallery.ContainerFromIndex(0)!;
        var card = (Border)container.Child!;
        var contextMenu = card.ContextMenu!;
        var tagItem = contextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "编辑 Tag"));
        var explorerItem = contextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "在资源管理器中打开"));
        var image = card.GetVisualDescendants().OfType<AsyncThumbnailImage>().Single();
        var selectionBorder = card.GetVisualDescendants().OfType<Border>().Single(border =>
            border.BorderBrush is ISolidColorBrush brush && brush.Color == Color.Parse("#FF3D7FFF"));
        WaitForCard(() => image.Source is Avalonia.Media.Imaging.Bitmap first && first.PixelSize.Width == 100);
        contextMenu.Open(card);
        WaitForCard(() => contextMenu.IsOpen && ReferenceEquals(tagItem.DataContext, firstItem));
        if (!selectionBorder.IsVisible || !tagItem.IsVisible)
            throw new InvalidOperationException("The first thumbnail did not show its selection and image commands.");

        var clearedWhileRecycling = false;
        card.DataContextChanged += (_, _) =>
        {
            if (card.DataContext is null && image.Source is null)
                clearedWhileRecycling = true;
        };
        if (!panel.ScrollToIndex(fixtures.Length - 1) || contextMenu.IsOpen)
            throw new InvalidOperationException("Recycling a card did not close its open context menu.");
        if (!ReferenceEquals(container, gallery.ContainerFromIndex(fixtures.Length - 1)) ||
            !ReferenceEquals(card, container.Child) || !ReferenceEquals(contextMenu, card.ContextMenu) ||
            !ReferenceEquals(tagItem, contextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "编辑 Tag"))) ||
            !ReferenceEquals(image, card.GetVisualDescendants().OfType<AsyncThumbnailImage>().Single()) ||
            !clearedWhileRecycling || image.Source is Avalonia.Media.Imaging.Bitmap stale && stale.PixelSize.Width == 100)
            throw new InvalidOperationException("The thumbnail template was rebuilt or retained the previous item.");

        WaitForCard(() => image.Source is Avalonia.Media.Imaging.Bitmap cover && cover.PixelSize.Width == 47);
        contextMenu.Open(card);
        WaitForCard(() => contextMenu.IsOpen && ReferenceEquals(explorerItem.DataContext, lastItem));
        if (!ReferenceEquals(container.DataContext, lastItem) || !ReferenceEquals(card.DataContext, lastItem) ||
            !ReferenceEquals(contextMenu.DataContext, lastItem) || selectionBorder.IsVisible || tagItem.IsVisible)
            throw new InvalidOperationException("The recycled template did not bind the music item's image, selection and menu state.");

        var getContextItem = typeof(MainWindow).GetMethod("GetCtxItem", BindingFlags.NonPublic | BindingFlags.Static)!;
        ImageViewItem? clickedItem = null;
        explorerItem.Click += (sender, _) => clickedItem = (ImageViewItem?)getContextItem.Invoke(null, [sender]);
        explorerItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (!ReferenceEquals(clickedItem, lastItem))
            throw new InvalidOperationException("The recycled menu action targeted the previous item.");
        contextMenu.Close();

        gallery.ItemTemplate = new FuncDataTemplate<ImageViewItem>((_, _) => new TextBlock { Text = "replacement template" });
        WaitForCard(() => gallery.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "replacement template"));
    }
    finally
    {
        window.Close();
    }
    Console.WriteLine("PASS gallery recycles card, menu, selection and async thumbnail across items and rebuilds on template change");

    void WaitForCard(Func<bool> condition)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            Thread.Sleep(10);
        } while (timer.Elapsed < TimeSpan.FromSeconds(5));
        throw new TimeoutException("The recycled gallery card did not reach its expected state.");
    }
}

void ComparePagedLayouts()
{
    var template = (IDataTemplate)main.Resources["ThumbnailItemTemplate"]!;
    var thumbnailBytes = encoded.ToArray();
    var previousMode = vm.WaterfallMode;
    var previousPadding = vm.KeepPadding;
    var previousAspectRatio = vm.AppSettings.ThumbnailAspectRatio;
    try
    {
        foreach (var mode in new[] { "None", "Vertical", "Horizontal" })
        foreach (var padding in new[] { false, true })
        {
            vm.WaterfallMode = mode;
            vm.KeepPadding = padding;
            vm.AppSettings.ThumbnailAspectRatio = mode == "None" ? 1.5 : previousAspectRatio;
            var legacy = new SmartWaterfallPanel { Mode = mode, ColumnWidth = vm.ThumbnailBaseWidth };
            var virtualized = new VirtualizingWaterfallPanel
            {
                IsPaged = true,
                Mode = mode,
                ColumnWidth = vm.ThumbnailBaseWidth,
                GridThumbnailHeight = vm.GridThumbnailHeight,
                KeepPadding = padding
            };
            var oldLayout = MeasureLayout(legacy);
            var newLayout = MeasureLayout(virtualized);
            if (oldLayout.Count != 400 || newLayout.Count >= 200)
                throw new InvalidOperationException($"{mode}/{padding} did not reduce realized controls: {oldLayout.Count} -> {newLayout.Count}.");
            if (newLayout.Cards.Count < 2 || !newLayout.Cards.ContainsKey(0) || !newLayout.Cards.ContainsKey(1))
                throw new InvalidOperationException($"{mode}/{padding} did not arrange the long and unknown-size fixture cards.");
            if (mode == "None" && !SameRect(oldLayout.LastGridCard, newLayout.LastGridCard))
                throw new InvalidOperationException($"Grid card bounds drifted at the end of the page: {oldLayout.LastGridCard} -> {newLayout.LastGridCard}.");
            foreach (var (index, actual) in newLayout.Cards)
            {
                if (!oldLayout.Cards.TryGetValue(index, out var expected) ||
                    !SameRect(actual.Card, expected.Card) || !SameRect(actual.Image, expected.Image))
                    throw new InvalidOperationException($"{mode}/{padding} card {index} changed: {expected} -> {actual}.");
            }
            Console.WriteLine($"PASS {mode} padding={padding} card and image bounds, controls {oldLayout.Count}->{newLayout.Count}, first layout {oldLayout.Milliseconds:F1}->{newLayout.Milliseconds:F1} ms");
        }
    }
    finally
    {
        vm.WaterfallMode = previousMode;
        vm.KeepPadding = previousPadding;
        vm.AppSettings.ThumbnailAspectRatio = previousAspectRatio;
    }

    (int Count, double Milliseconds, Rect LastGridCard, Dictionary<int, (Rect Card, Rect Image, double ImageWidth, double ItemWidth)> Cards) MeasureLayout(Panel panel)
    {
        var fixtures = Enumerable.Range(0, 400).Select(index => new ImageViewItem
        {
            FileName = index.ToString(),
            Width = index switch { 0 => 10, 1 => 0, _ => 100 + index % 100 },
            Height = index switch { 0 => 1000, 1 => 0, _ => 100 },
            ThumbnailData = index < 3 ? thumbnailBytes : null,
            IsLoaded = index < 3
        }).ToArray();
        ItemsControl gallery = panel is SmartWaterfallPanel ? new ItemsControl() : new ThumbnailItemsControl();
        gallery.ItemsSource = fixtures;
        gallery.ItemTemplate = template;
        gallery.ItemsPanel = new FuncTemplate<Panel?>(() => panel);
        // The folder section precedes the gallery inside the same viewer.
        var content = new StackPanel { Children = { new Border { Height = 130 }, gallery } };
        var scroll = new ScrollViewer { Content = content };
        var layoutWindow = new Window
        {
            Width = 920, Height = 640, Content = scroll, DataContext = vm, ShowInTaskbar = false
        };
        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            layoutWindow.Show();
            layoutWindow.UpdateLayout();
            timer.Stop();
            var decodeTimer = System.Diagnostics.Stopwatch.StartNew();
            while (decodeTimer.Elapsed < TimeSpan.FromSeconds(5))
            {
                Dispatcher.UIThread.RunJobs();
                layoutWindow.UpdateLayout();
                if (gallery.ContainerFromIndex(2)?.GetVisualDescendants().OfType<AsyncThumbnailImage>()
                    .FirstOrDefault() is { Source: not null, Bounds.Width: > 0, Bounds.Height: > 0 })
                    break;
                Thread.Sleep(10);
            }
            if (gallery.ContainerFromIndex(2)?.GetVisualDescendants().OfType<AsyncThumbnailImage>()
                .FirstOrDefault() is not { Source: not null, Bounds.Width: > 0, Bounds.Height: > 0 })
                throw new InvalidOperationException($"{panel.GetType().Name} did not arrange the decoded image.");
            for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); layoutWindow.UpdateLayout(); }
            var cards = new Dictionary<int, (Rect Card, Rect Image, double ImageWidth, double ItemWidth)>();
            foreach (var child in panel.Children)
            {
                if (child.DataContext is not ImageViewItem item ||
                    !child.IsEffectivelyVisible ||
                    child.TranslatePoint(default, scroll) is not { } point ||
                    point.Y >= scroll.Viewport.Height ||
                    child.GetVisualDescendants().OfType<AsyncThumbnailImage>().FirstOrDefault() is not { } image)
                    continue;
                cards.Add(int.Parse(item.FileName), (child.Bounds, image.Bounds, image.Width, SmartWaterfallPanel.GetItemWidth(image)));
            }
            var realizedCount = panel.Children.Count;
            var lastGridCard = default(Rect);
            var last = fixtures.Length - 1;
            if (panel is VirtualizingWaterfallPanel paged)
            {
                var allCards = paged.GetPagedItemsIntersecting(new Rect(0, 0, paged.Bounds.Width, paged.Bounds.Height));
                if (paged.PagedRetainedItems.Contains(fixtures[last]) || !allCards.SequenceEqual(fixtures))
                    throw new InvalidOperationException("Paged box selection missed unrealized cards.");
                if (paged.Mode == "Vertical")
                {
                    var column = cards[1].Card;
                    var beforeColumn = new Rect(column.X - 0.25, column.Y + 1, 0.25, 1);
                    var inColumn = new Rect(column.X, column.Y + 1, 0.1, 1);
                    var singleAxis = new Rect(column.X + 1, column.Y + 1, 0, 10);
                    if (paged.GetPagedItemsIntersecting(beforeColumn).Contains(fixtures[1]) ||
                        !paged.GetPagedItemsIntersecting(inColumn).Contains(fixtures[1]) ||
                        !paged.GetPagedItemsIntersecting(singleAxis).Contains(fixtures[1]))
                        throw new InvalidOperationException("Paged selection did not match the rounded column edge.");
                }
                if (!paged.ScrollToIndex(last))
                    throw new InvalidOperationException("Could not realize the last card with folders above the gallery.");
                layoutWindow.UpdateLayout();
                if (gallery.ContainerFromIndex(last) is not { } lastCard ||
                    lastCard.TranslatePoint(default, scroll) is not { } lastCardPosition ||
                    lastCardPosition.Y >= scroll.Viewport.Height || lastCardPosition.Y + lastCard.Bounds.Height <= 0)
                    throw new InvalidOperationException("The selected card stayed outside the viewer with folders above it.");
                if (paged.Mode == "None")
                    lastGridCard = lastCard.Bounds;
                if (paged.Mode == "Vertical")
                {
                    var contentY = paged.TranslatePoint(default, scroll)!.Value.Y + scroll.Offset.Y;
                    var longCardHeight = cards[0].Card.Height;
                    var expectedLongCardOffset = Math.Clamp(contentY + (longCardHeight - scroll.Viewport.Height) / 2,
                        0, scroll.Extent.Height - scroll.Viewport.Height);
                    if (!paged.ScrollToIndex(0) || Math.Abs(scroll.Offset.Y - expectedLongCardOffset) > 1)
                        throw new InvalidOperationException($"Paged search must center a tall card: {expectedLongCardOffset} vs {scroll.Offset.Y}.");
                }
            }
            else if (panel is SmartWaterfallPanel { Mode: "None" })
            {
                lastGridCard = gallery.ContainerFromIndex(last)!.Bounds;
            }
            return (realizedCount, timer.Elapsed.TotalMilliseconds, lastGridCard, cards);
        }
        finally
        {
            layoutWindow.Close();
        }
    }

    static bool SameRect(Rect a, Rect b) => Math.Abs(a.X - b.X) <= 1 &&
        Math.Abs(a.Y - b.Y) <= 1 && Math.Abs(a.Width - b.Width) <= 1 && Math.Abs(a.Height - b.Height) <= 1;
}

public class TestMainWindow : MainWindow
{
    protected override void OnLoaded(RoutedEventArgs e) { }
    protected override void OnClosing(WindowClosingEventArgs e) { }
}
public class MetadataProxy : DispatchProxy
{
    public TaskCompletionSource<List<TagCount>>? CoTagGate { get; set; }
    public int GatedCoTagCall { get; set; }
    public int CoTagCalls { get; private set; }

    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "GetSortMetadataByFolderAsync" => Task.FromResult<IReadOnlyList<ImageSortMetadata>>(Array.Empty<ImageSortMetadata>()),
        "GetSortMetadataByPathsAsync" => Task.FromResult<IReadOnlyList<ImageSortMetadata>>(Array.Empty<ImageSortMetadata>()),
        "GetDimensionsByPathsAsync" => Task.FromResult(((List<string>)args![0]!).ToDictionary(p => p, _ => (100, 100))),
        "GetTagMapByPathsAsync" => Task.FromResult(new Dictionary<string, List<string>>()),
        "GetCoOccurringTagsAsync" => GetCoTags((string?)args![2]),
        _ => throw new NotSupportedException(method.Name)
    };

    private Task<List<TagCount>> GetCoTags(string? filter)
    {
        CoTagCalls++;
        if (CoTagCalls == GatedCoTagCall && CoTagGate is not null)
            return CoTagGate.Task;

        var all = new List<TagCount>
        {
            new() { Name = "red", Count = 2 },
            new() { Name = "blue sky", Count = 1 }
        };
        return Task.FromResult(string.IsNullOrWhiteSpace(filter)
            ? all
            : all.Where(tag => tag.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList());
    }
}
