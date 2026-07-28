[CmdletBinding()]
param(
    [ValidateSet('Publish', 'Installed')]
    [string]$Target = 'Installed',

    [string]$PublishDirectory,

    [string]$ConfigurationPath,

    [string]$AvatarPath,

    [switch]$RequireRunning,

    [switch]$RequireRuntime,

    [switch]$ExerciseLifecycle,

    [ValidateRange(2, 60)]
    [int]$StartupTimeoutSeconds = 15,

    [ValidateRange(2, 60)]
    [int]$ShutdownTimeoutSeconds = 10,

    [string]$ReportPath,

    [switch]$PassThru
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

if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw 'LOCALAPPDATA is unavailable.'
}
$localAppData = [System.IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\')
$installDirectory = $localAppData + '\CodexBalanceWidget'
$targetDirectory = if ($Target -eq 'Installed') {
    $installDirectory
}
else {
    $PublishDirectory
}

if ($ExerciseLifecycle -and $Target -ne 'Installed') {
    throw '-ExerciseLifecycle is supported only with -Target Installed.'
}

$results = New-Object System.Collections.Generic.List[object]

function Add-Result {
    param(
        [ValidateSet('PASS', 'FAIL', 'INFO')]
        [string]$Status,
        [string]$Check,
        [string]$Detail
    )

    $item = [PSCustomObject]@{
        Status = $Status
        Check = $Check
        Detail = $Detail
    }
    $results.Add($item)

    $color = 'Gray'
    if ($Status -eq 'PASS') {
        $color = 'Green'
    }
    elseif ($Status -eq 'FAIL') {
        $color = 'Red'
    }
    Write-Host ("[{0}] {1}: {2}" -f $Status, $Check, $Detail) -ForegroundColor $color
}

function Test-ManagedAssembly {
    param(
        [string]$Path,
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Result 'FAIL' $Label "Missing: $Path"
        return
    }

    try {
        $assemblyName = [Reflection.AssemblyName]::GetAssemblyName($Path)
        Add-Result 'PASS' $Label "Managed assembly version $($assemblyName.Version); bytes=$((Get-Item -LiteralPath $Path).Length)"
    }
    catch {
        Add-Result 'FAIL' $Label "Unreadable managed assembly: $($_.Exception.Message)"
    }
}

function Get-PeMachine {
    param([string]$Path)

    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite
    )
    try {
        $reader = New-Object System.IO.BinaryReader($stream)
        try {
            if ($reader.ReadUInt16() -ne 0x5A4D) {
                return 'not-pe'
            }
            $stream.Position = 0x3C
            $peOffset = $reader.ReadInt32()
            if ($peOffset -lt 0 -or $peOffset -gt ($stream.Length - 6)) {
                return 'invalid-pe'
            }
            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550) {
                return 'invalid-pe'
            }
            $machine = $reader.ReadUInt16()
            if ($machine -eq 0x8664) {
                return 'x64'
            }
            if ($machine -eq 0x014C) {
                return 'x86'
            }
            if ($machine -eq 0xAA64) {
                return 'arm64'
            }
            return ('0x{0:X4}' -f $machine)
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Test-PublishedChecksums {
    param([string]$Directory)

    $manifestPath = Join-Path $Directory 'SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        Add-Result 'FAIL' 'Checksum manifest' "Missing: $manifestPath"
        return
    }

    $manifestNames = @{}
    $manifestFailures = 0
    foreach ($line in @(Get-Content -LiteralPath $manifestPath)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        if ($line -notmatch '^([0-9a-fA-F]{64})  ([^\\/:*?"<>|]+)$') {
            $manifestFailures++
            Add-Result 'FAIL' 'Checksum manifest' "Malformed line: $line"
            continue
        }

        $expectedHash = $Matches[1]
        $fileName = $Matches[2]
        $key = $fileName.ToLowerInvariant()
        if ($manifestNames.ContainsKey($key)) {
            $manifestFailures++
            Add-Result 'FAIL' 'Checksum manifest' "Duplicate entry: $fileName"
            continue
        }
        $manifestNames[$key] = $true

        $filePath = Join-Path $Directory $fileName
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            $manifestFailures++
            Add-Result 'FAIL' 'Checksum manifest' "Referenced file is missing: $fileName"
            continue
        }
        $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash
        if ($actualHash -ne $expectedHash) {
            $manifestFailures++
            Add-Result 'FAIL' 'Checksum manifest' "SHA-256 mismatch: $fileName"
        }
    }

    foreach ($file in @(Get-ChildItem -LiteralPath $Directory -File)) {
        if ($file.Name -eq 'SHA256SUMS.txt') {
            continue
        }
        if ($Target -eq 'Installed' -and $file.Name -eq 'Uninstall.ps1') {
            continue
        }
        if ($Target -eq 'Installed' -and
            @('settings.json', 'settings.backup.json') -contains $file.Name) {
            continue
        }
        if (-not $manifestNames.ContainsKey($file.Name.ToLowerInvariant())) {
            $manifestFailures++
            Add-Result 'FAIL' 'Checksum manifest' "Unmanifested file: $($file.Name)"
        }
    }

    foreach ($requiredName in @('CodexBalanceWidget.exe', 'CodexBalanceWidget.Core.dll')) {
        if (-not $manifestNames.ContainsKey($requiredName.ToLowerInvariant())) {
            $manifestFailures++
            Add-Result 'FAIL' 'Checksum manifest' "Required entry is missing: $requiredName"
        }
    }

    if ($manifestFailures -eq 0) {
        Add-Result 'PASS' 'Checksum manifest' "$($manifestNames.Count) artifact checksum(s) verified"
    }
}

