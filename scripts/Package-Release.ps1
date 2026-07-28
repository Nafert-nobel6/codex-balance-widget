[CmdletBinding()]
param(
    [string]$Version = '1.0.0',

    [switch]$SkipBuild
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:[-.][0-9A-Za-z.-]+)?$') {
    throw "Version must be a semantic version such as 1.0.0: $Version"
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishDirectory = Join-Path $repoRoot 'artifacts\publish'
$releaseDirectory = Join-Path $repoRoot 'artifacts\release'
$packageName = "CodexBalanceWidget-v$Version"
$stagingRoot = Join-Path $releaseDirectory $packageName
$zipPath = Join-Path $releaseDirectory ($packageName + '.zip')
$zipChecksumPath = $zipPath + '.sha256'
$releasePrefix = [System.IO.Path]::GetFullPath($releaseDirectory).TrimEnd('\') + '\'

foreach ($target in @($stagingRoot, $zipPath, $zipChecksumPath)) {
    $fullTarget = [System.IO.Path]::GetFullPath($target)
    if (-not $fullTarget.StartsWith(
        $releasePrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe release output target: $fullTarget"
    }
}

& (Join-Path $PSScriptRoot 'Test-SourceSecurity.ps1')

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'Build.ps1') -Clean -RunTests
    & (Join-Path $PSScriptRoot 'Smoke-Test.ps1')
}

foreach ($requiredPath in @(
    (Join-Path $publishDirectory 'CodexBalanceWidget.exe'),
    (Join-Path $publishDirectory 'CodexBalanceWidget.Core.dll'),
    (Join-Path $repoRoot 'installer\Install.ps1'),
    (Join-Path $repoRoot 'installer\Uninstall.ps1'),
    (Join-Path $repoRoot 'LICENSE'),
    (Join-Path $repoRoot 'README.md'),
    (Join-Path $repoRoot 'docs\USAGE.md'),
    (Join-Path $repoRoot 'docs\FEATURES.md'),
    (Join-Path $repoRoot 'SECURITY.md')
)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required release input is missing: $requiredPath"
    }
}

New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
foreach ($generatedTarget in @($stagingRoot, $zipPath, $zipChecksumPath)) {
    if (Test-Path -LiteralPath $generatedTarget) {
        Remove-Item -LiteralPath $generatedTarget -Recurse -Force
    }
}

$payloadDirectory = Join-Path $stagingRoot 'artifacts\publish'
$installerDirectory = Join-Path $stagingRoot 'installer'
$documentationDirectory = Join-Path $stagingRoot 'docs'
New-Item -ItemType Directory -Path $payloadDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $installerDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $documentationDirectory -Force | Out-Null

$payloadNames = @(
    'CodexBalanceWidget.Core.dll',
    'CodexBalanceWidget.exe'
)
foreach ($payloadName in $payloadNames) {
    Copy-Item -LiteralPath (Join-Path $publishDirectory $payloadName) `
        -Destination (Join-Path $payloadDirectory $payloadName)
}

$payloadHashLines = foreach ($payloadName in ($payloadNames | Sort-Object)) {
    $payloadPath = Join-Path $payloadDirectory $payloadName
    $hash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $payloadName"
}
Set-Content -LiteralPath (Join-Path $payloadDirectory 'SHA256SUMS.txt') `
    -Value $payloadHashLines -Encoding ASCII

Copy-Item -LiteralPath (Join-Path $repoRoot 'installer\Install.ps1') `
    -Destination (Join-Path $installerDirectory 'Install.ps1')
Copy-Item -LiteralPath (Join-Path $repoRoot 'installer\Uninstall.ps1') `
    -Destination (Join-Path $installerDirectory 'Uninstall.ps1')
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') `
    -Destination (Join-Path $stagingRoot 'README.md')
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') `
    -Destination (Join-Path $stagingRoot 'LICENSE')
Copy-Item -LiteralPath (Join-Path $repoRoot 'SECURITY.md') `
    -Destination (Join-Path $stagingRoot 'SECURITY.md')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\USAGE.md') `
    -Destination (Join-Path $documentationDirectory 'USAGE.md')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\FEATURES.md') `
    -Destination (Join-Path $documentationDirectory 'FEATURES.md')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\security.md') `
    -Destination (Join-Path $documentationDirectory 'security-design.md')
Set-Content -LiteralPath (Join-Path $stagingRoot 'VERSION.txt') `
    -Value $Version -Encoding ASCII

$allowedRelativeFiles = @(
    'LICENSE',
    'README.md',
    'SECURITY.md',
    'VERSION.txt',
    'artifacts\publish\CodexBalanceWidget.Core.dll',
    'artifacts\publish\CodexBalanceWidget.exe',
    'artifacts\publish\SHA256SUMS.txt',
    'docs\FEATURES.md',
    'docs\security-design.md',
    'docs\USAGE.md',
    'installer\Install.ps1',
    'installer\Uninstall.ps1'
)
$actualRelativeFiles = @(
    Get-ChildItem -LiteralPath $stagingRoot -File -Recurse -Force |
        ForEach-Object {
            $_.FullName.Substring($stagingRoot.Length).TrimStart('\')
        } |
        Sort-Object
)
$expectedRelativeFiles = @($allowedRelativeFiles | Sort-Object)
if (($actualRelativeFiles -join "`n") -ne ($expectedRelativeFiles -join "`n")) {
    throw "Release staging contents differ from the allowlist:`n$($actualRelativeFiles -join "`n")"
}

Compress-Archive -LiteralPath $stagingRoot -DestinationPath $zipPath `
    -CompressionLevel Optimal

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    foreach ($entry in $archive.Entries) {
        $entryName = $entry.FullName.Replace('/', '\')
        if ([System.IO.Path]::IsPathRooted($entryName) -or
            $entryName -match '(^|\\)\.\.(\\|$)' -or
            $entryName -match '(?i)(^|\\)(?:runtime|avatars|logs)(\\|$)' -or
            $entryName -match '(?i)(?:settings(?:\.backup)?\.json|codex(?:\.last-good)?\.exe)$') {
            throw "Unsafe ZIP entry: $($entry.FullName)"
        }
    }
}
finally {
    $archive.Dispose()
}

$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $zipChecksumPath `
    -Value "$zipHash  $([System.IO.Path]::GetFileName($zipPath))" `
    -Encoding ASCII

Write-Host ''
Write-Host 'GitHub Release package created.'
Write-Host "ZIP: $zipPath"
Write-Host "SHA-256: $zipHash"
Get-Item -LiteralPath $zipPath | Select-Object FullName, Length, LastWriteTime
