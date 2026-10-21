# AGENTS.md — 开发代理上手手册

本文件面向接手本项目的 AI 编码代理（人类开发者也可用作速查）。深度原理见 `DEVELOPMENT.md`，行为规格见 `README.md`。

## 项目一句话

Windows 精密触控板（PTP）手势工具：三指拖动、修饰键（默认 Alt）+三指移动窗口、修饰键+双指调整窗口大小（AltSnap 语义），手势映射可配置。纯 C# / net10.0 / WinExe / **零 NuGet 依赖**，全部 Win32 P/Invoke，无 UI 框架（托盘菜单用纯 Win32）。

## 构建与运行（每次改动的验证循环）

```
taskkill /IM ThreeFingerDragLite.exe /F        # 先杀实例！运行中会锁住输出文件（MSB3027）
dotnet publish ThreeFingerDragLite.csproj -c Release --self-contained false -o publish
publish\ThreeFingerDragLite.exe                 # 后台启动；单实例互斥，重复启动直接退出
```

- 目标机需 .NET 10 运行时；自包含单文件版由 CI 构建（见"发布"）。
- `tools/IconGen`、`tests/ReplayTests` 是独立工程，已被主 csproj 的 `DefaultItemExcludes` 排除，不要并入主工程编译。
- **改状态机后先跑回放测试**：`dotnet run --project tests/ReplayTests -c Release`（零依赖，假宿主 + 手动时钟，合成场景覆盖拖动/保持/修饰键/缩放/映射）。也可 `-- <log.txt>` 回放 `VerboseLog` 录下的真实触点帧，打印状态转移序列。新增手势行为请同步加场景。

## 验证与排障

- 日志：`%LOCALAPPDATA%\ThreeFingerDragLite\log.txt`（超 1MB 轮转 log.old.txt）。启动后 `tail` 此文件确认：
  - `self-test: SendInput rc=1, cursor moved by (≈38,22)` — 鼠标注入通道正常
  - `kbd-self-test: keyboard injection visible = True` — 键盘注入通道正常
  - `engine: ...` / `alt-resize: ...` — 状态机与缩放区域判定
- 逐帧触点日志：配置 `VerboseLog: true`（`diag:` 与 `contacts:` 前缀，量很大，仅排障开）。
- 用户配置：`%LOCALAPPDATA%\ThreeFingerDragLite\config.json`。托盘菜单"重新加载配置"→ `App.ApplyConfig()` 热重载（就地改共享实例字段）。

## 架构地图（单线程：一个隐藏窗口的消息循环承载一切）

```
触控板 HID → WM_INPUT → TouchpadHelper.ParseInput（preparsed 缓存 + HidP 解析）
           → ContactAssembler（把分片触点报告拼成完整帧）
           → DragEngine.OnContacts（状态机，本文件的心脏；只通过 IGestureHost 接缝与外界交互）
             └─ Win32GestureHost（真实宿主）
                  ├─ MouseInjector（SendInput 鼠标：按键 + 相对移动；绝对定位优先 SetCursorPos，失败回退 SendInput 绝对移动）
                  ├─ KeyInjector（启动自检 + Win 修饰键松开后抵消开始菜单）
                  ├─ AltWindowDragger（修饰键+三指：手指增量直接驱动光标与窗口，不回读光标；异步 SetWindowPos 按半个刷新周期合并；边缘即时贴靠）
                  ├─ AltWindowResizer（修饰键+双指：定向改边缩放 + 宽高比 + 刷新率对齐）
                  ├─ WindowProbe（可见边框/DPI/提权/慢窗口/刷新率）
                  ├─ ScrollSuppressor（手势期间的 WH_MOUSE_LL：吞滚轮）与 CursorOverlay（缩放箭头指针）
                  └─ Notifier（限频托盘气泡，如提权窗口提示）
WM_TIMER(16ms 手势中，30ms 空闲) → DragEngine.Tick（静默判抬指、保持超时、待补发的移动/缩放）
WM_POWERBROADCAST 唤醒 / GIDC_ARRIVAL → 重注册 Raw Input + 失效 preparsed 缓存
```

