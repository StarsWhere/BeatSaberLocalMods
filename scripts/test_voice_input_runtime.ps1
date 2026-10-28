[CmdletBinding()]
param(
  [string]$GameRoot=(Split-Path -Parent $PSScriptRoot),
  [string]$DotNetPath='C:/Program Files/dotnet/dotnet.exe',
  [ValidateRange(120,480)][int]$FiniteWaitSeconds=420,
  [string]$VoiceInputAssemblyPath,
  [string]$LivePcmPath,
  [string]$ExpectedText,
  [switch]$UseVr,
  [switch]$LiveOnly,
  [switch]$BsmlOnly,
  [switch]$PointerOnly,
  [switch]$ProbeBsmlReserve,
  [switch]$CompileOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$GameRoot=[IO.Path]::GetFullPath($GameRoot)
$gameExe=[IO.Path]::GetFullPath((Join-Path $GameRoot 'Beat Saber.exe'))
$livePcmResolved=$null
if($LivePcmPath){
  $livePcmResolved=[IO.Path]::GetFullPath($LivePcmPath)
  if(!(Test-Path -LiteralPath $livePcmResolved -PathType Leaf)){throw "Live PCM fixture is missing: $livePcmResolved"}
}
if($LiveOnly -and !$livePcmResolved){throw '-LiveOnly requires -LivePcmPath.'}
$source=Join-Path $PSScriptRoot 'voice_input/Tests/Runtime'
$taskRoot=Join-Path $GameRoot '.tmp/voice-input'
$runRoot=Join-Path $taskRoot ('runtime/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff'))
[IO.Directory]::CreateDirectory($runRoot)|Out-Null

function Resolve-VoiceInputAssembly {
  param([string]$ExplicitPath)
  $candidates=@()
  if($ExplicitPath){$candidates+=([IO.Path]::GetFullPath($ExplicitPath))}
  $candidates+=@(
    (Join-Path $GameRoot 'Plugins/VoiceInput.dll'),
    (Join-Path $GameRoot '.tmp/voice-input/build/bin/VoiceInput.dll'),
    (Join-Path $GameRoot '.tmp/voice-input/build/VoiceInput.dll'),
    (Join-Path $GameRoot '.tmp/voice-input/plugin-build/bin/VoiceInput.dll'),
    (Join-Path $GameRoot '.tmp/voice-input/plugin-build/VoiceInput.dll'),
    (Join-Path $PSScriptRoot 'voice_input/bin/VoiceInput.dll'),
    (Join-Path $PSScriptRoot 'voice_input/bin/Release/net472/VoiceInput.dll')
  )
  foreach($candidate in $candidates){
    if(Test-Path -LiteralPath $candidate -PathType Leaf){return [IO.Path]::GetFullPath($candidate)}
  }
  throw 'VoiceInput.dll was not found. Build the approved VoiceInput mod first or pass -VoiceInputAssemblyPath.'
}

function Save-FileState {
  param([string]$Path,[string]$Backup)
  if(Test-Path -LiteralPath $Path -PathType Leaf){
    [IO.File]::WriteAllBytes($Backup,[IO.File]::ReadAllBytes($Path))
    return $true
  }
  return $false
}

if(!(Test-Path -LiteralPath $source -PathType Container)){throw "Runtime source directory is missing: $source"}
if(!(Test-Path -LiteralPath $gameExe -PathType Leaf)){throw "Beat Saber executable is missing: $gameExe"}
$voiceAssembly=Resolve-VoiceInputAssembly $VoiceInputAssemblyPath
$plugin=Join-Path $GameRoot 'Plugins/VoiceInput.RuntimeSmoke.dll'
$helper=Join-Path $runRoot 'VoiceInput.RuntimeSmoke.dll'
$manifest=Join-Path $source 'RuntimeSmoke.manifest.json'
if(!(Test-Path -LiteralPath $manifest -PathType Leaf)){throw "Runtime manifest is missing: $manifest"}
if(!$CompileOnly -and (Test-Path -LiteralPath $plugin)){throw 'A VoiceInput runtime helper already exists.'}

$arguments=@(
  '/nologo','/target:library','/langversion:latest','/nostdlib+',
  ('/out:"'+$helper+'"'),
  ('/resource:"'+$manifest+'",VoiceInput.RuntimeSmoke.manifest.json')
)
$arguments+=@(Get-ChildItem -LiteralPath $source -Filter '*.cs' -File | Sort-Object Name | ForEach-Object {'"'+$_.FullName+'"'})
$arguments+=@(Get-ChildItem -LiteralPath (Join-Path $GameRoot 'Beat Saber_Data/Managed') -Filter '*.dll' -File | Sort-Object Name | ForEach-Object {'/r:"'+$_.FullName+'"'})
$arguments+=@(
  (Join-Path $GameRoot 'Libs/0Harmony.dll'),
  (Join-Path $GameRoot 'Libs/Newtonsoft.Json.dll'),
  (Join-Path $GameRoot 'Plugins/BSML.dll'),
  $voiceAssembly
) | Where-Object {Test-Path -LiteralPath $_ -PathType Leaf} | ForEach-Object {'/r:"'+$_+'"'}
$response=Join-Path $runRoot 'runtime.rsp'
$arguments|Set-Content -LiteralPath $response -Encoding utf8
$dotnet=[IO.Path]::GetFullPath($DotNetPath)
if(!(Test-Path -LiteralPath $dotnet -PathType Leaf)){throw "dotnet executable is missing: $dotnet"}
$sdkRoot=Join-Path (Split-Path -Parent $dotnet) 'sdk'
$compiler=Get-ChildItem -LiteralPath $sdkRoot -Directory | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1 | ForEach-Object {Join-Path $_.FullName 'Roslyn/bincore/csc.dll'}
if(!$compiler -or !(Test-Path -LiteralPath $compiler -PathType Leaf)){throw "Roslyn csc.dll was not found below $sdkRoot"}
& $dotnet $compiler ('@'+$response)
if($LASTEXITCODE -ne 0){throw 'VoiceInput runtime helper compilation failed.'}
if($CompileOnly){Write-Output $helper;return}

