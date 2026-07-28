[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$failures = New-Object System.Collections.Generic.List[string]
$passes = New-Object System.Collections.Generic.List[string]

function Add-SecurityPass {
    param([string]$Message)
    $passes.Add($Message)
    Write-Host "[PASS] $Message" -ForegroundColor Green
}

function Add-SecurityFailure {
    param([string]$Message)
    $failures.Add($Message)
    Write-Host "[FAIL] $Message" -ForegroundColor Red
}

function Get-RepositorySourceFiles {
    $roots = @(
        (Join-Path $repoRoot 'src'),
        (Join-Path $repoRoot 'tests'),
        (Join-Path $repoRoot 'scripts'),
        (Join-Path $repoRoot 'installer'),
        (Join-Path $repoRoot 'docs')
    )
    $files = New-Object System.Collections.Generic.List[System.IO.FileInfo]
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            continue
        }
        foreach ($file in @(Get-ChildItem -LiteralPath $root -File -Recurse -Force)) {
            if ($file.FullName -eq $PSCommandPath -or
                $file.FullName -match '(?i)\\(?:bin|obj|artifacts|runtime)\\') {
                continue
            }
            $files.Add($file)
        }
    }
    foreach ($rootFileName in @('README.md', '.gitignore', 'SECURITY.md')) {
        $rootFile = Join-Path $repoRoot $rootFileName
        if (Test-Path -LiteralPath $rootFile -PathType Leaf) {
            $files.Add((Get-Item -LiteralPath $rootFile))
        }
    }
    return $files.ToArray()
}

$repositoryPrefix = $repoRoot.TrimEnd('\') + '\'
$reparsePoints = @(
    Get-ChildItem -LiteralPath $repoRoot -Recurse -Force -ErrorAction Stop |
        Where-Object {
            $_.FullName -notmatch '(?i)\\\.git(?:\\|$)' -and
            ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
        }
)
if ($reparsePoints.Count -eq 0) {
    Add-SecurityPass 'Repository source tree contains no reparse points.'
}
else {
    foreach ($item in $reparsePoints) {
        Add-SecurityFailure "Repository contains a reparse point: $($item.FullName)"
    }
}

$sourceFiles = @(Get-RepositorySourceFiles)
$sensitiveFilePatterns = @(
    '(?i)^\.env(?:\.|$)',
    '(?i)\.(?:pfx|p12|key)$',
    '(?i)^id_(?:rsa|dsa|ecdsa|ed25519)$',
    '(?i)^settings(?:\.backup)?\.json$',
    '(?i)^avatar(?:\.backup)?\.(?:png|jpe?g)$',
    '(?i)^codex(?:\.last-good)?\.exe$'
)
foreach ($file in $sourceFiles) {
    foreach ($pattern in $sensitiveFilePatterns) {
        if ($file.Name -match $pattern) {
            Add-SecurityFailure "Sensitive or generated file must not be published: $($file.FullName)"
            break
        }
    }
}
if ($failures.Count -eq 0) {
    Add-SecurityPass 'No credentials, personal state, avatar, or private runtime files are in the source set.'
}

$textExtensions = @(
    '.cs', '.csproj', '.xaml', '.ps1', '.md', '.manifest', '.gitignore'
)
$secretPatterns = @(
    '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
    '\bsk-[A-Za-z0-9_-]{20,}\b',
    '\bgh[pousr]_[A-Za-z0-9]{20,}\b',
    '\bAKIA[0-9A-Z]{16}\b'
)
foreach ($file in $sourceFiles) {
    if ($textExtensions -notcontains $file.Extension.ToLowerInvariant() -and
        $file.Name -ne '.gitignore') {
        continue
    }
    $content = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($pattern in $secretPatterns) {
        if ($content -match $pattern) {
            Add-SecurityFailure "Possible credential material in $($file.FullName)"
        }
    }
}
if (-not ($failures | Where-Object { $_ -like 'Possible credential*' })) {
    Add-SecurityPass 'No common private-key, OpenAI-key, GitHub-token, or AWS-key signatures were found.'
}

$runtimeFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src') -File -Recurse -Force
)
$installerFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $repoRoot 'installer') -File -Recurse -Force
)
$executionBoundaryText = (
    @($runtimeFiles + $installerFiles) |
        ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
) -join [Environment]::NewLine