| 文件 | 职责 |
|---|---|
| `src/Native/NativeMethods.cs` | 全部 P/Invoke 与常量（唯一互操作层，改封送只改这里） |
| `src/Touchpad/*` | Raw Input 注册、HID 报文解析、触点拼包 |
| `src/Engine/DragEngine.cs` | 状态机：Idle/Armed/Dragging/Holding/Resizing（Armed 按和弦动作分流：Drag/MoveWindow/ResizeWindow） |
| `src/Engine/IGestureHost.cs` | 引擎对外接缝（时钟、键盘状态、鼠标注入、窗口移动/缩放）；测试用假宿主实现它 |
| `src/Engine/GainModel.cs` / `GainSampler.cs` | 被动学习系统单指指针增益曲线（Idle 单指笔画按速度分箱累计 光标px/触控板单位），存 `gain.json`；`DragEngine.ComputeScaled` 用它做三指增益，数据不足时回退旧 Sigmoid；注册表 `CursorSpeed` 变化时清空，托盘"重新校准指针曲线"手动清空 |
| `src/Engine/AltWindow*.cs` | 窗口手势的窗口操作（绕开按键模拟，直接调 API；静态类，仅由 `Win32GestureHost` 调用） |
| `tests/ReplayTests` | 零依赖回放/场景测试 |
| `src/App.cs` | 窗口/消息循环/自检/配置热重载 |
| `src/AppConfig.cs` | 全部配置字段 + 中文《配置说明.txt》文本 |
| `src/Ui/TrayIcon.cs` | 托盘菜单（命令 ID 分段：开关 1-14，子菜单 base 100/200/…/1000，每段 +98 为信息项） |

**和弦路由（改手势逻辑前必背）**：（指数, 修饰键是否按住）经配置映射到动作——默认：3 指=Drag；修饰键+3 指=MoveWindow；修饰键+2 指=ResizeWindow；4+ 指完整让给系统手势；拖动中 2~3 指继续、遇 4+ 指干净释放。任何手势状态遇到不一致指数/修饰键松开/静默超时，必须清理后回 Idle。不再有 `AltWindowDrag`/`AltWindowResize` 布尔，用 `*Action = None` 关闭。

## 常见改动清单

**加一个配置字段（5 处，漏一处=热重载悄悄失效）**：
1. `AppConfig.cs` 属性（中文 XML 注释）→ 2. 同文件 `DocsText` → 3. `App.ApplyConfig()` 复制一行 → 4. `README.md` 的 jsonc 示例 → 5. 需要菜单的话 `TrayIcon.cs`。

**加一个托盘菜单项**：`TrayIcon.cs` 加命令 ID 常量与菜单行，并同步 README 的菜单项对照表。菜单悬停说明（`WM_MENUSELECT` + tooltips_class32 跟踪模式）试过，在实机上不显示，已移除，不要再走这条路。

**加一个手势状态/动作**：`DragEngine` 的 State 枚举 / `GestureAction` → `OnContacts` 加 case（所有退出路径！）→ `Tick` 加静默/超时 case → `ForceRelease` 加清理；需要新的外部效果就先加到 `IGestureHost` 与 `Win32GestureHost`，并在 `tests/ReplayTests/FakeHost.cs` 实现、补场景。若手势挂了钩子或按住虚拟按键，**每一条退出路径**（包括 WndProc 异常路径）都要释放。

**调手感**：参数含义与默认值速查 `DEVELOPMENT.md` 第 5 节；改默认值要同步用户文档与 CHANGELOG。

## 铁律（违反=静默失败或互操作事故，全部真实踩过）

