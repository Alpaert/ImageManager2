using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageManager.App.ViewModels;

public partial class FolderTreeNode : ObservableObject
{
    private static readonly FolderTreeNode Placeholder = new() { DisplayName = "..." };

    public string Path { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Alias { get; set; }
    public long DbId { get; init; }

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSearchHighlight;
    [ObservableProperty] private ObservableCollection<FolderTreeNode> _children = new();

    public string HighlightColor => IsSearchHighlight ? "#AA98FB98" : "Transparent";

    partial void OnIsSearchHighlightChanged(bool value) => OnPropertyChanged(nameof(HighlightColor));

    private bool _childrenLoaded;
    public Task? LoadTask { get; private set; }

    public FolderTreeNode()
    {
        // Defer — caller should call EnsureExpanderVisible after setting Path
    }

    public void EnsureExpanderVisible()
    {
        if (!_childrenLoaded && HasRealChildren && Children.Count == 0)
            Children.Add(Placeholder);
    }

    private bool HasRealChildren
    {
        get
        {
            try { return Directory.Exists(Path) && Directory.EnumerateDirectories(Path).Any(); }
            catch { return false; }
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_childrenLoaded)
            LoadTask = LoadChildrenAsync();
    }

    private async Task LoadChildrenAsync()
    {
        var dir = Path;
        var buffer = new List<FolderTreeNode>();
        await Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = System.IO.Path.GetFileName(sub);
                    var node = new FolderTreeNode { Path = sub, DisplayName = name };
                    node.EnsureExpanderVisible();
                    buffer.Add(node);
                }
            }
            catch { }
        });
        // Atomic swap: replace placeholder with real children in one UI operation
        Children.Clear();
        foreach (var n in buffer)
            Children.Add(n);
        _childrenLoaded = true;
        LoadTask = null;
    }
}
