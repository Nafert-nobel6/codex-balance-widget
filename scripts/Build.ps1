[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputDirectory,

    [switch]$SkipTests,

    [switch]$RunTests,

    [switch]$Clean
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts\publish'
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot $OutputDirectory
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

function Resolve-FrameworkDirectory {
    $frameworkCandidates = @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319')
    )

    foreach ($candidate in $frameworkCandidates) {
        if ((Test-Path -LiteralPath (Join-Path $candidate 'csc.exe') -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $candidate 'MSBuild.exe') -PathType Leaf)) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    throw '.NET Framework 4.x csc.exe/MSBuild.exe was not found. Enable the Windows .NET Framework 4.8 feature and retry.'
}

function Get-CSharpSources {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory,

        [Parameter(Mandatory = $true)]
        [string]$ComponentName
    )

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "$ComponentName source directory is missing: $Directory"
    }

    $sourceFiles = @(
        Get-ChildItem -LiteralPath $Directory -Filter '*.cs' -File -Recurse |
            Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' } |
            Sort-Object -Property FullName |
            ForEach-Object { $_.FullName }
    )
    if ($sourceFiles.Count -eq 0) {
        throw "$ComponentName has no C# source files in $Directory"
    }

    return $sourceFiles
}

function Resolve-References {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FrameworkDirectory,

        [Parameter(Mandatory = $true)]
        [string[]]$AssemblyNames
    )

    $references = @()
    foreach ($assemblyName in $AssemblyNames) {
        $referencePath = Join-Path $FrameworkDirectory $assemblyName
        if (-not (Test-Path -LiteralPath $referencePath -PathType Leaf)) {
            try {
                $loadedAssembly = [Reflection.Assembly]::LoadWithPartialName(
                    [System.IO.Path]::GetFileNameWithoutExtension($assemblyName)
                )
                if ($null -eq $loadedAssembly) {
                    throw "Assembly load returned no result."
                }
                $referencePath = $loadedAssembly.Location
            }
            catch {
                throw "Required .NET Framework reference is missing: $referencePath"
            }
        }
        if ([string]::IsNullOrWhiteSpace($referencePath) -or
            -not (Test-Path -LiteralPath $referencePath -PathType Leaf)) {
            throw "Required .NET Framework reference could not be resolved: $assemblyName"
        }
        $references += $referencePath
    }
    return $references
}

function ConvertTo-XmlValue {
    param([AllowEmptyString()][string]$Value)
    return [System.Security.SecurityElement]::Escape($Value)
}

function Invoke-CSharpCompiler {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CompilerPath,

        [Parameter(Mandatory = $true)]
        [string]$Target,

        [Parameter(Mandatory = $true)]
        [string]$OutputPath,

        [Parameter(Mandatory = $true)]
        [string[]]$Sources,

        [Parameter(Mandatory = $true)]
        [string[]]$References
    )

    $arguments = @(
        '/nologo',
        '/utf8output',
        '/langversion:5',
        '/checked+',
        '/warn:4',
        '/platform:x64',
        "/target:$Target",
        "/out:$OutputPath"
    )

    if ($Configuration -eq 'Release') {
        $arguments += '/optimize+'
        $arguments += '/debug:pdbonly'
    }
    else {
        $arguments += '/optimize-'
        $arguments += '/debug:full'
        $arguments += '/define:DEBUG;TRACE'
    }

    foreach ($reference in $References) {
        $arguments += "/reference:$reference"
    }
    $arguments += $Sources

    Write-Host "Compiling $(Split-Path -Leaf $OutputPath)"
    & $CompilerPath @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "C# compilation failed for $OutputPath (exit code $LASTEXITCODE)."
    }
    if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
        throw "Compiler reported success but did not create $OutputPath"
    }
}

