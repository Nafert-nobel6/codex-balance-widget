# GitHub 发布流程

## 1. 发布前检查

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Test-SourceSecurity.ps1

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Build.ps1 -Clean -RunTests

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Smoke-Test.ps1
```

确认工作区中没有头像、设置、日志、诊断报告、官方运行时或签名私钥。

## 2. 生成 Release ZIP

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Package-Release.ps1 -Version 1.0.0
```

输出：

```text
artifacts/release/CodexBalanceWidget-v1.0.0.zip
artifacts/release/CodexBalanceWidget-v1.0.0.zip.sha256
```

ZIP 内容由脚本中的精确允许列表控制，不包含 PDB、测试程序、`codex.exe`、
头像、设置、日志、验收报告或源码工作区临时文件。

## 3. 创建版本

1. 确认主分支 CI 通过且工作区干净。
2. 创建带注释标签：`v1.0.0`。
3. 在 GitHub 的 **Releases → Draft a new release** 中选择该标签。
4. 上传 ZIP 和 `.sha256` 两个文件。
5. 在发布说明中列出功能、系统要求、升级说明、已知限制和 SHA-256。
6. 先发布为 Pre-release；在一台未安装过本工具的 Windows 11 设备完成安装、
   登录启动、更新和卸载测试后再转为正式版本。

## 4. 发布后验证

- 从 GitHub Release 重新下载资产，不使用本地原文件；
- 验证 ZIP SHA-256；
- 解压后运行 `Install.ps1 -WhatIf`；
- 正式安装并执行 `scripts\Test-Deployment.ps1 -Target Installed`；
- 检查 Release ZIP 下载链接和 `releases/latest` 链接；
- 不在 Issue 中收集原始日志、头像或账户截图。

## 5. 版本与兼容性

- 功能或兼容性更新：提升次版本，例如 `1.1.0`；
- 仅修复问题：提升修订版本，例如 `1.0.1`；
- 每个版本保留独立 Release，不覆盖既有 ZIP；
- Codex 官方协议变化时先发布 Pre-release，确认只读接口仍兼容后再稳定发布。
