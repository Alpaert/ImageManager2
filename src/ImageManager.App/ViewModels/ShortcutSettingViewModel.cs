using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ImageManager.App.ViewModels;

public partial class ShortcutItem : ObservableObject
{
    public string CommandId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;

    [ObservableProperty] private string _keyGesture = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRecording))]
    private bool _isRecording;

    public bool IsNotRecording => !IsRecording;

    public string DefaultGesture { get; init; } = string.Empty;

    public bool IsModified => KeyGesture != DefaultGesture;
}

public partial class ShortcutSettingViewModel : ViewModelBase
{
    public ObservableCollection<ShortcutItem> Shortcuts { get; } = new();

    private readonly Dictionary<string, string> _savedBindings;
    private readonly Func<Dictionary<string, string>, Task> _onSave;

    public ShortcutSettingViewModel(Dictionary<string, string> currentBindings, Func<Dictionary<string, string>, Task> onSave)
    {
        _savedBindings = currentBindings;
        _onSave = onSave;

        var defaults = new (string Id, string Name, string Category, string Default)[]
        {
            ("EditTag", "编辑 Tag", "文件", "Ctrl+T"),
        };

        foreach (var (id, name, cat, def) in defaults)
        {
            var gesture = _savedBindings.TryGetValue(id, out var saved) ? saved : def;
            Shortcuts.Add(new ShortcutItem
            {
                CommandId = id,
                DisplayName = name,
                Category = cat,
                KeyGesture = gesture,
                DefaultGesture = def
            });
        }
    }

    [RelayCommand]
    private void StartRecording(ShortcutItem item)
    {
        foreach (var s in Shortcuts)
            s.IsRecording = false;
        item.IsRecording = true;
    }

    public void RecordKey(string gesture)
    {
        var recording = Shortcuts.FirstOrDefault(s => s.IsRecording);
        if (recording == null) return;

        if (string.IsNullOrWhiteSpace(gesture))
        {
            recording.KeyGesture = "";
        }
        else
        {
            recording.KeyGesture = gesture;
        }

        recording.IsRecording = false;
    }

    [RelayCommand]
    private void ResetShortcut(ShortcutItem item)
    {
        item.KeyGesture = item.DefaultGesture;
    }

    [RelayCommand]
    private void ResetAll()
    {
        foreach (var s in Shortcuts)
            s.KeyGesture = s.DefaultGesture;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var bindings = new Dictionary<string, string>();
        foreach (var s in Shortcuts)
        {
            if (!string.IsNullOrWhiteSpace(s.KeyGesture))
                bindings[s.CommandId] = s.KeyGesture;
        }
        await _onSave(bindings);
    }
}