function Test-PrivateRuntime {
    param(
        [string]$Path,
        [bool]$IsRequired
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        if ($IsRequired) {
            Add-Result 'FAIL' 'Private Codex runtime' "Missing: $Path"
        }
        else {
            Add-Result 'INFO' 'Private Codex runtime' 'Not staged; trusted-runtime discovery remains pending'
        }
        return
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $subject = ''
    $publisher = ''
    if ($null -ne $signature.SignerCertificate) {
        $subject = [string]$signature.SignerCertificate.Subject
        $publisher = [string]$signature.SignerCertificate.GetNameInfo(
            [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName,
            $false
        )
    }
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        Add-Result 'FAIL' 'Private Codex runtime' "Authenticode status is $($signature.Status)"
        return
    }
    if ($publisher -ne 'OpenAI OpCo, LLC') {
        Add-Result 'FAIL' 'Private Codex runtime' "Unexpected signer: $subject"
        return
    }

    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    Add-Result 'PASS' 'Private Codex runtime' "Valid OpenAI signature; SHA-256=$hash"
}

function Test-ConfigurationFile {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Result 'FAIL' 'Configuration file' "Missing: $Path"
        return
    }
    $file = Get-Item -LiteralPath $Path
    if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        Add-Result 'FAIL' 'Configuration file' 'Reparse points are not accepted'
        return
    }
    if ($file.Length -gt 1MB) {
        Add-Result 'FAIL' 'Configuration file' "File exceeds 1 MiB: $($file.Length) bytes"
        return
    }

    try {
        if ($file.Extension -ieq '.json') {
            Add-Type -AssemblyName System.Web.Extensions
            $serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
            $serializer.MaxJsonLength = 1MB
            $serializer.RecursionLimit = 64
            $serializer.DeserializeObject((Get-Content -LiteralPath $Path -Raw)) | Out-Null
        }
        else {
            $settings = New-Object System.Xml.XmlReaderSettings
            $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
            $settings.XmlResolver = $null
            $reader = [System.Xml.XmlReader]::Create($Path, $settings)
            try {
                while ($reader.Read()) {
                }
            }
            finally {
                $reader.Dispose()
            }
        }

        $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        Add-Result 'PASS' 'Configuration file' "Parsed safely; bytes=$($file.Length); SHA-256=$hash"
    }
    catch {
        Add-Result 'FAIL' 'Configuration file' "Parse failed: $($_.Exception.Message)"
    }
}

