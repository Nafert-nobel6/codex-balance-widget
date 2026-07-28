# 安全、隐私与质量保证

## 安全边界

Codex Balance Widget 是单用户桌面组件，仅安装在
`%LOCALAPPDATA%\CodexBalanceWidget`。安装器只创建
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下名为
`CodexBalanceWidget` 的值，不写 `HKLM`、不请求管理员权限，也不修改
Codex 的 MSIX 安装目录。

组件不读取、复制、记录或显示 OAuth/API 凭据。身份验证由官方
`codex app-server` 负责。卸载器只删除上述 Run 值和组件安装目录；它不会
访问或删除 `%USERPROFILE%\.codex`、Codex 设置、会话或凭据。

重置卡是只读信息。程序不得调用
`account/rateLimitResetCredit/consume`，也不得为缺失的额度窗口或卡片到期
明细编造数值。

app-server 仅通过子进程标准输入/输出通信，不监听 TCP/HTTP 端口。每条 JSONL
消息最多 1 MiB、JSON 递归深度最多 64 层；单条诊断日志最多 64 KiB。
重置卡明细最多保留 128 条，所有用户可见文本均限制长度。超限数据被丢弃或
截断，不能触发动态类型解析、脚本执行或程序集加载。

## 官方运行时信任链

安装器优先从正在运行且路径属于
`\WindowsApps\OpenAI.Codex_...` 的 `ChatGPT.exe` 定位软件包，随后回退到
包族 `OpenAI.Codex_2p2nqsd0c76g0` 或最新匹配的 Codex MSIX 包。

复制 `codex.exe` 前必须同时满足：

1. 文件位于所发现 MSIX 包的 `resources` 目录内；
2. `Get-AuthenticodeSignature` 的状态为 `Valid`；
3. 签名证书主题包含 `OpenAI OpCo, LLC`；
4. 复制前后的 SHA-256 完全相同；
5. 复制后的 Authenticode 签名仍通过验证。

任何条件不满足时都拒绝该文件。没有可信来源时仍可安装组件文件，但显示
`runtime pending`，等待组件今后从可信官方包同步；不得因此读取凭据或改写
Codex 用户数据。

## 日志与诊断

应用日志必须轮换，并在写入前对名称含 `token`、`authorization`、
`cookie`、`secret` 或 `credential` 的值做脱敏。诊断脚本还会二次遮盖
Bearer 值、`sk-` 格式的密钥、电子邮箱及用户目录。

运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Collect-Diagnostics.ps1
```

报告默认写入 `artifacts\diagnostics`。分享前仍需人工检查；不要将原始日志、
`.codex` 目录或任何登录信息加入问题报告。

## 发布前 QA 清单

- [ ] `Test-SourceSecurity.ps1` 通过，无网络下载器、动态代码执行、危险反序列化、
  凭据特征、个人状态文件或私有运行时。
- [ ] Windows 11 x64、普通用户、PowerShell 5.1 环境构建成功。
- [ ] 构建全程不下载 NuGet 包或其他网络依赖，源码保持 C# 5。
- [ ] `Build.ps1 -Clean -RunTests` 成功，控制台测试退出码为 0。
- [ ] `Smoke-Test.ps1` 全部通过，发布文件 SHA-256 与清单一致。
- [ ] 先运行 `Install.ps1 -WhatIf`，确认目标仅为 `%LOCALAPPDATA%` 和 HKCU。
- [ ] 安装不弹 UAC；登录启动项只有一个 `CodexBalanceWidget` 值。
- [ ] 暂存的 `codex.exe` 签名有效、签名者正确、复制前后哈希一致。
- [ ] 没有可信运行时时，安装成功且状态明确为 `runtime pending`。
- [ ] Codex 未运行时组件无可见窗口；Codex 启动后无需点击即可出现。
- [ ] Codex 窗口最大化后 250 ms 内隐藏，恢复/最小化后重新显示。
- [ ] 仅在 Codex 完全退出后关闭私有 app-server 并隐藏组件。
- [ ] 主屏 100%、125%、150%、200% DPI 及任务栏尺寸变化下位置正确。
- [ ] 周额度和 5 小时额度按时长识别；缺失额度显示不可用而不是 100%。
- [ ] 重置卡按官方到期时间排序，默认可见两张，滚轮可查看其余卡。
- [ ] 到期不足 24 小时显示分钟级 `HH:mm` 倒计时。
- [ ] 30 秒轮询、通知后完整刷新、断线退避及最后快照保留均验证。
- [ ] 日志轮换和敏感键脱敏通过构造数据测试。
- [ ] Release ZIP 由精确允许列表生成，仅含两个运行程序集、安装脚本、文档和
  校验清单；ZIP 外另附 SHA-256。
- [ ] 卸载前运行 `Uninstall.ps1 -WhatIf`，目标精确且不包含 `.codex`。
- [ ] 卸载后应用目录和 HKCU Run 值消失，Codex 设置/会话/凭据仍存在。

## 非破坏性检查

`scripts\Smoke-Test.ps1` 只解析脚本、检查发布文件与哈希、运行本地控制台
测试，并只读探测 Codex 包。它不会安装/卸载、修改注册表、停止进程或复制
运行时：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Smoke-Test.ps1
```

安装与卸载都支持 PowerShell 通用参数 `-WhatIf`。正式执行前应先预演，并
阅读打印出的精确目标路径。