1. **封送数组必须 `[Out]`**（`HidP_GetValueCaps`）：非 blittable 数组不回拷，API"成功"而数据全零。
2. **SendInput 的 INPUT 是 union，x64 上必须 40 字节**：键盘结构要补 8 字节填充（`NativeMethods.INPUTK._unionPadding`），cbSize 不符 SendInput 直接返回 0。
3. **SendInput 合成事件被低级钩子工具（AltSnap/AltDrag）无视**：想让它们响应的唯一办法是绕开模拟直接调 API（本项目 Alt 系列全部如此）。鼠标注入同理会被无视——这是特性不是 bug。
4. **手势期间才允许存在的全局资源**（钩子/虚拟按键）必须进出成对：参考 `ScrollSuppressor.Install/Remove` 在 Armed(缩放)/Resizing 全部退出路径的覆盖方式。Win/Alt 作修饰键时，手势开始后需 `MaskKeyRelease` 抵消松开 Win 时的开始菜单、松开 Alt 时的菜单栏/KeyTips 激活；锁定宽高比的 Shift 与 Alt/Ctrl 同按时同理（否则松开会触发输入法切换）。
5. **PMv2 DPI**（`Program.cs` 入口 `SetProcessDpiAwarenessContext(-4)`）必须先于任何窗口/光标 API，否则混合 DPI 下命中错位。
6. 静默≠抬指：Synaptics 手指静止即停发报文，判"停住"用各手势自己的静默窗口（拖动 `LiftedSilenceMs`，缩放 ≥180ms）。修饰键窗口移动因静默而释放后 400ms 内再落指，只需 3 个单位移动即可接续（`DragEngine.ArmRegrab`）。
7. **窗口移动/缩放不得回读光标**：相对注入过指针弹道且异步生效，`GetCursorPos` 滞后一帧会让窗口抖动。手指增量同时驱动光标（绝对定位）和窗口矩形；回读只用于系统接管光标（单指）的帧，且在自己注入后 50ms 内不信任回读。
8. **异步 `SetWindowPos` 必须合并**：每帧一条会把慢窗口（浏览器/Electron）的消息队列堆满，窗口随后追着旧位置回放，表现为严重拖尾。移动按半个刷新周期合并（整周期会与 ~8ms 的输入帧混叠成 ~40Hz），缩放另有慢窗口降频；松手/结束时强制补发末帧。
9. **窗口移动必须有明确的结束路径**：`AltWindowDragger.Release(hold)` 在非保持时立即 `End()`（同时提交顶部贴靠的最大化）；静默释放同样走 `WindowMoveEnd`。拖动增量走 `IGestureHost.WindowMoveDelta`，不与普通拖动的 `MoveCursor` 混用，否则残留的跟随状态会劫持后续的普通三指拖动。
10. **三指注入的光标移动不经过触控板指针曲线**：它是相对 `SendInput`，走的是鼠标设置（`MouseSensitivity`/`MouseSpeed`），而不是触控板 `CursorSpeed`；只有单指运动由系统按触控板曲线处理（两指=滚动/缩放，三指起=系统手势，用户态无法伪装成单指）。因此三指增益靠 `GainModel` 从用户自己的单指笔画里学出系统曲线来对齐，不要硬编码曲线，也不要按 `CursorSpeed` 数值缩放（映射未测过）。采样排除贴屏幕边缘的块与离谱比值。

## 发布

推送 `v*` tag → GitHub Actions（`.github/workflows/build.yml`）自动构建 win-x64 自包含单文件并挂 Release。本机离线做不了自包含发布（缺 RID runtime pack），不要尝试。发版前更新 `CHANGELOG.md` 与 `ThreeFingerDragLite.csproj` 的 `<Version>`。

## 约定

- 注释与用户文档用中文；日志消息用英文（前缀分类：`engine:`/`diag:`/`alt-*`/`self-test`）。
- 提交信息英文一行式，格式 `Area: what changed`。
- 触控板测试只能真实手指（合成输入进不了 Raw Input 的 PTP 通道），验证靠日志行 + 用户实机反馈。