function Test-AvatarFile {
    param(
        [string]$Path,
        [switch]$RequireNormalized
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Result 'FAIL' 'Avatar file' "Missing: $Path"
        return
    }
    $file = Get-Item -LiteralPath $Path
    if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        Add-Result 'FAIL' 'Avatar file' 'Reparse points are not accepted'
        return
    }
    if ($file.Length -le 0 -or $file.Length -gt 5MB) {
        Add-Result 'FAIL' 'Avatar file' "Expected 1 byte to 5 MiB; actual=$($file.Length)"
        return
    }
    if ($RequireNormalized -and $file.Extension -ine '.png') {
        Add-Result 'FAIL' 'Avatar file' 'Imported avatar must be PNG'
        return
    }
    if (@('.png', '.jpg', '.jpeg') -notcontains $file.Extension.ToLowerInvariant()) {
        Add-Result 'FAIL' 'Avatar file' 'Only PNG and JPEG files are accepted'
        return
    }

    try {
        Add-Type -AssemblyName WindowsBase
        Add-Type -AssemblyName PresentationCore
        $stream = [System.IO.File]::Open(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read
        )
        try {
            $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
                $stream,
                [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
                [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
            )
        }
        finally {
            $stream.Dispose()
        }

        if ($decoder.Frames.Count -ne 1) {
            Add-Result 'FAIL' 'Avatar file' "Expected one frame; actual=$($decoder.Frames.Count)"
            return
        }
        $frame = $decoder.Frames[0]
        if ($frame.PixelWidth -lt 16 -or $frame.PixelHeight -lt 16 -or
            $frame.PixelWidth -gt 4096 -or $frame.PixelHeight -gt 4096) {
            Add-Result 'FAIL' 'Avatar file' "Dimensions outside 16..4096 px: $($frame.PixelWidth)x$($frame.PixelHeight)"
            return
        }
        if ($RequireNormalized -and
            ($frame.PixelWidth -ne 256 -or $frame.PixelHeight -ne 256)) {
            Add-Result 'FAIL' 'Avatar file' "Imported avatar must be 256x256; actual=$($frame.PixelWidth)x$($frame.PixelHeight)"
            return
        }

        $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        Add-Result 'PASS' 'Avatar file' "Decoded $($frame.PixelWidth)x$($frame.PixelHeight); SHA-256=$hash"
    }
    catch {
        Add-Result 'FAIL' 'Avatar file' "Decode failed: $($_.Exception.Message)"
    }
}

function Get-ExactWidgetProcesses {
    param([string]$ExpectedPath)

    $expectedFullPath = [System.IO.Path]::GetFullPath($ExpectedPath)
    $matches = @()
    foreach ($process in @(Get-Process -Name 'CodexBalanceWidget' -ErrorAction SilentlyContinue)) {
        $processPath = $null
        try {
            $processPath = [System.IO.Path]::GetFullPath($process.MainModule.FileName)
        }
        catch {
        }
        if ($null -ne $processPath -and
            $processPath.Equals($expectedFullPath, [StringComparison]::OrdinalIgnoreCase)) {
            $matches += $process
        }
    }
    return @($matches)
}

function Get-RunningCodexPackageProcesses {
    try {
        return @(
            Get-CimInstance Win32_Process -Filter "Name='ChatGPT.exe'" -ErrorAction Stop |
                Where-Object {
                    [string]$_.ExecutablePath -match
                        '(?i)\\WindowsApps\\OpenAI\.Codex_[^\\]+\\'
                }
        )
    }
    catch {
        $fallbackProcesses = @()
        foreach ($process in @(Get-Process -Name 'ChatGPT' -ErrorAction SilentlyContinue)) {
            try {
                $path = [System.IO.Path]::GetFullPath($process.MainModule.FileName)
                if ($path -match '(?i)\\WindowsApps\\OpenAI\.Codex_[^\\]+\\') {
                    $fallbackProcesses += [PSCustomObject]@{
                        ProcessId = $process.Id
                        ExecutablePath = $path
                    }
                }
            }
            catch {
            }
        }
        if ($fallbackProcesses.Count -gt 0) {
            Add-Result 'INFO' 'Codex process discovery' 'CIM unavailable; exact-path process fallback succeeded'
        }
        else {
            Add-Result 'INFO' 'Codex process discovery' "Unavailable: $($_.Exception.Message)"
        }
        return @($fallbackProcesses)
    }
}

