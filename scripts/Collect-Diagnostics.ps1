[CmdletBinding()]
param(
    [string]$OutputPath,

    [ValidateRange(0, 1000)]
    [int]$LogTailLines = 200,

    [switch]$ConsoleOnly
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$installRoot = $null
if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    $installRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'CodexBalanceWidget'))
}
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValueName = 'CodexBalanceWidget'

function Protect-DiagnosticText {
    param([AllowEmptyString()][string]$Text)

    if ($null -eq $Text) {
        return ''
    }

    $protected = $Text
    $protected = [regex]::Replace(
        $protected,
        '(?i)("?(?:token|authorization|cookie|secret|credential|api[_-]?key)"?\s*[:=]\s*)("[^"]*"|''[^'']*''|[^\s,;]+)',
        '$1[REDACTED]'
    )
    $protected = [regex]::Replace($protected, '(?i)\bBearer\s+[A-Za-z0-9._~+/\-=]+', 'Bearer [REDACTED]')
    $protected = [regex]::Replace($protected, '\bsk-[A-Za-z0-9_-]{12,}\b', '[REDACTED_API_KEY]')
    $protected = [regex]::Replace(
        $protected,
        '\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b',
        '[REDACTED_EMAIL]'
    )
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $protected = $protected.Replace($env:USERPROFILE, '%USERPROFILE%')
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $protected = $protected.Replace($env:LOCALAPPDATA, '%LOCALAPPDATA%')
    }
    return $protected
}

function Add-ReportLine {
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [AllowEmptyString()][string]$Value = ''
    )
    $Lines.Add((Protect-DiagnosticText -Text $Value))
}

function Get-SafeFileDescription {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return 'missing'
    }

    $file = Get-Item -LiteralPath $Path
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    $assemblyVersion = 'not-managed'
    try {
        $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($Path).Version.ToString()
    }
    catch {
    }
    return "present; bytes=$($file.Length); sha256=$hash; assemblyVersion=$assemblyVersion"
}

$lines = New-Object System.Collections.Generic.List[string]
Add-ReportLine $lines 'Codex Balance Widget diagnostics'
Add-ReportLine $lines ('CollectedUtc: ' + [DateTimeOffset]::UtcNow.ToString('o'))
Add-ReportLine $lines ('PowerShell: ' + $PSVersionTable.PSVersion.ToString())
Add-ReportLine $lines ('OS: ' + [Environment]::OSVersion.VersionString)
Add-ReportLine $lines ('Is64BitOS: ' + [Environment]::Is64BitOperatingSystem)
Add-ReportLine $lines ''

Add-ReportLine $lines '[Installation]'
if ($null -eq $installRoot) {
    Add-ReportLine $lines 'InstallRoot: unavailable (LOCALAPPDATA is not set)'
}
else {
    Add-ReportLine $lines ('InstallRoot: ' + $installRoot)
    Add-ReportLine $lines ('CodexBalanceWidget.exe: ' + (Get-SafeFileDescription (Join-Path $installRoot 'CodexBalanceWidget.exe')))
    Add-ReportLine $lines ('CodexBalanceWidget.Core.dll: ' + (Get-SafeFileDescription (Join-Path $installRoot 'CodexBalanceWidget.Core.dll')))
    $privateRuntime = Join-Path $installRoot 'runtime\codex.exe'
    Add-ReportLine $lines ('runtime\codex.exe: ' + (Get-SafeFileDescription $privateRuntime))
    if (Test-Path -LiteralPath $privateRuntime -PathType Leaf) {
        $runtimeSignature = Get-AuthenticodeSignature -LiteralPath $privateRuntime
        $runtimeSubject = ''
        if ($null -ne $runtimeSignature.SignerCertificate) {
            $runtimeSubject = [string]$runtimeSignature.SignerCertificate.Subject
        }
        Add-ReportLine $lines ("runtime signature: status=$($runtimeSignature.Status); subject=$runtimeSubject")
    }
}
Add-ReportLine $lines ''

