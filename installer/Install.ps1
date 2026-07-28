[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string]$PublishDirectory,

    [switch]$DoNotStart
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
    throw 'LOCALAPPDATA is unavailable. A per-user install cannot be created.'
}

$installRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'CodexBalanceWidget'))
$expectedInstallRoot = [System.IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\') + '\CodexBalanceWidget'
if (-not $installRoot.Equals($expectedInstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to install to an unexpected path: $installRoot"
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
        throw "Refusing to modify a reparse-point install root: $Path"
    }
    $reparsePoint = @(
        Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction Stop |
            Where-Object {
                ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
            } |
            Select-Object -First 1
    )
    if ($reparsePoint.Count -gt 0) {
        throw "Refusing to modify an install tree containing a reparse point: $($reparsePoint[0].FullName)"
    }
}

function Test-PublishPayload {
    param([string]$Directory)

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "Publish directory is missing: $Directory. Run scripts\Build.ps1 first."
    }

    foreach ($requiredName in @('CodexBalanceWidget.exe', 'CodexBalanceWidget.Core.dll')) {
        $requiredPath = Join-Path $Directory $requiredName
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Required build artifact is missing: $requiredPath"
        }
        [Reflection.AssemblyName]::GetAssemblyName($requiredPath) | Out-Null
    }

    $checksumPath = Join-Path $Directory 'SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
        throw "Required build checksum manifest is missing: $checksumPath"
    }

    $manifestFiles = @{}
    foreach ($line in (Get-Content -LiteralPath $checksumPath)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        if ($line -notmatch '^([0-9a-fA-F]{64})  ([^\\/:*?"<>|]+)$') {
            throw "Invalid checksum manifest line: $line"
        }
        $expectedHash = $Matches[1]
        $fileName = $Matches[2]
        $manifestKey = $fileName.ToLowerInvariant()
        if ($manifestFiles.ContainsKey($manifestKey)) {
            throw "Checksum manifest contains a duplicate file: $fileName"
        }
        $manifestFiles[$manifestKey] = $true
        $filePath = Join-Path $Directory $fileName
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "Checksum manifest references a missing file: $fileName"
        }
        $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash
        if ($actualHash -ne $expectedHash) {
            throw "Build artifact checksum mismatch: $fileName"
        }
    }

    if ($manifestFiles.Count -eq 0) {
        throw 'Checksum manifest is empty.'
    }
    foreach ($requiredName in @('CodexBalanceWidget.exe', 'CodexBalanceWidget.Core.dll')) {
        if (-not $manifestFiles.ContainsKey($requiredName.ToLowerInvariant())) {
            throw "Checksum manifest is missing required artifact: $requiredName"
        }
    }
    foreach ($publishedFile in @(Get-ChildItem -LiteralPath $Directory -File)) {
        if ($publishedFile.Name -eq 'SHA256SUMS.txt') {
            continue
        }
        if (-not $manifestFiles.ContainsKey($publishedFile.Name.ToLowerInvariant())) {
            throw "Publish directory contains an unmanifested file: $($publishedFile.Name)"
        }
    }
}

function Test-PublishCopy {
    param(
        [string]$SourceDirectory,
        [string]$DestinationDirectory
    )

    foreach ($sourceFile in @(Get-ChildItem -LiteralPath $SourceDirectory -File)) {
        $destinationFile = Join-Path $DestinationDirectory $sourceFile.Name
        if (-not (Test-Path -LiteralPath $destinationFile -PathType Leaf)) {
            throw "Installation verification failed: $($sourceFile.Name) is missing."
        }
        $sourceHash = (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash
        $destinationHash = (Get-FileHash -LiteralPath $destinationFile -Algorithm SHA256).Hash
        if ($sourceHash -ne $destinationHash) {
            throw "Installation verification failed: $($sourceFile.Name) checksum mismatch."
        }
    }
}

function Copy-PreservedUserState {
    param(
        [string]$SourceDirectory,
        [string]$DestinationDirectory
    )

    if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
        return
    }

    $stateFiles = @(
        [PSCustomObject]@{
            RelativePath = 'settings.json'
            MaximumBytes = 1MB
        },
        [PSCustomObject]@{
            RelativePath = 'settings.backup.json'
            MaximumBytes = 1MB
        },
        [PSCustomObject]@{
            RelativePath = 'avatars\avatar.png'
            MaximumBytes = 20MB
        },
        [PSCustomObject]@{
            RelativePath = 'avatars\avatar.backup.png'
            MaximumBytes = 20MB
        }
    )

    foreach ($stateFile in $stateFiles) {
        $sourcePath = Join-Path $SourceDirectory $stateFile.RelativePath
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            continue
        }

        $sourceInfo = Get-Item -LiteralPath $sourcePath -Force
        if (($sourceInfo.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to preserve a reparse-point user state file: $sourcePath"
        }
        if ($sourceInfo.Length -le 0 -or
            $sourceInfo.Length -gt [long]$stateFile.MaximumBytes) {
            throw "User state file has an unsafe size and cannot be preserved: $sourcePath"
        }

        $destinationPath = Join-Path $DestinationDirectory $stateFile.RelativePath
        $destinationParent = Split-Path -Parent $destinationPath
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
        Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force

        $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
        $destinationHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash
        if ($sourceHash -ne $destinationHash) {
            throw "User state preservation checksum mismatch: $($stateFile.RelativePath)"
        }
    }
}

