# ImageManager2 — 项目架构与状态文档

## [核心架构]

解决方案 `ImageManager2.sln` 包含 4 个项目，分层如下：

### ImageManager.Common
零依赖共享层。提供纯工具类：
- `FileSizeFormatter` — 字节数 → 可读文件大小字符串
- `PathHelper` — 生成无冲突文件路径（重名时自动加 `_N` 后缀）

### ImageManager.Core
领域模型 + 服务接口（无实现依赖）：
- **模型**：`ImageMeta`（图片元数据，含路径/哈希/尺寸/标签）、`FolderInfo`、`AppSettings`、`TagCount`
- **服务接口**：`IImageMetaRepository`、`ITagRepository`、`IFolderRepository`、`ISettingsRepository`、`IHashService`、`ISimilarImageService`、`IDuplicateService`、`IThumbnailCacheService`
- `AppSettings` 为 POCO（非 INPC），UI 层通过 `MainWindowViewModel` 的 observable mirror properties 绑定

### ImageManager.Infrastructure
所有核心逻辑的实现层：
- **数据层** (`Data/`)：SQLite 数据库（WAL 模式），使用 Dapper + `Microsoft.Data.Sqlite`。`AppDbContext` 负责建表/迁移（`ImageMeta`、`Tag`、`ImageTag`、`Folder`、`FavoriteTag`、`AppSetting`）。Repository 实现（`ImageMetaRepository`、`TagRepository`、`FolderRepository`、`SettingsRepository`）
- **哈希** (`Hashing/`)：`HashService` — MD5 精确哈希 + 组合感知哈希（aHash|dHash|wHash|颜色直方图），含汉明距离比较、多哈希投票相似度判定（2/3 通过即相似）
- **图像处理** (`Imaging/`)：`ThumbnailGenerator` — 基于 SkiaSharp 的缩略图生成（利用 JPEG 分层解码避免全分辨率加载）、尺寸读取、灰度像素提取
- **缓存** (`Caching/`)：`ThumbnailCacheService` + `DiskThumbnailCache` — 二级缓存（内存 ConcurrentDictionary → 磁盘文件），LRU 淘汰
- **服务** (`Services/`)：`DuplicateService`（MD5 精确去重）、`SimilarImageService`（直方图预过滤 + 多哈希投票相似搜索）
- **迁移**：`DataMigrator` — 旧 WPF 版 JSON 数据 → SQLite 迁移

### ImageManager.App
Avalonia 桌面应用（MVVM）：
- **DI**：`App.axaml.cs` 中 `ConfigureServices()` 完成 DI 注册（Singleton: 所有 Repository/Service/PageManager/MainWindowViewModel）
- **入口**：`Program.cs` → `BuildAvaloniaApp()`，崩溃日志写入桌面 `ImageManager_crash.log`
- **主 ViewModel**：`MainWindowViewModel` — 约 1100 行，管理文件夹列表、排序、方向筛选、相似/重复检测。标签搜索逻辑委托给 `TagSearchController`
- **伴生服务**：
  - `Services/PageManager` — 页面缓存（最多 3 页）、缩略图加载（`SemaphoreSlim(4)` + 可见区域优先）、缩放重建、搜索前后页面状态保存/恢复。通过 `PageChanged` 事件通知 VM
  - `Services/TagSearchController` — 标签搜索解析（a/o/e/- 四种运算符）、前缀联想、共现标签模式、标签状态循环。通过 `SearchCompleted`/`SuggestionsChanged`/`CoTagCycled` 事件通知 VM
- **布局**：自定义 `SmartWaterfallPanel`（继承 `Panel`），支持三种模式：Grid（默认网格）、Vertical（Masonry 瀑布流，最短列优先）、Horizontal（Justified 等行高自适应）
- **设置窗口**：外观主题、壁纸、窗口大小、缩略图参数、快捷键、内存/缓存、标签编辑
- **预览**：`PreviewViewModel` 支持图片导航、缩放
- **MVVM 框架**：CommunityToolkit.Mvvm 8.4.1（`[ObservableProperty]`、`[RelayCommand]`、source generators）
- **皮肤系统**：`App.axaml` 中 10+ 个 DynamicResource 笔刷，`ApplyColors(bool dark)` 切换深色/浅色