Add-ReportLine $lines '[Auto-start]'
$runValue = $null
if (Test-Path -LiteralPath $runKeyPath) {
    $runProperties = Get-ItemProperty -Path $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue
    if ($null -ne $runProperties) {
        $runValue = [string]$runProperties.$runValueName
    }
}
if ($null -eq $runValue) {
    Add-ReportLine $lines 'HKCU Run value: missing'
}
else {
    Add-ReportLine $lines ('HKCU Run value: ' + $runValue)
}
Add-ReportLine $lines ''

Add-ReportLine $lines '[Processes]'
$widgetProcesses = @(Get-Process -Name 'CodexBalanceWidget' -ErrorAction SilentlyContinue)
if ($widgetProcesses.Count -eq 0) {
    Add-ReportLine $lines 'Widget process: not running'
}
else {
    foreach ($process in $widgetProcesses) {
        $path = 'unavailable'
        try {
            $path = $process.MainModule.FileName
        }
        catch {
        }
        Add-ReportLine $lines "Widget process: pid=$($process.Id); path=$path"
    }
}

try {
    $chatGptProcesses = @(Get-CimInstance Win32_Process -Filter "Name='ChatGPT.exe'" -ErrorAction Stop)
    if ($chatGptProcesses.Count -eq 0) {
        Add-ReportLine $lines 'Codex ChatGPT.exe process: not running'
    }
    else {
        foreach ($process in $chatGptProcesses) {
            $isCodexPackage = ([string]$process.ExecutablePath -match '(?i)\\WindowsApps\\OpenAI\.Codex_')
            Add-ReportLine $lines "ChatGPT.exe: pid=$($process.ProcessId); CodexPackagePath=$isCodexPackage; path=$($process.ExecutablePath)"
        }
    }
}
catch {
    Add-ReportLine $lines ('ChatGPT.exe inspection error: ' + $_.Exception.Message)
}
Add-ReportLine $lines ''

Add-ReportLine $lines '[Codex MSIX packages]'
try {
    $packages = @(
        Get-AppxPackage -ErrorAction Stop |
            Where-Object {
                $_.PackageFamilyName -eq 'OpenAI.Codex_2p2nqsd0c76g0' -or
                $_.Name -like 'OpenAI.Codex*'
            } |
            Sort-Object -Property Version -Descending
    )
    if ($packages.Count -eq 0) {
        Add-ReportLine $lines 'No matching package found.'
    }
    else {
        foreach ($package in $packages) {
            Add-ReportLine $lines "Name=$($package.Name); Version=$($package.Version); Family=$($package.PackageFamilyName); Location=$($package.InstallLocation)"
        }
    }
}
catch {
    Add-ReportLine $lines ('Package query error: ' + $_.Exception.Message)
}

if ($LogTailLines -gt 0 -and $null -ne $installRoot) {
    Add-ReportLine $lines ''
    Add-ReportLine $lines "[Redacted log tails: last $LogTailLines lines per file]"
    $logDirectory = Join-Path $installRoot 'logs'
    if (-not (Test-Path -LiteralPath $logDirectory -PathType Container)) {
        Add-ReportLine $lines 'No log directory.'
    }
    else {
        foreach ($logFile in @(Get-ChildItem -LiteralPath $logDirectory -File | Sort-Object -Property Name)) {
            Add-ReportLine $lines ("--- " + $logFile.Name + " ---")
            try {
                foreach ($logLine in @(Get-Content -LiteralPath $logFile.FullName -Tail $LogTailLines -ErrorAction Stop)) {
                    Add-ReportLine $lines ([string]$logLine)
                }
            }
            catch {
                Add-ReportLine $lines ('Log read error: ' + $_.Exception.Message)
            }
        }
    }
}

$reportText = [string]::Join([Environment]::NewLine, $lines)
if ($ConsoleOnly) {
    $reportText
    return
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $diagnosticDirectory = Join-Path $repoRoot 'artifacts\diagnostics'
    New-Item -ItemType Directory -Path $diagnosticDirectory -Force | Out-Null
    $OutputPath = Join-Path $diagnosticDirectory ('CodexBalanceWidget-diagnostics-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $repoRoot $OutputPath
}
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}
Set-Content -LiteralPath $OutputPath -Value $reportText -Encoding UTF8

Write-Host "Redacted diagnostics written to: $OutputPath"
Write-Host 'Review the report before sharing it outside your organization.'
