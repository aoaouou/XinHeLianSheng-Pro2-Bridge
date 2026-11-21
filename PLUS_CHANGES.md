# Plus 版改动说明（相对官方 pro2-v3.2.7）

本仓库是 `NS2Pro/Plus` 的源码基线。上游说明见同目录 `README.md`（官方原文）。

## 基线

| 项 | 值 |
|---|---|
| 上游 | https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge |
| Tag | `pro2-v3.2.7` |
| 应用版本 | `3.2.7` / `ProductVersion: 3.2.7-plus*` |

## 相对官方 v3.2.7 的生产代码改动

**只有 Pro1 模式的振动参数换算**（两个文件）：

- `source/final-three-in-one/windows/v60_viiper_app/Switch1RumbleConverter.cs`
- `source/final-three-in-one/windows/v60_viiper_app/Pro2RumbleFrequencyPolicy.cs`
- 配套测试：`.../tools/tests/pure_software_core_test/ConvertedRumbleEncodingRegressionTests.cs`

### 问题

`TryMap()` 把 Pro1 帧解出的频率（Hz）直接当作 Pro2 的 10-bit 频率控制值：

```csharp
Pro2RumbleFrequencyPolicy.FromEstimatedHz(left.LowHz)   // Hz 当控制值
```

Pro1 的频段字段是**指数刻度**（dekuNukem `rumble_data_table`：
`Hz = 10 * 2^(code/32)`，高频带 `0x04..0xFC` → 约 82~313Hz，低频带 → 约 41~626Hz），
而 Pro2 的 10-bit 频率控制（`0x187`/`0x112`）物理含义未标定
（项目自标 `legacy_hz_proxy_unvalidated`）。两者跨量纲，导致音色信息丢失、
听感退化为普通震动。

### 修法

保留两个 SDL 标定控制值 `0x112`/`0x187`，只换算幅度。

### 边界

- 未做实体手柄听感验证。
- 固定参考值会丢弃 400Hz 以上的强调能力（需要 Pro2 频率控制实测曲线，
  本项目无此数据，故未编造映射）。

## 未做的改动（重要）

**viiper（Go 后端）保持官方原样，一行未改。**

曾一度修改 `device/ns2pro` 的 0x09 报告布局（按键移到 `b[5..9]`、摇杆移到
`b[11..17]`），试图解决"Ryujinx 能识别但按键无反应"。后来实测证明：

1. **原布局在 Ryujinx/SDL3 下工作正常**；
2. 真正原因是 Ryujinx 目录缺 `libusb-1.0.dll`——SDL3 的 Switch2 驱动
   `HIDAPI_DriverSwitch2_InitUSB` 第一行就强制 `SDL_InitLibUSB`，
   没有它整条驱动初始化失败，设备退化成通用 HID
   （`isGamepad=False`、`name='If_Hid'`、`axes=4 buttons=21`），按键恒为 0；
3. 补上 DLL 后，**原版 viiper 加 libusb 即可正常使用**。

所以那次布局改动是多余且有风险的，已全部回退。
详见 `../release/README_真正根因_libusb缺失.md`。

## 构建

```bash
# global.json 钉死 SDK 8.0.420；本机若只有更高版本，需临时把
# rollForward 改为 latestMajor，构建完记得还原
cd source/final-three-in-one/windows/v60_viiper_app
dotnet publish -c Release -p:PureSoftwareRelease=true \
  -p:Version=3.2.7 -p:InformationalVersion=3.2.7-plus \
  -p:AssemblyVersion=3.2.7.0 -p:FileVersion=3.2.7.0 -o <输出目录>
```

测试（必须带 `PURE_SOFTWARE_RELEASE`，否则 `TestReleasePackaging` 会失败）：

```bash
cd source/final-three-in-one/tools/tests/pure_software_core_test
dotnet run -c Release -p:DefineConstants="PURE_SOFTWARE_RELEASE" -- <输出目录>
```

## 对 Ryujinx 的要求

**必须在 Ryujinx 目录放 `libusb-1.0.dll`**（本机已放，备份在
`../reference/libusb-1.0.28/libusb-1.0.dll`）。
Ryujinx 更新换目录后需重新放一次。
