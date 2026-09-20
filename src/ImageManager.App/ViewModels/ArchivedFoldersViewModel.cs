using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.Core.Models;
using ImageManager.Core.Services;

namespace ImageManager.App.ViewModels;

/// <summary>Displays folders hidden from the main folder tree and allows restoring them.</summary>
public partial class ArchivedFoldersViewModel : ViewModelBase
{
    private readonly IFolderRepository _folderRepository;

    [ObservableProperty]
    private ObservableCollection<FolderInfo> _archivedFolders = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public bool HasNoArchivedFolders => !IsLoading && ArchivedFolders.Count == 0;

    public ArchivedFoldersViewModel(IFolderRepository folderRepository)
    {
        _folderRepository = folderRepository ?? throw new ArgumentNullException(nameof(folderRepository));
    }

    public async Task InitializeAsync()
    {
        if (IsLoading) return;

        IsLoading = true;
        StatusText = string.Empty;
        try
        {
            var folders = await _folderRepository.GetAllAsync();
            ArchivedFolders.Clear();
            foreach (var folder in folders.Where(f => f.IsArchived))
                ArchivedFolders.Add(folder);

            NotifyListStateChanged();

            StatusText = ArchivedFolders.Count == 0
                ? "暂无已归档文件夹"
                : $"共 {ArchivedFolders.Count} 个已归档文件夹";
        }
        catch (Exception ex)
        {
            ArchivedFolders.Clear();
            NotifyListStateChanged();
            StatusText = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task Restore(FolderInfo? folder)
    {
        if (folder is null || IsLoading) return;

        try
        {
            await _folderRepository.SetArchivedAsync(folder.Id, false);
            ArchivedFolders.Remove(folder);
            NotifyListStateChanged();
            StatusText = ArchivedFolders.Count == 0
                ? "暂无已归档文件夹"
                : $"共 {ArchivedFolders.Count} 个已归档文件夹";
        }
        catch (Exception ex)
        {
            StatusText = $"恢复失败：{ex.Message}";
        }
    }

    private void NotifyListStateChanged()
    {
        OnPropertyChanged(nameof(HasNoArchivedFolders));
    }

    partial void OnIsLoadingChanged(bool value)
        => OnPropertyChanged(nameof(HasNoArchivedFolders));
}