$forbiddenRuntimePatterns = [ordered]@{
    'network client API' = '(?i)\b(?:HttpClient|WebClient|WebRequest|TcpClient|UdpClient|Socket)\b'
    'network download command' = '(?i)\b(?:Invoke-WebRequest|Invoke-RestMethod|Start-BitsTransfer|DownloadString|DownloadFile)\b'
    'dynamic PowerShell execution' = '(?i)\b(?:Invoke-Expression|ScriptBlock\s*::\s*Create)\b'
    'unsafe .NET deserialization' = '(?i)\b(?:BinaryFormatter|LosFormatter|ObjectStateFormatter)\b'
    'dynamic assembly loading' = '(?i)\bAssembly\s*\.\s*Load(?:From|File)?\s*\('
    'runtime XAML loading' = '(?i)\bXamlReader\s*\.\s*(?:Load|Parse)\s*\('
}
foreach ($entry in $forbiddenRuntimePatterns.GetEnumerator()) {
    if ($executionBoundaryText -match $entry.Value) {
        Add-SecurityFailure "Forbidden $($entry.Key) was found in runtime or installer source."
    }
}
if (-not ($failures | Where-Object { $_ -like 'Forbidden *' })) {
    Add-SecurityPass 'No network client, downloader, dynamic-code, or unsafe-deserialization API is present in runtime/installer code.'
}

$coreSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'src\CodexBalanceWidget.Core\CodexAppServerRateLimitSource.cs'
) -Raw
if ($coreSource -match 'account/rateLimitResetCredit/consume') {
    Add-SecurityFailure 'Reset-credit consumption API is present; the widget must remain read-only.'
}
else {
    Add-SecurityPass 'Reset-credit consumption API is absent.'
}
if ($coreSource -notmatch 'app-server --listen stdio://' -or
    $coreSource -notmatch 'UseShellExecute\s*=\s*false' -or
    $coreSource -notmatch 'CreateNoWindow\s*=\s*true') {
    Add-SecurityFailure 'The private app-server launch contract is not fixed to hidden stdio mode.'
}
else {
    Add-SecurityPass 'Private app-server launch is fixed to hidden stdio mode with shell execution disabled.'
}
if ($coreSource -match 'MaxJsonLength\s*=\s*int\.MaxValue' -or
    $coreSource -notmatch 'MaxJsonMessageCharacters\s*=\s*1024\s*\*\s*1024') {
    Add-SecurityFailure 'App-server JSON input is not bounded to the expected 1 MiB limit.'
}
else {
    Add-SecurityPass 'App-server JSONL input has a bounded message size.'
}

$installerSource = Get-Content -LiteralPath (
    Join-Path $repoRoot 'installer\Install.ps1'
) -Raw
if ($installerSource -notmatch 'GetNameInfo' -or
    $installerSource -match "-notlike\\s+'\\*OpenAI OpCo, LLC\\*'") {
    Add-SecurityFailure 'Installer publisher validation is not an exact certificate-name comparison.'
}
else {
    Add-SecurityPass 'Installer requires the exact OpenAI certificate publisher name.'
}

$gitIgnore = Get-Content -LiteralPath (Join-Path $repoRoot '.gitignore') -Raw
foreach ($requiredIgnore in @(
    'artifacts/',
    'runtime/',
    'logs/',
    'avatars/',
    'settings.json',
    '*.pfx',
    '.env'
)) {
    if ($gitIgnore -notmatch [regex]::Escape($requiredIgnore)) {
        Add-SecurityFailure ".gitignore is missing required exclusion: $requiredIgnore"
    }
}
if (-not ($failures | Where-Object { $_ -like '.gitignore*' })) {
    Add-SecurityPass '.gitignore excludes build output, runtime copies, personal state, logs, and signing secrets.'
}

Write-Host ''
Write-Host "Source security checks: passed=$($passes.Count); failed=$($failures.Count)"
if ($failures.Count -gt 0) {
    throw 'Source security checks failed.'
}
