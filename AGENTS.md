# AGENTS.md — 开发代理上手手册

本文件面向接手本项目的 AI 编码代理（人类开发者也可用作速查）。深度原理见 `DEVELOPMENT.md`，行为规格见 `README.md`。

## 项目一句话

Windows 精密触控板（PTP）手势工具：三指拖动、Alt+三指移动窗口、Alt+双指调整窗口大小（AltSnap 语义）、可选四指手势接管。纯 C# / net10.0 / WinExe / **零 NuGet 依赖**，全部 Win32 P/Invoke，无 UI 框架（托盘菜单用纯 Win32）。

## 构建与运行（每次改动的验证循环）

```
taskkill /IM ThreeFingerDragLite.exe /F        # 先杀实例！运行中会锁住输出文件（MSB3027）
dotnet publish ThreeFingerDragLite.csproj -c Release --self-contained false -o publish
publish\ThreeFingerDragLite.exe                 # 后台启动；单实例互斥，重复启动直接退出
```

- 目标机需 .NET 10 运行时；自包含单文件版由 CI 构建（见"发布"）。
- `tools/IconGen` 是独立辅助工程，已被主 csproj 的 `DefaultItemExcludes` 排除，不要并入主工程编译。

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
           → DragEngine.OnContacts（状态机，本文件的心脏）
             ├─ MouseInjector（SendInput 鼠标：按键 + 相对移动 + 绝对定位）
             ├─ KeyInjector（SendInput 键盘和弦：切桌面/切换器热键）
             ├─ AltWindowDragger（Alt+三指：直接 SetWindowPos 移窗）
             ├─ AltWindowResizer（Alt+双指：定向改边缩放）
             └─ ScrollSuppressor（手势期间的 WH_MOUSE_LL：吞滚轮 + 缩放箭头指针）
WM_TIMER(30ms) → DragEngine.Tick（静默判抬指、保持超时）
```

| 文件 | 职责 |
|---|---|
| `src/Native/NativeMethods.cs` | 全部 P/Invoke 与常量（唯一互操作层，改封送只改这里） |
| `src/Touchpad/*` | Raw Input 注册、HID 报文解析、触点拼包 |
| `src/Engine/DragEngine.cs` | 状态机：Idle/Armed/Dragging/Holding/ResizeArmed/Resizing/FourArmed/FourSwiping |
| `src/Engine/AltWindow*.cs` | Alt 手势的窗口操作（绕开按键模拟，直接调 API） |
| `src/App.cs` | 窗口/消息循环/自检/配置热重载 |
| `src/AppConfig.cs` | 全部配置字段 + 中文《配置说明.txt》文本 |
| `src/Ui/TrayIcon.cs` | 托盘菜单（命令 ID 分段：开关 1-9，子菜单 base 100/200/300/400，98/99=信息项） |

**指数路由（改手势逻辑前必背）**：恰好 3 指=拖动；Alt+恰好 2 指=缩放；4 指默认完整让给系统手势（`FourFingerGestures` 开启才接管，默认关）；拖动中 2~3 指继续、遇 4+ 指干净释放。任何手势状态遇到不一致指数/Alt 松开/静默超时，必须清理后回 Idle。

## 常见改动清单

**加一个配置字段（5 处，漏一处=热重载悄悄失效）**：
1. `AppConfig.cs` 属性（中文 XML 注释）→ 2. 同文件 `DocsText` → 3. `App.ApplyConfig()` 复制一行 → 4. `README.md` 的 jsonc 示例 → 5. 需要菜单的话 `TrayIcon.cs`。

**加一个手势状态**：`DragEngine` 的 State 枚举 → `OnContacts` 加 case（所有退出路径！）→ `Tick` 加静默/超时 case → `ForceRelease` 加清理。若手势挂了钩子或按住虚拟按键，**每一条退出路径**（包括 WndProc 异常路径）都要释放。

**调手感**：参数含义与默认值速查 `DEVELOPMENT.md` 第 5 节；改默认值要同步用户文档与 CHANGELOG。

## 铁律（违反=静默失败或互操作事故，全部真实踩过）

1. **封送数组必须 `[Out]`**（`HidP_GetValueCaps`）：非 blittable 数组不回拷，API"成功"而数据全零。
2. **SendInput 的 INPUT 是 union，x64 上必须 40 字节**：键盘结构要补 8 字节填充（`NativeMethods.INPUTK._unionPadding`），cbSize 不符 SendInput 直接返回 0。
3. **SendInput 合成事件被低级钩子工具（AltSnap/AltDrag）无视**：想让它们响应的唯一办法是绕开模拟直接调 API（本项目 Alt 系列全部如此）。鼠标注入同理会被无视——这是特性不是 bug。
4. **手势期间才允许存在的全局资源**（钩子/虚拟按键）必须进出成对：参考 `ScrollSuppressor.Install/Remove` 在 ResizeArmed/Resizing 全部退出路径的覆盖方式。
5. **PMv2 DPI**（`Program.cs` 入口 `SetProcessDpiAwarenessContext(-4)`）必须先于任何窗口/光标 API，否则混合 DPI 下命中错位。
6. 静默≠抬指：Synaptics 手指静止即停发报文，判"停住"用各手势自己的静默窗口（拖动 `LiftedSilenceMs`，缩放 ≥180ms，四指横滑 `FourFingerLiftMs`）。

## 发布

推送 `v*` tag → GitHub Actions（`.github/workflows/build.yml`）自动构建 win-x64 自包含单文件并挂 Release。本机离线做不了自包含发布（缺 RID runtime pack），不要尝试。发版前更新 `CHANGELOG.md` 与 `ThreeFingerDragLite.csproj` 的 `<Version>`。

## 约定

- 注释与用户文档用中文；日志消息用英文（前缀分类：`engine:`/`diag:`/`alt-*`/`self-test`）。
- 提交信息英文一行式，格式 `Area: what changed`。
- 触控板测试只能真实手指（合成输入进不了 Raw Input 的 PTP 通道），验证靠日志行 + 用户实机反馈。
