# 使用说明

## 1.1 新增与修复

展开后点击右上角的横杠按钮，立即缩为头像气泡；再次点击气泡即可展开。
该按钮不需要等待设置中的 10/15/30/60 秒自动收回时间。

在 Codex 中切换账号时，小窗检测登录状态文件变化后会清空旧额度和重置卡，
自动重新连接。检测间隔为 1 秒，随后获取新额度的时间取决于网络和官方服务。
退出登录时显示登录提示，重新登录后自动同步，无需重启 Codex。
如使用仅存于系统凭据库或外部注入的特殊登录方式，跨进程变更可能没有文件通知；
本版本尚未验证这类账号切换，必要时重启小窗。

## 运行环境

- Windows 11 x64
- Microsoft Store/MSIX 版 Codex
- Windows 自带的 .NET Framework 4.8
- Windows PowerShell 5.1

本工具是非官方的只读额度窗口，不属于 OpenAI 官方产品。它不会把
`codex.exe`、账户凭据或用户数据打包在下载文件中。

## 从 GitHub Release 安装

1. 从仓库的 **Releases** 页面下载同一版本的：
   - `CodexBalanceWidget-vX.Y.Z.zip`
   - `CodexBalanceWidget-vX.Y.Z.zip.sha256`
2. 使用 `Get-FileHash` 验证 ZIP，方法见根目录 `SECURITY.md`。
3. 将 ZIP 完整解压到普通用户可写目录。不要直接在压缩包预览窗口中运行脚本。
4. 打开 Windows PowerShell 5.1，进入解压后的版本目录。
5. 先执行安装预演：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\installer\Install.ps1 -WhatIf
```

6. 确认目标为 `%LOCALAPPDATA%\CodexBalanceWidget` 后安装：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\installer\Install.ps1
```

安装器只使用当前用户权限，创建一个 HKCU 登录启动项。它会从本机已安装的
官方 Codex MSIX 包定位 `codex.exe`，验证 OpenAI Authenticode 签名和
SHA-256 后复制到私有运行目录。ZIP 本身不携带该官方程序。

## 日常使用

- Codex 启动后，小窗监督器自动连接并显示额度气泡。
- Codex 主窗口最大化时小窗隐藏；恢复、最小化或进入后台后重新显示。
- 鼠标停留在气泡上 1.5 秒或单击气泡可展开窗口。
- 单击气泡或已展开窗口会使其非激活置顶，避免被其他普通窗口遮盖；自动收回或隐藏后解除置顶。
- 拖动“Codex 额度”标题栏可在右下区域内小范围调整位置。
- 从窗口右侧或四角可调整尺寸，最小为 240×148。
- 右下角三角入口包含主题、透明度、收回时间和头像设置。
- 头像选择后可移动、缩放和调整裁剪框，确认后立即生效。
- 重置卡超过两张时可使用滚轮，或长按右侧滑块快速浏览。

## 更新

下载新的 Release ZIP，验证校验值后再次运行新版
`installer\Install.ps1`。安装器会事务性替换程序文件，并保留：

- 主题和透明度；
- 窗口尺寸及安全范围内的位置；
- 收回气泡时间；
- 自定义头像。

不要把新版本文件手动覆盖到安装目录，也不要自行替换
`runtime\codex.exe`。

## 卸载

先预演：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "$env:LOCALAPPDATA\CodexBalanceWidget\Uninstall.ps1" -WhatIf
```

确认后去掉 `-WhatIf`。卸载只移除小窗目录及
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的
`CodexBalanceWidget` 值，不删除 Codex 设置、会话或凭据。

## 常见问题

### 安装后没有气泡

- 确认 Codex 正在运行；
- Codex 最大化时小窗按设计隐藏；
- 首次运行若未找到可信官方运行时，可退出并重新启动 Codex；
- 查看 `%LOCALAPPDATA%\CodexBalanceWidget\logs\widget.log`，分享前必须脱敏。

### Windows 提示未知发布者

当前项目二进制尚未使用项目方 Authenticode 证书签名。不要关闭系统安全功能。
应从仓库正式 Release 下载、验证 ZIP 的 SHA-256，或从源码自行构建。

### 任务栏遮挡窗口

当前版本按显示器工作区和高 DPI 计算位置，并保留底部安全距离。标题栏拖动
也受安全边界限制。如仍出现问题，请在 Issue 中提供 Windows 缩放比例、任务栏
位置和匿名化截图，不要上传真实头像或额度数据。
