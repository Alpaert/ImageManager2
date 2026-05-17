using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ImageManager.App.Helpers;
using ImageManager.App.ViewModels;

namespace ImageManager.App.Views.Settings;

public partial class ShortcutSettingWindow : Window
{
    private ShortcutSettingViewModel Vm => (ShortcutSettingViewModel)DataContext!;

    public ShortcutSettingWindow()
    {
        InitializeComponent();
        KeyDown += OnWindowKeyDown;
    }

    private void GestureBox_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Tag is ShortcutItem item)
        {
            Vm.StartRecordingCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var recording = Vm.Shortcuts.FirstOrDefault(s => s.IsRecording);
        if (recording == null) return;

        if (KeyGestureHelper.IsModifierOnly(e))
            return;

        Vm.RecordKey(KeyGestureHelper.KeyEventArgsToGesture(e));
        e.Handled = true;
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }
}
