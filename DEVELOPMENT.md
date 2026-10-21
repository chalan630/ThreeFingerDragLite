# 开发文档（DEVELOPMENT.md）

面向二次开发和重新上手的贡献者。功能与使用层面的说明见 [README](README.md)。

## 1. 项目结构

```
ThreeFingerDragLite/
├── ThreeFingerDragLite.csproj      # net10.0 / WinExe / x64，无任何 NuGet 依赖
├── app.ico                          # 程序图标（原创，由 tools/IconGen 生成）
├── tools/
│   └── IconGen/                     # 图标生成器（GDI+ 绘制多尺寸并打包 ICO，可改参数重新生成）
├── tests/
│   └── ReplayTests/                 # 零依赖控制台测试：假宿主 + 手动时钟跑 DragEngine，可回放 VerboseLog
└── src/
    ├── Program.cs                   # 入口：DPI 感知、单实例互斥、加载配置、启动 App
    ├── App.cs                       # 隐藏窗口 + 消息循环：Raw Input 分发、定时器、注入自检
    ├── AppConfig.cs                 # 配置模型 + 读写 + 《配置说明.txt》生成
    ├── Logger.cs                    # 日志（轮转 1MB；SetEnabled 支持运行时开日志）
    ├── Native/
    │   └── NativeMethods.cs         # 全部 P/Invoke 声明与 Win32 常量（唯一互操作层）
    ├── Touchpad/
    │   ├── TouchpadContact.cs       # 触点结构（ContactId/X/Y）
    │   ├── TouchpadHelper.cs        # Raw Input 注册、HID 报文解析、preparsed 缓存
    │   └── ContactAssembler.cs      # 把"部分触点报告"拼装成完整帧
    ├── Engine/
    │   ├── DragEngine.cs            # 核心：手势状态机（本项目的心脏）
    │   ├── IGestureHost.cs          # 引擎的对外接缝（时钟/键盘状态/注入/窗口操作）
    │   ├── Win32GestureHost.cs      # 真实宿主：委托给下面这些静态类
    │   ├── MouseInjector.cs         # SendInput：按键注入 + 带小数累积的相对移动；绝对定位用 SetCursorPos（不过低级钩子），失败回退 SendInput
    │   ├── KeyInjector.cs           # SendInput：键盘注入自检、Win 修饰键抵消开始菜单
    │   ├── AltWindowDragger.cs      # 修饰键+三指：增量驱动光标与窗口（异步合并提交、边缘即时贴靠、窗口解析与提权检测）
    │   ├── AltWindowResizer.cs      # 修饰键+双指：九宫格缩放（宽高比、刷新率对齐、DPI 最小尺寸）
    │   ├── WindowProbe.cs           # 可见边框、DPI、提权、慢窗口、刷新率
    │   └── Notifier.cs              # 限频托盘气泡
    └── Ui/
        └── TrayIcon.cs              # 托盘图标 + 菜单（含参数子菜单、置灰联动）
```

## 2. 架构与数据流

```
 触控板硬件
    │  (HID 报文, ~100-140Hz)
    ▼
 Raw Input (WM_INPUT, RIDEV_INPUTSINK 后台也收)
    ▼
 TouchpadHelper.ParseInput ── HidP_* 逐 usage 提取触点
    │   （preparsed data 来自缓存，带 0x0D/0x0005 校验）
    ▼
 ContactAssembler.Receive ──── 拼装完整帧（处理拆包的设备）
    ▼
 DragEngine.OnContacts ─────── 状态机判定
    │                            │
    ▼  IGestureHost（测试时换成假宿主）
 Win32GestureHost
    │            Drag 动作        │        MoveWindow / ResizeWindow 动作
    ▼                            ▼
 MouseInjector              AltWindowDragger / AltWindowResizer
 (SendInput 左键+移动)       (SetWindowPos 直接改窗口，含宽高比)
```