function Get-CandidatePackageRoots {
    $roots = New-Object System.Collections.Generic.List[string]

    try {
        $runningCodexProcesses = @(
            Get-CimInstance Win32_Process -Filter "Name='ChatGPT.exe'" -ErrorAction Stop
        )
        foreach ($process in $runningCodexProcesses) {
            $executablePath = [string]$process.ExecutablePath
            if ($executablePath -match '(?i)^(.*\\WindowsApps\\OpenAI\.Codex_[^\\]+)\\') {
                $roots.Add([System.IO.Path]::GetFullPath($Matches[1]))
            }
        }
    }
    catch {
        Write-Verbose "Could not inspect running ChatGPT.exe paths: $($_.Exception.Message)"
    }

    try {
        $packages = @(
            Get-AppxPackage -ErrorAction Stop |
                Where-Object {
                    $_.PackageFamilyName -eq 'OpenAI.Codex_2p2nqsd0c76g0' -or
                    $_.Name -like 'OpenAI.Codex*'
                } |
                Sort-Object -Property @{ Expression = { [version]$_.Version }; Descending = $true }
        )
        foreach ($package in $packages) {
            if (-not [string]::IsNullOrWhiteSpace([string]$package.InstallLocation)) {
                $roots.Add([System.IO.Path]::GetFullPath([string]$package.InstallLocation))
            }
        }
    }
    catch {
        Write-Verbose "Could not query the Codex MSIX package: $($_.Exception.Message)"
    }

    return @($roots | Select-Object -Unique)
}

function Test-TrustedCodexRuntime {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $signerSubject = ''
    $signerPublisher = ''
    if ($null -ne $signature.SignerCertificate) {
        $signerSubject = [string]$signature.SignerCertificate.Subject
        $signerPublisher = [string]$signature.SignerCertificate.GetNameInfo(
            [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName,
            $false
        )
    }
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        return $null
    }
    if ($signerPublisher -ne 'OpenAI OpCo, LLC') {
        return $null
    }

    return [PSCustomObject]@{
        Path = [System.IO.Path]::GetFullPath($Path)
        SignerSubject = $signerSubject
        Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    }
}

