# 全仓库代码审查（2026-10-15）

## 最终复审与收敛结果

用户要求继续审查全部修复后，重新核查前述 3 项修复、已有 API/绑定/设置/输入修复及同时更新的 Cryptex 生命周期代码。前述 3 项回归继续通过；复审又确认并处理下列问题，完成本轮可在本机执行的验证。

1. **WPF 测试绕过关闭流程，遗留全局 D3D 预览。** 对旧转储使用 PE unwind 表展开栈，确认异常发生在 `LdrShutdownProcess` → Core CRT 退出析构 → 全局 `preview_renderer` 的析构 → D3D11/AMD COM Release。进一步对旧 Core DLL 反汇编：`Core+0xD7695` 是全局预览退出析构，`Core+0x91DC8` 为 renderer 成员的 COM Release。此前仅扫描原始栈不足以定位；本轮使用展开栈与反汇编补全证据。测试 helper `CloseWorkspaceTestWindow` 设置 `_allowClose=true`，绕过了应用的正常清理。现显式 Dispose HwndHost、在 UI SynchronizationContext 中等待 VM Shutdown，并断言关闭后 `ForcePreviewRefresh` 为 false。**UI 专项和完整 Runtime Tests 均以 0 正常退出**。这是本机测试夹具退出问题的修复，未修改 AMD 驱动，也没有将其表述为修复所有 AMD/实机退出问题。
2. **Cryptex 清理超时可能覆盖已知断连原因。** 复审第一次全量 Python 运行实际失败：已有 `test_connect_exception_is_preserved_even_when_close_stalls` 得到 `developer_image_service_timeout` 而非 `apple_connection_lost`。根因为 SDK 在连接异常处理内进入 close，外层操作预算恰在清理期间到期。现保留进入清理时的原始异常，在被取消的清理完成 abort 后继续交还它；没有原始错误时仍正常传播用户取消/超时。新增强制“操作期限早于清理期限”的回归，不放宽原断言。16 项真实 SDK 生命周期专项和全部 443 项 Python 测试通过。
3. **模拟编码器测试启动了错误进程。** 用 `dotnet <dll>` 运行时 `Environment.ProcessPath` 是 dotnet.exe，原夹具因此产生 `dotnet--hide_banner` 诊断，却未真正执行模拟程序。现显式使用本测试项目生成的 apphost 并检查文件存在，完整逻辑套件通过且不再出现该诊断。

还发现此前托管测试输出引用的是 10 月 13 日的暂存 Core DLL，而本机 native 构建已是 10 月 15 日版本。本轮按构建流程更新忽略目录 `src/App/native` 的暂存 DLL，再构建 Runtime Tests，确认测试目录与本机 native 产物 SHA-256 完全相同：`2868F4270F11A6AF7358E3455CE1672D65E7E5AA8A582D46D42F149598B69D33`。没有把旧 DLL 的测试等同于当前原生源码验证。

最终验证记录（所有列为通过的命令均确认实际退出码 0）：

| 验证 | 结果 | 日志（均位于 `work/`） |
| --- | --- | --- |
| 主程序、Runtime/Logic/Driver Tests、蓝牙工具构建 | 通过，0 警告、0 错误 | `reaudit-20261015-*-build.log` |
| 完整 Logic Tests，含 3 项修复回归 | 通过，无 dotnet--hide_banner 诊断 | `reaudit-20261015-logic.log` |
| 完整 Runtime Tests 默认入口 | **通过，进程正常退出** | `reaudit-20261015-runtime.log` |
| UI 专项，含逐窗口原生清理断言 | 通过，进程正常退出 | `reaudit-20261015-ui.log` |
| DriverInstaller.Tests | 通过，只读识别及模拟测试 | `reaudit-20261015-driver.log` |
| CTest | 17/17 通过 | `reaudit-20261015-ctest.log` |
| 原生静态画面像素、旋转与故障重试 | 子 HWND 和 DirectComposition、直接/限尺寸路径均通过 | `reaudit-20261015-preview.log` |
| Python unittest | 443 项通过 | `reaudit-20261015-python.log` |
| 自动化启动与取消长专项 | 46 秒启动、管理锁、已有/新建会话清理、临时配置和换密钥通过 | `reaudit-20261015-automation-lifecycle-long-startup.log` |
| 键盘焦点、远程文本及多设备输入隔离 | 通过，输入包在内存捕获 | `reaudit-20261015-keyboard-focus.log` |
| DDI 启动、用户操作等待及错误展示 | 80 项错误路由及相关状态通过 | `reaudit-20261015-{ddi-startup,control-error-presentation}.log` |
| 设置工具/绑定流程 | 取消、目标稳定性、保存、重插、删除抑制、四语言与两种主题通过 | `reaudit-20261015-setup.log` |
| 驱动清理安全模拟、清理本地化、桥接源码暂存 | 通过，未运行真实驱动清理入口 | `reaudit-20261015-test_*.log` |
| 语言资源校验、SRS HTTP | 通过 | `reaudit-20261015-{verify_localization,srs}.log` |
| 脚本语法与 diff 检查 | PowerShell 62 个脚本、Python 66 个文件通过；diff 无空白错误 | 本轮工具输出、`reaudit-20261015-diff-check.log` |

