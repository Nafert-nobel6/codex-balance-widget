# Codex Balance Widget

当前版本：**1.1.0**。

- 右上角新增横杠最小化按钮，点击立即收为头像气泡，无需等待自动收回。
- 修复 Codex 不退出时切换账号后额度无法正常更新的问题：检测登录状态变化，
  清空旧额度并自动重建数据连接；退出登录时显示登录提示。
- 保留点击后置顶、收回后解除置顶的窗口层级优化。

> 非官方社区工具。本项目不属于 OpenAI，也不代表 OpenAI。Codex 和 OpenAI
> 名称及相关商标归其权利人所有。

Codex Balance Widget 是 Windows 11 上的右下角额度小窗。它随用户登录启动，
监测 Microsoft Store 版 Codex：Codex 运行时自动显示并同步额度，Codex
主窗口最大化时隐藏，Codex 完全退出后关闭私有数据进程并隐藏。

界面并排显示周/长周期额度和预留的 5 小时额度圆环；下方按到期时间显示
重置卡。默认视口容纳两张卡，其余可用鼠标滚轮查看。官方返回的到期时间
不足 24 小时时显示分钟级倒计时。额度每 30 秒完整读取一次，并在收到官方
更新通知后立即重新读取。

窗口默认以 56×56 的头像额度气泡常驻在主工作区右下方，距右侧 24、距底部
48 个逻辑像素；鼠标停留 1.5 秒或单击后展开。展开窗口默认是 300×320 的白色紧凑
布局，可从四边/四角缩放到 240×148，并在竖向、横向和最小布局间自动切换。
展开窗口默认比显示器底边上移 64 个逻辑像素；即使任务栏采用自动隐藏或覆盖
模式，也保留至少 48 个逻辑像素。拖动标题栏可在右下区域内小范围
调整位置，位置会被安全边界限制并自动保存。
右下角三角入口包含白/黑主题滑块、70–100% 透明度、10/15/30/60 秒收回
气泡和头像更换。头像通过
可拖动的圆形裁剪器确认，支持 1–8× 缩放及 176–232 px 裁剪框预览。
展开界面以头像为视觉原点平滑缩放回气泡，不再逐帧重排窗口。组件默认处于
普通窗口的下层；用户单击气泡或展开窗口后，组件以不抢焦点的方式置顶，
自动收回、隐藏或 Codex 最大化时解除置顶。

## 系统要求

- Windows 11 x64
- Microsoft Store/MSIX 版 Codex
- Windows 自带的 .NET Framework 4.8
- Windows PowerShell 5.1

项目使用 C# 5 和系统自带编译器，不需要 Visual Studio、NuGet 或联网安装
构建依赖。

## 构建与验证

在仓库根目录打开 Windows PowerShell 5.1：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -Clean -RunTests
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Smoke-Test.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-StateServices.ps1
```

输出位于 `artifacts\publish`，包含应用、核心程序集、控制台测试和
`SHA256SUMS.txt`。`Build.ps1` 显式引用 WPF、WindowsBase、System.Xaml 和
System.Web.Extensions 等 .NET Framework 程序集。可选的
`Build-VisualHarness.ps1` 用于发布前的三档尺寸、主题、裁剪器和气泡真机
视觉验收，不会写入正式安装状态。

## 安装

普通用户应从 GitHub **Releases** 下载同一版本的 ZIP 与 `.sha256` 文件，
先按照 [使用说明](docs/USAGE.md) 验证完整性并解压，再运行安装脚本。Release
ZIP 不包含 OpenAI 的 `codex.exe`、用户头像、设置、日志或账户数据。

先预演，检查将要修改的精确路径：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\installer\Install.ps1 -WhatIf
```

确认后安装：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\installer\Install.ps1
```

安装位置固定为 `%LOCALAPPDATA%\CodexBalanceWidget`，并只添加当前用户的
HKCU Run 启动项。安装器会启动隐藏的轻量监督进程，因此不必再点击小窗。
监督进程在 Codex 没有运行时不显示界面。

Microsoft Store 版 Codex 的程序目录是受保护、带签名且随版本号变化的 MSIX
目录，不适合作为第三方组件的写入位置：写入会破坏信任边界，并可能在 Codex
更新时被覆盖。因此组件不修改 Codex 程序文件夹，而使用稳定的用户级安装目录。
Codex 完全退出后，私有 app-server 会同步退出并释放账户数据连接；只有不显示
界面的启动监督器保留，用于在下一次 Codex 启动时自动唤起小窗。

安装器只接受 Authenticode 状态为 `Valid` 且签名者名称精确等于
`OpenAI OpCo, LLC` 的官方 `codex.exe`，并复核复制前后的 SHA-256。当前
找不到可信官方运行时时，应用文件仍会安全安装并报告 `runtime pending`；
之后由监督进程在官方包可用时同步。

## 卸载

建议先预演：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\CodexBalanceWidget\Uninstall.ps1" -WhatIf
```

确认后去掉 `-WhatIf`。卸载器只停止安装目录中的小窗进程、删除名为
`CodexBalanceWidget` 的 HKCU Run 值和组件安装目录。它不会触碰
`%USERPROFILE%\.codex`、Codex 设置、会话或登录凭据。

## 诊断

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Collect-Diagnostics.ps1
```

诊断报告默认位于 `artifacts\diagnostics`。脚本只读收集安装状态、文件哈希、
进程、Codex 包信息以及经过二次脱敏的日志末尾；分享前仍请人工检查。

完整的信任边界、隐私说明与发布清单见
[`docs/security.md`](docs/security.md)。

## 文档

- [使用与安装](docs/USAGE.md)
- [功能说明](docs/FEATURES.md)
- [安全策略](SECURITY.md)
- [安全设计与隐私边界](docs/security.md)
- [架构说明](docs/architecture.md)
- [验收手册](docs/acceptance.md)
- [GitHub 发布流程](docs/RELEASE.md)

## 发布包

维护者可用一条命令完成安全检查、构建、测试和最小化 ZIP 封装：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Package-Release.ps1 -Version 1.1.0
```

输出位于 `artifacts\release`。完整源码由 GitHub 仓库提供；Release ZIP
只提供最终用户安装所需文件。

## 许可证

本项目依据 [MIT License](LICENSE) 发布。
