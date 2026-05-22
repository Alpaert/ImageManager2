using ImageManager.Common.Helpers;
using ImageManager.Core.Services;

namespace ImageManager.Infrastructure.Services;

/// <summary>
/// 根据 TagMode 创建对应的打标服务实例。
/// 不依赖 DI 容器，由 App 层在 DI 注册后传入具体实例。
/// </summary>
public class TagServiceFactory
{
    private readonly SingleModelTagService _singleModel;
    private readonly EnsembleTagService _ensemble;

    public TagServiceFactory(SingleModelTagService singleModel, EnsembleTagService ensemble)
    {
        _singleModel = singleModel;
        _ensemble = ensemble;
    }

    public IEnsembleTagService Create(TagMode mode)
    {
        AppLogger.Info($"TagServiceFactory.Create mode={mode}");
        return mode switch
        {
            TagMode.SingleModel => _singleModel,
            TagMode.Ensemble => _ensemble,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知打标模式")
        };
    }
}
