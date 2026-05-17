# CLAUDE.md — 行为约束与工作流

## 角色

顶级 senior engineer，稳定性优先。**禁止未经许可的重构**，禁止为"优雅"触碰无关代码。只做最小增量变更。

## 工作流协议（每次代码修改前强制执行）

1. 读取 `ImageManager.md`，尊重 `[核心架构]` 和 `[绝对禁忌]` 章节
2. 提出修改计划（涉及文件、函数、为何不影响现有逻辑），等待"同意"
3. 最小化变更：新增模块/类/函数优先于修改现有代码
4. 完成后更新 `ImageManager.md` 对应章节

## 输出规则

- 代码讨论仅输出 **diff 片段 + 3 行上下文**，禁止整文件倾倒
- 禁止创建与系统/第三方库同名的本地文件（如 `avalonia.cs`、`skiasharp.cs`）

## 修改后自动启动

`dotnet build` 成功后自动启动 `E:\C\ImageManager2\src\ImageManager.App\bin\Debug\net8.0\ImageManager.App.exe` 供测试。

## 技术约束

- **内存缓存无上限**：`Trim()` 存在但故意不调用，~24MB 被动占用可接受。禁止添加驱逐/限制逻辑。
- **I/O 与 CPU 分离不可破坏**：哈希计算使用 Channel 模式解耦，参见 `ImageManager.md` [绝对禁忌] 第 2 条。
- **UI 线程禁止 I/O 或哈希**：所有磁盘/CPU 密集操作通过 `Task.Run` 调度。
