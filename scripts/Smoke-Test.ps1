[CmdletBinding()]
param(
    [string]$PublishDirectory,

    [switch]$SkipConsoleTests
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path $repoRoot 'artifacts\publish'
}
elseif (-not [System.IO.Path]::IsPathRooted($PublishDirectory)) {
    $PublishDirectory = Join-Path $repoRoot $PublishDirectory
}
$PublishDirectory = [System.IO.Path]::GetFullPath($PublishDirectory)

$failures = New-Object System.Collections.Generic.List[string]
$passes = New-Object System.Collections.Generic.List[string]

function Add-Pass {
    param([string]$Message)
    $passes.Add($Message)
    Write-Host "[PASS] $Message" -ForegroundColor Green
}

function Add-Failure {
    param([string]$Message)
    $failures.Add($Message)
    Write-Host "[FAIL] $Message" -ForegroundColor Red
}

function Test-PowerShellSyntax {
    param([string]$Path)

    $tokens = $null
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$parseErrors
    ) | Out-Null
    if ($parseErrors.Count -eq 0) {
        Add-Pass "PowerShell syntax: $([System.IO.Path]::GetFileName($Path))"
    }
    else {
        foreach ($parseError in $parseErrors) {
            Add-Failure "PowerShell syntax in $Path at $($parseError.Extent.StartLineNumber): $($parseError.Message)"
        }
    }
}

Write-Host 'Codex Balance Widget non-destructive smoke test'
Write-Host 'This script does not install, uninstall, change the registry, stop processes, or copy a Codex runtime.'
Write-Host ''

foreach ($scriptPath in @(
    (Join-Path $repoRoot 'scripts\Build.ps1'),
    (Join-Path $repoRoot 'scripts\Build-VisualHarness.ps1'),
    (Join-Path $repoRoot 'scripts\Collect-Diagnostics.ps1'),
    (Join-Path $repoRoot 'scripts\Package-Release.ps1'),
    (Join-Path $repoRoot 'scripts\Smoke-Test.ps1'),
    (Join-Path $repoRoot 'scripts\Test-SourceSecurity.ps1'),
    (Join-Path $repoRoot 'scripts\Test-Deployment.ps1'),
    (Join-Path $repoRoot 'installer\Install.ps1'),
    (Join-Path $repoRoot 'installer\Uninstall.ps1')
)) {
    if (Test-Path -LiteralPath $scriptPath -PathType Leaf) {
        Test-PowerShellSyntax -Path $scriptPath
    }
    else {
        Add-Failure "Required script is missing: $scriptPath"
    }
}

try {
    & (Join-Path $repoRoot 'scripts\Test-SourceSecurity.ps1')
    Add-Pass 'Source security policy checks completed successfully.'
}
catch {
    Add-Failure "Source security policy checks failed: $($_.Exception.Message)"
}

$installerText = ''
foreach ($installerPath in @(
    (Join-Path $repoRoot 'installer\Install.ps1'),
    (Join-Path $repoRoot 'installer\Uninstall.ps1')
)) {
    if (Test-Path -LiteralPath $installerPath -PathType Leaf) {
        $installerText += [Environment]::NewLine + (Get-Content -LiteralPath $installerPath -Raw)
    }
}

if ($installerText -match '(?i)HKLM:|HKEY_LOCAL_MACHINE') {
    Add-Failure 'Installer scripts reference a machine-wide registry hive.'
}
else {
    Add-Pass 'Installer registry scope is per-user (no HKLM reference).'
}

$codexDataMutationLines = @(
    $installerText -split '\r?\n' |
        Where-Object {
            $_ -match '(?i)\b(?:Remove-Item|Move-Item|Copy-Item|Set-Content|Clear-Content|New-Item)\b' -and
            $_ -match '(?i)\\\.codex(?:\\|''|")'
        }
)
if ($codexDataMutationLines.Count -gt 0) {
    Add-Failure 'Installer scripts contain a mutating command that directly targets Codex user data.'
}
else {
    Add-Pass 'Installer scripts contain no mutating command directly targeting the Codex user-data directory.'
}

if ($installerText -notmatch 'OpenAI OpCo, LLC' -or
    $installerText -notmatch 'Get-AuthenticodeSignature' -or
    $installerText -notmatch 'GetNameInfo' -or
    $installerText -notmatch 'Get-FileHash') {
    Add-Failure 'Installer does not visibly enforce signer and SHA-256 runtime checks.'
}
else {
    Add-Pass 'Installer contains exact-publisher Authenticode and SHA-256 runtime checks.'
}

