using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ImageManager.App.ViewModels;

namespace ImageManager.App.Controls;

public sealed class BreadcrumbNavigationEventArgs(string path) : EventArgs
{
    public string Path { get; } = path;
}

public sealed class ResponsiveBreadcrumbBar : Panel
{
    public static readonly StyledProperty<IReadOnlyList<FolderTreeNode>?> ItemsSourceProperty =
        AvaloniaProperty.Register<ResponsiveBreadcrumbBar, IReadOnlyList<FolderTreeNode>?>(nameof(ItemsSource));

    private const double RowHeight = 28;
    private const double OverflowWidth = 28;
    private const double SeparatorWidth = 16;

    private static readonly FuncDataTemplate<string> MenuHeaderTemplate = new((name, _) => new TextBlock
    {
        Text = name,
        MaxWidth = 320,
        TextTrimming = TextTrimming.CharacterEllipsis
    });

    private readonly MenuFlyout _menu = new();
    private readonly Button _overflowButton;
    private readonly Border _overflowSlot;
    private readonly TextBlock _overflowSeparator;
    private readonly List<Crumb> _crumbs = [];
    private int _hiddenCount;

    public ResponsiveBreadcrumbBar()
    {
        ClipToBounds = true;
        _overflowButton = CreateButton();
        _overflowButton.Name = "BreadcrumbOverflow";
        _overflowButton.Width = OverflowWidth;
        _overflowButton.FontSize = 18;
        _overflowButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        _overflowButton.Padding = default;
        _overflowButton.Content = "…";
        _overflowButton.Flyout = _menu;
        AutomationProperties.SetName(_overflowButton, "显示上级文件夹");
        ToolTip.SetTip(_overflowButton, "显示上级文件夹");

        _overflowSlot = new Border { Child = _overflowButton, IsVisible = false };
        _overflowSeparator = CreateSeparator();
        _overflowSeparator.IsVisible = false;
        Children.Add(_overflowSlot);
        Children.Add(_overflowSeparator);
    }

