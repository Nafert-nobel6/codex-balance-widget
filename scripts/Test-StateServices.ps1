[CmdletBinding()]
param(
    [string]$PublishDirectory
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

$coreAssemblyPath = Join-Path $PublishDirectory 'CodexBalanceWidget.Core.dll'
$appAssemblyPath = Join-Path $PublishDirectory 'CodexBalanceWidget.exe'
foreach ($assemblyPath in @($coreAssemblyPath, $appAssemblyPath)) {
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "Build artifact is missing: $assemblyPath"
    }
}

Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName PresentationCore
Add-Type -Path $coreAssemblyPath
[Reflection.Assembly]::LoadFrom($appAssemblyPath) | Out-Null

$passed = 0
$failed = 0

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )
    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Equal {
    param(
        $Expected,
        $Actual,
        [string]$Message
    )
    if (-not [object]::Equals($Expected, $Actual)) {
        throw "$Message (expected '$Expected', actual '$Actual')"
    }
}

function Assert-Near {
    param(
        [double]$Expected,
        [double]$Actual,
        [double]$Tolerance,
        [string]$Message
    )
    if ([Math]::Abs($Expected - $Actual) -gt $Tolerance) {
        throw "$Message (expected '$Expected', actual '$Actual')"
    }
}

function Invoke-Test {
    param(
        [string]$Name,
        [scriptblock]$Test
    )

    try {
        & $Test
        $script:passed++
        Write-Host "[PASS] $Name" -ForegroundColor Green
    }
    catch {
        $script:failed++
        Write-Host "[FAIL] $Name`: $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Write-TestPng {
    param(
        [string]$Path,
        [int]$Width,
        [int]$Height,
        [byte]$Red,
        [byte]$Green,
        [byte]$Blue
    )

    $stride = $Width * 4
    $pixels = New-Object byte[] ($stride * $Height)
    for ($offset = 0; $offset -lt $pixels.Length; $offset += 4) {
        $pixels[$offset] = $Blue
        $pixels[$offset + 1] = $Green
        $pixels[$offset + 2] = $Red
        $pixels[$offset + 3] = 255
    }

    $bitmap = New-Object System.Windows.Media.Imaging.WriteableBitmap(
        $Width,
        $Height,
        96.0,
        96.0,
        [System.Windows.Media.PixelFormats]::Bgra32,
        $null
    )
    $bitmap.WritePixels(
        (New-Object System.Windows.Int32Rect(0, 0, $Width, $Height)),
        $pixels,
        $stride,
        0
    )
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add(
        [System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap)
    )
    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None
    )
    try {
        $encoder.Save($stream)
        $stream.Flush()
    }
    finally {
        $stream.Dispose()
    }
}