桥接器从本轮源码重新暂存、打包。将 `dist`、`outputs/iPhoneMirror/tools`、`outputs/iPhoneMirror.Installer/tools` 的三件套（EXE、`_internal`、manifest）同步到同一版本，原载荷保存在 `work/reaudit-20261015-previous-runtime/`，未删除备份。三处运行时、清单全部文件哈希及归档 DDI 代码与源码的递归字节码比较均通过；应用侧也确认新桥接器完整性。EXE SHA-256 为 `A369449428C49AF7989E77716D3E056A7F67311E8CCE49AD2956F481AE42BC34`，证据见 `reaudit-20261015-bridge-{build,sync,archive}.log`、`reaudit-20261015-bridge-artifacts.json`、`reaudit-20261015-packaged-ddi.log`。

本轮确认的源码与测试夹具问题均已修复，没有剩余已确认但未处理的本轮发现。验证边界仍包括：未操作真实手机/蓝牙输入、未安装卸载驱动、未调用 Apple TSS 或挂载真实 DDI、未验收最终 ZIP/Setup；同步安装器载荷目录不等于重新生成或验证最终安装包。以下各节保留此前失败与修复历史，其“AMD 仍未解决”等描述为当时状态，以本节的本机复测结论为准。

## 修复与复测记录

用户随后要求修复，下述 3 个确认问题现已处理，保留工作区原有修改：

| 问题 | 修复 | 回归验证 |
| --- | --- | --- |
| 并行输出覆盖原生读回缓冲区 | 新增 `NativeOutputReader`，每份输出服务独立持有并复用视频/音频缓冲区；不保留 reader 的兼容查询返回独立快照 | 两路不同尺寸的读回交错执行，待发布帧保持正确，同一路复用缓冲区，音频包不被后续读取修改，空/已关闭会话不触及原生接口 |
| 创建下载文件失败后删除已有文件 | 单流和分段下载都记录本次文件创建是否成功，只有获得所有权才清理；删除分段回退入口的重复清理 | 两种下载路径均保留已有文件内容；本次创建的单流/分段残缺文件仍会清理 |
| 录制另存回退自锁 | 将复制逻辑移至 `PendingRecordingStore.CopyAndDeleteAsync`，关闭并刷新两个文件流后才删除源文件 | 复制完成后源文件可删除、目标字节正确且可独占打开；目标锁定导致失败时保留源文件，解除锁后可以重试 |

新增回归：[ReviewFixRegressionTests.cs](../src/App.Logic.Tests/ReviewFixRegressionTests.cs)，已接入逻辑套件默认入口，也可用 `--review-fixes` 单独运行。缓冲区回归只替换原生复制函数的实现，运行真实 reader、帧载荷与 FrameLatch；不连接手机。

修复后验证：

- 主程序、Runtime Tests、Logic Tests、DriverInstaller.Tests Release 构建通过，0 警告、0 错误。
- 新增回归、完整 Logic Tests、DriverInstaller.Tests、`Runtime Tests --media-review` 均以 0 退出。完整逻辑套件仍含已有的两次 `dotnet--hide_banner` 子进程诊断。
- 使用模拟原生 DLL 复测生产 `CreateOutputReader` 路径，输出 `NV12 same array=False`、`recorded=0x32, expected=0x32`、`Recording buffer reused=True`；下载文件碰撞后输出 `previous file exists=True`。
- **完整 Runtime Tests 仍失败**：断言执行完毕并打印通过后，进程退出码仍为 `-1073740791`（`0xC0000409`）。19:51:43 的 Windows 事件与新转储 `dotnet.exe.38724.dmp` 再次确认 `amdxx64.dll+0xDC4BF5`、fast-fail 参数 7。不能据此认定 AMD 退出故障已修复，也未据不完整证据改动图形驱动或原生渲染代码。
- 本轮没有原生/Python 产品代码变更，未重复上一轮已通过的 CTest/Python 全套；没有执行真实手机、驱动变更及 ZIP/Setup 验收。

