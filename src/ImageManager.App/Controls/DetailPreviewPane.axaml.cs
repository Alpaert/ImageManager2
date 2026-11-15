using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ImageManager.App.ViewModels;
using ImageManager.Common.Constants;

namespace ImageManager.App.Controls;

internal enum DetailPaneAction
{
    Collapse, Open, EditTags, RegionSearch, SimilarContent, Perceptual, Semantic, Atmosphere, Color, CopyPath,
    Explorer, CopyToFolder, MoveToFolder, RegenerateThumbnail, RetryPreview, RetrySave, RefreshFolder
}

public partial class DetailPreviewPane : UserControl
{
    private DetailPaneViewModel? _model;
    private string? _path;
    private bool _isFolder;
    private IReadOnlyList<string>? _tags;
    private bool _tagsExpanded;
    private bool _measureTags;
    private double _lastTagWidth;
    private double _lastRenderScaling;
    private MenuFlyout? _moreMenu;
    private readonly DispatcherTimer _decodeWidthTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private double _lastPreviewPixels;

    internal event Action<DetailPaneAction, string?>? ActionRequested;

    public DetailPreviewPane()
    {
        InitializeComponent();
        LayoutUpdated += (_, _) =>
        {
            MeasureTags();
            ScheduleDecodeWidth();
        };
        _decodeWidthTimer.Tick += (_, _) =>
        {
            _decodeWidthTimer.Stop();
            if (_model is { IsOpen: true } model && IsEffectivelyVisible)
            {
                var pixels = _lastPreviewPixels;
                model.SetPreviewDecodeWidth(pixels <= 256 ? 256 : pixels <= 512 ? 512 : pixels <= 1024 ? 1024 : 1536);
            }
        };
        DetailThumbnail.PropertyChanged += (_, e) =>
        {
            if (e.Property == AsyncThumbnailImage.HasDecodeErrorProperty)
                UpdatePreviewFeedback();
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.IsAttachedToVisualTree())
            SetModel(DataContext as DetailPaneViewModel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SetModel(DataContext as DetailPaneViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _decodeWidthTimer.Stop();
        _lastPreviewPixels = 0;
        SetModel(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void ScheduleDecodeWidth()
    {
        if (_model is not { IsOpen: true } || !IsEffectivelyVisible) return;
        // The art control is hidden in the empty state, so measure the pane
        // minus the content margins until the actual preview bounds are known.
        var width = _model.HasItem && DetailArt.Bounds.Width > 0 ? DetailArt.Bounds.Width : Bounds.Width - 33;
        var pixels = width * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        if (pixels <= 0 || Math.Abs(pixels - _lastPreviewPixels) < 0.1) return;
        _lastPreviewPixels = pixels;
        _decodeWidthTimer.Stop();
        _decodeWidthTimer.Start();
    }

    internal void ApplySize(double width, double clientHeight, bool overlay)
    {
        Width = overlay ? width : double.NaN;
        HorizontalAlignment = overlay ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Stretch;
        PaneSurface.BorderThickness = overlay ? new Thickness(1, 0, 0, 0) : new Thickness(0);
        DetailArt.Height = Math.Clamp(clientHeight * 0.26, 160, 240);
    }

    private void SetModel(DetailPaneViewModel? model)
    {
        if (ReferenceEquals(_model, model)) return;
        if (_model is not null)
            _model.PropertyChanged -= ModelChanged;
        _model = model;
        if (model is not null)
            model.PropertyChanged += ModelChanged;
        UpdateContent();
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e) => UpdateContent();

    private void UpdateContent()
    {
        var path = _model?.CurrentPath;
        var isFolder = _model?.IsFolder == true;
        if (_isFolder != isFolder || !StringComparer.OrdinalIgnoreCase.Equals(_path, path))
        {
            _isFolder = isFolder;
            _path = path;
            _tagsExpanded = false;
            _measureTags = true;
            SetSimilarExpanded(false);
            _moreMenu?.Hide();
            DetailScrollViewer.Offset = default;
            TagScroller.Offset = default;
        }

        if (_model is not { IsOpen: true })
        {
            _moreMenu?.Hide();
            _decodeWidthTimer.Stop();
            _lastPreviewPixels = 0;
        }

        var isImage = !isFolder && path != null && FileTypeConstants.IsImageFile(path);
        DetailImageSearchActions.IsVisible = isImage;
        ToggleSimilar.IsVisible = isImage;
        DetailOpen.Content = DetailArtHint.Text = isFolder ? "打开文件夹" : isImage ? "查看大图" : "播放";
        DetailPlaceholder.Text = isFolder ? "暂无文件夹封面" : _model?.IsMusic == true ? "暂无封面" : "暂无预览";
        AutomationProperties.SetName(DetailArt, DetailArtHint.Text);
        UpdatePreviewFeedback();
        if (_model is { } model)
        {
            DetailMetaLine.Text = model.DimensionsText == "未知"
                ? model.FormatText
                : $"{model.FormatText} · {model.DimensionsText}";
        }

        var tags = _model?.Tags;
        if (ReferenceEquals(_tags, tags))
            return;

        _tags = tags;
        TagChips.Children.Clear();
        if (tags is not null)
        {
            foreach (var tag in tags)
            {
                var chip = new Border { Classes = { "detail-tag" }, Child = new TextBlock { Text = tag } };
                ToolTip.SetTip(chip, tag);
                TagChips.Children.Add(chip);
            }
        }
        NoTags.IsVisible = TagChips.Children.Count == 0;
        TagScroller.IsVisible = !NoTags.IsVisible;
        ToggleTags.IsVisible = false;
        _measureTags = true;
    }

    private void UpdatePreviewFeedback()
    {
        var model = _model;
        var decodeFailed = model is { HasItem: true, IsFolder: false } && DetailThumbnail.HasDecodeError;
        PreviewFeedback.Text = decodeFailed ? "无法解码预览" : model?.PreviewStatusText;
        PreviewFeedback.IsVisible = !string.IsNullOrEmpty(PreviewFeedback.Text);
        RetryPreviewButton.IsVisible = decodeFailed || model?.CanRetryPreview == true;
        ReadingDetails.Text = model?.IsFolder == true ? "正在统计文件夹…" : "正在读取文件信息…";
    }

    private void MeasureTags()
    {
        var width = TagScroller.Bounds.Width;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        if (width <= 0 || NoTags.IsVisible || !IsEffectivelyVisible ||
            (!_measureTags && width.Equals(_lastTagWidth) && scaling.Equals(_lastRenderScaling)))
            return;

        _measureTags = false;
        _lastTagWidth = width;
        _lastRenderScaling = scaling;

        var shown = 0;
        var row = 1;
        var used = 0d;
        foreach (var chip in TagChips.Children)
        {
            chip.MaxWidth = width;
            chip.IsVisible = true;
            chip.Measure(new Size(width, double.PositiveInfinity));
            var nextWidth = chip.DesiredSize.Width;
            if (used > 0 && used + TagChips.ItemSpacing + nextWidth > width)
            {
                row++;
                used = 0;
            }
            used = used > 0 ? used + TagChips.ItemSpacing + nextWidth : nextWidth;
            chip.IsVisible = _tagsExpanded || row <= 2;
            if (row <= 2)
                shown++;
        }

        var hidden = TagChips.Children.Count - shown;
        TagScroller.VerticalScrollBarVisibility = _tagsExpanded ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        ToggleTags.IsVisible = _tagsExpanded || hidden > 0;
        ToggleTags.Content = _tagsExpanded ? "收起标签" : $"展开其余 {hidden} 个";
    }

    private void ToggleTags_Click(object? sender, RoutedEventArgs e)
    {
        _tagsExpanded = !_tagsExpanded;
        if (!_tagsExpanded)
            TagScroller.Offset = default;
        _measureTags = true;
        MeasureTags();
    }

    private void ToggleSimilar_Click(object? sender, RoutedEventArgs e) => SetSimilarExpanded(!SimilarOptions.IsVisible);

    private void SetSimilarExpanded(bool expanded)
    {
        SimilarOptions.IsVisible = expanded;
        SimilarChevron.Text = expanded ? "⌃" : "⌄";
    }

    private void Action_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string tag } && Enum.TryParse<DetailPaneAction>(tag, out var action))
        {
            if (action == DetailPaneAction.RetryPreview && DetailThumbnail.HasDecodeError)
                action = DetailPaneAction.RegenerateThumbnail;
            ActionRequested?.Invoke(action, _model?.CurrentPath);
        }
    }

    private void MoreActions_Click(object? sender, RoutedEventArgs e)
    {
        if (_model?.CurrentPath is not { } path)
            return;

        var menu = new MenuFlyout();
        AddItem("在资源管理器中显示", DetailPaneAction.Explorer);
        if (_model.IsFolder)
        {
            AddItem("刷新文件夹统计", DetailPaneAction.RefreshFolder);
        }
        else
        {
            AddItem("复制到文件夹…", DetailPaneAction.CopyToFolder);
            AddItem("移动到文件夹…", DetailPaneAction.MoveToFolder);
            AddItem(FileTypeConstants.IsMusicFile(path) ? "重新读取音乐封面" : "重新生成缩略图",
                DetailPaneAction.RegenerateThumbnail);
        }
        _moreMenu = menu;
        menu.ShowAt(MoreActions);

        void AddItem(string header, DetailPaneAction action)
        {
            var item = new MenuItem { Header = header, Classes = { "detail-pane-menu-item" } };
            item.Click += (_, _) => ActionRequested?.Invoke(action, path);
            menu.Items.Add(item);
        }
    }
}
