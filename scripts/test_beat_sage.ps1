#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$GameRoot = '',
    [string]$BuildDirectory = '',
    [string]$DotnetPath = 'C:/Program Files/dotnet/dotnet.exe'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($GameRoot)) { $GameRoot = Split-Path -Parent $PSScriptRoot }
$GameRoot = [IO.Path]::GetFullPath($GameRoot)
if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    $BuildDirectory = Join-Path $GameRoot '.tmp/beatsage-mod/tests'
} else {
    $BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
}
$project = Join-Path $GameRoot 'scripts/beat_sage/Tests/BeatSage.Tests.csproj'
$coreProject = Join-Path $GameRoot 'scripts/beat_sage/Tests/BeatSage.Core.Tests.csproj'
if (!(Test-Path -LiteralPath $project -PathType Leaf)) { throw "Missing Beat Sage test project: $project" }
if (!(Test-Path -LiteralPath $coreProject -PathType Leaf)) { throw "Missing Beat Sage core test project: $coreProject" }
if (!(Test-Path -LiteralPath $DotnetPath -PathType Leaf)) { throw "Missing dotnet executable: $DotnetPath" }

$output = Join-Path $BuildDirectory 'bin'
$obj = Join-Path $BuildDirectory 'obj'
[IO.Directory]::CreateDirectory($output) | Out-Null
[IO.Directory]::CreateDirectory($obj) | Out-Null
$contractBuild = Join-Path $BuildDirectory 'contract-build'
$generated = Join-Path $contractBuild 'generated'
$generatorProject = Join-Path $GameRoot 'scripts/beat_sage/ContractGenerator/ContractGenerator.csproj'
$generatorOutput = Join-Path $contractBuild 'generator/bin'
$generatorObj = Join-Path $contractBuild 'generator/obj'
$frameworkReferenceDirectory = Join-Path $env:USERPROFILE '.nuget/packages/microsoft.netframework.referenceassemblies.net472/1.0.3/build/.NETFramework/v4.7.2'
[IO.Directory]::CreateDirectory($generatorOutput) | Out-Null
[IO.Directory]::CreateDirectory($generatorObj) | Out-Null

$generatorBuild = @(
    'build', $generatorProject, '--configuration', 'Release',
    ('-p:GameRoot=' + $GameRoot), ('-p:OutputPath=' + $generatorOutput + '/'),
    ('-p:BaseIntermediateOutputPath=' + $generatorObj + '/')
)
& $DotnetPath @generatorBuild
if ($LASTEXITCODE -ne 0) { throw 'Beat Sage contract generator compilation failed.' }
$generatorRun = @(
    (Join-Path $generatorOutput 'BeatSage.ContractGenerator.dll'),
    '--schema', (Join-Path $GameRoot 'scripts/beat_sage/Contracts/contracts.schema.json'),
    '--endpoints', (Join-Path $GameRoot 'scripts/beat_sage/Contracts/endpoints.json'),
    '--output', $generated, '--namespace', 'BeatSage.Contracts', '--root-name', 'ContractDocument'
)
& $DotnetPath @generatorRun
if ($LASTEXITCODE -ne 0) { throw 'Beat Sage contract generation failed.' }

$restore = @(
    'restore', $project,
    ('-p:GameRoot=' + [IO.Path]::GetFullPath($GameRoot)),
    ('-p:OutputPath=' + $output + '/'),
    ('-p:BaseIntermediateOutputPath=' + $obj + '/')
)
& $DotnetPath @restore
if ($LASTEXITCODE -ne 0) { throw 'Beat Sage test dependency restore failed.' }

$build = @(
    'build', $project, '--configuration', 'Release', '--no-restore',
    ('-p:GameRoot=' + [IO.Path]::GetFullPath($GameRoot)),
    ('-p:OutputPath=' + $output + '/'),
    ('-p:BaseIntermediateOutputPath=' + $obj + '/')
)
& $DotnetPath @build
if ($LASTEXITCODE -ne 0) { throw 'Beat Sage test compilation failed.' }

$testAssembly = Join-Path $output 'BeatSage.Tests.dll'
if (!(Test-Path -LiteralPath $testAssembly -PathType Leaf)) { throw "Missing test assembly: $testAssembly" }
& $DotnetPath $testAssembly
if ($LASTEXITCODE -ne 0) { throw 'Beat Sage LocalMusic tests failed.' }
Write-Output $testAssembly

$coreOutput = Join-Path $BuildDirectory 'core-bin'
$coreObj = Join-Path $BuildDirectory 'core-obj'
[IO.Directory]::CreateDirectory($coreOutput) | Out-Null
[IO.Directory]::CreateDirectory($coreObj) | Out-Null
$coreRestore = @(
    'restore', $coreProject,
    ('-p:GameRoot=' + $GameRoot), ('-p:OutputPath=' + $coreOutput + '/'),
    ('-p:BaseIntermediateOutputPath=' + $coreObj + '/'),
    ('-p:ContractGeneratedDirectory=' + $generated),
    ('-p:FrameworkPathOverride=' + $frameworkReferenceDirectory)
)
& $DotnetPath @coreRestore
if ($LASTEXITCODE -ne 0) { throw 'Beat Sage core test dependency restore failed.' }
if (!(Test-Path -LiteralPath $frameworkReferenceDirectory -PathType Container)) { throw "Missing net472 reference assemblies: $frameworkReferenceDirectory" }
$coreBuild = @(
    'build', $coreProject, '--configuration', 'Release', '--no-restore',
    ('-p:GameRoot=' + $GameRoot), ('-p:OutputPath=' + $coreOutput + '/'),
    ('-p:BaseIntermediateOutputPath=' + $coreObj + '/'),
    ('-p:ContractGeneratedDirectory=' + $generated),
    ('-p:FrameworkPathOverride=' + $frameworkReferenceDirectory)
)
& $DotnetPath @coreBuild
if ($LASTEXITCODE -ne 0) { throw 'Beat Sage core test compilation failed.' }
$coreAssembly = Join-Path $coreOutput 'BeatSage.Core.Tests.exe'
if (!(Test-Path -LiteralPath $coreAssembly -PathType Leaf)) { throw "Missing core test assembly: $coreAssembly" }
& $coreAssembly
if ($LASTEXITCODE -ne 0) { throw 'Beat Sage core tests failed.' }
Write-Output $coreAssembly