**线程模型**：整个程序是单线程 Win32 消息循环（`GetMessage`/`DispatchMessage`）。Raw Input、定时器、托盘菜单回调全部在同一线程——这意味着所有状态（引擎、缓存、注入缓冲）都不需要锁。新增代码请保持"只在 UI 线程触碰状态"的约定。

## 3. 手势状态机（DragEngine）

```
Idle ──恰好3指落下──▶ Armed ──累计移动≥StartThreshold──▶ Dragging ──全部抬离──▶ Holding
 ▲                     │                                     │  ▲                  │
 └─非3指/抬离/出现4+指──┘                           2~3指继续 │  │放回2~3指     超时/单指轻移/4+指
                                                     单指接管│  └──────────────────┘
                                                             └──(系统移动光标,按键保持)
```

**和弦路由**：（指数, 修饰键是否按住）经配置映射到动作。默认 3 指 = Drag；修饰键+3 指 = MoveWindow；修饰键+2 指 = ResizeWindow（`ThreeFingerAction` / `ModifiedThreeFingerAction` / `ModifiedTwoFingerAction`，可选 Drag/MoveWindow/ResizeWindow/None）。4+ 指完整让给系统手势；进行中的拖动/保持遇到 4+ 指立即干净释放。缩放走独立的 Resizing 状态（取参与手指的平均增量），其余动作共用 Dragging/Holding。

关键机制：

- **渐进触发**：Idle 状态下每帧都维护 trust 记录（`UpdateTrust`），所以"先单指移动、再补两指"时原来那根手指已是 trusted，第三指落下立即累计移动、无隔离等待。这是与原版 ThreeFingerDragOnWindows 手感差异的核心。
- **隔离区（quarantine）**：新出现的触点 40ms（`TrustDelayMs`）内不参与位移计算，防止手指落板瞬间的坐标跳变传给光标。
- **修饰键模式生命周期**：武装时采样修饰键，但全程跟踪——`EndWindowDragIfModifierReleased()` 在 Dragging/Holding 每帧和 Tick 里检查，松开修饰键立即放下窗口回到 Idle；武装后才按下的修饰键会把三指手势升级为窗口手势。
- **静默判定**：触控板在手指静止时可能停发报文（本机 Synaptics 实测如此），用"超过 `LiftedSilenceMs` 无报文"判定全部抬离；WM_TIMER（30ms）兜底检测，不依赖新帧到来。

## 4. 互操作陷阱清单（血泪史，二次开发必读）

这些坑全部实际踩过并验证过修复方案，改动相关代码前请先读：

1. **`HidP_GetValueCaps` 的数组参数必须标 `[Out]`**。`HIDP_VALUE_CAPS` 内嵌 `ByValArray` 字段，属非 blittable 类型——无 `[Out]` 时 .NET 只传入不回拷，Windows 填好的数据永远留在原生临时缓冲区里，托管数组保持全零。症状：caps 查询"成功"、每个 usage 取值全部失败、零报错。本项目 v1.0 曾因此全功能失效。
2. **`GetRawInputDeviceList` 不要用结构体数组封送**。本机实测 struct[] 版本拿回全零句柄；用 `IntPtr` 原生缓冲区 + `Marshal.ReadIntPtr/ReadInt32` 按偏移读才可靠（条目 16 字节，dwType 在偏移 8）。
3. **`GetRawInputDeviceInfo(RIDI_DEVICEINFO)` 要求调用方先写 `cbSize`**。缓冲区必须确定性初始化（pinned byte[] + 显式写 4 字节）。
4. **多集合（multi-TLC）设备的 preparsed data 陷阱**：从 WM_INPUT 的 hDevice 查 preparsed 可能拿到描述符但语义可疑；本项目缓存 preparsed 并用 `HidP_GetCaps` 校验 UsagePage/Usage == 0x0D/0x0005，不匹配则回退到"枚举出来的触控板句柄"。设备移除事件（`WM_INPUT_DEVICE_CHANGE` wParam=2）时主动失效缓存。
5. **SendInput 合成事件带注入标记，低级钩子类工具（AltSnap/AltDrag）会无视它们**。想让这类工具响应只有两条路：驱动级注入（没做）或绕开模拟直接调 API（本项目 Alt 模式的做法）。
6. **窗口命中需要 Per-Monitor V2 DPI 感知**：`WindowFromPoint`/`GetCursorPos` 在未声明 PMv2 的进程里拿到的是缩放虚拟化坐标，混合 DPI 多屏下窗口拖动会错位。入口处 `SetProcessDpiAwarenessContext(-4)`。
7. **WM_TIMER 精度约 15.6ms**，只适合 30ms 级兜底，别拿它做精细计时。
8. **`SendInput` 的 `INPUT` 是 union，大小由 `MOUSEINPUT` 决定（x64 上 40 字节）**。只为键盘声明 `type + KEYBDINPUT` 会得到 32 字节的结构，`SendInput` 校验 cbSize 不符直接返回 0（一个事件都不注入，`GetLastError` 也未必有值）——症状是"键盘合成全部静默失败而鼠标注入正常"。键盘结构必须补 8 字节填充对齐到 40（见 `NativeMethods.INPUTK` 的 `_unionPadding`）。与陷阱 1 同族：封送错误不报编译错、不抛运行时异常，只在行为上静默失效。