foreach ($binaryName in @('CodexBalanceWidget.exe', 'CodexBalanceWidget.Core.dll')) {
    $binaryPath = Join-Path $PublishDirectory $binaryName
    if (-not (Test-Path -LiteralPath $binaryPath -PathType Leaf)) {
        Add-Failure "Build artifact is missing: $binaryPath"
        continue
    }
    try {
        $assemblyName = [Reflection.AssemblyName]::GetAssemblyName($binaryPath)
        Add-Pass "$binaryName is a readable managed assembly (version $($assemblyName.Version))."
    }
    catch {
        Add-Failure "$binaryName is not a readable managed assembly: $($_.Exception.Message)"
    }
}

$checksumPath = Join-Path $PublishDirectory 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
    Add-Failure "Checksum manifest is missing: $checksumPath"
}
else {
    $checksumFailures = 0
    $manifestFiles = @{}
    foreach ($line in (Get-Content -LiteralPath $checksumPath)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        if ($line -notmatch '^([0-9a-fA-F]{64})  ([^\\/:*?"<>|]+)$') {
            $checksumFailures++
            Add-Failure "Malformed checksum line: $line"
            continue
        }
        $expectedHash = $Matches[1]
        $fileName = $Matches[2]
        $manifestKey = $fileName.ToLowerInvariant()
        if ($manifestFiles.ContainsKey($manifestKey)) {
            $checksumFailures++
            Add-Failure "Duplicate checksum target: $fileName"
            continue
        }
        $manifestFiles[$manifestKey] = $true
        $filePath = Join-Path $PublishDirectory $fileName
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            $checksumFailures++
            Add-Failure "Checksum target is missing: $filePath"
            continue
        }
        $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash
        if ($actualHash -ne $expectedHash) {
            $checksumFailures++
            Add-Failure "Checksum mismatch: $filePath"
        }
    }
    foreach ($publishedFile in @(Get-ChildItem -LiteralPath $PublishDirectory -File)) {
        if ($publishedFile.Name -ne 'SHA256SUMS.txt' -and
            -not $manifestFiles.ContainsKey($publishedFile.Name.ToLowerInvariant())) {
            $checksumFailures++
            Add-Failure "Published artifact is not covered by checksums: $($publishedFile.Name)"
        }
    }
    if ($checksumFailures -eq 0) {
        Add-Pass 'All published artifact SHA-256 checksums match.'
    }
}

if ($SkipConsoleTests) {
    Write-Host '[SKIP] Console tests were explicitly skipped.' -ForegroundColor Yellow
}
else {
    foreach ($testContract in @(
        @{ Name = 'Core'; Path = (Join-Path $PublishDirectory 'CodexBalanceWidget.Core.Tests.exe') },
        @{ Name = 'Infrastructure'; Path = (Join-Path $PublishDirectory 'CodexBalanceWidget.Infrastructure.Tests.exe') }
    )) {
        if (-not (Test-Path -LiteralPath $testContract.Path -PathType Leaf)) {
            Add-Failure "$($testContract.Name) console test executable is missing: $($testContract.Path)"
            continue
        }
        Write-Host "[RUN ] $($testContract.Name) console tests"
        & $testContract.Path
        if ($LASTEXITCODE -eq 0) {
            Add-Pass "$($testContract.Name) console tests completed successfully."
        }
        else {
            Add-Failure "$($testContract.Name) console tests failed with exit code $LASTEXITCODE."
        }
    }
}

try {
    $matchingPackages = @(
        Get-AppxPackage -ErrorAction Stop |
            Where-Object {
                $_.PackageFamilyName -eq 'OpenAI.Codex_2p2nqsd0c76g0' -or
                $_.Name -like 'OpenAI.Codex*'
            }
    )
    if ($matchingPackages.Count -gt 0) {
        Add-Pass 'A matching Codex MSIX package is discoverable (read-only probe).'
    }
    else {
        Write-Host '[INFO] No Codex MSIX package was found; runtime staging would remain pending.'
    }
}
catch {
    Write-Host "[INFO] Codex package discovery was unavailable: $($_.Exception.Message)"
}

Write-Host ''
Write-Host "Passes: $($passes.Count); Failures: $($failures.Count)"
if ($failures.Count -gt 0) {
    throw 'Smoke test failed. Review the failures above.'
}
Write-Host 'Smoke test passed. No system state was changed.'