$process=$null
$ownsPlugin=$false
$disabledPath=Join-Path $GameRoot 'UserData/Disabled Mods.json'
$disabledBackup=Join-Path $runRoot 'disabled-before.json'
$disabledExisted=Save-FileState $disabledPath $disabledBackup
$environmentBefore=@{}
foreach($name in @('VOICE_INPUT_SMOKE_DIR','VOICE_INPUT_SMOKE_VR','VOICE_INPUT_LIVE_PCM','VOICE_INPUT_LIVE_EXPECT','VOICE_INPUT_LIVE_ONLY','VOICE_INPUT_BSML_ONLY','VOICE_INPUT_POINTER_ONLY','VOICE_INPUT_BSML_RESERVE_PROBE')){$environmentBefore[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}

try {
  if(@(Get-CimInstance Win32_Process -Filter "Name = 'Beat Saber.exe'" | Where-Object {[String]::Equals($_.ExecutablePath,$gameExe,[StringComparison]::OrdinalIgnoreCase)}).Count){throw 'This Beat Saber installation is already running; refusing to interrupt it.'}
  Copy-Item -LiteralPath $helper -Destination $plugin
  $ownsPlugin=$true
  [Environment]::SetEnvironmentVariable('VOICE_INPUT_SMOKE_DIR',$runRoot,'Process')
  [Environment]::SetEnvironmentVariable('VOICE_INPUT_SMOKE_VR',$(if($UseVr){'1'}else{'0'}),'Process')
  [Environment]::SetEnvironmentVariable('VOICE_INPUT_LIVE_PCM',$(if($livePcmResolved){$livePcmResolved}else{''}),'Process')
  [Environment]::SetEnvironmentVariable('VOICE_INPUT_LIVE_EXPECT',$(if($ExpectedText){$ExpectedText}else{''}),'Process')
  [Environment]::SetEnvironmentVariable('VOICE_INPUT_LIVE_ONLY',$(if($LiveOnly){'1'}else{'0'}),'Process')
  [Environment]::SetEnvironmentVariable('VOICE_INPUT_BSML_ONLY',$(if($BsmlOnly){'1'}else{'0'}),'Process')
  [Environment]::SetEnvironmentVariable('VOICE_INPUT_POINTER_ONLY',$(if($PointerOnly){'1'}else{'0'}),'Process')
  [Environment]::SetEnvironmentVariable('VOICE_INPUT_BSML_RESERVE_PROBE',$(if($ProbeBsmlReserve){'1'}else{'0'}),'Process')
  $launch=@('--voice-input-smoke')
  if(!$UseVr){$launch+=@('--fpfc','fpfc')}
  $process=Start-Process -FilePath $gameExe -WorkingDirectory $GameRoot -ArgumentList $launch -WindowStyle Hidden -PassThru
  Write-Output ('Runtime process '+$process.Id+'; evidence '+$runRoot)
  $deadline=[DateTime]::UtcNow.AddSeconds($FiniteWaitSeconds)
  while(!$process.HasExited -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Seconds 1;$process.Refresh()}
  if(!$process.HasExited){throw 'VoiceInput runtime checks exceeded the finite deadline.'}
  $report=Join-Path $runRoot 'report.json'
  if(!(Test-Path -LiteralPath $report -PathType Leaf)){throw 'Game exited without a VoiceInput runtime report.'}
  $result=Get-Content -LiteralPath $report -Raw|ConvertFrom-Json
  if(!$result.passed){throw ('VoiceInput runtime checks failed: '+($result.errors -join "`n"))}
  if($process.ExitCode -ne 0){throw ('VoiceInput runtime process exited with code '+$process.ExitCode)}
  Write-Output ('Passed '+$result.checks.Count+' checks in '+$result.elapsed+' seconds. Evidence: '+$runRoot)
}
finally {
  if($process){$process.Refresh();if(!$process.HasExited){Stop-Process -Id $process.Id;try{$null=$process.WaitForExit(10000)}catch{}};$process.Dispose()}
  if($ownsPlugin -and (Test-Path -LiteralPath $plugin -PathType Leaf)){Remove-Item -LiteralPath $plugin}
  if($disabledExisted){[IO.File]::WriteAllBytes($disabledPath,[IO.File]::ReadAllBytes($disabledBackup))}
  elseif(Test-Path -LiteralPath $disabledPath -PathType Leaf){Remove-Item -LiteralPath $disabledPath}
  foreach($name in $environmentBefore.Keys){[Environment]::SetEnvironmentVariable($name,$environmentBefore[$name],'Process')}
}