## 5. 调参与手感指南

| 手感问题 | 调哪里 |
|---|---|
| 太容易误触发拖动 | `StartThreshold` 调大（20/30） |
| 要划很远才开始拖 | `StartThreshold` 调小（6） |
| 光标太快/太慢 | `Speed`（120 标准） |
| 拖动保持太短/太长 | `HoldTimeMs`（默认 1000） |
| 慢速精细移动不够稳 | `Acceleration` 设 0 关闭加速（线性）；或托盘"重新校准指针曲线"后多用单指重新学 |

托盘菜单已内置这些档位；`配置说明.txt` 有完整中文说明。

## 5b. 测试

`dotnet run --project tests/ReplayTests -c Release`：无 NuGet 依赖的控制台测试。`FakeHost` 实现 `IGestureHost`（手动时钟、可设按键状态、事件日志），场景覆盖拖动/保持/续拖、4 指忽略、修饰键移窗与升级、缩放平均增量/停顿/无效目标/宽高比、映射自定义。附加参数传入 `VerboseLog` 生成的 `log.txt` 时，按时间戳回放其中的 `contacts:` 行并打印状态转移。窗口 API 层（DPI、SetWindowPos）仍只能实机验证。

## 6. 调试技巧

- **日志**：`%LOCALAPPDATA%\ThreeFingerDragLite\log.txt`。关键行：`self-test`（启动注入自检是否通过）、`engine:` 前缀（状态机转移）、`diag: WM_INPUT parsed N contacts`（解析层，需 VerboseLog）。
- **逐帧排查**：配置 `VerboseLog: true` → 托盘"重新加载配置" → 复现 → 看 `contacts:` 行（触点 ID 与坐标逐帧变化即解析正常）。
- **分层验证法**（本项目排障实证有效）：写独立小控制台程序复刻单层（只做 caps 查询 / 只做解析 / 只做注入），逐层对照"能用的版本"与"不能用的版本"的每一行差异——`[Out]` 的丢失就是这么找到的。

## 7. 构建

```
dotnet build -c Release
dotnet publish -c Release --self-contained false -o publish
# 自包含单文件（CI/有网环境做）
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

注意：本机离线环境装不了 RID 运行时包，自包含发布需在有网环境或 CI 中执行。

## 8. 已知限制与路线图

- 多精密触控板混流（可做：按设备句柄分流 + 每设备配置）
- 无法拖动提权窗口（可做：Elevator 辅助进程，原版 ThreeFingerDragOnWindows 有参考实现）
- 普通拖动中两指时系统滚动仍会触发（根治需低级鼠标钩子接管滚轮——即路线图上的"惯性滚动/捏合缩放"模块，与 Mac 化体验是同一套基础设施）
- 无自动更新、无图形设置界面（刻意保持极简）
