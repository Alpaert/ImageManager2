using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageManager.Core.Services;

namespace ImageManager.App.ViewModels;

public partial class AiRecommendViewModel : ViewModelBase
{
    private readonly IAiRecommendService _recommendService;
    private readonly ITagMappingRepository _tagMappingRepo;

    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private string _outputText = string.Empty;
    [ObservableProperty] private bool _isLoading;

    public AiRecommendViewModel(IAiRecommendService recommendService, ITagMappingRepository tagMappingRepo)
    {
        _recommendService = recommendService;
        _tagMappingRepo = tagMappingRepo;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText)) return;

        IsLoading = true;
        OutputText = "正在请求 AI 推荐...";

        try
        {
            var mappings = await _tagMappingRepo.GetAllAsync();
            OutputText = await _recommendService.RecommendAsync(InputText.Trim(), mappings);
        }
        catch (Exception ex)
        {
            OutputText = $"请求失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
