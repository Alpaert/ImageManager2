# 自动打标模块 — 开发文档

## 1. 需求概述

解决手工对几千张图片逐个打标的效率问题。使用 ONNX WD14 模型自动推理英文标签，通过 DeepSeek API 批量翻译为中文，在审核界面上由用户逐条确认建立英文→中文映射，最终将中文标签写入图片的 Tag 系统。

### 核心原则
- **新模块独立开发**，不修改现有文件夹加载/分页/搜索逻辑
- **复用现有模式**：Channel + SemaphoreSlim 控制并发、事件驱动通知 UI
- **映射表累积复用**：用户确认过的映射永久保存，下次相同英文标签自动采用

---

## 2. 功能流程

```
用户右键文件夹 → [计算文件夹图片Tag]
       ↓
后台: ONNX WD14 推理每张图 → 输出英文标签 + 置信度 → 写入 ImageTag 表（标记来源=AutoTag）
       ↓
后台: 收集所有唯一英文标签 → 查映射表（已有映射的直接使用） → 未映射的批量发 DeepSeek API 翻译
       ↓
弹窗: [标签翻译审核界面]
  ┌──────────────────────────────────────────────┐
  │  English Tag (左)        │  中文翻译 (右)    │ ✅确认 │
  │  ────────────────────────┼──────────────────│────────│
  │  blonde_hair  [查看图片] │  金发             │   ✓    │
  │  sitting     [查看图片]  │  坐着             │   ✓    │
  │  outdoors    [查看图片]  │  户外             │   ✓    │
  │  ...                     │  ...              │  ...   │
  └──────────────────────────────────────────────┘
       ↓
用户逐条确认 → 确认的映射写入映射表 → 对所有含有该英文标签的图片，写入中文标签到 ImageTag
```

### 2.1 查看图片子窗口

点击左侧英文标签 → 弹出小窗口：
- 显示当前文件夹内**含有该英文标签的第一张图片**（缩略图质量即可）
- 左右方向键翻页，浏览所有含该标签的图片
- 到头循环（最后一张 → 第一张）
- 不可编辑、仅查看

---

## 3. 架构设计

### 3.1 新增项目结构

```
ImageManager.Core/
  Models/
    TagMapping.cs          ← 英文→中文映射模型
    AutoTagState.cs        ← 文件夹打标状态（Pending/Processing/Done）
  Services/
    IAutoTagService.cs     ← 打标服务接口
    ITagMappingRepository.cs ← 映射表 CRUD 接口
    ITranslationService.cs ← 翻译服务接口（DeepSeek API）

ImageManager.Infrastructure/
  Data/Repositories/
    TagMappingRepository.cs
  Services/
    OnnxTagService.cs      ← WD14 ONNX 推理
    DeepSeekTranslationService.cs ← DeepSeek API 调用
    AutoTagPipelineService.cs    ← 编排整个流程

ImageManager.App/
  ViewModels/
    AutoTagReviewViewModel.cs    ← 审核界面 VM
    TagImageViewerViewModel.cs   ← 查看图片子窗口 VM
  Views/Settings/
    AutoTagReviewWindow.axaml    ← 审核界面
    AutoTagReviewWindow.axaml.cs
    TagImageViewerWindow.axaml   ← 查看图片子窗口
    TagImageViewerWindow.axaml.cs
  Services/
    AutoTagController.cs         ← UI 层流程控制器
```

### 3.2 复用现有模式

| 现有模式 | 新模块对应 |
|----------|-----------|
| Channel + I/O信号量 + CPU信号量 | ONNX 推理并发控制 |
| `PageManager.PageChanged` 事件 | `AutoTagController.ProgressChanged` 事件 |
| `TagSearchController.SearchCompleted` | `AutoTagController.TagComputationCompleted` |
| `LoadFolderAsync` → `PrecomputeHashesAsync` 延迟 3s | 打标任务延迟 1s 启动 |

### 3.3 数据流

