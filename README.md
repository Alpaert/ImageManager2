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
| ONNX Runtime 1.26 (GPU) | AI 打标模型推理（PixAI + WD14 双模型） |
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

将以下模型文件放入缓存目录的 `models\` 子目录（默认 `C:\ImageManagerCache\models\`）：

```
models\
├── pixai\                    # PixAI 标签模型
│   ├── model.onnx
│   └── selected_tags.csv     # 中文标签库
├── wd14\                     # WD14 分级模型（Ensemble 模式需要）
│   ├── model.onnx
│   └── selected_tags.csv
├── artist_embeddings.bin     # 画师嵌入库（可选）
└── artist_names.txt          # 画师中文名列表（可选）
```

**两种打标模式：**
- **SingleModel** — 仅 PixAI，轻量快速
- **Ensemble** — PixAI + WD14 双模型串行推理，带内容分级和画师识别

## 项目结构

```
src/
├── ImageManager.Common/       # 零依赖工具层（文件大小格式化、路径处理）
├── ImageManager.Core/         # 领域模型 + 服务接口
├── ImageManager.Infrastructure/ # 实现层（SQLite、哈希、缩略图、ONNX、缓存）
└── ImageManager.App/          # Avalonia 桌面应用（MVVM 视图 + ViewModel）
```

## AI 打标流程

**Ensemble 模式：**
```
图片文件 → WD14（内容分级）→ PixAI（标签推理 + 嵌入提取）
    → 合并去重 → ChineseTagLibrary 本地 CSV 查表替换中文
    → 画师识别（嵌入向量匹配）→ 直接写入数据库，标记 Done
```

**SingleModel 模式：**
```
图片文件 → PixAI 单模型推理 → CSV 查表替换中文 → 写入数据库
```

全程自动，无需人工审核。中文标签库从 PixAI 模型附带的 `selected_tags.csv` 加载。

## 许可证

MIT