function Initialize-NativeWindowProbe {
    if ('CodexBalanceWidgetAcceptance.NativeMethods' -as [type]) {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace CodexBalanceWidgetAcceptance
{
    public static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsZoomed(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

        public static long GetExtendedStyle(IntPtr hwnd)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hwnd, -20).ToInt64()
                : GetWindowLong32(hwnd, -20);
        }
    }
}
'@
}

function Get-MainWindowState {
    param([System.Diagnostics.Process]$Process)

    Initialize-NativeWindowProbe
    try {
        $Process.Refresh()
        $handle = $Process.MainWindowHandle
        $visible = $handle -ne [IntPtr]::Zero -and
            [CodexBalanceWidgetAcceptance.NativeMethods]::IsWindowVisible($handle)
        return [PSCustomObject]@{
            Handle = $handle
            Visible = $visible
        }
    }
    catch {
        return [PSCustomObject]@{
            Handle = [IntPtr]::Zero
            Visible = $false
        }
    }
}

function Test-WindowContract {
    param(
        [System.Diagnostics.Process]$Process,
        [bool]$ExpectedVisible
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $windowState = $null
    do {
        $windowState = Get-MainWindowState -Process $Process
        if ($windowState.Visible -eq $ExpectedVisible) {
            break
        }
        Start-Sleep -Milliseconds 200
    }
    while ([DateTime]::UtcNow -lt $deadline -and -not $Process.HasExited)

    if ($windowState.Visible -ne $ExpectedVisible) {
        Add-Result 'FAIL' 'Window visibility' "Expected visible=$ExpectedVisible; actual=$($windowState.Visible)"
        return
    }
    Add-Result 'PASS' 'Window visibility' "Expected visible=$ExpectedVisible"

    if (-not $ExpectedVisible) {
        return
    }

    Initialize-NativeWindowProbe
    $extendedStyle =
        [CodexBalanceWidgetAcceptance.NativeMethods]::GetExtendedStyle($windowState.Handle)
    $requiredStyles = @(
        [PSCustomObject]@{ Name = 'tool-window'; Value = 0x00000080L },
        [PSCustomObject]@{ Name = 'no-activate'; Value = 0x08000000L }
    )
    foreach ($requiredStyle in $requiredStyles) {
        if (($extendedStyle -band $requiredStyle.Value) -eq 0) {
            Add-Result 'FAIL' 'Window extended style' "Missing $($requiredStyle.Name)"
        }
        else {
            Add-Result 'PASS' 'Window extended style' $requiredStyle.Name
        }
    }
    if (($extendedStyle -band 0x00000008L) -ne 0) {
        Add-Result 'FAIL' 'Window extended style' 'Unexpected topmost style'
    }
    else {
        Add-Result 'PASS' 'Window extended style' 'normal bottom-layer window'
    }

    Add-Type -AssemblyName System.Windows.Forms
    $rect = New-Object CodexBalanceWidgetAcceptance.NativeMethods+Rect
    if (-not [CodexBalanceWidgetAcceptance.NativeMethods]::GetWindowRect(
        $windowState.Handle,
        [ref]$rect
    )) {
        Add-Result 'FAIL' 'Window geometry' 'GetWindowRect failed'
        return
    }
    $workArea = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $rightGap = $workArea.Right - $rect.Right
    $bottomGap = $workArea.Bottom - $rect.Bottom
    if ($rightGap -lt -8 -or $rightGap -gt 64 -or
        $bottomGap -lt -8 -or $bottomGap -gt 64) {
        Add-Result 'FAIL' 'Window geometry' "Unexpected work-area gaps: right=$rightGap px; bottom=$bottomGap px"
    }
    else {
        Add-Result 'PASS' 'Window geometry' "Anchored lower-right: right=$rightGap px; bottom=$bottomGap px"
    }
}

function Test-RunningWidget {
    param(
        [string]$ExecutablePath,
        [bool]$MustBeRunning
    )

    $processes = @(Get-ExactWidgetProcesses -ExpectedPath $ExecutablePath)
    if ($processes.Count -eq 0) {
        if ($MustBeRunning) {
            Add-Result 'FAIL' 'Widget process' 'Expected the installed supervisor to be running'
        }
        else {
            Add-Result 'INFO' 'Widget process' 'Not running'
        }
        return
    }
    if ($processes.Count -ne 1) {
        Add-Result 'FAIL' 'Widget process' "Expected one exact-path process; actual=$($processes.Count)"
        return
    }

    $process = $processes[0]
    Add-Result 'PASS' 'Widget process' "One exact-path process; pid=$($process.Id)"

    $codexProcesses = @(Get-RunningCodexPackageProcesses)
    $codexRunning = $codexProcesses.Count -gt 0
    $codexMaximized = $false
    if ($codexRunning) {
        Initialize-NativeWindowProbe
        foreach ($codexProcessInfo in $codexProcesses) {
            try {
                $codexProcess = Get-Process -Id $codexProcessInfo.ProcessId -ErrorAction Stop
                $handle = $codexProcess.MainWindowHandle
                if ($handle -ne [IntPtr]::Zero -and
                    [CodexBalanceWidgetAcceptance.NativeMethods]::IsWindowVisible($handle) -and
                    [CodexBalanceWidgetAcceptance.NativeMethods]::IsZoomed($handle)) {
                    $codexMaximized = $true
                    break
                }
            }
            catch {
            }
        }
    }
    $expectedVisible = $codexRunning -and -not $codexMaximized
    Test-WindowContract -Process $process -ExpectedVisible $expectedVisible
}

function Invoke-LifecycleExercise {
    param([string]$ExecutablePath)

    $existing = @(Get-ExactWidgetProcesses -ExpectedPath $ExecutablePath)
    if ($existing.Count -gt 0) {
        Add-Result 'FAIL' 'Lifecycle precondition' 'Stop the installed widget before lifecycle exercise'
        return
    }
    if (@(Get-RunningCodexPackageProcesses).Count -gt 0) {
        Add-Result 'FAIL' 'Lifecycle precondition' 'Exit Codex first; this prevents spawning an app-server child during the controlled test'
        return
    }

    $started = $null
    try {
        $started = Start-Process -FilePath $ExecutablePath `
            -ArgumentList '--acceptance-test' -WindowStyle Hidden -PassThru
        Start-Sleep -Milliseconds 1000
        $started.Refresh()
        if ($started.HasExited) {
            Add-Result 'FAIL' 'Lifecycle startup' "Supervisor exited with code $($started.ExitCode)"
            return
        }
        Add-Result 'PASS' 'Lifecycle startup' "Supervisor remained alive; pid=$($started.Id)"

        Test-WindowContract -Process $started -ExpectedVisible $false

        $second = Start-Process -FilePath $ExecutablePath `
            -ArgumentList '--acceptance-test' -WindowStyle Hidden -PassThru
        if (-not $second.WaitForExit(5000)) {
            Stop-Process -Id $second.Id -Force -ErrorAction SilentlyContinue
            Add-Result 'FAIL' 'Single instance' 'Second instance did not exit within 5 seconds'
        }
        elseif ($second.ExitCode -ne 0) {
            Add-Result 'FAIL' 'Single instance' "Second instance exit code=$($second.ExitCode)"
        }
        else {
            Add-Result 'PASS' 'Single instance' 'Second instance exited cleanly'
        }

        Stop-Process -Id $started.Id -Force -ErrorAction Stop
        $started.WaitForExit($ShutdownTimeoutSeconds * 1000)
        if (-not $started.HasExited) {
            Add-Result 'FAIL' 'Lifecycle shutdown' 'Started process did not exit'
        }
        else {
            Add-Result 'PASS' 'Lifecycle shutdown' 'Controlled test process exited'
        }
    }
    catch {
        Add-Result 'FAIL' 'Lifecycle exercise' $_.Exception.Message
    }
    finally {
        if ($null -ne $started -and -not $started.HasExited) {
            try {
                $actualPath = [System.IO.Path]::GetFullPath($started.MainModule.FileName)
                if ($actualPath.Equals(
                    [System.IO.Path]::GetFullPath($ExecutablePath),
                    [StringComparison]::OrdinalIgnoreCase
                )) {
                    Stop-Process -Id $started.Id -Force -ErrorAction SilentlyContinue
                }
            }
            catch {
            }
        }
    }

    if (@(Get-ExactWidgetProcesses -ExpectedPath $ExecutablePath).Count -eq 0) {
        Add-Result 'PASS' 'Lifecycle cleanup' 'No test widget process remains'
    }
    else {
        Add-Result 'FAIL' 'Lifecycle cleanup' 'A test widget process remains'
    }
}

Write-Host "Codex Balance Widget deployment acceptance: $Target"
Write-Host "Target directory: $targetDirectory"
if ($ExerciseLifecycle) {
    Write-Host 'Lifecycle mode explicitly enabled: it starts and stops only the exact installed widget executable.'
}
else {
    Write-Host 'Read-only mode: no process, registry, installation, or user file is changed.'
}
Write-Host ''

if (-not (Test-Path -LiteralPath $targetDirectory -PathType Container)) {
    Add-Result 'FAIL' 'Target directory' "Missing: $targetDirectory"
}
else {
    Add-Result 'PASS' 'Target directory' $targetDirectory
    $appPath = Join-Path $targetDirectory 'CodexBalanceWidget.exe'
    $corePath = Join-Path $targetDirectory 'CodexBalanceWidget.Core.dll'
    Test-ManagedAssembly -Path $appPath -Label 'Application executable'
    Test-ManagedAssembly -Path $corePath -Label 'Core assembly'

    if (Test-Path -LiteralPath $appPath -PathType Leaf) {
        $machine = Get-PeMachine -Path $appPath
        if ($machine -eq 'x64') {
            Add-Result 'PASS' 'Application architecture' 'x64'
        }
        else {
            Add-Result 'FAIL' 'Application architecture' "Expected x64; actual=$machine"
        }
    }

    Test-PublishedChecksums -Directory $targetDirectory

    if ($Target -eq 'Installed') {
        $runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        $runValueName = 'CodexBalanceWidget'
        $expectedRunValue = '"' + $appPath + '" --startup'
        $actualRunValue = $null
        if (Test-Path -LiteralPath $runKeyPath) {
            $runProperties =
                Get-ItemProperty -Path $runKeyPath -Name $runValueName `
                    -ErrorAction SilentlyContinue
            if ($null -ne $runProperties) {
                $actualRunValue = [string]$runProperties.$runValueName
            }
        }
        if ($actualRunValue -eq $expectedRunValue) {
            Add-Result 'PASS' 'HKCU auto-start' $actualRunValue
        }
        else {
            Add-Result 'FAIL' 'HKCU auto-start' "Expected '$expectedRunValue'; actual '$actualRunValue'"
        }

        $duplicateRunValues = @()
        if (Test-Path -LiteralPath $runKeyPath) {
            $allRunProperties = Get-ItemProperty -Path $runKeyPath
            foreach ($property in $allRunProperties.PSObject.Properties) {
                if ($property.Name -like 'PS*' -or $property.Name -eq $runValueName) {
                    continue
                }
                if ([string]$property.Value -like "*$appPath*") {
                    $duplicateRunValues += $property.Name
                }
            }
        }
        if ($duplicateRunValues.Count -eq 0) {
            Add-Result 'PASS' 'Duplicate auto-start' 'None'
        }
        else {
            Add-Result 'FAIL' 'Duplicate auto-start' ($duplicateRunValues -join ', ')
        }

        $uninstallerPath = Join-Path $targetDirectory 'Uninstall.ps1'
        if (Test-Path -LiteralPath $uninstallerPath -PathType Leaf) {
            Add-Result 'PASS' 'Installed uninstaller' $uninstallerPath
        }
        else {
            Add-Result 'FAIL' 'Installed uninstaller' "Missing: $uninstallerPath"
        }

        Test-PrivateRuntime `
            -Path (Join-Path $targetDirectory 'runtime\codex.exe') `
            -IsRequired ([bool]$RequireRuntime)

        if ($ExerciseLifecycle) {
            Invoke-LifecycleExercise -ExecutablePath $appPath
        }
        else {
            Test-RunningWidget -ExecutablePath $appPath -MustBeRunning ([bool]$RequireRunning)
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($ConfigurationPath)) {
    if (-not [System.IO.Path]::IsPathRooted($ConfigurationPath)) {
        $ConfigurationPath = Join-Path $repoRoot $ConfigurationPath
    }
    Test-ConfigurationFile -Path ([System.IO.Path]::GetFullPath($ConfigurationPath))
}
else {
    $defaultConfiguration = Join-Path $targetDirectory 'CodexBalanceWidget.exe.config'
    if (Test-Path -LiteralPath $defaultConfiguration -PathType Leaf) {
        Test-ConfigurationFile -Path $defaultConfiguration
    }
    else {
        Add-Result 'INFO' 'Configuration file' 'No external configuration file is part of this build'
    }
}

if ($Target -eq 'Installed') {
    $installedSettings = Join-Path $targetDirectory 'settings.json'
    if (Test-Path -LiteralPath $installedSettings -PathType Leaf) {
        Test-ConfigurationFile -Path $installedSettings
    }
    else {
        Add-Result 'INFO' 'Widget settings' 'No settings.json yet; first-run defaults are expected'
    }
}

if (-not [string]::IsNullOrWhiteSpace($AvatarPath)) {
    if (-not [System.IO.Path]::IsPathRooted($AvatarPath)) {
        $AvatarPath = Join-Path $repoRoot $AvatarPath
    }
    Test-AvatarFile -Path ([System.IO.Path]::GetFullPath($AvatarPath))
}
elseif ($Target -eq 'Installed' -and
    (Test-Path -LiteralPath (Join-Path $targetDirectory 'avatars\avatar.png') -PathType Leaf)) {
    Test-AvatarFile `
        -Path (Join-Path $targetDirectory 'avatars\avatar.png') `
        -RequireNormalized
}
else {
    Add-Result 'INFO' 'Avatar file' 'No custom avatar is currently installed'
}

$failureCount = @($results | Where-Object { $_.Status -eq 'FAIL' }).Count
$passCount = @($results | Where-Object { $_.Status -eq 'PASS' }).Count
$infoCount = @($results | Where-Object { $_.Status -eq 'INFO' }).Count
$report = [PSCustomObject]@{
    SchemaVersion = 1
    CollectedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Target = $Target
    TargetDirectory = $targetDirectory
    ExerciseLifecycle = [bool]$ExerciseLifecycle
    Summary = [PSCustomObject]@{
        Passed = $passCount
        Failed = $failureCount
        Informational = $infoCount
    }
    Results = $results.ToArray()
}

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    if (-not [System.IO.Path]::IsPathRooted($ReportPath)) {
        $ReportPath = Join-Path $repoRoot $ReportPath
    }
    $ReportPath = [System.IO.Path]::GetFullPath($ReportPath)
    $reportDirectory = Split-Path -Parent $ReportPath
    if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
        New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    }
    $report | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $ReportPath -Encoding UTF8
    Write-Host "Machine-readable report: $ReportPath"
}

Write-Host ''
Write-Host "Acceptance summary: passed=$passCount; failed=$failureCount; info=$infoCount"
if ($PassThru) {
    $report
}
if ($failureCount -gt 0) {
    throw 'Deployment acceptance failed. Review the FAIL results above.'
}