function Invoke-WpfCompiler {
    param(
        [Parameter(Mandatory = $true)]
        [string]$MSBuildPath,

        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,

        [Parameter(Mandatory = $true)]
        [string[]]$Sources,

        [Parameter(Mandatory = $true)]
        [string[]]$References,

        [Parameter(Mandatory = $true)]
        [string]$CoreAssemblyPath,

        [Parameter(Mandatory = $true)]
        [string]$StageDirectory
    )

    $appXaml = Join-Path $SourceDirectory 'App.xaml'
    if (-not (Test-Path -LiteralPath $appXaml -PathType Leaf)) {
        throw "WPF application definition is missing: $appXaml"
    }

    $pageFiles = @(
        Get-ChildItem -LiteralPath $SourceDirectory -Filter '*.xaml' -File -Recurse |
            Where-Object {
                $_.FullName -ne $appXaml -and
                $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]'
            } |
            Sort-Object -Property FullName
    )
    if ($pageFiles.Count -eq 0) {
        throw "WPF app has no Page XAML files in $SourceDirectory"
    }

    $compileItems = foreach ($source in $Sources) {
        $relativePath = $source.Substring($SourceDirectory.TrimEnd('\').Length).TrimStart('\')
        '    <Compile Include="' + (ConvertTo-XmlValue $source) + '">' +
            '<Link>' + (ConvertTo-XmlValue $relativePath) + '</Link></Compile>'
    }
    $pageItems = foreach ($pageFile in $pageFiles) {
        $relativePath = $pageFile.FullName.Substring($SourceDirectory.TrimEnd('\').Length).TrimStart('\')
        '    <Page Include="' + (ConvertTo-XmlValue $pageFile.FullName) + '">' +
            '<Link>' + (ConvertTo-XmlValue $relativePath) + '</Link>' +
            '<Generator>MSBuild:Compile</Generator><SubType>Designer</SubType></Page>'
    }
    $referenceItems = foreach ($reference in $References) {
        $referenceName = [System.IO.Path]::GetFileNameWithoutExtension($reference)
        '    <Reference Include="' + (ConvertTo-XmlValue $referenceName) + '">' +
            '<Private>False</Private></Reference>'
    }
    $referenceItems +=
        '    <Reference Include="CodexBalanceWidget.Core"><HintPath>' +
        (ConvertTo-XmlValue $CoreAssemblyPath) +
        '</HintPath><Private>True</Private></Reference>'

    $manifestPath = Join-Path $SourceDirectory 'app.manifest'
    $manifestProperty = ''
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        $manifestProperty =
            '    <ApplicationManifest>' +
            (ConvertTo-XmlValue $manifestPath) +
            '</ApplicationManifest>'
    }

    $outputPath = Join-Path $StageDirectory 'wpf-output'
    $intermediatePath = Join-Path $StageDirectory 'wpf-obj'
    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
    New-Item -ItemType Directory -Path $intermediatePath -Force | Out-Null

    $debugSymbols = if ($Configuration -eq 'Debug') { 'true' } else { 'false' }
    $debugType = if ($Configuration -eq 'Debug') { 'full' } else { 'pdbonly' }
    $optimize = if ($Configuration -eq 'Debug') { 'false' } else { 'true' }
    $constants = if ($Configuration -eq 'Debug') { 'DEBUG;TRACE' } else { 'TRACE' }
    $temporaryProject = Join-Path $StageDirectory 'CodexBalanceWidget.WpfBuild.csproj'

    $projectLines = @(
        '<?xml version="1.0" encoding="utf-8"?>',
        '<Project ToolsVersion="4.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">',
        '  <PropertyGroup>',
        ('    <Configuration>' + $Configuration + '</Configuration>'),
        '    <Platform>AnyCPU</Platform>',
        '    <PlatformTarget>x64</PlatformTarget>',
        '    <Prefer32Bit>false</Prefer32Bit>',
        '    <OutputType>WinExe</OutputType>',
        '    <RootNamespace>CodexBalanceWidget.App</RootNamespace>',
        '    <AssemblyName>CodexBalanceWidget</AssemblyName>',
        '    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>',
        '    <TargetFrameworkProfile></TargetFrameworkProfile>',
        '    <LangVersion>5</LangVersion>',
        '    <FileAlignment>512</FileAlignment>',
        '    <WarningLevel>4</WarningLevel>',
        ('    <DebugSymbols>' + $debugSymbols + '</DebugSymbols>'),
        ('    <DebugType>' + $debugType + '</DebugType>'),
        ('    <Optimize>' + $optimize + '</Optimize>'),
        ('    <DefineConstants>' + $constants + '</DefineConstants>'),
        ('    <OutputPath>' + (ConvertTo-XmlValue ($outputPath.TrimEnd('\') + '\')) + '</OutputPath>'),
        ('    <IntermediateOutputPath>' + (ConvertTo-XmlValue ($intermediatePath.TrimEnd('\') + '\')) + '</IntermediateOutputPath>'),
        $manifestProperty,
        '  </PropertyGroup>',
        '  <ItemGroup>',
        ($referenceItems -join [Environment]::NewLine),
        '  </ItemGroup>',
        '  <ItemGroup>',
        ('    <ApplicationDefinition Include="' + (ConvertTo-XmlValue $appXaml) + '"><Link>App.xaml</Link><Generator>MSBuild:Compile</Generator><SubType>Designer</SubType></ApplicationDefinition>'),
        ($pageItems -join [Environment]::NewLine),
        ($compileItems -join [Environment]::NewLine),
        '  </ItemGroup>',
        '  <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />',
        '</Project>'
    )
    Set-Content -LiteralPath $temporaryProject -Value $projectLines -Encoding UTF8

    Write-Host 'Compiling CodexBalanceWidget.exe (WPF markup pass)'
    $msbuildOutput = @(
        & $MSBuildPath $temporaryProject '/nologo' '/verbosity:minimal' '/target:Rebuild' `
            ("/property:Configuration=$Configuration") '/property:Platform=AnyCPU' `
            '/property:TargetPlatformSdkPath=C:\Windows\System32'
    )
    $msbuildExitCode = $LASTEXITCODE
    foreach ($msbuildLine in $msbuildOutput) {
        Write-Host $msbuildLine
    }
    if ($msbuildExitCode -ne 0) {
        throw "WPF MSBuild compilation failed (exit code $msbuildExitCode)."
    }

    $builtExecutable = Join-Path $outputPath 'CodexBalanceWidget.exe'
    if (-not (Test-Path -LiteralPath $builtExecutable -PathType Leaf)) {
        throw "WPF build reported success but did not create $builtExecutable"
    }

    $stagedExecutable = Join-Path $StageDirectory 'CodexBalanceWidget.exe'
    Copy-Item -LiteralPath $builtExecutable -Destination $stagedExecutable -Force
    $builtPdb = Join-Path $outputPath 'CodexBalanceWidget.pdb'
    if (Test-Path -LiteralPath $builtPdb -PathType Leaf) {
        Copy-Item -LiteralPath $builtPdb -Destination (Join-Path $StageDirectory 'CodexBalanceWidget.pdb') -Force
    }
    return $stagedExecutable
}

$frameworkDirectory = Resolve-FrameworkDirectory
$compiler = Join-Path $frameworkDirectory 'csc.exe'
$msbuild = Join-Path $frameworkDirectory 'MSBuild.exe'
$compilerReferenceNames = @(
    'System.dll',
    'System.Core.dll',
    'System.Configuration.dll',
    'System.Net.Http.dll',
    'System.Runtime.Serialization.dll',
    'System.Web.Extensions.dll'
)
$wpfReferenceNames = @(
    'System.dll',
    'System.Core.dll',
    'System.Xaml.dll',
    'System.Web.Extensions.dll',
    'WindowsBase.dll',
    'PresentationCore.dll',
    'PresentationFramework.dll'
)
$compilerReferences = Resolve-References -FrameworkDirectory $frameworkDirectory -AssemblyNames $compilerReferenceNames
$wpfCompilerReferences = Resolve-References -FrameworkDirectory $frameworkDirectory -AssemblyNames $wpfReferenceNames

$coreSourceDirectory = Join-Path $repoRoot 'src\CodexBalanceWidget.Core'
$appSourceDirectory = Join-Path $repoRoot 'src\CodexBalanceWidget.App'
$testSourceDirectory = Join-Path $repoRoot 'tests\Core.Tests'
$infrastructureTestSourceDirectory = Join-Path $repoRoot 'tests\Infrastructure.Tests'
$coreSources = Get-CSharpSources -Directory $coreSourceDirectory -ComponentName 'Core'
$appSources = Get-CSharpSources -Directory $appSourceDirectory -ComponentName 'App'

$stageDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('CodexBalanceWidget-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stageDirectory -Force | Out-Null

try {
    $coreOutput = Join-Path $stageDirectory 'CodexBalanceWidget.Core.dll'
    Invoke-CSharpCompiler -CompilerPath $compiler -Target 'library' -OutputPath $coreOutput `
        -Sources $coreSources -References $compilerReferences

    $appOutput = Invoke-WpfCompiler -MSBuildPath $msbuild -SourceDirectory $appSourceDirectory `
        -Sources $appSources -References $wpfReferenceNames -CoreAssemblyPath $coreOutput `
        -StageDirectory $stageDirectory
    $appReferences = @($compilerReferences + $coreOutput)

    $testOutput = $null
    $infrastructureTestOutput = $null
    if (-not $SkipTests) {
        if (-not (Test-Path -LiteralPath $testSourceDirectory -PathType Container)) {
            throw "Console test directory is missing: $testSourceDirectory. Use -SkipTests only for an intentional app-only build."
        }
        $testSources = Get-CSharpSources -Directory $testSourceDirectory -ComponentName 'Core console tests'
        $testOutput = Join-Path $stageDirectory 'CodexBalanceWidget.Core.Tests.exe'
        Invoke-CSharpCompiler -CompilerPath $compiler -Target 'exe' -OutputPath $testOutput `
            -Sources $testSources -References $appReferences

        if (-not (Test-Path -LiteralPath $infrastructureTestSourceDirectory -PathType Container)) {
            throw "Infrastructure test directory is missing: $infrastructureTestSourceDirectory."
        }
        $infrastructureTestSources = Get-CSharpSources -Directory $infrastructureTestSourceDirectory -ComponentName 'Infrastructure console tests'
        $infrastructureTestOutput = Join-Path $stageDirectory 'CodexBalanceWidget.Infrastructure.Tests.exe'
        $infrastructureTestReferences = @(
            $compilerReferences +
            $wpfCompilerReferences +
            $coreOutput +
            $appOutput |
                Select-Object -Unique
        )
        Invoke-CSharpCompiler -CompilerPath $compiler -Target 'exe' -OutputPath $infrastructureTestOutput `
            -Sources $infrastructureTestSources -References $infrastructureTestReferences
    }

    $appConfig = Join-Path $appSourceDirectory 'App.config'
    if (Test-Path -LiteralPath $appConfig -PathType Leaf) {
        Copy-Item -LiteralPath $appConfig -Destination (Join-Path $stageDirectory 'CodexBalanceWidget.exe.config') -Force
    }

    [Reflection.AssemblyName]::GetAssemblyName($coreOutput) | Out-Null
    [Reflection.AssemblyName]::GetAssemblyName($appOutput) | Out-Null
    if ($null -ne $testOutput) {
        [Reflection.AssemblyName]::GetAssemblyName($testOutput) | Out-Null
    }
    if ($null -ne $infrastructureTestOutput) {
        [Reflection.AssemblyName]::GetAssemblyName($infrastructureTestOutput) | Out-Null
    }

    if ($RunTests) {
        if ($null -eq $testOutput -or $null -eq $infrastructureTestOutput) {
            throw '-RunTests cannot be combined with -SkipTests.'
        }
        Write-Host 'Running core console tests'
        & $testOutput
        if ($LASTEXITCODE -ne 0) {
            throw "Core console tests failed (exit code $LASTEXITCODE)."
        }
        Write-Host 'Running infrastructure console tests'
        & $infrastructureTestOutput
        if ($LASTEXITCODE -ne 0) {
            throw "Infrastructure console tests failed (exit code $LASTEXITCODE)."
        }
    }

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $allowedPublishedNames = @(
        'CodexBalanceWidget.Core.dll',
        'CodexBalanceWidget.Core.pdb',
        'CodexBalanceWidget.exe',
        'CodexBalanceWidget.pdb',
        'CodexBalanceWidget.exe.config',
        'CodexBalanceWidget.Core.Tests.exe',
        'CodexBalanceWidget.Core.Tests.pdb',
        'CodexBalanceWidget.Infrastructure.Tests.exe',
        'CodexBalanceWidget.Infrastructure.Tests.pdb'
    )
    if ($Clean) {
        $knownOutputs = @($allowedPublishedNames + @(
            'SHA256SUMS.txt',
            'CodexBalanceWidget.WpfBuild.csproj'
        ))
        foreach ($knownOutput in $knownOutputs) {
            $knownPath = Join-Path $OutputDirectory $knownOutput
            if (Test-Path -LiteralPath $knownPath) {
                Remove-Item -LiteralPath $knownPath -Force
            }
        }
    }

    Get-ChildItem -LiteralPath $stageDirectory -File |
        Where-Object { $allowedPublishedNames -contains $_.Name } |
        ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $OutputDirectory $_.Name) -Force
    }

    $unexpectedPublishedFiles = @(
        Get-ChildItem -LiteralPath $OutputDirectory -File |
            Where-Object { $_.Name -ne 'SHA256SUMS.txt' -and $allowedPublishedNames -notcontains $_.Name }
    )
    if ($unexpectedPublishedFiles.Count -gt 0) {
        throw "Publish directory contains unexpected files; use a clean dedicated output directory: $($unexpectedPublishedFiles[0].Name)"
    }

    $publishedFiles = @(
        Get-ChildItem -LiteralPath $OutputDirectory -File |
            Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
            Sort-Object -Property Name
    )
    $hashLines = foreach ($publishedFile in $publishedFiles) {
        $hash = (Get-FileHash -LiteralPath $publishedFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($publishedFile.Name)"
    }
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Value $hashLines -Encoding ASCII

    Write-Host ''
    Write-Host "Build completed: $OutputDirectory"
    Get-ChildItem -LiteralPath $OutputDirectory -File |
        Sort-Object -Property Name |
        Select-Object Name, Length, LastWriteTime
}
finally {
    if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $stageDirectory -PathType Container)) {
        Remove-Item -LiteralPath $stageDirectory -Recurse -Force
    }
    elseif (Test-Path -LiteralPath $stageDirectory -PathType Container) {
        Write-Host "Failed-build diagnostics retained at: $stageDirectory"
    }
}