```
ONNX 推理（I/O + CPU 分离）:
  生产者: 读取文件 → 预处理为 ONNX 输入 tensor → 写入 Channel
  消费者: 从 Channel 读取 → ONNX 推理 → 解析输出标签 → 写入 DB

翻译:
  收集唯一英文标签 → 过滤已有映射 → 分批(默认每批 50 个) → DeepSeek API
```

---

## 4. 数据库设计

### 4.1 新表：TagMapping

```sql
CREATE TABLE TagMapping (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    EnglishName TEXT NOT NULL UNIQUE COLLATE NOCASE,
    ChineseName TEXT NOT NULL,
    ConfirmedAt DATETIME DEFAULT (datetime('now')),
    UpdatedAt   DATETIME DEFAULT (datetime('now'))
);
CREATE INDEX IX_TagMapping_English ON TagMapping(EnglishName COLLATE NOCASE);
```

### 4.2 新表：AutoTagState

```sql
CREATE TABLE AutoTagState (
    FolderId    INTEGER PRIMARY KEY,
    Status      TEXT NOT NULL DEFAULT 'Pending',  -- Pending / Processing / AwaitingReview / Done / Failed
    TotalFiles  INTEGER DEFAULT 0,
    Processed   INTEGER DEFAULT 0,
    LastFileCount INTEGER DEFAULT 0,   -- 上次打标时的文件总数（用于检测新文件）
    StartedAt   DATETIME,
    CompletedAt DATETIME,
    ErrorMsg    TEXT,
    FOREIGN KEY (FolderId) REFERENCES Folder(Id) ON DELETE CASCADE
);
```

状态机：
```
                    ┌─ 全部确认 ──→ Done
Pending → Processing → AwaitingReview
                         │
                         ├─ 部分确认+保存 → AwaitingReview（可恢复）
                         └─ 文件夹有新文件 → 提示仅对新文件打标
```

### 4.3 新表：AutoTagTranslation（暂存翻译结果）

```sql
CREATE TABLE AutoTagTranslation (
    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    FolderId            INTEGER NOT NULL,
    EnglishTag          TEXT NOT NULL,
    ChineseTranslation  TEXT,               -- DeepSeek 翻译结果（可为空 = 未翻译）
    UserEditedText      TEXT,               -- 用户手动编辑后的翻译
    IsConfirmed         INTEGER DEFAULT 0,  -- 0=待确认, 1=已确认
    IsExistingMapping   INTEGER DEFAULT 0,  -- 1=来自已有映射表（自动确认）
    CreatedAt           DATETIME DEFAULT (datetime('now')),
    FOREIGN KEY (FolderId) REFERENCES Folder(Id) ON DELETE CASCADE,
    UNIQUE(FolderId, EnglishTag)
);
```

### 4.4 现有表修改

在 `ImageTag` 表新增 `Source` 列（可为 NULL，兼容旧数据）：
```sql
ALTER TABLE ImageTag ADD COLUMN Source TEXT DEFAULT NULL;
-- 值: NULL(手动) / 'AutoTag'(自动打标) / 'AutoTagConfirmed'(已确认)
```

### 4.4 参数预留

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `OnnxConcurrency` | 2 | ONNX 推理并发数 |
| `OnnxConfidenceThreshold` | 0.35 | 标签置信度阈值（WD14 推荐的默认值） |
| `OnnxMaxTagsPerImage` | 20 | 每张图最大标签数 |
| `TranslationBatchSize` | 50 | DeepSeek API 每批发送的英文标签数 |
| `TranslationApiDelayMs` | 500 | 每批 API 调用间延迟（避免限流） |

---

## 5. 模块详细设计

### 5.1 ONNX 推理服务 (`OnnxTagService`)