    public IReadOnlyList<FolderTreeNode>? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public event EventHandler<BreadcrumbNavigationEventArgs>? NavigateRequested;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty)
            RebuildCrumbs();
        else if (change.Property == IsVisibleProperty && !IsVisible)
            _menu.Hide();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _menu.Hide();
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = 0d;
        var unbounded = new Size(double.PositiveInfinity, RowHeight);
        foreach (var crumb in _crumbs)
        {
            // The button retains its natural size even when its slot is hidden.
            crumb.Button.Measure(unbounded);
            width += crumb.Width;
        }

        return new Size(Math.Min(width, availableSize.Width), _crumbs.Count == 0 ? 0 : RowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var firstVisible = GetFirstVisibleIndex(finalSize.Width);
        var previousFirstVisible = _hiddenCount;
        SetHiddenCount(firstVisible);
        var showOverflow = firstVisible > 0;
        var overflowHadFocus = _overflowButton.IsKeyboardFocusWithin;
        _overflowSlot.IsVisible = showOverflow;
        _overflowSeparator.IsVisible = showOverflow;

        var left = 0d;
        if (showOverflow)
        {
            var overflowWidth = Math.Min(OverflowWidth, finalSize.Width / 2);
            var separatorWidth = Math.Min(SeparatorWidth, Math.Max(0, finalSize.Width - overflowWidth - OverflowWidth));
            ArrangeChild(_overflowSlot, overflowWidth);
            ArrangeChild(_overflowSeparator, separatorWidth);
        }

        for (var i = 0; i < _crumbs.Count; i++)
        {
            var crumb = _crumbs[i];
            var visible = i >= firstVisible;
            if (!visible && crumb.Button.IsKeyboardFocusWithin)
                _overflowButton.Focus(NavigationMethod.Directional);
            crumb.Slot.IsVisible = visible;
            if (crumb.Separator is { } separator)
                separator.IsVisible = visible;
            if (!visible)
                continue;

            ArrangeChild(crumb.Slot, Math.Min(crumb.Button.DesiredSize.Width, Math.Max(0, finalSize.Width - left)));
            if (crumb.Separator is { } visibleSeparator)
                ArrangeChild(visibleSeparator, SeparatorWidth);
        }

        if (!showOverflow && overflowHadFocus && previousFirstVisible > 0)
            _crumbs[previousFirstVisible - 1].Button.Focus(NavigationMethod.Directional);

        return finalSize;

        void ArrangeChild(Control child, double width)
        {
            child.Measure(new Size(double.PositiveInfinity, RowHeight));
            child.Arrange(new Rect(left, 0, width, finalSize.Height));
            left += width;
        }
    }

    private int GetFirstVisibleIndex(double availableWidth)
    {
        if (_crumbs.Count <= 1)
            return 0;

        var fullWidth = 0d;
        foreach (var crumb in _crumbs)
            fullWidth += crumb.Width;
        if (fullWidth <= availableWidth)
            return 0;

        var firstVisible = _crumbs.Count - 1;
        var width = OverflowWidth + SeparatorWidth + _crumbs[firstVisible].Width;
        while (firstVisible > 0 && width + _crumbs[firstVisible - 1].Width <= availableWidth)
        {
            firstVisible--;
            width += _crumbs[firstVisible].Width;
        }

        return firstVisible;
    }

    private void RebuildCrumbs()
    {
        SetHiddenCount(0);
        foreach (var crumb in _crumbs)
        {
            crumb.Label.PropertyChanged -= LabelPropertyChanged;
            crumb.Button.Click -= Navigate;
            crumb.MenuItem.Click -= Navigate;
            crumb.LabelBinding.Dispose();
            crumb.MenuBinding.Dispose();
            Children.Remove(crumb.Slot);
            if (crumb.Separator is { } separator)
                Children.Remove(separator);
        }
        _crumbs.Clear();

        if (ItemsSource is not { } nodes)
            return;

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var current = i == nodes.Count - 1;
            var label = new TextBlock
            {
                LineHeight = 20,
                TextTrimming = current ? TextTrimming.CharacterEllipsis : TextTrimming.None
            };
            label.PropertyChanged += LabelPropertyChanged;
            var labelBinding = label.Bind(TextBlock.TextProperty, CreateNameBinding(node));
            var button = CreateButton();
            button.DataContext = node;
            button.Content = label;
            button.Click += Navigate;
            ToolTip.SetTip(button, node.Path);
            button.Bind(Button.ForegroundProperty, button.GetResourceObservable(current ? "WorkspaceTextBrush" : "WorkspaceMutedBrush"));
            if (current)
                button.FontWeight = FontWeight.Medium;

            var slot = new Border { Child = button, ClipToBounds = true };
            var separator = current ? null : CreateSeparator();
            var menuItem = new MenuItem { DataContext = node, HeaderTemplate = MenuHeaderTemplate };
            menuItem.Classes.Add("breadcrumb-menu-item");
            menuItem.Click += Navigate;
            ToolTip.SetTip(menuItem, node.Path);
            var menuBinding = menuItem.Bind(MenuItem.HeaderProperty, CreateNameBinding(node));

            _crumbs.Add(new Crumb(button, slot, separator, label, menuItem, labelBinding, menuBinding));
            Children.Add(slot);
            if (separator is not null)
                Children.Add(separator);
        }
    }

    private void SetHiddenCount(int count)
    {
        if (count == _hiddenCount)
            return;

        _menu.Hide();
        while (_hiddenCount > count)
        {
            _menu.Items.RemoveAt(0);
            _hiddenCount--;
        }
        while (_hiddenCount < count)
        {
            _menu.Items.Insert(0, _crumbs[_hiddenCount].MenuItem);
            _hiddenCount++;
        }
    }

    private void LabelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBlock.TextProperty)
            InvalidateMeasure();
    }

    private void Navigate(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: FolderTreeNode node })
        {
            _menu.Hide();
            NavigateRequested?.Invoke(this, new BreadcrumbNavigationEventArgs(node.Path));
        }
    }

    private static ReflectionBinding CreateNameBinding(FolderTreeNode node) =>
        new(nameof(FolderTreeNode.DisplayName))
        {
            Source = node,
            Converter = new FuncValueConverter<string?, string>(name => string.IsNullOrWhiteSpace(name) ? node.Path : name)
        };

    private static Button CreateButton()
    {
        var button = new Button
        {
            Height = RowHeight,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(6, 4),
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        button.Classes.Add("workspace-quiet");
        return button;
    }

    private static TextBlock CreateSeparator()
    {
        var separator = new TextBlock
        {
            Text = "›",
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 15,
            Opacity = 0.55,
            IsHitTestVisible = false
        };
        separator.Bind(TextBlock.ForegroundProperty, separator.GetResourceObservable("WorkspaceMutedBrush"));
        return separator;
    }

    private sealed record Crumb(Button Button, Border Slot, TextBlock? Separator, TextBlock Label,
        MenuItem MenuItem, IDisposable LabelBinding, IDisposable MenuBinding)
    {
        public double Width => Button.DesiredSize.Width + (Separator is null ? 0 : SeparatorWidth);
    }
}