### 关键数据流
1. 选择文件夹 → `LoadFolderAsync` → 优先走 DB 索引（`GetByFolderIdAsync`），否则磁盘枚举 → 分页显示。随后串行执行 `SyncFolderAsync` → `PrecomputeHashesAsync`（先同步后哈希，避免新文件漏算）
2. **文件夹实时监控**：`FileSystemWatcher` 监听当前文件夹的 Created / Deleted 事件，2 秒防抖后自动触发 `SyncCurrentFolderAsync`（sync + hash 补算）。拖拽文件到左侧文件夹后同样触发此流程
3. 后台哈希预计算：`PrecomputeHashesAsync` 使用 `Channel<Bounded>(50)` 生产者-消费者模式，I/O 信号量（~12）和 CPU 信号量（~核心数）分离，批量 upsert
4. **标签搜索**（已提取至 `TagSearchController`）：`SearchByTagAsync` 解析 `a`/`o`/`e`/`-` 四种运算符 → 查询 `IImageMetaRepository` → 与 `_allFiles` 取交集 → `SearchCompleted` 事件通知 VM 更新分页和 `Images` → 进入 co-tag 模式 → `RefreshCoTagSuggestionsAsync` 查询共现标签 → `SuggestionsChanged` 更新下拉。前缀模式点击通过 backing field 设置文本避免 Avalonia NPE。共现标签循环通过 `CycleCoTag` 管理 AND/AND-each/NOT 状态
5. **分页与缩略图加载**（已提取至 `PageManager`）：`ShowPageAsync` → 查 page cache（最多 3 页）→ 未命中则 `CreatePlaceholderItems` 从 `ActiveFileList` 创建 200 个占位 item → 触发 `LoadPageThumbnailsAsync`（优先加载可见区域首屏 item，其余后台排队）→ `SemaphoreSlim(4)` 并发 → `GetOrCreateThumbnailAsync` 先查 L1 内存缓存再查 L2 磁盘缓存最后 SkiaSharp 生成 → `PageChanged` 事件通知 VM 更新 `Images`/`CurrentPage`/`LoadedInfoText`
6. **搜索返回/清除筛选**：`BackFromSearch` / `ClearFilter` 通过 `PageManager.TryRestorePreSearchState` 恢复搜索前页面状态（先清 page cache 避免搜索结果残留 → 注入保存的 item → 直接调用 `ShowPageAsync`）
7. 剪贴板复制：右键菜单 `复制图片到剪贴板` 单张复制；`Ctrl+C` 批量复制选中图片（使用 `SetFilesAsync` 文件拖放格式，可在 QQ/资源管理器直接粘贴）

## [绝对禁忌]

1. **禁止创建与第三方库同名文件**：如不可创建 `mediapipe.py` / `avalonia.cs` / `skiasharp.cs` 等，会导致导入/命名空间冲突。本地文件请用安全命名（如 `main1.py`、`MyAvaloniaHelper.cs`）

2. **禁止破坏 I/O 与 CPU 分离模式**：`PrecomputeHashesAsync` 和 `SyncFolderAsync` 中的哈希计算已通过 Channel 模式解耦 I/O 和 CPU。修改哈希/导入流程时必须保持此分离，严禁在持有 I/O 信号量时执行 CPU 密集型哈希，或反之

3. **禁止跳过 `_isNavigating` 守卫**：`OnCurrentPageChanged` 中 `_isNavigating` 挡板用于防止页号变化时触发重入加载。修改分页逻辑时必须保留此守卫

4. **禁止在 DB 查询中漏掉 FolderId 过滤**：所有按文件夹查询图片的路径必须通过 `FolderId` 索引，禁止全表扫描 `GetByFolderAsync`（使用 LIKE 匹配路径前缀）作为主路径——该接口仅用于兼容旧数据

5. **禁止修改 `AppSettings` 结构后不更新 `HashVersion`**：如果感知哈希算法或关键设置字段有变化，必须递增 `HashVersion` 以触发全量重算

6. **禁止在 UI 线程上执行文件 I/O 或哈希计算**：所有磁盘读取和哈希计算必须通过 `Task.Run` 调度到线程池

## [当前状态]

### 最近一次成功运行的功能（2026-05-18 更新）
- **架构优化**：
  - 提取 `PageManager`（250 行）：页面缓存、缩略图加载、缩放控制、搜索恢复
  - 提取 `TagSearchController`（280 行）：标签搜索解析、前缀联想、共现标签、状态循环
  - VM 从 1751 行降至 ~1100 行，移除 `_pageItemsCopy` 死代码，统一 `PageSize` 常量
  - `ImageViewItem.NotifyAll` 10→5 次 PropertyChanged（移除 `[ObservableProperty]` 自动通知的冗余触发）
  - XAML 绑定零改动
    - **内存优化**：1) `PrecomputeHashesAsync` 全文件读入 → `DecodeForHashInput(path, 256)` 低分辨率解码（两段法：原生比例→Resize），Channel 内存从 50MB+/张 降至 ~15KB/张。2) `ThumbnailCacheService` 内存缓存添加 50MB 上限，`AddToMemory` 自动触发 LRU 淘汰。3) `PrecomputeHashesAsync` 3 处调用点加 3 秒延迟启动 + I/O 并发降至 2。4) `LoadPageThumbnailsAsync` 每 10 张 yield。I/O/CPU 分离保持不变
