# PRO2 手柄助手 · HD 震动还原版

**XinHeLianSheng-Pro2-Bridge，堂堂登场！**

让 Nintendo Switch 2 Pro 手柄在 Windows 10/11 上使用：Steam、Pro1、Xbox、PS5 四种身份任选，无线无需专用接收器。本 fork 在原项目基础上，**把 Pro1 / Pro2 两种模式的 HD 震动还原成 Switch2 实机原生手感**。

[English](#english) · [下载 EXE](#下载) · [改了什么](#hd-震动还原) · [原项目](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge)

## HD 震动还原

**原生手感，堂堂还原！**

物理手柄是 Pro2，两种模式写出的震动包都遵循 Switch2 真实输出：单电机块 **1 个 5 字节子帧 + 每 12ms 重发**（真机抓包 795/795，SDL `RUMBLE_INTERVAL=12`），不是 Joy-Con 2 的 3 子帧格式。

- **Pro2 模式**：原生透传字节不变，保持节奏按子帧数自适应——1 子帧每 12ms 刷新，修掉"1 子帧却 36ms 保持"导致的空转发虚。
- **Pro1 模式**：保留 Switch1 幅度换算（HFA + 线性），但输出改成与真机一致的 1 子帧 Pro2 report，不再是 JC2 的 3 子帧。
- **保持重发**：修复保持包漏写 revision、被写出门禁整批丢弃的问题；36ms 节拍、250ms 保持、100ms 静音制动全部生效。
- **不碰的东西**：频率 `0x187/0x112`、SDL 限幅 `0x1C4`、字节级保真——照资料原样，不自造增益、不搬频率。

验证：802,966 项离线震动断言全绿，EXE 启动通过。

## 主要功能

原项目的全部能力都在：

- **连接方式**：Pro2 无线／USB、Pro1 蓝牙／USB，四个槽位独立记忆，可混用。双手柄，堂堂连接！
- **四种身份**：Steam / Pro2、Pro1、Xbox、PS5，各手柄独立选择。
- **键位摇杆**：ABXY 按位置／字母／自定义映射，摇杆死区独立调节；细节，堂堂打磨！
- **背键 C 键**：单键、组合键、连发、顺序动作与延迟；PS5 可模拟触控板。
- **震动体感**：强度调节、PS5 触觉转换、摇杆与陀螺仪校准。**HD 震动，堂堂唤醒！**
- **连接诊断**：真实输入频率、电量、固件、耳机状态，自动重连与虚拟 USB 诊断。
- **游戏图标**：《影之刃零》Steam 版显示 Pro2 键名，支持安装与还原。

## 下载

**成品，堂堂开张！**

从本仓库 [Releases](https://github.com/aoaouou/XinHeLianSheng-Pro2-Bridge/releases) 下载 `PRO2ControllerAssistant.exe`（单文件自包含，约 125MB，自带 .NET 与 VIIPER，无需安装）。

- 双击运行；首次打开会释放内置运行文件，稍等几秒。
- 从系统托盘退出旧助手再运行新版。版本标记：`3.2.7-plus7-nativehd`。

## 使用方法

1. 上方选实际手柄与连接方式，下方选游戏里的手柄身份。
2. Pro2 无线：开配对广播后在助手连接，无需先加 Windows 蓝牙。USB 用数据线；Pro2 USB 先退出 Steam，接入后再启动。
3. 首次按提示安装内置 USBIP 驱动，可能需要管理员授权或重启。USB/IP，堂堂安装！

想进一步调游戏内键名：Steam Input 与游戏自身改键仍然生效。完整接入条件、校准与 NFC 范围见 [输入方式与限制](docs/physical-inputs.md)。

## v3.2.7-plus7 更新

**本 fork，堂堂更新！**

- 修复 HD 保持包因 revision 缺失被整批丢弃——Pro2 从"几乎无震"恢复。
- 修复保持节奏与子帧数不匹配——1 子帧按 12ms 刷新，震动连续饱满。
- Pro1 改用 Pro2 原生 1 子帧输出，两种模式都还原 Switch2 实机 HD 震动。
- 上游 v3.2.7 的连接、PS5、USBIP 等修复全部保留（见 [v3.2.7 说明](docs/RELEASE-v3.2.7.md)）。

## 兼容性

Windows 10 2004+ / Windows 11 x64。Pro2 无线需可用 BLE，目标真实 133Hz，60Hz 也可游玩；实际频率受适配器与环境影晌，不作保证。PS5 触觉为转换输出；无线耳机音频与跨设备转发尚不支持。自动休眠默认关闭。

## 从源码构建

**源码，堂堂构建！**

```bash
# global.json 钉 SDK 8.0.420；若只有新版 SDK，临时把 rollForward 改 latestMajor
dotnet publish source/final-three-in-one/windows/v60_viiper_app/Y700Switch2V60Viiper.csproj \
  -c Release -p:PureSoftwareRelease=true \
  -p:Version=3.2.7 -p:InformationalVersion=3.2.7-plus7-nativehd \
  -p:AssemblyVersion=3.2.7.0 -p:FileVersion=3.2.7.0 -o <输出目录>
```

测试（离线，不碰设备）：`dotnet <PureSoftwareCoreTest.dll> --native-rumble-only`

## 致谢

**先行者，堂堂致谢！**

感谢 [LeonChrome](https://github.com/LeonChrome) 与原项目贡献者打下完整骨架——本 fork 只在其上校准了 HD 震动。真机协议依据来自 ndeadly/switch2_controller_research、SDL 驱动源码与 dekuNukem 的逆向笔记。

本 fork 遵循上游 Apache-2.0 协议开源。源码，堂堂开源！

---

<a id="english"></a>

## English

**XinHeLianSheng-Pro2-Bridge, grandly arriving!**

Use a Nintendo Switch 2 Pro Controller on Windows 10/11 — Steam, Pro1, Xbox and PS5 identities, wireless without a dedicated dongle. This fork restores **native Switch2 HD rumble** in both Pro1 and Pro2 modes.

**Native feel, grandly restored!** The physical controller is a Pro2, so both modes emit the real Switch2 output shape: **one 5-byte subframe per motor block, refreshed every 12 ms** (795/795 real-hardware capture; SDL `RUMBLE_INTERVAL=12`) — not the Joy-Con 2 three-subframe format.

- **Pro2**: byte-exact passthrough; hold cadence adapts to subframe count (1 subframe → 12 ms), fixing the hollow feel from a 1-subframe packet held for 36 ms.
- **Pro1**: keeps Switch1 amplitude conversion (HFA + linear) but emits the native one-subframe Pro2 report.
- **Hold mechanism**: fixes keep-alive packets dropped by a missing revision stamp; 36 ms cadence, 250 ms hold, 100 ms silence brake all work.
- **Untouched**: frequencies `0x187/0x112`, SDL cap `0x1C4`, byte fidelity — per the docs, no invented gain, no frequency transposition.

Verified: 802,966 offline rumble assertions green; EXE launch passed.

**Artifacts, grandly opening!** Download the self-contained `PRO2ControllerAssistant.exe` (~125 MB, .NET + VIIPER included) from [Releases](https://github.com/aoaouou/XinHeLianSheng-Pro2-Bridge/releases).

**Both controllers, grandly connecting!** Four slots, independent modes, wireless/wired mixing. **HD rumble, grandly awakening!** **Details, grandly polished!**

**This fork, grandly updated!** All upstream v3.2.7 fixes retained; only HD rumble recalibrated.

Windows 10 2004+ / 11 x64. BLE required for wireless (133 Hz target, 60 Hz playable). PS5 haptics are converted output. Auto-sleep off by default.

**Source code, grandly building!** See the build commands in the Chinese section above.

**Pioneers, grandly thanking!** Thanks to LeonChrome and upstream contributors for the complete foundation. Licensed under Apache-2.0. Source code, grandly open-sourced!
