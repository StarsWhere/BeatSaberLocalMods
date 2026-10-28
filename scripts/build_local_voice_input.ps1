#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$GameRoot = $env:BEAT_SABER_DIR,
    [string]$BuildDirectory = '',
    [string]$OutputDirectory = '',
    [string]$DotNetPath = 'C:/Program Files/dotnet/dotnet.exe',
    [switch]$CreateArchive
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build_local_paths.ps1')
if ((Get-Content -LiteralPath (Join-Path $GameRoot 'BeatSaberVersion.txt') -Raw).Trim() -ne '1.45.2_28848') { throw 'VoiceInput targets Beat Saber 1.45.2_28848.' }
if (!$BuildDirectory) { $BuildDirectory = Join-Path $RepoRoot '.tmp/voice-input/plugin-build' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $RepoRoot 'releases/VoiceInput-0.1.1-bs1.45.2-win-x64' }
$BuildDirectory = (Resolve-RepoBuildPath $BuildDirectory)
& (Join-Path $PSScriptRoot 'build_local_mod_ui.ps1') -GameRoot $GameRoot -OutputDirectory $OutputDirectory | Out-Null

$OutputDirectory = (Resolve-RepoBuildPath $OutputDirectory)
$source = Join-Path $PSScriptRoot 'voice_input'
$generator = Join-Path $PSScriptRoot 'beat_sage/ContractGenerator/ContractGenerator.csproj'
$generatorOutput = Join-Path $BuildDirectory 'generator/bin'
$generated = Join-Path $BuildDirectory 'generated'
$bin = Join-Path $BuildDirectory 'bin'
$obj = Join-Path $BuildDirectory 'obj'
foreach ($directory in @($generatorOutput, $generated, $bin, $obj)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
& $DotNetPath build $generator --configuration Release ('-p:OutputPath=' + $generatorOutput + '/') ('-p:BaseIntermediateOutputPath=' + (Join-Path $BuildDirectory 'generator/obj') + '/')
if ($LASTEXITCODE -ne 0) { throw 'Shared contract generator compilation failed.' }
& $DotNetPath (Join-Path $generatorOutput 'BeatSage.ContractGenerator.dll') --schema (Join-Path $source 'Contracts/contracts.schema.json') --endpoints (Join-Path $source 'Contracts/endpoints.json') --output $generated --namespace VoiceInput.Contracts --root-name ContractDocument
if ($LASTEXITCODE -ne 0) { throw 'VoiceInput contract generation failed.' }
$referencePath = Join-Path $env:USERPROFILE '.nuget/packages/microsoft.netframework.referenceassemblies.net472/1.0.3/build/.NETFramework/v4.7.2'
& $DotNetPath build (Join-Path $source 'VoiceInput.csproj') --configuration Release ('-p:GameRoot=' + $GameRoot) ('-p:OutputPath=' + $bin + '/') ('-p:BaseIntermediateOutputPath=' + $obj + '/') ('-p:FrameworkPathOverride=' + $referencePath) ('-p:ContractGeneratedDirectory=' + $generated)
if ($LASTEXITCODE -ne 0) { throw 'VoiceInput compilation failed.' }
$pluginOutput = Join-Path $OutputDirectory 'Plugins'
$libOutput = Join-Path $OutputDirectory 'Libs'
foreach ($directory in @($pluginOutput, $libOutput)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
foreach ($name in @('VoiceInput.dll', 'VoiceInput.pdb')) { Copy-Item -LiteralPath (Join-Path $bin $name) -Destination (Join-Path $pluginOutput $name) }
$libraries = @('NJsonSchema.dll', 'NJsonSchema.Annotations.dll', 'Namotion.Reflection.dll', 'Newtonsoft.Json.dll')
foreach ($name in $libraries) { Copy-Item -LiteralPath (Join-Path $GameRoot ('Libs/' + $name)) -Destination (Join-Path $libOutput $name) }
$schema = Get-Content -LiteralPath (Join-Path $source 'Contracts/contracts.schema.json') -Raw | ConvertFrom-Json
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'VoiceInput.example.json'), (($schema.'x-defaultConfiguration' | ConvertTo-Json -Depth 15) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

function Copy-SourceTree([string]$From, [string]$To) {
    [IO.Directory]::CreateDirectory($To) | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $From -Force) {
        if ($item.Name -in @('bin','obj','.git','.tmp','__pycache__')) { continue }
        $destination = Join-Path $To $item.Name
        if ($item.PSIsContainer) { Copy-SourceTree $item.FullName $destination }
        else { Copy-Item -LiteralPath $item.FullName -Destination $destination }
    }
}
$sourceOutput = Join-Path $OutputDirectory 'source/scripts'
Copy-SourceTree $source (Join-Path $sourceOutput 'voice_input')
Copy-SourceTree (Join-Path $PSScriptRoot 'shared_http_contracts') (Join-Path $sourceOutput 'shared_http_contracts')
Copy-SourceTree (Join-Path $PSScriptRoot 'beat_sage/ContractGenerator') (Join-Path $sourceOutput 'beat_sage/ContractGenerator')
Copy-SourceTree (Join-Path $RepoRoot 'licenses') (Join-Path $OutputDirectory 'licenses')
Copy-Item -LiteralPath (Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $OutputDirectory 'THIRD-PARTY-NOTICES.txt')
foreach ($name in @('build_local_voice_input.ps1','test_voice_input.ps1','test_voice_input_runtime.ps1')) {
    $path = Join-Path $PSScriptRoot $name
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $sourceOutput $name) }
}
$checksums = @(Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse | Where-Object Name -ne 'SHA256SUMS' | Sort-Object FullName | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.FullName.Substring($OutputDirectory.Length + 1).Replace('\','/')
})
[IO.File]::WriteAllLines((Join-Path $OutputDirectory 'SHA256SUMS'), $checksums, [Text.UTF8Encoding]::new($false))
if ($CreateArchive) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = $OutputDirectory.TrimEnd('\','/') + '.zip'
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
    [IO.Compression.ZipFile]::CreateFromDirectory($OutputDirectory, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($archive + '.sha256'), ($hash + '  ' + [IO.Path]::GetFileName($archive) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    Write-Output $archive
}
Write-Output (Join-Path $pluginOutput 'VoiceInput.dll')
