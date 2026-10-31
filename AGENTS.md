# AGENTS.md

面向在此仓库工作的 AI 编码助手，内容均已对照仓库文件核实。

## 项目概述

- ModPerformanceMonitor（创意工坊名“谁偷我帧数？”，`publishedFileId = 3594997139`）：《逃离鸭科夫》调试 mod，在屏幕右侧显示 FPS 以及各已加载 mod 的运行耗时占比，按 F3 开关显示。
- `ducky7go/Duckov_ModPerformanceMonitor` 是 fork，上游为 `kalonocat/Duckov_ModPerformanceMonitor`（`upstream` 远程，默认分支 `master`）。相对 `upstream/master` 的差异：新增 `.gitignore`（`obj`、`bin`），以及 `ModBehaviour.cs` 中按程序集根命名空间过滤待打补丁类型的 5 行改动（提交“根命名空间过滤”）。

## 仓库结构

- `ModBehaviour.cs`：全部逻辑，命名空间 `ModPerformanceMonitor`，类 `ModBehaviour : Duckov.Modding.ModBehaviour`。`OnEnable` 里 `InitializeAndPatch()`：用 Harmony（id `com.fshelix.modperformancemonitor`）遍历已加载程序集，给含 `ModBehaviour` 子类的程序集中的方法打计时补丁，并订阅 `ModManager.OnModActivated` 给之后激活的 mod 打补丁；`OnDisable` 里 `UnpatchAll` 并清理。
- `ModPerformanceMonitor.csproj`、`info.ini`、`preview.png`。
- `0Harmony.dll`、`Mono.Cecil.dll`：已提交的二进制依赖。

## 构建

- `netstandard2.1`，`Nullable` 开启。通过 `$(DuckovPath)\Duckov_Data\Managed\` 引用 `TeamSoda.*`、`ItemStatsSystem.dll`、`SodaLocalization.dll`、`Unity*`，并引用仓库根的 `0Harmony.dll`；NuGet 包 `Mono.Cecil 0.11.6`、`Newtonsoft.Json 13.0.4`。
- `csproj` 中 `DuckovPath` 硬编码为 `C:/SteamLibrary/steamapps/common/Escape from Duckov`，本地构建需改成本机路径（不要把个人路径提交回来）。没有游戏安装无法编译。
- 无测试、无 CI。发布形态是一个 mod 文件夹（`ModPerformanceMonitor.dll` + `info.ini` + `preview.png`，加载规则见 `ducky7go/duckov_modding` 的 README）。

## 约定与注意事项

- 注释和日志为中文/英文混用，日志前缀 `[ModPerformanceMonitor]`；改动请保持与现有风格一致，且尽量最小（fork 以跟随上游为主）。
- 上游代码中有 `// FIXME Thread Safety?`（`assemblyTotals`/`records` 为静态字典，补丁可能在非主线程触发），改动这一块要考虑线程安全。
- `0Harmony.dll` 是随 mod 发布/引用的副本；游戏本身不内置 Harmony，不同版本的 Harmony 同时加载会冲突（见 `duckov_modding` README），升级 Harmony 前确认版本。
- `info.ini` 在上传创意工坊时会被改写，不要在里面放其他数据。
- 该 mod 开启/停用较慢并可能影响性能（`info.ini` 描述），属预期行为。
