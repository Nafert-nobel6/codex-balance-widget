[CmdletBinding()]
param(
    [string]$PublishDirectory,
    [string]$OutputDirectory
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path $repoRoot 'artifacts\publish'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts\qa\visual-harness'
}
$PublishDirectory = [System.IO.Path]::GetFullPath($PublishDirectory)
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkDirectory 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "C# compiler is missing: $compiler"
}

$appAssembly = Join-Path $PublishDirectory 'CodexBalanceWidget.exe'
$coreAssembly = Join-Path $PublishDirectory 'CodexBalanceWidget.Core.dll'
foreach ($path in @($appAssembly, $coreAssembly)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required publish artifact is missing: $path"
    }
}

$references = @(
    (Join-Path $frameworkDirectory 'System.dll'),
    (Join-Path $frameworkDirectory 'System.Core.dll')
)
foreach ($assemblyName in @(
    'System.Xaml',
    'WindowsBase',
    'PresentationCore',
    'PresentationFramework'
)) {
    $assembly = [Reflection.Assembly]::LoadWithPartialName($assemblyName)
    if ($null -eq $assembly -or
        [string]::IsNullOrWhiteSpace($assembly.Location)) {
        throw "WPF reference could not be resolved: $assemblyName"
    }
    $references += $assembly.Location
}
$references += @($coreAssembly, $appAssembly)

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$outputPath = Join-Path $OutputDirectory 'CodexBalanceWidget.VisualHarness.exe'
$arguments = @(
    '/nologo',
    '/utf8output',
    '/langversion:5',
    '/platform:x64',
    '/target:winexe',
    ('/out:' + $outputPath)
)
foreach ($reference in $references) {
    $arguments += '/reference:' + $reference
}
$arguments += Join-Path $repoRoot 'tests\VisualHarness\Program.cs'

& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "Visual harness compilation failed (exit code $LASTEXITCODE)."
}

Copy-Item -LiteralPath $appAssembly `
    -Destination (Join-Path $OutputDirectory 'CodexBalanceWidget.exe') -Force
Copy-Item -LiteralPath $coreAssembly `
    -Destination (Join-Path $OutputDirectory 'CodexBalanceWidget.Core.dll') -Force

[Reflection.AssemblyName]::GetAssemblyName($outputPath) | Out-Null
Write-Host "Visual acceptance harness built: $outputPath"
