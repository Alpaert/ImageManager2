# ImageManager2

AI 驱动的本地图片管理工具，支持大规模图片库的标签搜索、AI 自动打标、相似/重复检测和多种瀑布流浏览模式。

## 功能特性

- **标签组合搜索** — 支持 `a`（AND）、`o`（OR）、`e`（EACH）、`-`（NOT）四种运算符，配合共现标签循环模式快速筛选
- **AI 自动打标** — ONNX 模型推理 + 本地中文标签库自动替换，批量文件夹打标全程自动，无需人工审核
- **AI 标签推荐** — 通过 DeepSeek API 输入自然语言描述，自动推荐匹配的本地标签
- **三种瀑布流布局** — Grid（网格）、Vertical Masonry（竖向瀑布流）、Horizontal Justified（横向等行高自适应）
- **相似图片搜索** — 多哈希投票（aHash + dHash + wHash + 颜色直方图）+ 汉明距离相似度判定
- **MD5 精确去重** — 快速查找完全相同的文件
- **文件夹实时监控** — FileSystemWatcher 监听外部新增/删除文件，自动同步 + 哈希补算
- **画师识别** — 基于 PixAI 嵌入向量的画师风格匹配与嵌入库管理
- **右键在线搜图** — 自动上传到 SauceNAO / IQDB / ascii2d / trace.moe 等平台搜图
- **深色/浅色主题** — 自定义 DynamicResource 笔刷，无需第三方主题包
- **批量剪贴板复制** — Ctrl+C 多选后以文件拖放格式复制，可直接粘贴到 QQ/资源管理器

## 技术栈

| 技术 | 用途 |
|------|------|
| .NET 8 | 运行时 |
| Avalonia UI 12.0 | 跨平台桌面 UI |
| CommunityToolkit.Mvvm 8.4 | MVVM 源生成器 |
| SQLite (Microsoft.Data.Sqlite + Dapper) | 嵌入式数据库，WAL 模式 |
| ONNX Runtime 1.26 (GPU) | AI 打标模型推理（WD14 SwinV2-v3） |
| SkiaSharp 3.116 | 图像解码、缩略图生成、感知哈希 |
| DeepSeek API | AI 标签推荐 |

## 快速开始

### 环境要求

- .NET SDK 8.0
- Windows 10+（当前仅支持 Windows）
- GPU（可选，用于 ONNX 推理加速）

### 构建与运行

```bash
# 还原依赖
dotnet restore

# 编译
dotnet build

# 运行
dotnet run --project src/ImageManager.App
```

首次启动会在 `%LocalAppData%\ImageManager\` 生成配置文件 `config.json`，数据库和缩略图缓存默认存储在 `C:\ImageManagerCache\`（可在设置中修改）。

### ONNX 模型准备

将以下 ONNX 模型文件放入缓存目录的 `models\` 子目录：

```
models\
├── wd-swinv2-v3.onnx          # WD14 通用标签模型
├── wd-swinv2-v3-rating.onnx   # 内容分级模型（可选）
├── model_repository.onnx      # PixAI 模型
├── model_artist.onnx          # 画师识别模型（可选）
├── pixai_tags.csv             # 对应中文标签库
├── wd14_tags.csv              # 对应中文标签库
└── artist_names.txt           # 画师名列表
```

## 项目结构

```
src/
├── ImageManager.Common/       # 零依赖工具层（文件大小格式化、路径处理）
├── ImageManager.Core/         # 领域模型 + 服务接口
├── ImageManager.Infrastructure/ # 实现层（SQLite、哈希、缩略图、ONNX、缓存）
└── ImageManager.App/          # Avalonia 桌面应用（MVVM 视图 + ViewModel）
```

## AI 打标流程

```
图片文件 → ONNX SwinV2-v3 推理 → 英文标签列表
    → ChineseTagLibrary 本地 CSV 查表 → 自动替换为中文标签
    → 直接写入数据库，标记 Done
```

全程自动，无需人工审核。中文标签库从模型附带的 CSV 文件加载，支持 pixai / wd14 / camie 三种标签体系的 CSV 格式。

## 许可证

MIT
