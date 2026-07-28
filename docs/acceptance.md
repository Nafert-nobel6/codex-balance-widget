# 自动化验收手册

本手册把验收分为四层。前三层不会安装、覆盖或卸载现有程序；第四层只有在
根负责人明确通知后才执行。

## 1. 构建与单元测试

使用 Windows PowerShell 5.1 和系统自带 .NET Framework 4.8：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Build.ps1 -Clean -RunTests
```

通过标准：

- Core、WPF App 和控制台测试均以 C# 5/x64 编译；
- 控制台测试退出码为 0；
- `artifacts\publish` 只包含允许发布的文件；
- `SHA256SUMS.txt` 覆盖全部发布文件。

## 2. 非破坏性发布检查

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Smoke-Test.ps1

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Test-Deployment.ps1 `
  -Target Publish `
  -ReportPath .\artifacts\qa\publish-acceptance.json
```

`Test-Deployment.ps1` 的默认行为是只读的。它验证：

- EXE/DLL 是可读取的托管程序集；
- 应用 PE 架构为 x64；
- 发布清单无重复、缺失、额外文件或 SHA-256 不一致；
- PowerShell 5.1 可输出结构化 JSON 验收报告。

## 3. 配置和头像文件

配置与头像是可选部署输入；安装后验收会自动检查安装目录中的
`settings.json` 和 `avatars\avatar.png`。组件当前没有设置或头像时，脚本只
输出 `INFO`，不会把首次运行状态误报为产品故障。也可以显式传入路径：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Test-Deployment.ps1 `
  -Target Publish `
  -ConfigurationPath C:\path\widget.config.json `
  -AvatarPath C:\path\avatar.png
```

配置验收：

- 文件不是重解析点，且不超过 1 MiB；
- JSON 必须能由 .NET 4.8 解析，XML 禁止 DTD 和外部实体；
- 报告只记录大小和 SHA-256，不输出配置内容。

显式输入头像验收：

- 文件不是重解析点，且属于受支持的 PNG/JPEG；
- 仅接受 PNG/JPEG；
- 必须能由 Windows Imaging Component 解码；
- 仅单帧，宽高各为 16 至 4096 px；
- 报告只记录尺寸和 SHA-256。

应用导入头像本身限制为 20 MiB、8192×8192，经过 EXIF 方向校正和圆形裁剪
后必须成为单帧 256×256 PNG；安装后脚本按这个更严格的输出合同验收。

## 4. 安装后验收

只有在收到最终安装验收通知后，才运行安装脚本。先预演：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\installer\Install.ps1 -WhatIf
```

安装完成后进行只读检查：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Test-Deployment.ps1 `
  -Target Installed `
  -RequireRunning `
  -RequireRuntime `
  -ReportPath .\artifacts\qa\installed-acceptance.json
```

检查内容包括：

- 安装路径固定为 `%LOCALAPPDATA%\CodexBalanceWidget`；
- HKCU Run 值的名称和命令行完全匹配，且无指向同一 EXE 的重复值；
- 已安装文件与发布哈希一致；
- 私有 `codex.exe` 的 Authenticode 状态为 `Valid`，签名者包含
  `OpenAI OpCo, LLC`；
- 组件进程数量为 1，且映像路径与安装路径精确匹配；
- Codex 未运行或最大化时窗口隐藏；
- Codex 正常/最小化/后台运行时窗口显示；
- 可见窗口具有 tool-window、no-activate 样式且没有 topmost 样式；
- 气泡位于主工作区右下角并与右侧保留 24、与底部保留 48 个逻辑像素，窗口保持普通应用
  下层，不覆盖正在使用的其他窗口。

如果官方包当时不可发现，允许第一次安装显示 `runtime pending`。这种场景先
省略 `-RequireRuntime`，待监督进程成功同步后再次执行严格检查。

## 5. 受控生命周期演练

生命周期演练会启动并停止安装目录中路径精确匹配的组件进程，因此不是只读
操作。它不会启动或停止 Codex、不会修改注册表、不会安装/卸载文件。执行前：

1. 完全退出 Codex；
2. 确认组件进程当前未运行；
3. 得到根负责人明确授权。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Test-Deployment.ps1 `
  -Target Installed `
  -ExerciseLifecycle `
  -ReportPath .\artifacts\qa\lifecycle-acceptance.json
```

脚本验证组件能稳定启动、Codex 不运行时没有可见窗口、第二实例因互斥锁正常
退出，以及受控测试进程退出后没有残留。为了避免产生或遗留私有 app-server
子进程，只要检测到 Codex 仍在运行，演练就拒绝开始。

## 6. 人工补充项

先构建隔离的视觉验收程序：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Build-VisualHarness.ps1
```

该程序使用临时状态目录和模拟额度，不连接账户，也不影响正式安装。用于检查
300×320、约 280×200、240×148 三档布局、白/黑主题、双滑块头像裁剪、
56 px 气泡以及收回/展开动画。

自动化无法可靠替代以下人工检查：

- 鼠标滚轮浏览第三张及之后的重置卡；
- 气泡停留 1.5 秒展开，以及 10/15/30/60 秒四档自动收回；
- 240 px 最小宽度下内容完整，标题栏拖动受限且不会进入任务栏区域；
- 100%、125%、150%、200% DPI 下文字清晰度；
- 任务栏置于不同边缘时的视觉间距；
- 24 小时边界前后的分钟倒计时视觉切换；
- Codex 最大化和恢复时肉眼确认无闪烁。

这些结果应与两个 JSON 自动化报告一起保留，作为最终验收记录。
