# ThreeFingerDragLite

Windows 精密触控板上的 macOS 式**三指拖动**，外加 **Alt + 三指直接移动窗口**（AltSnap 兼容）——一个不到 1 MB、零 UI 框架依赖的纯 Win32 托盘工具。

- 触控板输入管线移植自 [ThreeFingerDragOnWindows](https://github.com/ClementGre/ThreeFingerDragOnWindows)（MIT），并做了若干健壮性修复（含部分设备上设备枚举拿不到句柄的封送问题）
- 交互行为规格参考 [FastGestures](https://fg.zhaokeli.com) 的公开文档（**未使用其任何代码或资源**）
- 在两者之上新增：Alt + 三指移动窗口，与 AltSnap / AltDrag 类工具完全共存

## 功能

| 手势 | 效果 |
|---|---|
| 三指按住移动 | 左键拖拽（文件、标题栏、框选），支持"先单指移动、不抬手补上两指"的渐进触发 |
| 手指全部抬离 | 拖动保持（默认 1 秒）：放回 ≥2 指继续拖，单指轻移即结束，适合长距离拖动 |
| 拖动中只剩一指 | 交给系统光标速度继续拖动（消除多指抖动，FastGestures 同款行为） |
| **Alt + 三指移动** | **直接拖动光标下的窗口**：最大化窗口自动还原，松开 Alt 立即放下 |
| 真实鼠标 Alt+拖拽 | 不受任何影响，仍由 AltSnap 全权处理 |

> 为什么 Alt 模式是"直接移动窗口"：AltSnap/AltDrag 通过低级钩子监听**物理**鼠标按键，任何工具用 SendInput 模拟的按键对它们不可见（已实测验证）。因此本工具检测到 Alt 时绕开按键模拟，直接调用窗口 API 移动窗口——两条输入路径互不干扰。

## 快速开始

1. 从 Releases 下载自包含版（无需安装运行时；自行用源码构建的框架依赖版需要 .NET 10 运行时）
2. 双击运行，托盘出现图标即常驻（无窗口、无后台服务）
3. 建议设置：系统"三指手势"改为"无"（设置 → 蓝牙和其他设备 → 触控板），避免与系统手势抢输入
4. **不要**与 FastGestures / ThreeFingerDragOnWindows 等同类工具同时运行
5. 开机自启：把快捷方式放进 `Win+R` → `shell:startup` 文件夹

## 配置：全部在托盘菜单完成

常用参数**无需打开任何窗口、无需手改文件**——托盘菜单直接点选，立即生效并写盘；与当前状态无关的选项会自动置灰：

```
✓ 启用三指拖动
──────────────
✓ 拖动状态保持
   保持时长 ▸ 0.5 / 1 / 1.5 / 2 / 3 秒     （开关关闭时置灰）
✓ 单指系统接管
✓ Alt+三指移动窗口
──────────────
   拖动按键 ▸ 左 / 中 / 右
   光标速度 ▸ 60 ~ 240 六档
   触发灵敏度 ▸ 严格 ~ 极敏 五档           （总开关关闭时置灰）
──────────────
   打开配置文件夹 / 重新加载配置
   退出
```

高级参数在 `%LOCALAPPDATA%\ThreeFingerDragLite\config.json`，首次运行会自动生成一份中文《配置说明.txt》放在旁边；手改后在托盘菜单点"重新加载配置"即生效：

```jsonc
{
  "Enabled": true,                  // 总开关
  "DragButton": "Left",             // 拖动按下的鼠标键：Left / Right / Middle
  "Speed": 120,                     // 光标速度，120 为标准值
  "Acceleration": 10,               // 指针加速度，10 为标准值，0 关闭
  "StartThreshold": 12,             // 触发灵敏度：三指累计移动量，越小越易触发
  "HoldEnabled": true,              // 拖动状态保持
  "HoldTimeMs": 1000,               // 保持时长（毫秒）
  "HoldResumeEnabled": true,        // 保持期间放回 ≥2 指可继续拖
  "TakeoverWhenSingleFinger": true, // 拖动中剩一指时交给系统光标速度
  "AltWindowDrag": true,            // Alt+三指直接移动窗口
  "LiftedSilenceMs": 60,            // 输入静默多久判定"手指已全部抬离"
  "HoldExitMoveThreshold": 5,       // 保持期间单指移动多少结束拖动
  "Logging": true,                  // 运行日志
  "VerboseLog": false               // 逐帧触点日志（仅排障）
}
```

## 与相关项目的关系

**对比 ThreeFingerDragOnWindows**（本项目为其衍生作品）：

| 特性 | ThreeFingerDragOnWindows | ThreeFingerDragLite |
|---|---|---|
| 触发方式 | 三指同时开始移动 | 渐进触发：单指移动中补上两指即可 |
| 抬手后 | 需重新放三指 | 2 指即可接续 + 单指轻移结束 |
| 拖动中剩一指 | 继续注入（易抖） | 系统接管光标速度 |
| Alt 拖窗口 | 不可用（模拟按键被 AltSnap 无视） | 直接移动窗口，松 Alt 即放 |
| 调参方式 | WinUI 设置窗口 | 托盘菜单即时点选 |
| 体积与依赖 | WinUI 3 + MSIX + 多个 NuGet 包 | 纯 Win32 P/Invoke，< 1 MB |

**关于 FastGestures**：本项目的拖动保持、渐进触发、单指接管等行为语义参考其公开文档与作者博客，为独立实现；未使用、未捆绑其任何二进制或代码。

## 已知限制

- 需要Windows 精密触控板（Precision Touchpad）；普通触控板无法获得触点数据
- 多块精密触控板同时使用时触点会混流（按单触控板设计）
- 普通权限运行时无法拖动管理员权限窗口（Windows UIPI 限制，以管理员运行本工具即可）
- 普通三指拖动中途抬到两指时系统两指滚动仍会触发——这是 Windows 触控板栈的原生行为，用户态无法阻止
- FastGestures 的"速率适配（0=自动）"无对应实现，等价手段是手动调 `Speed`

## 排障

- 日志：`%LOCALAPPDATA%\ThreeFingerDragLite\log.txt`（超过 1 MB 自动轮转为 log.old.txt）
- 程序启动时做一次注入自检（光标轻微移动后立即复位），日志 `self-test` 行可确认注入通道正常
- 反馈问题：把配置里 `VerboseLog` 改为 `true` → 托盘"重新加载配置" → 复现一次 → 附上 log.txt

## 从源码构建

需要 .NET 10 SDK：

```
dotnet build ThreeFingerDragLite.csproj -c Release
dotnet publish ThreeFingerDragLite.csproj -c Release --self-contained false -o publish
# 自包含单文件（目标机器免装运行时）
dotnet publish ThreeFingerDragLite.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## 致谢与许可

[MIT License](LICENSE)。本项目为衍生作品，感谢：

- [ClementGre/ThreeFingerDragOnWindows](https://github.com/ClementGre/ThreeFingerDragOnWindows)（MIT）——Raw Input/HID 触点解析、触点拼包、防跳变隔离、指针加速曲线与图标
- [emoacht/RawInput.Touchpad](https://github.com/emoacht/RawInput.Touchpad)（MIT）——上述项目的输入解析上游
- [FastGestures / zhaokeli](https://fg.zhaokeli.com)——交互行为规格的公开文档