日志：`work/fix-review-20261015-{regression,logic,driver,media,native-probe,runtime,runtime-crash,dump}.log`，各项目构建日志使用相同前缀加 `-build.log`。上述 3 项修复完成，AMD 退出故障仍为独立未解决项。

## 原始审查记录

审查基线为 `829c591` 上的当前工作区，包含已有未提交修改与新增文件。审查覆盖 WPF 主程序、设备身份与会话、输入与 Automation API、媒体输出、C++ 采集/协议/渲染、虚拟摄像头、Python 反控/DDI、驱动管理、共享下载/提权逻辑及构建脚本的关键路径。源码及配置清单共 630 项；这是模块与风险路径审查，不能解读为对每个文件逐行验证或对第三方二进制作安全背书。

本轮没有修改产品源码。只新增本报告，并将模拟原生接口的复现程序和日志放在忽略目录 `work/` 中。没有执行驱动变更、向手机发送实际输入或进行发布包验收。

## 已确认问题

### 1. [P1] 并行录制和推流共用可变原生读回缓冲区

- 引入并发的位置：[MainViewModel.cs:1205](../src/App/ViewModels/MainViewModel.cs#L1205)。两份 `MediaOutputService` 都调用同一 `_core` 的帧和音频提供器。
- 共享缓冲区的位置：[NativeCore.cs:1170](../src/App/Interop/NativeCore.cs#L1170)、[NativeCore.cs:1183](../src/App/Interop/NativeCore.cs#L1183)。`GetDeviceOutputNv12Frame` 复用并返回实例字段 `_outputNv12FrameBuffer`，没有互斥或独立所有权。`GetDeviceOutputAudioPacket` 也复用 `_outputAudioBuffer`，复制为独立音频包之前仍有并发覆盖窗口。
- 触发：同时录制与推流，两路 `FrameReaderAsync` 各自在后台读帧。A 完成原生读回后、将帧复制进自己的 `FrameLatch` 前，B 可以覆盖同一个数组；不同输出尺寸时，A 会拿自己的宽高解释 B 写入的数据。
- 后果：录制或直播可能出现错误画面、撕裂、颜色/布局损坏，音频也可能被并发覆盖。缓冲区字段与传给原生函数的容量分别读取/更新，首次并发分配还存在数组与容量不一致的窗口，优先处理此处原生边界的竞态。
- 复现：保留真实 `NativeCore.GetDeviceOutputNv12Frame`、`GetNv12FramePayload` 和 `FrameLatch`，仅将 P/Invoke 的原生帧源替换为无设备的模拟 DLL。先读 320×320、暂停发布，再读 160×160，随后发布第一帧，得到：

  ```text
  NV12 same array=True, before=0x32, recorded=0x16, expected=0x32
  ```

- 建议：按输出消费者分配读回缓冲区并维持从原生读回到入队复制完成的所有权；或者在统一锁内完成读回与独立快照。只锁原生函数调用、返回后继续共享数组，仍不能消除覆盖。增加两种分辨率并行输出的真实缓冲区回归，现有 coordinator/UI 测试使用模拟会话，未覆盖这层。
- 证据：`work/review-20261015-probe/Program.cs`、`work/review-20261015-probe/copy_stub.c`、`work/review-20261015-probe.log`。复现未尝试触发越界写入。

### 2. [P2] 下载器创建失败时删除原本已存在的目标文件

- 位置：[SegmentedHttpDownloader.cs:149](../src/Shared/Networking/SegmentedHttpDownloader.cs#L149)、[SegmentedHttpDownloader.cs:181](../src/Shared/Networking/SegmentedHttpDownloader.cs#L181)；分段路径在第 200、262 行有相同问题。
- 触发：调用 `DownloadAsync` 时，目标文件已经存在。`FileMode.CreateNew` 按预期抛出 `IOException`，但外层 `catch` 无条件执行 `TryDelete(destination)`。
- 后果：本次请求没有创建或获得该文件的所有权，却在失败清理时删除它。重复调用、并发下载或目标名碰撞会使原有文件丢失。上层更新器自身也使用固定的 `.download` 路径，不能依赖本类的 `CreateNew` 来保护并发文件。
- 复现：使用实际应用程序集中的下载器、内存 HTTP 响应及一个预先写入的临时文件。调用抛出 `IOException` 后，输出：

  ```text
  Download CreateNew collision: previous file exists=False (expected True)
  ```

- 建议：仅在本次调用成功创建目标后才删除它，并对普通和分段路径统一处理；若要支持并发调用，应使用每次请求独立的临时路径和最终提交。
- 证据：同一 `work/review-20261015-probe/Program.cs` 和 `work/review-20261015-probe.log`。HTTP 响应完全在进程内模拟，没有访问外网。

### 3. [P2] 录制另存的复制回退在关闭源文件前删除它

- 位置：[MediaOutputSettingsWindow.xaml.cs:361](../src/App/Windows/MediaOutputSettingsWindow.xaml.cs#L361)、[MediaOutputSettingsWindow.xaml.cs:369](../src/App/Windows/MediaOutputSettingsWindow.xaml.cs#L369)。
- 触发：`File.Move` 抛出 `IOException` 后进入复制回退，且复制成功。这里的 `await using var source` 以 `FileShare.Read` 打开源文件，直到 `catch` 代码块退出时才关闭；`File.Delete(pending)` 却在同一块内先执行。
- 后果：Windows 拒绝删除仍被该句柄打开、未允许删除共享的源文件。目标已复制完成，界面却报告保存失败，`MarkPendingRecordingSaved` 不执行，待保存状态仍保留；新逻辑又会阻止有待保存文件时开始下一次录制。
- 验证：以相同的打开模式复制临时文件，在释放源句柄前删除，稳定得到“文件正由另一个进程使用”的错误。验证的是复制回退的文件句柄顺序，没有自动操作保存对话框。并非声称每次跨卷移动都会进入回退，正常 `File.Move` 可以处理跨卷。
- 建议：将复制和 Flush 包在独立的 `await using` 作用域内，两个流关闭后再删除源文件并更新待保存状态。
- 证据：`work/review-20261015-save-fallback.log`。

## 测试结果与限制

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| 主程序与 Runtime Tests Release 构建 | 通过，0 警告、0 错误 | `work/review-20261015-runtime-build.log` |
| Logic Tests Release 构建及执行 | 退出码 0；仍输出两次已有的 `dotnet--hide_banner` 子进程诊断，不据此认定这些子进程场景正确 | `work/review-20261015-logic-build.log`、`work/review-20261015-logic.log` |
| DriverInstaller.Tests 构建及执行 | 通过 | `work/review-20261015-driver-build.log`、`work/review-20261015-driver.log` |
| 原生 Release 构建与 CTest | 通过，17/17 | `work/review-20261015-native-build.log`、`work/review-20261015-ctest.log` |
| Python unittest | 通过，425 项 | `work/review-20261015-python.log` |
| Automation 生命周期专项 | 通过，含取消、锁等待、原生创建清理、后台启动及换密钥 | `work/review-20261015-automation.log` |
| 完整 Runtime Tests 默认入口 | **失败**，虽已打印全部通过，进程最终退出码为 `-1073740791`（`0xC0000409`） | `work/review-20261015-runtime.log`、`work/review-20261015-runtime-crash.log` |
| SRS HTTP 异常请求 | 通过，1/1 | `node --test scripts/test_srs_lab_server.mjs` 本轮输出 |
| 蓝牙压力工具 Release 构建 | 通过，0 警告、0 错误；未运行实机压力操作 | `work/review-20261015-bluetooth-build.log` |
| PowerShell/Python 全量语法解析 | 通过，分别 63/65 个文件 | 本轮解析输出，PowerShell 错误列表为空 |
| 四语言资源与引用检查 | 通过，App 每种 1599 键，Driver 每种 199 键 | `scripts/verify_localization.ps1` 本轮输出 |
| `git diff --check` | 通过；输出仅含已有换行符转换提示 | `work/review-20261015-diff-check.log` |

完整 Runtime Tests 的失败由 Windows Application Error 1000 事件确认：2026-10-15 19:34:38（中国时间），测试进程 PID 41640，故障模块 `amdxx64.dll`，版本 `32.0.21032.8002`，偏移 `0xDC4BF5`，异常 `0xC0000409`。这是已有审查记录中 AMD 退出故障的再次出现。仅凭事件不能确定应用源码根因，也不能将其归因于上述并行输出问题；本轮将它保留为独立的未解决验证故障。

本轮没有执行所有专项 WPF 入口、真实 USB/AirPlay/DDI/蓝牙输入、真实驱动安装或 ZIP/Setup 验收。通过现有测试不代表上述三个问题已修复，修复后应分别添加对应回归并复测。