- Tag 编辑窗口自动推荐：全量展示（移除数量上限）、显示使用次数 "name (count)"、ToggleSwitch 控制是否显示数量、点击添加时仅取 tag 名不含计数
- Tag 搜索新增 `-every` 关键字：筛选未标记任何 Tag 的图片（`GetFilePathsWithNoTagsAsync` SQL: `WHERE Id NOT IN (SELECT ImageMetaId FROM ImageTag)`）
- 帮助窗口（设置 → 帮助）：展示完整 Tag 搜索规则语法、共现标签模式、快捷键
- Tag 搜索栏交互优化：输入搜索 → 点击图片/空白 → 自动失焦关闭联想框 → 再次聚焦搜索框 → 联想框重开（co-tag 模式复用已加载建议，前缀模式重建）；Enter 搜索后自动失焦
- 缩略图加载优先可见区域：首屏 item 优先加载（`EstimateVisibleItemCount`），其余后台排队，SemaphoreSlim(4) 不变
- 深色模式输入框文字可见性：所有 TextBox 背景改用 `DynamicResource InputBgBrush`（浅色 #FFF / 深色 #1A1A24）
- 修复前缀模式点击 tag 建议崩溃（`SelectTagSuggestion` 绕开 backing field 避免 mid-click 清空 ObservableCollection 导致 Avalonia NPE）
- 修复返回搜索前按钮页表恢复（`BackFromSearch` / `ClearFilter`：直接调用 `ShowPageAsync` + 先清 page cache 再注入恢复页，消除搜索结果残留）
- 修复连续删除图片时僵尸图重现（`RemoveFilesFromView` 开头调 `_pageManager.InvalidateCache()` 清除脏缓存，防止后续 `ShowPageAsync` 命中旧缓存返回已删除项）
- 删除图片时同步清理磁盘缩略图缓存（`DiskThumbnailCache.DeleteAllWidths` 遍历所有 `w*` 子目录删除对应 `.jpg`）
- 移除文件夹时同步清理该文件夹下所有图片的磁盘缩略图缓存（`RemoveFolderAsync` 通过 `GetByFolderIdAsync` 查全量路径后逐条删除）
- 右键在线搜图自动上传（SauceNAO / IQDB / ascii2d / trace.moe 通过 HTTP POST 直接上传图片，结果自动在浏览器打开；Google / Yandex / soutubot 打开首页手动上传）
- 文件夹实时监控（FileSystemWatcher：外部新增/删除文件自动 sync + 哈希补算）
- 大文件夹导入哈希补算修复（sync → hash 串行，消除时序竞争导致新文件漏算哈希）
- 批量复制图片到剪贴板（Ctrl+C 选中多张，SetFilesAsync 文件拖放格式）
- 大文件夹快速导入（Channel 模式 I/O/CPU 分离 + 批量 DB upsert）
- 标签组合搜索（a/o/e/- 四种运算符 + 共现标签循环）
- 相似图片搜索（直方图预过滤 + 多哈希投票）
- MD5 精确去重
- 深色/浅色主题切换
- 三种瀑布流布局（Grid / Vertical Masonry / Horizontal Justified）
- 缩略图缩放滑块（离散级别插值 + 防抖重载）
- 数据迁移（旧 JSON → SQLite）
- 文件夹重定位（外部路径变更后更新 DB）

### 最近的 Git 提交
- 2026-05-17：PageManager 提取（架构优化）、Tag 编辑优化、搜索框交互改进、缩略图可见区域优先加载、深色模式修复、前缀点击/返回搜索 bug 修复
- `be379fd` — Revert "路径修改前备份"
- `1ffa895` — 路径修改前备份
- `296f308` — 添加项目文件
- `9ae474e` — 添加 .gitattributes 和 .gitignore

### 未完成的 TODO / 已知问题
- `DuplicateService` 中模糊重复（fuzzy）尚未实现，当前仅做精确 MD5 去重
- `Microsoft.ML.OnnxRuntime` 包已引入但尚未在任何代码中使用（预留给未来的 ML 功能）
- `App.SetValue(ItemWidthProperty, ...)` 在 `SmartWaterfallPanel` 中有静态方法调用但未使用 `SetValue`（代码审查项）
- 文件夹搜索/过滤功能仅有 UI 绑定 `FolderSearchText`，未实现实际过滤逻辑
- `StopSearch` 命令当前为空实现
- `SmartWaterfallPanel` 无 UI 虚拟化，200 个元素全量 measure/arrange

## [环境依赖]

| 依赖 | 版本 | 用途 |
|------|------|------|
| .NET SDK | 8.0 | 运行时 |
| Avalonia / Avalonia.Desktop / Avalonia.Themes.Fluent / Avalonia.Fonts.Inter | 12.0.2 | 跨平台 UI 框架 |
| CommunityToolkit.Mvvm | 8.4.1 | MVVM 源生成器 |
| SkiaSharp | 3.116.1 | 图像解码/缩略图/哈希 |
| Microsoft.Data.Sqlite | 8.0.11 | 嵌入式数据库 |
| Dapper | 2.1.35 | 轻量 ORM |
| Microsoft.Extensions.DependencyInjection | 8.0.1 | DI 容器 |
| Microsoft.ML.OnnxRuntime | 1.26.0 | (预留，未使用) |
| 数据库路径 | `%LocalAppData%/ImageManager/data.db` | SQLite WAL 模式 |
| 磁盘缓存路径 | `C:\ImageManagerCache`（默认，可配置） | 缩略图磁盘缓存 |
| 崩溃日志 | `桌面/ImageManager_crash.log` | 未处理异常日志 |
