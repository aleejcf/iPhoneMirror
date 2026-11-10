# Issue #52：iPhone19,2 / iOS 27.0.1 投屏及反控失败

原文：<https://github.com/RayrenSX/iPhoneMirror/issues/52>

## 中文翻译

标题：iPhone 18 Pro（iPhone19,2）、iOS 27.0.1 在 v1.8.5-pre2 中无法使用 USB 投屏和有线／无线反控。

环境：iPhoneMirror 1.8.5-pre2；Windows 11 x64；iPhone 18 Pro / iPhone19,2 / iOS 27.0.1；
已安装 iTunes / Apple Mobile Device Support。

驱动状态：没有记录界面显示的准确文字。应用日志报告 Windows x64、
Microsoft Windows 10.0.26300。USB 采集进入打开接口阶段后失败，当前配置为 3，
描述符配置为 5，错误码为 -2103。

复现步骤：

1. 使用 USB 连接手机并运行 v1.8.5-pre2。
2. 分别尝试 A、B、C 三种 USB 投屏模式，均失败。
3. 开启 AirPlay 投屏，画面显示正常。
4. 尝试有线反控，再尝试无线反控，均无法正常启动。
5. 尝试蓝牙反控，输入有效但延迟较高。

预期：这组设备与系统版本可以正常启动 USB 投屏以及有线／无线反控。

实际：AirPlay 正常；蓝牙反控可用但延迟高；USB 三种模式全部失败；有线反控在准备
开发者镜像时失败；无线反控在初始化触控服务时失败，另一些尝试也在准备开发者镜像时失败。

爱思助手、ApowerMirror 和 Air Screen Mirror 已成功在 Windows 上通过 USB 投屏同一台手机。
这提示可能是 iPhoneMirror 自身的兼容性问题，但不能确定根因，也不能证明这些应用使用相同协议。
提交者希望排查 QuickTime USB 配置处理，以及该机型／系统的 Personalized DDI 和 CoreDevice HID 兼容性。

脱敏日志含义（来自多次独立尝试，不是一个连续会话）：

| 日志 | 中文含义 |
| --- | --- |
| `UsbConnection / InterfaceOpen / -2103; active=3, descriptor=5` | 当前 USB 配置 3 与 QuickTime 描述符要求的配置 5 不一致。 |
| `developer_image_download_incompatible` | DDI 构建清单不支持此设备。 |
| `hid_source_rejected`，两个 `com.apple.coredevice.hid.universalhidservice` / `com.apple.coredevice.hid.universalhid` 服务返回 `InvalidServiceError` | 两种 Universal HID 服务均被拒绝。 |
| `hid_service_inventory; product=iPhone19,2; ios=27.0.1; services=[]` | 该设备返回的可用服务列表为空。 |
| `direct_hid_fallback; No such service: com.apple.coredevice.displayservice` | 缺少显示服务，媒体流认证不可用，转而验证直接 Universal HID。 |
| `developer_image_required` | 无线 HID 不可用。请先通过 USB 连接并解锁手机，准备个性化 DDI，再重试无线反控。 |
| 未挂载 Personalized Developer Disk Image | 请挂载与设备系统匹配的官方 Xcode 镜像后重试。 |

提交者已确认移除私人标识及个人内容，并确认这不是安全漏洞。

## 处理决定（2026-10-11）

维护者要求回滚本次针对该 issue 的 USB 等待条件、DDI 构建号校验及相关测试和说明变更。
现已恢复修改前的行为，保留原有未提交改动。

回复先建议重新安装 Apple 驱动（iTunes / Apple Mobile Device Support）、重启 Windows，
重新连接并解锁手机、确认信任此电脑，再尝试 USB 投屏及反控。若仍失败，请提供新的
脱敏日志和驱动状态文字。驱动是优先排查方向，现有日志尚不能确定所有故障的共同根因。