```
职责: 加载 WD14 ONNX 模型，对单张图片推理，返回 List<(tagName, confidence)>

核心方法:
  LoadModel(string modelPath)         → 加载 .onnx 文件
  Task<List<TagPrediction>> PredictAsync(string imagePath)
                                      → 预处理 → 推理 → 解析输出
  Dispose()                           → 释放 ONNX 资源

模型来源:
  1. sdwebui 的 models/tagger/ 目录下的 .onnx 文件
  2. 或从 HuggingFace: SmilingWolf/wd-v1-4-convnext-tagger-v2 下载
  3. 模型文件 ~350MB，放在 AppSettings 可配置路径

预处理:
  1. 读图片 → SKBitmap
  2. 缩放到 448×448 (WD14 ConvNext 输入)
  3. 归一化: mean=[0.4814,0.4578,0.4082], std=[0.2686,0.2613,0.2758]
  4. 转为 float[1,3,448,448] tensor（CHW 格式）

后处理:
  ONNX 输出 → float[1, N_classes] → softmax(可选) → top-K 标签 → 阈值过滤
```

### 5.2 翻译服务 (`DeepSeekTranslationService`)

```
职责: 将英文标签列表批量发给 DeepSeek API，返回中文翻译列表

核心方法:
  Task<List<string>> TranslateBatchAsync(List<string> englishTags)
  Task<string> TranslateSingleAsync(string englishTag)

API 特点:
  - DeepSeek API 兼容 OpenAI SDK 格式
  - 使用 System.Net.Http.HttpClient
  - API Key 从 AppSettings 读取
  - System Prompt: "你是一个图片标签翻译器。将给定的英文标签翻译为中文，保持简洁准确。
    每个标签单独一行输出，不要编号，不要解释。"

输入示例:
  blonde_hair
  sitting
  outdoors
  looking_at_viewer

输出示例:
  金发
  坐着
  户外
  看向观众

参数:
  model: "deepseek-chat"
  temperature: 0.1（低温度确保翻译一致性）
  max_tokens: 200 × 标签数量
```

### 5.3 打标管道服务 (`AutoTagPipelineService`)

```
职责: 编排从 ONNX 推理到翻译审核的完整流程

核心方法:
  Phase 1 - InferTagsAsync(long folderId)
    → 获取文件夹下所有图片路径
    → Channel + SemaphoreSlim 并发推理
    → 写入 ImageTag(Source='AutoTag')
    → 更新 AutoTagState.Processed 进度
    → 通过事件通知 UI 进度

  Phase 2 - TranslateTagsAsync(List<string> uniqueEnglishTags)
    → 查询 TagMapping 过滤已映射的
    → 未映射的分批(TranslationBatchSize)调 DeepSeek API
    → 返回 Dictionary<string, string> (English→Chinese)

  Phase 3 - OpenReviewWindow( Dictionary<string, string> translations )
    → 打开 AutoTagReviewWindow
    → 用户逐条审核确认

进度事件:
  event Action<AutoTagProgress>? ProgressChanged;
  event Action<List<TagTranslation>>? TranslationReady;
```

### 5.4 审核界面 ViewModel (`AutoTagReviewViewModel`)

