using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ImageManager.App.ViewModels;

namespace ImageManager.App.Views.Settings;

public partial class AutoTagReviewWindow : Window
{
    private AutoTagReviewViewModel Vm => (AutoTagReviewViewModel)DataContext!;

    public AutoTagReviewWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        Closing += (_, _) => Vm.Items.Clear();
    }

    private void CloseWindow()
    {
        DataContext = null;
        Close();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => CloseWindow();

    private void Root_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        // Commit any active edit when user clicks outside the editing TextBox
        var source = e.Source as Avalonia.Controls.Control;
        if (source is Avalonia.Controls.TextBox) return; // don't commit when clicking the edit box itself

        foreach (var item in Vm.Items)
        {
            if (item.IsEditing)
                Vm.CommitEditCommand.Execute(item);
        }
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ViewModels.TagTranslationItem item) return;
        await Vm.DeleteCommand.ExecuteAsync(item);
    }

    private void ChineseText_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is not Avalonia.Controls.TextBlock tb || tb.Tag is not ViewModels.TagTranslationItem item)
            return;
        Vm.StartEditCommand.Execute(item);
    }

    private void EditBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not Avalonia.Controls.TextBox box || box.Tag is not ViewModels.TagTranslationItem item)
            return;
        Vm.CommitEditCommand.Execute(item);
    }

    private async void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ViewModels.TagTranslationItem item) return;
        await Vm.ConfirmCommand.ExecuteAsync(item);
    }

    private async void EnglishTag_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string englishTag) return;

        var imagePaths = await Vm.GetImagesForTag(englishTag);
        if (imagePaths.Count == 0) return;

        var viewerVm = new TagImageViewerViewModel();
        viewerVm.Initialize(englishTag, imagePaths);

        var viewer = new TagImageViewerWindow { DataContext = viewerVm };
        viewer.ShowDialog(this);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) CloseWindow();
    }
}