$temporaryRoot = Join-Path (
    [System.IO.Path]::GetTempPath()
) ('CodexBalanceWidget-state-tests-' + [Guid]::NewGuid().ToString('N'))
$temporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
$expectedTemporaryPrefix = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::GetTempPath()
).TrimEnd('\') + '\CodexBalanceWidget-state-tests-'
if (-not $temporaryRoot.StartsWith(
    $expectedTemporaryPrefix,
    [StringComparison]::OrdinalIgnoreCase
)) {
    throw "Unsafe temporary test path: $temporaryRoot"
}
New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null

try {
    $stateDirectory = Join-Path $temporaryRoot 'state'
    $store = New-Object CodexBalanceWidget.App.Infrastructure.WidgetSettingsStore(
        $stateDirectory,
        $null
    )

    Invoke-Test 'default settings load' {
        $settings = $store.Load()
        Assert-Equal 'Light' $settings.Theme 'default theme'
        Assert-Near 0.94 $settings.PanelOpacity 0.0001 'default opacity'
        Assert-Near 30.0 $settings.ArchiveDelay.TotalSeconds 0.0001 'default archive delay'
        Assert-Near 300.0 $settings.WindowWidth 0.0001 'default width'
        Assert-Near 320.0 $settings.WindowHeight 0.0001 'default height'
        Assert-Equal ([string]::Empty) $settings.AvatarPath 'default avatar'
    }

    Invoke-Test 'settings sanitization and path confinement' {
        $unsafe = New-Object CodexBalanceWidget.App.Infrastructure.WidgetSettings
        $unsafe.Theme = 'InjectedTheme'
        $unsafe.PanelOpacity = [double]::NaN
        $unsafe.ArchiveDelay = [TimeSpan]::FromMinutes(7)
        $unsafe.WindowWidth = 10.0
        $unsafe.WindowHeight = 99999.0
        $unsafe.WindowOffsetX = -999.0
        $unsafe.WindowOffsetY = 999.0
        $unsafe.BubbleY = -10.0
        $unsafe.AvatarPath = Join-Path $temporaryRoot 'outside.png'

        $safe = $store.Sanitize($unsafe)
        Assert-Equal 'Light' $safe.Theme 'invalid theme fallback'
        Assert-Near 0.94 $safe.PanelOpacity 0.0001 'non-finite opacity fallback'
        Assert-Near 30.0 $safe.ArchiveDelay.TotalSeconds 0.0001 'invalid archive delay fallback'
        Assert-Near 240.0 $safe.WindowWidth 0.0001 'minimum width'
        Assert-Near 320.0 $safe.WindowHeight 0.0001 'maximum height'
        Assert-Near -96.0 $safe.WindowOffsetX 0.0001 'minimum window X offset'
        Assert-Near 16.0 $safe.WindowOffsetY 0.0001 'maximum window Y offset'
        Assert-Near 0.0 $safe.BubbleY 0.0001 'minimum bubble position'
        Assert-Equal ([string]::Empty) $safe.AvatarPath 'outside avatar rejection'
    }

    Invoke-Test 'settings round-trip and relative avatar storage' {
        $avatarDirectory = Join-Path $stateDirectory 'avatars'
        New-Item -ItemType Directory -Path $avatarDirectory -Force | Out-Null
        $avatarPath = Join-Path $avatarDirectory 'avatar.png'

        $settings = New-Object CodexBalanceWidget.App.Infrastructure.WidgetSettings
        $settings.Theme = 'Dark'
        $settings.PanelOpacity = 0.81
        $settings.ArchiveDelay = [TimeSpan]::FromSeconds(15)
        $settings.WindowWidth = 244.0
        $settings.WindowHeight = 232.0
        $settings.WindowOffsetX = -44.0
        $settings.WindowOffsetY = -32.0
        $settings.BubbleY = 73.0
        $settings.AvatarPath = $avatarPath
        $store.Save($settings)

        $json = Get-Content -LiteralPath $store.SettingsPath -Raw
        Assert-True ($json -notmatch [regex]::Escape($stateDirectory)) `
            'settings must not persist an absolute state path'

        $loaded = $store.Load()
        Assert-Equal 'Dark' $loaded.Theme 'round-trip theme'
        Assert-Near 0.81 $loaded.PanelOpacity 0.0001 'round-trip opacity'
        Assert-Near 15.0 $loaded.ArchiveDelay.TotalSeconds 0.0001 'round-trip delay'
        Assert-Near -44.0 $loaded.WindowOffsetX 0.0001 'round-trip X offset'
        Assert-Near -32.0 $loaded.WindowOffsetY 0.0001 'round-trip Y offset'
        Assert-Equal $avatarPath $loaded.AvatarPath 'resolved avatar path'
    }

    Invoke-Test 'damaged settings recover from backup' {
        $next = New-Object CodexBalanceWidget.App.Infrastructure.WidgetSettings
        $next.Theme = 'System'
        $store.Save($next)
        Set-Content -LiteralPath $store.SettingsPath -Value '{invalid json' -Encoding UTF8

        $recovered = $store.Load()
        Assert-Equal 'Dark' $recovered.Theme 'backup should retain prior settings'
    }

    Invoke-Test 'state path traversal rejection' {
        $paths = New-Object CodexBalanceWidget.App.Infrastructure.WidgetStatePaths(
            $stateDirectory
        )
        Assert-True ($paths.IsInsideStateDirectory($paths.SettingsPath)) `
            'settings path should be inside state directory'
        Assert-True (-not $paths.IsInsideStateDirectory(
            (Join-Path $temporaryRoot 'sibling\settings.json')
        )) 'sibling path must be rejected'
        Assert-True (-not $paths.IsInsideAvatarDirectory(
            (Join-Path $stateDirectory 'avatar.png')
        )) 'avatar outside avatar directory must be rejected'
    }

    $sourceOne = Join-Path $temporaryRoot 'source-one.png'
    $sourceTwo = Join-Path $temporaryRoot 'source-two.png'
    Write-TestPng -Path $sourceOne -Width 96 -Height 64 -Red 20 -Green 80 -Blue 160
    Write-TestPng -Path $sourceTwo -Width 64 -Height 96 -Red 180 -Green 60 -Blue 20
    $avatarService =
        New-Object CodexBalanceWidget.App.Infrastructure.AvatarImageService(
            $stateDirectory,
            $null
        )

    Invoke-Test 'avatar inspection' {
        $information = $avatarService.Inspect($sourceOne)
        Assert-Equal 96 $information.SourceWidth 'source width'
        Assert-Equal 64 $information.SourceHeight 'source height'
        Assert-True ($information.FileBytes -gt 0) 'source size'
    }

    Invoke-Test 'avatar crop creates validated 256px PNG' {
        $crop =
            New-Object CodexBalanceWidget.App.Infrastructure.AvatarCropParameters(
                0.5,
                0.5,
                1.5
            )
        $outputPath = $avatarService.ImportAndCrop($sourceOne, $crop)
        Assert-Equal $avatarService.OutputPath $outputPath 'output path'
        Assert-True (Test-Path -LiteralPath $outputPath -PathType Leaf) `
            'output file should exist'
        $outputInformation = $avatarService.Inspect($outputPath)
        Assert-Equal 256 $outputInformation.SourceWidth 'output width'
        Assert-Equal 256 $outputInformation.SourceHeight 'output height'
    }

    Invoke-Test 'avatar replacement retains last-known-good backup' {
        $crop =
            [CodexBalanceWidget.App.Infrastructure.AvatarCropParameters]::Centered
        $avatarService.ImportAndCrop($sourceTwo, $crop) | Out-Null
        $backupPath = Join-Path $stateDirectory 'avatars\avatar.backup.png'
        Assert-True (Test-Path -LiteralPath $backupPath -PathType Leaf) `
            'backup avatar should exist'
        $backupInformation = $avatarService.Inspect($backupPath)
        Assert-Equal 256 $backupInformation.SourceWidth 'backup width'
        Assert-Equal 256 $backupInformation.SourceHeight 'backup height'
    }

    Invoke-Test 'invalid crop parameters fail closed' {
        $threw = $false
        try {
            New-Object CodexBalanceWidget.App.Infrastructure.AvatarCropParameters(
                0.5,
                0.5,
                9.0
            ) | Out-Null
        }
        catch {
            $threw = $true
        }
        Assert-True $threw 'zoom above 8x must fail'
    }

    Invoke-Test 'invalid image contents fail closed' {
        $invalidImage = Join-Path $temporaryRoot 'invalid.png'
        Set-Content -LiteralPath $invalidImage -Value 'not an image' -Encoding ASCII
        $threw = $false
        try {
            $avatarService.Inspect($invalidImage) | Out-Null
        }
        catch [CodexBalanceWidget.App.Infrastructure.AvatarImageException] {
            $threw = $true
        }
        Assert-True $threw 'invalid PNG contents must fail'
    }

    Invoke-Test 'oversized image fails before decoding' {
        $oversized = Join-Path $temporaryRoot 'oversized.png'
        $stream = [System.IO.File]::Open(
            $oversized,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None
        )
        try {
            $stream.SetLength(
                [CodexBalanceWidget.App.Infrastructure.AvatarImageService]::MaximumInputBytes +
                1
            )
        }
        finally {
            $stream.Dispose()
        }

        $threw = $false
        try {
            $avatarService.Inspect($oversized) | Out-Null
        }
        catch [CodexBalanceWidget.App.Infrastructure.AvatarImageException] {
            $threw = $true
        }
        Assert-True $threw 'oversized input must fail'
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
        if (-not $resolvedTemporaryRoot.StartsWith(
            $expectedTemporaryPrefix,
            [StringComparison]::OrdinalIgnoreCase
        )) {
            throw "Refusing unsafe test cleanup: $resolvedTemporaryRoot"
        }
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}

Write-Host ''
Write-Host "State-service tests: passed=$passed; failed=$failed"
if ($failed -gt 0) {
    throw "$failed state-service test(s) failed."
}
