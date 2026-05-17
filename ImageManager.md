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
- **DI**：`App.axaml.cs` 中 `ConfigureServices()` 完成 DI 注册（Singleton: 所有 Repository/Service；Transient: `MainWindowViewModel`）
- **入口**：`Program.cs` → `BuildAvaloniaApp()`，崩溃日志写入桌面 `ImageManager_crash.log`
- **主 ViewModel**：`MainWindowViewModel` — 约 1650 行，管理文件夹列表、图片分页（每页 200 张）、标签搜索、缩略图缩放、排序、方向筛选、相似/重复检测
- **布局**：自定义 `SmartWaterfallPanel`（继承 `Panel`），支持三种模式：Grid（默认网格）、Vertical（Masonry 瀑布流，最短列优先）、Horizontal（Justified 等行高自适应）
- **设置窗口**：外观主题、壁纸、窗口大小、缩略图参数、快捷键、内存/缓存、标签编辑
- **预览**：`PreviewViewModel` 支持图片导航、缩放
- **MVVM 框架**：CommunityToolkit.Mvvm 8.4.1（`[ObservableProperty]`、`[RelayCommand]`、source generators）
- **皮肤系统**：`App.axaml` 中 10+ 个 DynamicResource 笔刷，`ApplyColors(bool dark)` 切换深色/浅色

### 关键数据流
1. 选择文件夹 → `LoadFolderAsync` → 优先走 DB 索引（`GetByFolderIdAsync`），否则磁盘枚举 → 分页显示。随后串行执行 `SyncFolderAsync` → `PrecomputeHashesAsync`（先同步后哈希，避免新文件漏算）
2. **文件夹实时监控**：`FileSystemWatcher` 监听当前文件夹的 Created / Deleted 事件，2 秒防抖后自动触发 `SyncCurrentFolderAsync`（sync + hash 补算）。拖拽文件到左侧文件夹后同样触发此流程
3. 后台哈希预计算：`PrecomputeHashesAsync` 使用 `Channel<Bounded>(50)` 生产者-消费者模式，I/O 信号量（~12）和 CPU 信号量（~核心数）分离，批量 upsert
4. 标签搜索：支持 `a`（AND-all）、`o`（OR）、`e`（AND-each）、` - `（NOT）四种运算符 + 共现标签循环模式
5. 缩略图加载：`LoadPageThumbnailsAsync` 有 `SemaphoreSlim(4)` 限制并发，`GetOrCreateThumbnailAsync` 先查内存缓存再查磁盘缓存最后 SkiaSharp 生成
6. 剪贴板复制：右键菜单 `复制图片到剪贴板` 单张复制；`Ctrl+C` 批量复制选中图片（使用 `SetFilesAsync` 文件拖放格式，可在 QQ/资源管理器直接粘贴）

## [绝对禁忌]

1. **禁止创建与第三方库同名文件**：如不可创建 `mediapipe.py` / `avalonia.cs` / `skiasharp.cs` 等，会导致导入/命名空间冲突。本地文件请用安全命名（如 `main1.py`、`MyAvaloniaHelper.cs`）

2. **禁止破坏 I/O 与 CPU 分离模式**：`PrecomputeHashesAsync` 和 `SyncFolderAsync` 中的哈希计算已通过 Channel 模式解耦 I/O 和 CPU。修改哈希/导入流程时必须保持此分离，严禁在持有 I/O 信号量时执行 CPU 密集型哈希，或反之

3. **禁止跳过 `_isNavigating` 守卫**：`OnCurrentPageChanged` 中 `_isNavigating` 挡板用于防止页号变化时触发重入加载。修改分页逻辑时必须保留此守卫

4. **禁止在 DB 查询中漏掉 FolderId 过滤**：所有按文件夹查询图片的路径必须通过 `FolderId` 索引，禁止全表扫描 `GetByFolderAsync`（使用 LIKE 匹配路径前缀）作为主路径——该接口仅用于兼容旧数据

5. **禁止修改 `AppSettings` 结构后不更新 `HashVersion`**：如果感知哈希算法或关键设置字段有变化，必须递增 `HashVersion` 以触发全量重算

6. **禁止在 UI 线程上执行文件 I/O 或哈希计算**：所有磁盘读取和哈希计算必须通过 `Task.Run` 调度到线程池

## [当前状态]

### 最近一次成功运行的功能
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

### 最近的 Git 提交（2026-05-14 ~ 2026-05-16）
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
