using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.App.ViewModels;

public partial class TagManageViewModel : ViewModelBase
{
    private readonly Func<string, string, Task<RenameResult>> _onRename;
    private readonly Func<string, string, Task> _onMerge;
    private readonly Func<string, Task> _onDelete;
    private readonly List<TagCount> _allTags;

    [ObservableProperty] private ObservableCollection<TagCount> _tags = new();
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;

    private CancellationTokenSource? _filterCts;
    private int _filterVersion;

    public TagManageViewModel(
        List<TagCount> allTags,
        Func<string, string, Task<RenameResult>> onRename,
        Func<string, string, Task> onMerge,
        Func<string, Task> onDelete)
    {
        _allTags = allTags;
        _onRename = onRename;
        _onMerge = onMerge;
        _onDelete = onDelete;
        ApplyFilter(string.Empty);
    }

    partial void OnFilterTextChanged(string value)
    {
        _filterCts?.Cancel();
        _filterCts = new CancellationTokenSource();
        var token = _filterCts.Token;
        var captured = value;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, token);
                if (!token.IsCancellationRequested)
                    Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ApplyFilter(captured));
            }
            catch (OperationCanceledException) { }
        });
    }

    private void ApplyFilter(string keyword)
    {
        int version = Interlocked.Increment(ref _filterVersion);
        var source = _allTags.ToList();

        _ = Task.Run(() =>
        {
            var filtered = string.IsNullOrWhiteSpace(keyword)
                ? source
                : source.Where(t => t.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

            var sorted = filtered.OrderByDescending(t => t.Count).ToList();

            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _filterVersion) return;
                Tags = new ObservableCollection<TagCount>(sorted);
                StatusText = $"共 {sorted.Count} 个标签";
            });
        });
    }

    [RelayCommand]
    private async Task DeleteTag(TagCount tag)
    {
        var count = tag.Count;
        await _onDelete(tag.Name);
        _allTags.RemoveAll(t => string.Equals(t.Name, tag.Name, StringComparison.OrdinalIgnoreCase));
        ApplyFilter(FilterText);
        StatusText = $"已删除 \"{tag.Name}\"（{count} 张图片受影响）";
    }

    public async Task<RenameResult> HandleRenameAsync(string oldName, string newName)
        => await _onRename(oldName, newName);

    public async Task HandleMergeAsync(string oldName, string newName)
        => await _onMerge(oldName, newName);

    public void RefreshList() => ApplyFilter(FilterText);

    public void UpdateTagName(string oldName, string newName)
    {
        // Save index before modifying existing.Name (they're the same object refs)
        int idx = -1;
        for (int i = 0; i < Tags.Count; i++)
        {
            if (string.Equals(Tags[i].Name, oldName, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }

        var existing = _allTags.FirstOrDefault(t =>
            string.Equals(t.Name, oldName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            existing.Name = newName;

        if (idx >= 0)
            Tags[idx] = new TagCount { Name = newName, Count = Tags[idx].Count };
        StatusText = $"共 {Tags.Count} 个标签";
    }

    public async Task RefreshAsync()
    {
        // Will be re-initialized by caller
    }
}