```
职责: 展示 English→Chinese 翻译对照表，用户逐条确认或双击编辑

属性:
  ObservableCollection<TagTranslationItem> Items  ← 绑定列表
  int ConfirmedCount / TotalCount                 ← 进度
  bool HasPendingEdits                            ← 有未保存的编辑

TagTranslationItem 结构:
  string EnglishTag
  string ChineseTranslation       ← 当前显示的中文翻译
  string? UserEditedText          ← 用户双击编辑后的文本
  bool IsConfirmed
  bool IsExistingMapping           ← 来自已有映射（浅蓝色，自动确认）
  int ImageCount                   ← 当前文件夹内含此标签的图片数
  bool IsEditing                   ← 是否正在编辑中

命令:
  [RelayCommand] Confirm(TagTranslationItem)      ← 确认单条翻译
  [RelayCommand] ViewImages(englishTag)            ← 弹出查看子窗口
  [RelayCommand] SaveAll()                         ← 保存翻译草稿到 AutoTagTranslation
  [RelayCommand] ConfirmAll()                      ← 一键确认所有未确认
  [RelayCommand] StartEdit(TagTranslationItem)     ← 双击进入编辑模式
  [RelayCommand] CommitEdit(TagTranslationItem)    ← 编辑完成(Enter/失焦)

确认逻辑:
  Confirm(item):
    1. 取 UserEditedText ?? ChineseTranslation 为最终中文
    2. 写入 TagMapping 表
    3. 写入 AutoTagTranslation: IsConfirmed=1
    4. 对该文件夹内所有含有此英文标签的图片:
       - 创建中文 Tag（或复用已有中文 Tag）
       - 更新 ImageTag: Source='AutoTagConfirmed', TagId=中文Tag.Id
    5. item.IsConfirmed = true

保存逻辑 (SaveAll):
  将所有未确认的 Items 的 UserEditedText 写入 AutoTagTranslation.UserEditedText
  已确认的不需要保存（已写入 TagMapping + ImageTag）
  弹 Toast: "翻译草稿已保存，下次打开此文件夹可继续编辑"

编辑逻辑:
  StartEdit: item.IsEditing = true（UI 切换为 TextBox）
  CommitEdit: item.UserEditedText = 编辑后文本; item.IsEditing = false
  ChineseTranslation 显示: UserEditedText ?? ChineseTranslation

自动确认:
  englishTag 在 TagMapping 表中 → IsExistingMapping=true(浅蓝背景)
  自动使用映射值，IsConfirmed=true，计入 ConfirmedCount
```

### 5.5 查看图片子窗口 (`TagImageViewerWindow`)

```
职责: 展示某个英文标签对应的所有图片

核心:
  - 接收: (englishTag, folderPath)
  - 查询: 当前文件夹下所有含有该 englishTag 的图片 → List<string> 文件路径列表
  - 显示: 首张图片（缩略图质量即可，200px 解码宽度）
  - 键盘: ← → 翻页，到头循环（idx = (idx + 1) % count）
  - 标题栏: "blonde_hair — 第 3/15 张"
  - 窗口尺寸: 600×500（适合查看缩略图）
```

---

## 6. UI 设计

### 6.1 右键菜单入口

在 `MainWindow.axaml` 左侧文件夹列表的 ContextMenu 中添加：
```xml
<MenuItem Header="计算此文件夹的图片标签" Click="MenuComputeAutoTags_Click"/>
```

### 6.2 审核界面布局

