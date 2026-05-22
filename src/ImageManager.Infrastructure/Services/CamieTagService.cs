namespace ImageManager.Infrastructure.Services;

/// <summary>
/// Camie Tagger v2 ONNX — 档案管理员。
/// 仅在模式 B 中使用，输出 Category 1 (artist) + Category 3 (copyright)。
/// Category 0/4 全部丢弃（防长尾噪声）。
/// </summary>
public class CamieTagService : OnnxTagServiceBase
{
    protected override string ModelRepo => "Camais03/camie-tagger-v2";
    protected override string ModelFileName => "model.onnx";
    protected override string TagsFileName => "tags.csv";
    protected override string ModelSubDir => "camie";
    protected override int InputSize => 512;
    protected override float[] Mean => [0.485f, 0.456f, 0.406f];  // ImageNet
    protected override float[] Std => [0.229f, 0.224f, 0.225f];
    protected override bool PreserveAspectRatio => true;
    protected override int CsvTagIdIndex => 1;   // camie CSV: id,tag_id,name,category,...
    protected override int CsvCategoryIndex => 3;
    protected override bool NeedsSigmoid => false; // "output" 输出已含 sigmoid
    protected override HashSet<int>? EnabledCategories => [1];  // 仅 artist（画师）
    protected override double DefaultThreshold => 0.001;       // 极低阈值，只取置信度最高的那个
    protected override int MaxResults => 1;                     // 每张图只取 top 1 画师
}
