using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.App.ViewModels;

public partial class TagManageViewModel : ViewModelBase, IDisposable
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
        // Pre-sort once: Count desc, then Name asc — filter only applies Where, no re-sort
        _allTags.Sort((a, b) =>
        {
            int cmp = b.Count.CompareTo(a.Count);
            return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        ApplyFilter(string.Empty);
    }

    partial void OnFilterTextChanged(string value)
    {
        _filterCts?.Cancel();
        _filterCts?.Dispose();
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
        var source = _allTags; // pre-sorted, no copy needed
        var hasFilter = !string.IsNullOrWhiteSpace(keyword);

        _ = Task.Run(() =>
        {
            var result = hasFilter
                ? source.Where(t => t.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList()
                : source;

            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _filterVersion) return;
                Tags.Clear();
                foreach (var t in result)
                    Tags.Add(t);
                StatusText = $"共 {result.Count} 个标签";
            });
        });
    }

    [RelayCommand]
    private async Task DeleteTag(TagCount tag)
    {
        await _onDelete(tag.Name);
        _allTags.RemoveAll(t => string.Equals(t.Name, tag.Name, StringComparison.OrdinalIgnoreCase));
        Tags.Remove(tag); // in-place remove, no full list rebuild
        StatusText = $"已删除 \"{tag.Name}\"";
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

    public void Dispose()
    {
        _filterCts?.Cancel();
        _filterCts?.Dispose();
    }
}