function Find-TrustedCodexRuntime {
    foreach ($packageRoot in (Get-CandidatePackageRoots)) {
        if ($packageRoot -notmatch '(?i)\\WindowsApps\\OpenAI\.Codex_[^\\]+$') {
            continue
        }

        $candidatePaths = @(
            (Join-Path $packageRoot 'resources\codex.exe'),
            (Join-Path $packageRoot 'app\resources\codex.exe')
        )
        foreach ($candidatePath in $candidatePaths) {
            $fullCandidatePath = [System.IO.Path]::GetFullPath($candidatePath)
            $rootPrefix = $packageRoot.TrimEnd('\') + '\'
            if (-not $fullCandidatePath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $trustedFile = Test-TrustedCodexRuntime -Path $fullCandidatePath
            if ($null -eq $trustedFile) {
                if (Test-Path -LiteralPath $fullCandidatePath -PathType Leaf) {
                    $rejectedSignature = Get-AuthenticodeSignature -LiteralPath $fullCandidatePath
                    $rejectedSubject = ''
                    if ($null -ne $rejectedSignature.SignerCertificate) {
                        $rejectedSubject = [string]$rejectedSignature.SignerCertificate.Subject
                    }
                    Write-Warning "Rejected Codex runtime: status=$($rejectedSignature.Status); signer=$rejectedSubject; path=$fullCandidatePath"
                }
                continue
            }

            return [PSCustomObject]@{
                Path = $trustedFile.Path
                PackageRoot = $packageRoot
                SignerSubject = $trustedFile.SignerSubject
                Sha256 = $trustedFile.Sha256
            }
        }
    }

    return $null
}

function Stop-InstalledWidget {
    param([string]$ExpectedExecutablePath)

    $expected = [System.IO.Path]::GetFullPath($ExpectedExecutablePath)
    $stoppedAny = $false
    foreach ($process in @(Get-Process -Name 'CodexBalanceWidget' -ErrorAction SilentlyContinue)) {
        $actualPath = $null
        try {
            $actualPath = [System.IO.Path]::GetFullPath($process.MainModule.FileName)
        }
        catch {
            Write-Warning "Could not verify path for widget process $($process.Id); it was not stopped."
        }

        if ($null -ne $actualPath -and
            $actualPath.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
            [void]$process.WaitForExit(5000)
            $stoppedAny = $true
        }
    }

    return $stoppedAny
}

$requestedWhatIfPreference = $WhatIfPreference
try {
    $WhatIfPreference = $false
Test-PublishPayload -Directory $PublishDirectory
$trustedRuntime = Find-TrustedCodexRuntime
$runtimeToStage = $trustedRuntime
if ($null -eq $runtimeToStage) {
    foreach ($existingRuntimeCandidate in @(
        (Join-Path $installRoot 'runtime\codex.exe'),
        (Join-Path $installRoot 'runtime\codex.last-good.exe')
    )) {
        $existingRuntimeTrust = Test-TrustedCodexRuntime -Path $existingRuntimeCandidate
        if ($null -ne $existingRuntimeTrust) {
            $runtimeToStage = $existingRuntimeTrust
            break
        }
    }
}

Write-Host "Install target: $installRoot"
if ($null -eq $runtimeToStage) {
    Write-Warning 'No trusted Codex runtime is currently discoverable. App files can be installed; runtime staging will remain pending until the supervisor finds a trusted Codex package.'
}
elseif ($null -eq $trustedRuntime) {
    Write-Host "Trusted existing runtime retained: $($runtimeToStage.Path)"
    Write-Host "Signer: $($runtimeToStage.SignerSubject)"
    Write-Host "SHA-256: $($runtimeToStage.Sha256)"
}
else {
    Write-Host "Trusted Codex runtime: $($runtimeToStage.Path)"
    Write-Host "Signer: $($runtimeToStage.SignerSubject)"
    Write-Host "SHA-256: $($runtimeToStage.Sha256)"
}

}
finally {
    $WhatIfPreference = $requestedWhatIfPreference
}
if (-not $PSCmdlet.ShouldProcess($installRoot, 'Install Codex Balance Widget, set its HKCU Run value, and start its supervisor')) {
    Write-Host 'No changes made.'
    return
}

$priorRunExists = $false
$priorRunValue = $null
if (Test-Path -LiteralPath $runKeyPath) {
    $priorRunProperties = Get-ItemProperty -Path $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue
    if ($null -ne $priorRunProperties) {
        $priorRunExists = $true
        $priorRunValue = [string]$priorRunProperties.$runValueName
    }
}

Assert-NoReparsePoints -Path $installRoot

$stageRoot = $installRoot + '.installing.' + [Guid]::NewGuid().ToString('N')
$backupRoot = $installRoot + '.previous.' + [Guid]::NewGuid().ToString('N')
$oldInstallMoved = $false
$newInstallMoved = $false
$transactionCommitted = $false
$priorWidgetWasRunning = $false

try {
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null

    foreach ($artifact in @(Get-ChildItem -LiteralPath $PublishDirectory -File)) {
        Copy-Item -LiteralPath $artifact.FullName -Destination (Join-Path $stageRoot $artifact.Name) -Force
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') `
        -Destination (Join-Path $stageRoot 'Uninstall.ps1') -Force

    if ($null -ne $runtimeToStage) {
        $runtimeDirectory = Join-Path $stageRoot 'runtime'
        New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
        $stagedRuntime = Join-Path $runtimeDirectory 'codex.exe'
        Copy-Item -LiteralPath $runtimeToStage.Path -Destination $stagedRuntime -Force
        $stagedHash = (Get-FileHash -LiteralPath $stagedRuntime -Algorithm SHA256).Hash
        if ($stagedHash -ne $runtimeToStage.Sha256) {
            throw 'The staged Codex runtime SHA-256 does not match its trusted source.'
        }

        $stagedTrust = Test-TrustedCodexRuntime -Path $stagedRuntime
        if ($null -eq $stagedTrust) {
            throw 'The staged Codex runtime failed post-copy Authenticode verification.'
        }

        $existingRuntime = Join-Path $installRoot 'runtime\codex.exe'
        $existingTrust = Test-TrustedCodexRuntime -Path $existingRuntime
        if ($null -ne $existingTrust -and $existingTrust.Sha256 -ne $stagedHash) {
            $previousRuntime = Join-Path $runtimeDirectory 'codex.last-good.exe'
            Copy-Item -LiteralPath $existingRuntime -Destination $previousRuntime -Force
            $previousHash = (Get-FileHash -LiteralPath $previousRuntime -Algorithm SHA256).Hash
            if ($previousHash -ne $existingTrust.Sha256 -or
                $null -eq (Test-TrustedCodexRuntime -Path $previousRuntime)) {
                throw 'The last-known-good Codex runtime failed preservation verification.'
            }
        }
    }

    $priorWidgetWasRunning = [bool](
        Stop-InstalledWidget -ExpectedExecutablePath $installedExecutable
    )
    Copy-PreservedUserState `
        -SourceDirectory $installRoot `
        -DestinationDirectory $stageRoot

    if (Test-Path -LiteralPath $installRoot -PathType Container) {
        Move-Item -LiteralPath $installRoot -Destination $backupRoot
        $oldInstallMoved = $true
    }
    Move-Item -LiteralPath $stageRoot -Destination $installRoot
    $newInstallMoved = $true

    New-Item -Path $runKeyPath -Force | Out-Null
    $runCommand = '"' + $installedExecutable + '" --startup'
    New-ItemProperty -Path $runKeyPath -Name $runValueName -PropertyType String `
        -Value $runCommand -Force | Out-Null

    Test-PublishCopy -SourceDirectory $PublishDirectory -DestinationDirectory $installRoot
    if (-not (Test-Path -LiteralPath (Join-Path $installRoot 'Uninstall.ps1') -PathType Leaf)) {
        throw 'Installation verification failed: Uninstall.ps1 is missing.'
    }
    $installedRunValue = (Get-ItemProperty -Path $runKeyPath -Name $runValueName -ErrorAction Stop).$runValueName
    if ($installedRunValue -ne $runCommand) {
        throw 'Installation verification failed: the per-user Run value is incorrect.'
    }

    if ($null -ne $runtimeToStage) {
        $installedRuntime = Join-Path $installRoot 'runtime\codex.exe'
        $installedRuntimeTrust = Test-TrustedCodexRuntime -Path $installedRuntime
        if ($null -eq $installedRuntimeTrust -or
            $installedRuntimeTrust.Sha256 -ne $runtimeToStage.Sha256) {
            throw 'Installation verification failed: the private Codex runtime is not trusted or its SHA-256 changed.'
        }
    }
    elseif (Test-Path -LiteralPath (Join-Path $installRoot 'runtime\codex.exe')) {
        throw 'Installation verification failed: a runtime was staged without a trusted source.'
    }

    if (-not $DoNotStart) {
        $startedProcess = Start-Process -FilePath $installedExecutable `
            -ArgumentList '--startup' -WindowStyle Hidden -PassThru
        if ($startedProcess.WaitForExit(1000)) {
            throw "The installed supervisor exited during startup with code $($startedProcess.ExitCode)."
        }
    }

    $transactionCommitted = $true
}
catch {
    $installError = $_

    if (Test-Path -LiteralPath $runKeyPath) {
        if ($priorRunExists) {
            New-ItemProperty -Path $runKeyPath -Name $runValueName -PropertyType String `
                -Value $priorRunValue -Force -ErrorAction SilentlyContinue | Out-Null
        }
        else {
            Remove-ItemProperty -Path $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue
        }
    }

    [void](Stop-InstalledWidget -ExpectedExecutablePath $installedExecutable)

    if ($newInstallMoved -and (Test-Path -LiteralPath $installRoot -PathType Container)) {
        Remove-Item -LiteralPath $installRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($oldInstallMoved -and (Test-Path -LiteralPath $backupRoot -PathType Container)) {
        Move-Item -LiteralPath $backupRoot -Destination $installRoot -ErrorAction SilentlyContinue
    }

    if ($priorWidgetWasRunning -and -not $DoNotStart -and
        (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) {
        Start-Process -FilePath $installedExecutable -ArgumentList '--startup' `
            -WindowStyle Hidden -ErrorAction SilentlyContinue
    }

    throw $installError
}
finally {
    if (Test-Path -LiteralPath $stageRoot -PathType Container) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not $transactionCommitted) {
    throw 'Installation transaction did not commit.'
}

if ($oldInstallMoved -and (Test-Path -LiteralPath $backupRoot -PathType Container)) {
    try {
        Remove-Item -LiteralPath $backupRoot -Recurse -Force -ErrorAction Stop
    }
    catch {
        Write-Warning "Installation succeeded, but the previous-version backup could not be removed: $backupRoot"
    }
}

Write-Host ''
Write-Host 'Installation verified.'
Write-Host "Application: $installedExecutable"
Write-Host "Auto-start: HKCU Run\$runValueName"
if (Test-Path -LiteralPath (Join-Path $installRoot 'runtime\codex.exe') -PathType Leaf) {
    Write-Host 'Runtime: staged and verified'
}
else {
    Write-Host 'Runtime: pending trusted Codex package discovery'
}
