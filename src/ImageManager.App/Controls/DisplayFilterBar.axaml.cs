using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ImageManager.App.ViewModels;
using ImageManager.Common.Constants;
using ImageManager.Core.Models;

namespace ImageManager.App.Controls;

public partial class DisplayFilterBar : UserControl
{
    private readonly ComboBox _type;
    private readonly ComboBox _orientation;
    private readonly CheckBox _unknown;
    private readonly TextBlock _hint;
    private readonly TextBlock _unknownCount;
    private readonly Flyout _flyout;
    private MainWindowViewModel? _vm;
    private readonly List<TypeChoice> _types = BuildTypes();

    private sealed record TypeChoice(string? Id, string Name, bool SupportsDimensions)
    {
        public override string ToString() => Name;
    }

    public DisplayFilterBar()
    {
        InitializeComponent();
        _type = new ComboBox { MinWidth = 230, ItemsSource = _types };
        _type.Classes.Set("toolbar-compact", true);
        _orientation = new ComboBox
        {
            MinWidth = 230, ItemsSource = new[] { "不限", "横向", "竖向", "正方形" }
        };
        _orientation.Classes.Set("toolbar-compact", true);
        _unknown = new CheckBox { Content = "尺寸未知的文件也显示", IsChecked = true };
        _unknown.Classes.Set("toolbar-compact", true);
        _hint = new TextBlock { FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        _unknownCount = new TextBlock { FontSize = 12, Opacity = 0.75 };
        _type.SelectionChanged += (_, _) => UpdateDimensionState();
        _orientation.SelectionChanged += (_, _) => UpdateDimensionState();

        var apply = new Button { Content = "应用" };
        apply.Classes.Set("toolbar-compact", true);
        var reset = new Button { Content = "重置" };
        reset.Classes.Set("toolbar-compact", true);
        apply.Click += async (_, _) => await ApplyAsync();
        reset.Click += (_, _) => SetDraft(new DisplayFilterOptions());
        var panel = new StackPanel { Spacing = 10, Width = 280, Margin = new Thickness(6) };
        panel.Children.Add(new TextBlock { Text = "显示筛选", FontSize = 16 });
        panel.Children.Add(new TextBlock { Text = "文件类型" });
        panel.Children.Add(_type);
        panel.Children.Add(new TextBlock { Text = "画面方向" });
        panel.Children.Add(_orientation);
        panel.Children.Add(_hint);
        panel.Children.Add(_unknown);
        panel.Children.Add(_unknownCount);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Children = { reset, apply }
        });
        _flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };
        _flyout.Opened += (_, _) => SetDraft(_vm?.DisplayFilter ?? new());
        panel.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _flyout.Hide();
            e.Handled = true;
        }, RoutingStrategies.Bubble);
        FilterButton.Flyout = _flyout;
        DataContextChanged += (_, _) => AttachViewModel();
        AttachedToVisualTree += (_, _) => AttachViewModel();
        DetachedFromVisualTree += (_, _) =>
        {
            if (_vm != null) _vm.PropertyChanged -= VmChanged;
            _vm = null;
        };
    }

    private void AttachViewModel()
    {
        if (_vm != null) _vm.PropertyChanged -= VmChanged;
        _vm = DataContext as MainWindowViewModel;
        if (_vm != null) _vm.PropertyChanged += VmChanged;
        RefreshSummary();
    }

    private void VmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.DisplayFilterCountText)
            or nameof(MainWindowViewModel.IsDisplayFilterEmpty)) RefreshSummary();
    }

    private void SetDraft(DisplayFilterOptions options)
    {
        _type.SelectedIndex = Math.Max(0, _types.FindIndex(x => x.Id == options.TypeId));
        _orientation.SelectedIndex = (int)options.Orientation;
        _unknown.IsChecked = options.IncludeUnknownDimensions;
        _unknownCount.Text = _vm?.DisplayFilterUnknownText;
        _unknownCount.IsVisible = !string.IsNullOrEmpty(_unknownCount.Text);
        UpdateDimensionState();
    }

    private async Task ApplyAsync()
    {
        if (_vm == null) return;
        var type = _type.SelectedItem as TypeChoice;
        var options = new DisplayFilterOptions(type?.Id,
            type?.SupportsDimensions == false ? MediaOrientation.All
                : (MediaOrientation)Math.Max(0, _orientation.SelectedIndex),
            _unknown.IsChecked == true);
        _flyout.Hide();
        await _vm.ApplyDisplayFilterAsync(options);
    }

    private void UpdateDimensionState()
    {
        var supports = (_type.SelectedItem as TypeChoice)?.SupportsDimensions != false;
        _orientation.IsEnabled = supports;
        _unknown.IsVisible = supports && _orientation.SelectedIndex > 0;
        _hint.IsVisible = !supports;
        _hint.Text = "此类型没有画面方向，方向筛选不适用。";
    }

    private void RefreshSummary()
    {
        if (_vm == null) return;
        FilterButton.Content = _vm.DisplayFilterButtonText;
        FilterButton.Classes.Set("accent", _vm.HasDisplayFilter);
    }

    private static List<TypeChoice> BuildTypes() => new[] { new TypeChoice(null, "全部类型", true) }
        .Concat(FileTypeConstants.SupportedTypes.Select(t => new TypeChoice(t.Id, t.DisplayName, t.SupportsDimensions)))
        .ToList();
}