```
┌─ Tag 翻译审核 ── 文件夹: 壁纸合集 ──────────────────────────┐
│ 进度: 已确认 12/50    [保存草稿]  [全部确认] [×关闭]        │
├──────────────────────────────────────────────────────────────┤
│ #  │ English Tag         │  中文翻译(双击编辑)  │ 图片 │ 确认│
│────┼─────────────────────┼──────────────────────┼──────┼─────│
│ 1  │ blonde_hair (120张) │  金发                │[查看]│ [✓] │ ← 浅蓝=已映射
│ 2  │ sitting  (85张)     │  坐着                │[查看]│ [✓] │ ← 浅绿=已确认
│ 3  │ outdoors (67张)     │ [户外_____] (编辑中) │[查看]│[确认]│ ← 双击编辑中
│ 4  │ solo     (200张)    │  独自                │[查看]│[确认]│ ← 待确认
│ 5  │ looking_at_viewer   │  (未翻译)            │[查看]│[确认]│ ← API失败
│ ...│ ...                 │  ...                 │ ...  │ ... │
└──────────────────────────────────────────────────────────────┘

行颜色:
  浅蓝色 = 已有映射表自动确认 (IsExistingMapping)
  浅绿色 = 用户手动确认 (IsConfirmed && !IsExistingMapping)
  白色   = 待确认
  浅黄色 = 翻译失败，待手动编辑 (ChineseTranslation == NULL)

交互:
  - 双击中文列 → 进入编辑模式 (TextBox)
  - Enter 或失焦 → 退出编辑，UserEditedText 更新
  - 点击 [✓] → 已确认状态，不可撤销（可在 TagEditWindow 中删除标签）
  - 点击 [确认] → 调用 Confirm(item)
  - 点击 [保存草稿] → 调用 SaveAll()，持久化所有编辑内容到 AutoTagTranslation
  - 点击 [全部确认] → 一键确认所有待确认行（使用 UserEditedText ?? ChineseTranslation）
```
```

- 已确认行：浅绿色背景
- 已有映射（自动确认）：浅蓝色背景，显示 "已映射"
- 列表可滚动（可能几百行）
- [查看] 按钮：点击打开 TagImageViewerWindow
- 右上角 [全部确认] 按钮：一键确认所有未确认的（批量接受当前翻译）

---

## 7. 需求确认结果

### 7.1 ✅ 映射复用 — 全局复用
确认过的映射写入 `TagMapping` 表，任何文件夹再次出现相同英文标签时自动跳过确认，直接采用已有映射。用户在审核界面看到已映射的标签显示为浅蓝色"已映射"状态，无需手动操作。

### 7.2 ✅ 置信度阈值 — 用户可调节，默认 0.35
在 AppSettings 中新增 `OnnxConfidenceThreshold`，默认 0.35。设置窗口提供滑块调节（0.0~1.0，步长 0.05）。低于阈值的标签不存储、不显示、不翻译。

### 7.3 ✅ 已有标签 — 追加
手动标签（Source=NULL）和自动标签（Source='AutoTag'）独立并存。ONNX 打标不会覆盖或删除已有手动标签。图片的 Tag 列表合并显示两者。

### 7.4 ✅ 未确认标签 — 保存暂存 + 禁止重复打标（展开分析）

#### 保存机制
审核界面的翻译结果（含用户手动编辑过的文本）存储在 `AutoTagTranslation` 表中，即使用户关闭窗口也不丢失。界面上提供 **[保存]** 按钮，将当前所有翻译文本（包括待确认和已确认的）持久化到该表。

**注意**：`AutoTagTranslation` 仅存储翻译文本草稿。已确认的映射写入 `TagMapping` 表。中文标签只有在用户点击确认后才写入 `ImageTag` 表。

#### 再次打标 — 分情境处理

用户对已打标过的文件夹再次点击"计算此文件夹的图片标签"时：

**情境 A — 全部已确认（Status = Done）**
- 检测当前文件夹文件数是否与 `AutoTagState.LastFileCount` 一致
  - **A1**：文件数未变 → 提示"此文件夹已完成打标，无需重复。"，阻止操作
  - **A2**：文件数增加 → 提示"此文件夹已打标，但检测到 N 张新图片。是否仅对新图片打标？" → 确认后仅对新图片推理 → 新英文标签进入审核界面（追加到已有审核列表）
  - **A3**：文件数减少 → 同 A1，文件删除不影响已打标状态

**情境 B — 部分未确认（Status = AwaitingReview）**
- 提示"此文件夹有待确认的翻译，是否继续上次的审核？"
  - **B1**：继续审核 → 打开 `AutoTagReviewWindow`，恢复上次的翻译列表和确认状态
  - **B2**：重新打标 → 提示"重新打标将覆盖之前的翻译结果，已确认的映射不会丢失。确定重新打标？" → 确认后清空 `AutoTagTranslation` 中该文件夹的未确认记录，重新推理
  - **B3**：检测到新文件（`LastFileCount` < 当前文件数） → 同 B1 但额外提示"检测到 N 张新图片，恢复审核后可以对新图片推理"

**情境 C — 正在处理中（Status = Processing）**
- 提示"此文件夹正在打标中（已完成 X/N），请等待完成。"，显示进度，不允许重复启动

**情境 D — 上次失败（Status = Failed）**
- 提示"上次打标失败。错误：{ErrorMsg}。是否重新打标？" → 确认后清空旧状态，重新开始

**情境 E — 从未打过标（Status = Pending 或无记录）**
- 正常启动推理流程

### 7.5 ✅ 翻译失败处理
DeepSeek API 调用失败时：
- 该批次的英文标签保留原文（`ChineseTranslation` 为 NULL）
- 审核界面中，未翻译的标签显示英文原文作为占位翻译，用户可双击手动编辑
- 已有映射表（`TagMapping`）自动兜底：即使 API 失败，之前确认过的映射仍然生效
- 界面顶部显示警告："N 个标签翻译失败，已用已有映射兜底，其余请手动编辑"

### 7.6 ✅ 双击编辑翻译
中文翻译列默认显示 DeepSeek 翻译结果（或已有映射值）。用户双击单元格进入编辑模式，修改后按 Enter 或点击其他地方确认编辑。编辑后的文本存储在 `UserEditedText` 字段。用户编辑过但未确认的翻译在下一次打开审核界面时保留编辑内容。

### 7.7 ✅ ONNX 模型路径
默认路径：`{DiskCacheDirectory}\models\wd14.onnx`（跟随用户设置的缓存目录变更）。
- `DiskCacheDirectory` 默认 `C:\ImageManagerCache` → 模型默认 `C:\ImageManagerCache\models\wd14.onnx`
- 用户更改缓存目录时，模型路径自动跟随。首次使用如模型不存在则提示下载。

### 7.8 ✅ API Key 存储
`AppSettings` 新增 `DeepSeekApiKey` 字段。在设置窗口（内存与缓存设置页面或新建 AI 设置页面）提供输入框。不硬编码。

### 7.9 ✅ 查看图片 — 缩略图质量
查看图片子窗口显示 200px 解码宽度的缩略图。目的是让用户快速浏览判断标签准确性，无需原图清晰度。

---

## 8. 实施计划

### 阶段 1: 基础设施（无 UI 变更）
1. DB 迁移：建 `TagMapping` 表 + `AutoTagState` 表 + `AutoTagTranslation` 表 + `ImageTag.Source` 列
2. Core 层：`TagMapping` 模型 + `AutoTagState` 模型 + `ITagMappingRepository` + `IAutoTagService` + `ITranslationService`
3. Infrastructure 层：`TagMappingRepository` + `OnnxTagService` + `DeepSeekTranslationService`
4. App Settings：新增 `OnnxConfidenceThreshold`、`DeepSeekApiKey`、可调参数（#4.4）
5. ONNX 模型路径：默认 `{DiskCacheDirectory}\models\wd14.onnx`

### 阶段 2: 推理管道
6. `AutoTagPipelineService` 实现 ONNX 推理流程（Channel + 双信号量）
7. 打标前情境判断逻辑（7.4 节 A-E 五种情境）
8. 右键菜单入口 + `AutoTagController` 启动管道，进度事件通知 UI

### 阶段 3: 翻译 + 审核界面
9. `DeepSeekTranslationService` 实现（分批发送 + 已有映射过滤 + 失败重试）
10. `AutoTagReviewWindow` + `AutoTagReviewViewModel`（审核列表 + 双击编辑 + 保存草稿）
11. `TagImageViewerWindow` + `TagImageViewerViewModel`（查看图片子窗口）
12. `AutoTagPipelineService` 实现恢复审核流程（从 `AutoTagTranslation` 表加载）

### 阶段 4: 收尾
13. 设置窗口添加置信度阈值滑块 / API Key 输入框
14. 更新 `ImageManager.md` 文档

---

## 9. 风险与缓解

| 风险 | 影响 | 缓解 |
|------|------|------|
| ONNX 模型不兼容某些图片格式 | 该图跳过 | catch 异常，记录，继续处理 |
| DeepSeek API 请求过多被限流 | 翻译中断 | `TranslationApiDelayMs` 延迟 + 重试 |
| 5000 张图 ONNX 推理耗时过长 | 用户等待 | 后台运行，进度显示，支持取消 |
| ONNX 模型加载失败（文件缺失） | 功能不可用 | 启动时检查，提示用户 |
| 翻译结果与标签含义偏差 | 用户审核负担重 | 低 temperature + 详细 System Prompt |
