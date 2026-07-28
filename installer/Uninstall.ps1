[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw 'LOCALAPPDATA is unavailable. The per-user install path cannot be resolved safely.'
}

$localAppData = [System.IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\')
$installRoot = [System.IO.Path]::GetFullPath((Join-Path $localAppData 'CodexBalanceWidget'))
$expectedInstallRoot = $localAppData + '\CodexBalanceWidget'
if (-not $installRoot.Equals($expectedInstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to uninstall from an unexpected path: $installRoot"
}

$installedExecutable = Join-Path $installRoot 'CodexBalanceWidget.exe'
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValueName = 'CodexBalanceWidget'

function Assert-NoReparsePoints {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $rootItem = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to uninstall a reparse-point install root: $Path"
    }
    $reparsePoint = @(
        Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction Stop |
            Where-Object {
                ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
            } |
            Select-Object -First 1
    )
    if ($reparsePoint.Count -gt 0) {
        throw "Refusing to uninstall an install tree containing a reparse point: $($reparsePoint[0].FullName)"
    }
}

Write-Host "Uninstall target: $installRoot"
Write-Host 'Codex settings, sessions, credentials, and %USERPROFILE%\.codex are outside this target and will not be changed.'

Assert-NoReparsePoints -Path $installRoot

if (-not $PSCmdlet.ShouldProcess($installRoot, 'Remove the Codex Balance Widget HKCU Run value and installed files')) {
    Write-Host 'No changes made.'
    return
}

if (Test-Path -LiteralPath $runKeyPath) {
    Remove-ItemProperty -Path $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue
}

foreach ($process in @(Get-Process -Name 'CodexBalanceWidget' -ErrorAction SilentlyContinue)) {
    $actualPath = $null
    try {
        $actualPath = [System.IO.Path]::GetFullPath($process.MainModule.FileName)
    }
    catch {
        Write-Warning "Could not verify path for widget process $($process.Id); it was not stopped."
    }

    if ($null -ne $actualPath -and
        $actualPath.Equals($installedExecutable, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
        $process.WaitForExit(5000)
    }
}

$currentDirectory = [System.IO.Path]::GetFullPath((Get-Location).Path)
if ($currentDirectory.Equals($installRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $currentDirectory.StartsWith($installRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    Set-Location -LiteralPath $localAppData
}

if (Test-Path -LiteralPath $installRoot -PathType Container) {
    Remove-Item -LiteralPath $installRoot -Recurse -Force
}

$remainingRunValue = $null
if (Test-Path -LiteralPath $runKeyPath) {
    $remainingRunValue = Get-ItemProperty -Path $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue
}
if ($null -ne $remainingRunValue) {
    throw 'Uninstall verification failed: the HKCU Run value still exists.'
}
if (Test-Path -LiteralPath $installRoot) {
    throw 'Uninstall verification failed: installed files still exist.'
}

Write-Host ''
Write-Host 'Uninstall verified.'
Write-Host 'Removed only the widget installation and its HKCU Run value.'
Write-Host 'Codex user data was preserved.'
