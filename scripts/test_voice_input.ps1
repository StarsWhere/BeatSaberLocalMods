#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$GameRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$BuildDirectory = '',
    [string]$DotNetPath = 'C:/Program Files/dotnet/dotnet.exe',
    [switch]$Live,
    [string]$PcmPath,
    [string]$ExpectedText
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$GameRoot = [IO.Path]::GetFullPath($GameRoot)
if (!$BuildDirectory) { $BuildDirectory = Join-Path $GameRoot '.tmp/voice-input/tests' }
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
$generatorBin = Join-Path $BuildDirectory 'generator/bin'
$generated = Join-Path $BuildDirectory 'generated'
$coreBin = Join-Path $BuildDirectory 'core/bin'
$source = Join-Path $PSScriptRoot 'voice_input'
foreach ($directory in @($generatorBin,$generated,$coreBin)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
& $DotNetPath build (Join-Path $PSScriptRoot 'beat_sage/ContractGenerator/ContractGenerator.csproj') --configuration Release ('-p:OutputPath=' + $generatorBin + '/') ('-p:BaseIntermediateOutputPath=' + (Join-Path $BuildDirectory 'generator/obj') + '/')
if ($LASTEXITCODE -ne 0) { throw 'Shared contract generator compilation failed.' }
& $DotNetPath (Join-Path $generatorBin 'BeatSage.ContractGenerator.dll') --schema (Join-Path $source 'Contracts/contracts.schema.json') --endpoints (Join-Path $source 'Contracts/endpoints.json') --output $generated --namespace VoiceInput.Contracts --root-name ContractDocument
if ($LASTEXITCODE -ne 0) { throw 'VoiceInput contract generation failed.' }
$coreProject = Join-Path $source 'Tests/Core/VoiceInput.Core.Tests.csproj'
& $DotNetPath build $coreProject --configuration Release --framework net8.0 ('-p:GameRoot=' + $GameRoot) ('-p:OutputPath=' + $coreBin + '/') ('-p:BaseIntermediateOutputPath=' + (Join-Path $BuildDirectory 'core/obj') + '/') ('-p:ContractGeneratedDirectory=' + $generated)
if ($LASTEXITCODE -ne 0) { throw 'VoiceInput core test compilation failed.' }
$coreDll = Join-Path $coreBin 'VoiceInput.Core.Tests.dll'
& $DotNetPath $coreDll
if ($LASTEXITCODE -ne 0) { throw 'VoiceInput core tests failed.' }
$referencePath = Join-Path $env:USERPROFILE '.nuget/packages/microsoft.netframework.referenceassemblies.net472/1.0.3/build/.NETFramework/v4.7.2'
& $DotNetPath build $coreProject --configuration Release --framework net472 ('-p:GameRoot=' + $GameRoot) ('-p:OutputPath=' + (Join-Path $BuildDirectory 'net472/bin') + '/') ('-p:BaseIntermediateOutputPath=' + (Join-Path $BuildDirectory 'core/obj') + '/') ('-p:ContractGeneratedDirectory=' + $generated) ('-p:FrameworkPathOverride=' + $referencePath)
if ($LASTEXITCODE -ne 0) { throw 'VoiceInput net472 core compilation failed.' }
$sharedProject = Join-Path $PSScriptRoot 'shared_http_contracts/Tests/SharedHttpContracts.Tests.csproj'
& $DotNetPath build $sharedProject --configuration Release ('-p:OutputPath=' + (Join-Path $BuildDirectory 'shared/bin') + '/') ('-p:BaseIntermediateOutputPath=' + (Join-Path $BuildDirectory 'shared/obj') + '/')
if ($LASTEXITCODE -ne 0) { throw 'Shared HTTP contract test compilation failed.' }
& $DotNetPath (Join-Path $BuildDirectory 'shared/bin/SharedHttpContracts.Tests.dll')
if ($LASTEXITCODE -ne 0) { throw 'Shared HTTP contract tests failed.' }
if ($Live) {
    if (!$PcmPath -or !(Test-Path -LiteralPath $PcmPath -PathType Leaf)) { throw '-Live requires a 16 kHz, mono, signed 16-bit PCM fixture via -PcmPath.' }
    $liveArguments = @($coreDll,'--live-config',$GameRoot,'--pcm',[IO.Path]::GetFullPath($PcmPath))
    if ($ExpectedText) { $liveArguments += @('--expect-contains',$ExpectedText) }
    & $DotNetPath @liveArguments
    if ($LASTEXITCODE -ne 0) { throw 'VoiceInput live recognition validation failed.' }
}
[IO.File]::WriteAllText((Join-Path $BuildDirectory 'report.json'), ((@{passed=$true;core=$true;net472Build=$true;sharedContracts=$true;live=[bool]$Live} | ConvertTo-Json) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
Write-Output 'VoiceInput core and shared contract checks passed.'
