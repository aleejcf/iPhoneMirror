# Issue #52 修复与验证（2026-10-13）

依据：[issue #52 最新实测反馈](https://github.com/RayrenSX/iPhoneMirror/issues/52#issuecomment-6130243819)。
本次基于当前工作区实现，保留已有未提交改动，没有整体应用附件中的累计补丁。

## 修改

- iOS 27 通过可信 USB 的 CoreDevice/RSD/Cryptex 服务准备 DDI，避免进入旧的
  BoardID/ChipID 匹配流程；其他系统保持旧流程。
- 镜像固定到上游提交 `6eae353ae694bda1c421d4a3eee5459ae59c99a1`，校验五个文件的
  大小、SHA-256、`27A5228h` 构建号和 Apple manifest 中的 payload SHA-384。
  下载连接或 HTTP 错误时，从 raw GitHub 回退到同一提交的 GitHub API。
- 使用 pymobiledevice3 11.3.1 的资产解析、nonce domain handle、Cryptex TSS tags
  和安装参数。签名请求继续使用可取消的异步 HTTP；设备负责验证 Apple ticket。
- 保留已安装镜像，安装后检查 inventory，再关闭准备隧道并重建控制 RSD。
  反控仍必须通过真实 HID surface 检查。无线缺少 HID 时，先通过指定手机的 USB
  准备镜像，再重试无线；不会将无线输入偷偷改为 USB。
- USB QuickTime 描述符已存在、活动配置尚未就绪时，进入既有 5 秒等待机制。
  只使用当前句柄读取描述符和 GET_CONFIGURATION，保留取消、超时、配置一致性
  检查及原有受限 SET_CONFIGURATION 条件。
- 激活日志区分 PnP 重新枚举成功与活动配置验证完成，避免 `stable=true` 造成误判。
- 打包显式包含 Cryptex 模块，桥接 stdout/stderr 使用 UTF-8。

## 已执行验证

- Python 完整 `unittest discover -s tests -p '*test.py'`：425 项通过。
- Release Core DLL 与相关原生测试编译成功。
- Release CTest：CoreProtocolTests、QtUsbTransportTests、LibUsb0ActivationTests、
  UsbConfigurationRestorePolicyTests、UsbConfigurationSwitchIdentityTests，5 组全部通过。
- 新原生测试调用真实 `LibUsb0Connection::open_quicktime`，用假的 libusb0 API
  模拟描述符已出现而配置延迟从 3 到 5、描述符延迟、已激活设备、无激活授权、
  受限 pipe-table 初始化、取消及持续停留配置 3 的超时。不连接真实 USB 设备。
- 固定上游五个 Cryptex 文件实际下载后，全套文件哈希、构建号和 Apple payload
  digest 校验通过；未请求真实 Apple ticket 或安装到手机。
- PyInstaller 桥接器构建成功，成品 `--check-runtime` 能加载 Cryptex 模块及两种
  USB 后端，所有运行时功能检查通过。
- `scripts/test_usb_bridge_build_source.ps1` 与改动文件 `git diff --check` 通过。

## 待实机验证

没有 iPhone19,2 / iOS 27.0.1 的实机复测结果。Cryptex 方向依据提交者的真实设备
成功反馈；USB 等待修改修复的是已确认的中间状态处理缺口，不能证明设备持续停留
配置 3 时等待能解决问题。

需要在该手机上验证：首次 DDI 准备、USB 音视频、有线输入、拔线后的无线输入、
停止/重开、插拔恢复及长期帧率。最终 ZIP 和 Setup 尚未制作或验收，不能将上述
源码/桥接构建测试当成完整发布包或 issue 已彻底解决的证明。
