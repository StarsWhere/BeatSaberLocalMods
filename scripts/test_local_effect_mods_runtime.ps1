[CmdletBinding()]
param(
    [string]$GameRoot=(Split-Path -Parent $PSScriptRoot),
    [string]$LevelId='custom_level_95344776AF34545AF972E79FD307875D0EFECC02',
    [ValidateRange(0,600)][int]$Seconds=30,
    [switch]$UseVr,
    [switch]$Practice,
    [ValidateRange(0.5,2.0)][float]$PracticeSpeed=1.0,
    [ValidateSet('Default','Disabled','Map','Fixed','MultiplierLow','MultiplierHigh','Identity','Effects','Variable','Percentage','Entry','Easy','Normal','Hard','Half','Stable','Early','Cap','CapVariable','TimingOnly','StableStrict')][string]$TrainingScenario='Default',
    [string]$Characteristic,
    [ValidateSet('Normal','Hard','Expert','ExpertPlus','Easy')][string]$Difficulty,
    [switch]$AssetAuditOnly,
    [switch]$MenuOnly,
    [switch]$TrainingUi,
    [switch]$SettingsNavigation,
    [switch]$AdditionalSettingsNavigation,
    [switch]$FullModUiAudit,
    [string]$UiCaptionContracts,
    [string]$ModUiAuditFocus,
    [switch]$CompileOnly,
    [switch]$Reload,
    [switch]$HideAvatarForCapture,
    [switch]$VerifyAudio,
    [switch]$NeutralSongFilters,
    [string[]]$DisableForTest=@(),
    [switch]$SkipTrainingChecks,
    [switch]$TraceShutdown,
    [switch]$QuitFromGameplay,
    [switch]$LocalCompatibilityChecks,
    [switch]$EnergyOnly,
    [switch]$NoteTrainingOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$GameRoot=[IO.Path]::GetFullPath($GameRoot)
if($Reload -and !$Practice){throw 'Heck hot reload is available in practice mode; pass -Practice.'}
if($PracticeSpeed -ne 1 -and !$Practice){throw 'PracticeSpeed requires -Practice.'}
if($FullModUiAudit){$TrainingUi=$true}
if($TrainingUi){$MenuOnly=$true}
if (!$CompileOnly -and @(Get-Process -Name 'Beat Saber' -ErrorAction SilentlyContinue).Count) { throw 'Beat Saber is already running.' }
$runRoot=Join-Path $GameRoot ('.tmp/effect-mods-1452/'+$(if($CompileOnly){'helper-build/'}else{'runtime/'})+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff'))
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
if($FullModUiAudit){& (Join-Path $PSScriptRoot 'get_installed_mod_ui_inventory.ps1') -GameRoot $GameRoot -OutputDirectory (Join-Path $runRoot 'inventory')}
$source=Join-Path $PSScriptRoot 'effect_mods/Tests'
$plugin=Join-Path $GameRoot 'Plugins/EffectModsSmoke.dll'
if (!$CompileOnly -and (Test-Path -LiteralPath $plugin)) { throw 'A runtime check plugin already exists.' }
$helper=Join-Path $runRoot 'EffectModsSmoke.dll'
$arguments=@('/nologo','/target:library','/langversion:latest','/nostdlib+',('/out:"'+$helper+'"'),('/resource:"'+(Join-Path $source 'RuntimeSmoke.manifest.json')+'",EffectModsSmoke.manifest.json'))
$arguments+=@(Get-ChildItem -LiteralPath $source -File -Filter '*.cs' | Sort-Object Name | ForEach-Object { '"'+$_.FullName+'"' })
$arguments+=@(Get-ChildItem -LiteralPath (Join-Path $GameRoot 'Beat Saber_Data/Managed') -File -Filter '*.dll' | ForEach-Object { '/r:"'+$_.FullName+'"' })
$arguments+=@('Libs/0Harmony.dll','Libs/Newtonsoft.Json.dll','Plugins/SongCore.dll','Plugins/CustomJSONData.dll') | ForEach-Object { '/r:"'+(Join-Path $GameRoot $_)+'"' }
$response=Join-Path $runRoot 'smoke.rsp'; $arguments | Set-Content -LiteralPath $response -Encoding utf8
$compiler=Get-ChildItem -LiteralPath 'C:/Program Files/dotnet/sdk' -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 | ForEach-Object { Join-Path $_.FullName 'Roslyn/bincore/csc.dll' }
& 'C:/Program Files/dotnet/dotnet.exe' $compiler ('@'+$response)
if ($LASTEXITCODE -ne 0) { throw 'Runtime helper compilation failed.' }
if ($CompileOnly) { Write-Output $helper; return }
$process=$null
$configBackup=Join-Path $runRoot 'configs-before'
[IO.Directory]::CreateDirectory($configBackup) | Out-Null
$userDataRoot=[IO.Path]::GetFullPath((Join-Path $GameRoot 'UserData'))
$configExtensions=$(if($FullModUiAudit){@('.json','.ini','.cfg')}else{@('.json','.ini')})
$configFiles=@(Get-ChildItem -LiteralPath $userDataRoot -File -Recurse:$FullModUiAudit | Where-Object { $_.Extension -in $configExtensions })
$profileRoot=Join-Path $env:USERPROFILE 'AppData/LocalLow/Hyperbolic Magnetism/Beat Saber'
$profileFiles=@(Get-ChildItem -LiteralPath $profileRoot -File | Where-Object { $_.Extension -in @('.dat','.ini','.bak') })
$profileBackup=Join-Path $runRoot 'profile-before'
[IO.Directory]::CreateDirectory($profileBackup) | Out-Null
foreach ($file in $configFiles) {
    $savedConfig=Join-Path $configBackup ([IO.Path]::GetRelativePath($userDataRoot,$file.FullName))
    [IO.Directory]::CreateDirectory((Split-Path -Parent $savedConfig))|Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $savedConfig
}
foreach ($file in $profileFiles) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $profileBackup $file.Name) }
$before=@{}
$before['EFFECT_SMOKE_MENU_ONLY']=[Environment]::GetEnvironmentVariable('EFFECT_SMOKE_MENU_ONLY','Process')
$before['EFFECT_SMOKE_PRACTICE_SPEED']=[Environment]::GetEnvironmentVariable('EFFECT_SMOKE_PRACTICE_SPEED','Process')
$before['EFFECT_SMOKE_TRAINING_SCENARIO']=[Environment]::GetEnvironmentVariable('EFFECT_SMOKE_TRAINING_SCENARIO','Process')
foreach ($name in @('EFFECT_SMOKE_LEVEL','EFFECT_SMOKE_DIR','EFFECT_SMOKE_SECONDS','EFFECT_SMOKE_PRACTICE','EFFECT_SMOKE_CHARACTERISTIC','EFFECT_SMOKE_DIFFICULTY','EFFECT_SMOKE_VERIFY_AUDIO','EFFECT_SMOKE_SKIP_TRAINING','EFFECT_SMOKE_ENERGY_ONLY','EFFECT_SMOKE_ASSETS','EFFECT_SMOKE_RELOAD','EFFECT_SMOKE_VR','EFFECT_SMOKE_HIDE_AVATAR','EFFECT_SMOKE_TRACE_SHUTDOWN','EFFECT_SMOKE_QUIT_GAMEPLAY','EFFECT_SMOKE_LOCAL_COMPATIBILITY','EFFECT_SMOKE_TRAINING_UI','EFFECT_SMOKE_SETTINGS_NAVIGATION','EFFECT_SMOKE_ADDITIONAL_SETTINGS_NAVIGATION','EFFECT_SMOKE_FULL_MOD_UI_AUDIT','EFFECT_SMOKE_UI_INVENTORY','EFFECT_SMOKE_UI_CAPTIONS','EFFECT_SMOKE_UI_FOCUS')) { $before[$name]=[Environment]::GetEnvironmentVariable($name,'Process') }
$before['EFFECT_SMOKE_NOTE_TRAINING_ONLY']=[Environment]::GetEnvironmentVariable('EFFECT_SMOKE_NOTE_TRAINING_ONLY','Process')
try {
    if($FullModUiAudit){
        $languagePath=Join-Path $userDataRoot 'ModUiChinese.json'
        if(Test-Path -LiteralPath $languagePath){
            $languageConfig=Get-Content -LiteralPath $languagePath -Raw|ConvertFrom-Json -AsHashtable
            $languageConfig.Global.Enabled=$true
            $languageConfig.Global.Mods=@{}
            $languageConfig|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $languagePath -Encoding utf8NoBOM
        }
    }
    if ($NeutralSongFilters) {
        $filterPath=Join-Path $GameRoot 'UserData/AdvancedSongFilters.json'
        if (Test-Path -LiteralPath $filterPath) {
            # Normalize derives Enabled from the expression, so clear the expression itself.
            '{"SchemaVersion":1,"Enabled":false,"ExcludeOfficial":false,"Mode":"All","Difficulties":[]}' |
                Set-Content -LiteralPath $filterPath -Encoding utf8NoBOM
        }
    }
    if ($DisableForTest.Count) {
        $disabledPath=Join-Path $GameRoot 'UserData/Disabled Mods.json'
        $disabled=Get-Content -LiteralPath $disabledPath -Raw | ConvertFrom-Json
        $disabled.DisabledModIds=[string[]]@((@($disabled.DisabledModIds)+$DisableForTest) | Select-Object -Unique)
        $disabled | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $disabledPath -Encoding utf8NoBOM
    }
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_MENU_ONLY',$(if($MenuOnly){'1'}else{'0'}),'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_TRAINING_UI',$(if($TrainingUi){'1'}else{'0'}),'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_SETTINGS_NAVIGATION',$(if($SettingsNavigation){'1'}else{'0'}),'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_ADDITIONAL_SETTINGS_NAVIGATION',$(if($AdditionalSettingsNavigation){'1'}else{'0'}),'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_FULL_MOD_UI_AUDIT',$(if($FullModUiAudit){'1'}else{'0'}),'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_UI_INVENTORY',$(if($FullModUiAudit){Join-Path $runRoot 'inventory/installed-mod-ui.json'}else{''}),'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_UI_CAPTIONS',$(if($UiCaptionContracts){[IO.Path]::GetFullPath($UiCaptionContracts)}else{''}),'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_UI_FOCUS',$ModUiAuditFocus,'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_PRACTICE_SPEED',$PracticeSpeed.ToString([Globalization.CultureInfo]::InvariantCulture),'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_TRAINING_SCENARIO',$TrainingScenario,'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_ENERGY_ONLY',$(if($EnergyOnly){'1'}else{'0'}),'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_NOTE_TRAINING_ONLY',$(if($NoteTrainingOnly){'1'}else{'0'}),'Process')
    Copy-Item -LiteralPath $helper -Destination $plugin
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_LEVEL',$LevelId,'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_DIR',$runRoot,'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_SECONDS',[string]$Seconds,'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_PRACTICE',$(if($Practice){'1'}else{'0'}),'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_CHARACTERISTIC',$Characteristic,'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_DIFFICULTY',$Difficulty,'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_VERIFY_AUDIO',$(if($VerifyAudio){'1'}else{'0'}),'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_SKIP_TRAINING',$(if($SkipTrainingChecks){'1'}else{'0'}),'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_ASSETS',$(if($AssetAuditOnly){'1'}else{'0'}),'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_RELOAD',$(if($Reload){'1'}else{'0'}),'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_VR',$(if($UseVr){'1'}else{'0'}),'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_HIDE_AVATAR',$(if($HideAvatarForCapture){'1'}else{'0'}),'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_TRACE_SHUTDOWN',$(if($TraceShutdown){'1'}else{'0'}),'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_QUIT_GAMEPLAY',$(if($QuitFromGameplay){'1'}else{'0'}),'Process')
    [Environment]::SetEnvironmentVariable('EFFECT_SMOKE_LOCAL_COMPATIBILITY',$(if($LocalCompatibilityChecks){'1'}else{'0'}),'Process')
    $launch=@('--effect-mods-smoke'); if (!$UseVr) { $launch+=@('--fpfc','fpfc') }
    if($Reload){$launch+='-aerolunaisthebestmodder'}
    $logsBefore=@(Get-ChildItem -LiteralPath (Join-Path $GameRoot 'Logs') -File -Filter '*.log' | ForEach-Object FullName)
    $process=Start-Process -FilePath (Join-Path $GameRoot 'Beat Saber.exe') -WorkingDirectory $GameRoot -ArgumentList $launch -WindowStyle $(if($UseVr){'Normal'}else{'Hidden'}) -PassThru
    Write-Output ('Runtime process '+$process.Id+'; report '+$runRoot)
    $deadline=[DateTime]::UtcNow.AddSeconds($(if($FullModUiAudit){1200}elseif ($Seconds) {$Seconds/$PracticeSpeed+180} else {780/$PracticeSpeed})+$(if($SettingsNavigation){120}else{0})+$(if($AdditionalSettingsNavigation){180}else{0}))
    while (!$process.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Seconds 1; $process.Refresh() }
    if (!$process.HasExited) { throw 'Effect mod runtime checks exceeded their deadline.' }
    $sessionLogs=@(Get-ChildItem -LiteralPath (Join-Path $GameRoot 'Logs') -File -Filter '*.log' | Where-Object { $_.Name-ne'_latest.log' -and $_.FullName-notin$logsBefore })
    if($sessionLogs.Count-ne1 -or $sessionLogs[0].Length-eq0){throw 'Expected one non-empty log created by this game process.'}
    Copy-Item -LiteralPath $sessionLogs[0].FullName -Destination (Join-Path $runRoot 'game.log')
    $report=Join-Path $runRoot 'report.json'
    if (!(Test-Path -LiteralPath $report)) { throw 'Game exited without a runtime report.' }
    $result=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if(!(Select-String -LiteralPath (Join-Path $runRoot 'game.log') -Pattern '^\[DEBUG.*\| IPA\] Command-line:.*--effect-mods-smoke')){throw 'The captured session log does not identify the runtime test process.'}
    Copy-Item -LiteralPath $report -Destination (Join-Path $runRoot 'pre-exit-report.json')
    $modErrors=@(Select-String -LiteralPath (Join-Path $runRoot 'game.log') -Pattern '^\[(ERROR|CRITICAL).*\| (SongCore|CustomJSONData|Heck|NoodleExtensions|Chroma|Vivify|AudioLink|Note Training Control|NoteMotionControl|DensityReducer|ActivityEnergyCounter|ModUiChinese|RoomCalibration)(/|\])')
    $modErrors+=@(Select-String -LiteralPath (Join-Path $runRoot 'game.log') -Pattern '^\[CRITICAL.*\| SiraUtil\].*(SongCore|CustomJSONData|Heck|NoodleExtensions|Chroma|Vivify|AudioLink|Note Training Control|NoteMotionControl|DensityReducer|ActivityEnergyCounter|ModUiChinese|RoomCalibration)')
    $modErrors+=@(Select-String -LiteralPath (Join-Path $runRoot 'game.log') -Pattern '^\[(ERROR|CRITICAL).*\| UnityEngine\].*(CustomJSONData\.|Heck\.|NoodleExtensions\.|Chroma\.|Vivify\.|AudioLink\.|NoteMotionControl\.|DensityReducer\.|ActivityEnergyCounter\.|ModUiChinese\.|RoomCalibration\.)')
    $modErrors+=@(Select-String -LiteralPath (Join-Path $runRoot 'game.log') -Pattern '^\[(ERROR|CRITICAL).*\| BeatSaberMarkupLanguage\].*(NoteMotionControl|DensityReducer|ActivityEnergyCounter|ModUiChinese)')
    $modErrors+=@(Select-String -LiteralPath (Join-Path $runRoot 'game.log') -Pattern '^\[(ERROR|CRITICAL).*\| IPA/Config\].*Room Calibration')
    $modErrors+=@(Select-String -LiteralPath (Join-Path $runRoot 'game.log') -Pattern '^\[(ERROR|CRITICAL).*\| IPA\].*(Note Training Control|Note Speed Control|Density Reducer|Activity Energy Counter|ModUiChinese|Room Calibration)')
    $modErrors+=@(Select-String -LiteralPath (Join-Path $runRoot 'game.log') -Pattern '^\[(ERROR|CRITICAL).*\| (Unity Shutdown Fix|UnityShutdownFix)(/|\])|^\[(ERROR|CRITICAL).*\| (UnityEngine|IPA)\].*UnityShutdownFix')
    $pipelineErrors=@(Select-String -LiteralPath (Join-Path $runRoot 'game.log') -Pattern '^\[(ERROR|CRITICAL).*\].*(NullReferenceException|InvalidCastException|InvalidProgramException|BeatmapCallbacksController|BeatmapDataCallbackWrapper|CallbacksInTime|BeatmapCallbacksUpdater|VariableMovementDataProvider\.HandleNoteJumpMovementSpeedEvent|NoteJumpSpeedEventData\.get_nextSameTypeEventData)')
    $modErrors+=@([regex]::Matches([IO.File]::ReadAllText((Join-Path $runRoot 'game.log')), '(?ms)^\[(?:ERROR|CRITICAL)[^\r\n]*\r?\n.*?(?=^\[|\z)') |
        Where-Object {$_.Value -match '\b(PlaylistManager|MorePlaylists|SongChartVisualizer)\b'} |
        ForEach-Object {[pscustomobject]@{Line=$_.Value}})
    $cleanupErrors=@(Select-String -LiteralPath (Join-Path $runRoot 'game.log') -Pattern 'Releasing render texture that is set as Camera.targetTexture|^\[(ERROR|CRITICAL).*\| UnityEngine\].*(RenderGraph|RenderPass:|EndRenderPass:|GrabPass)')
    $exitPassed=$process.ExitCode -eq 0
    $result.checks+=@(
        [pscustomobject]@{name='process-exited-cleanly';passed=$exitPassed;detail=('Exit code '+$process.ExitCode)},
        [pscustomobject]@{name='complete-log-effect-errors';passed=($modErrors.Count -eq 0);detail=($modErrors.Count.ToString()+' errors including shutdown')},
        [pscustomobject]@{name='complete-log-callback-pipeline';passed=($pipelineErrors.Count -eq 0);detail=($pipelineErrors.Count.ToString()+' callback or runtime type errors including shutdown')},
        [pscustomobject]@{name='complete-log-render-cleanup';passed=($cleanupErrors.Count -eq 0);detail=($cleanupErrors.Count.ToString()+' errors including shutdown')}
    )
    if($LocalCompatibilityChecks){
        $localErrors=@(Select-String -LiteralPath (Join-Path $runRoot 'game.log') -Pattern 'The referenced script.*missing|has invalid command name|Could not expose (CoreGameHUDController|NoteCutSoundEffectManager)')
        $result.checks+=[pscustomobject]@{name='complete-log-local-compatibility';passed=($localErrors.Count-eq0);detail=($localErrors.Count.ToString()+' local compatibility warnings/errors including shutdown')}
        $result.errors+=@($localErrors|ForEach-Object Line)
        $result.passed=$result.passed -and $localErrors.Count-eq0
    }
    if(!$exitPassed){$result.errors+=('Game process exited with code '+$process.ExitCode)}
    $result.errors+=@($modErrors | ForEach-Object Line)
    $result.errors+=@($pipelineErrors | ForEach-Object Line | Select-Object -Unique -First 20)
    $result.errors+=@($cleanupErrors | ForEach-Object Line)
    $result.passed=$result.passed -and $exitPassed -and $modErrors.Count -eq 0 -and $pipelineErrors.Count -eq 0 -and $cleanupErrors.Count -eq 0
    $uiSnapshots=@(Get-ChildItem -LiteralPath $runRoot -File -Filter '*.json' | Where-Object {
        $_.Name -like 'training-ui-*-zh*.json' -or $_.Name -like 'training-ui-shortcut-*.json' -or
        $_.Name -like 'mod-ui-snapshot-*.json' -or $_.Name -in @('training-ui-main-training-shortcuts.json','training-ui-gameplay-energy.json','results.json')
    } | ForEach-Object FullName)
    if($uiSnapshots.Count){
        $uiAudit=Join-Path $runRoot 'localization-audit.json'
        $scopeArgs = @()
        if ($ModUiAuditFocus -eq 'LocalModUI') {
            foreach ($scope in @('LocalModUI','NoteMotionControl','ActivityEnergyCounter','RoomCalibration','ModUiChinese','VoiceInput','BeatSage','SongDuplicateCleaner','AdvancedSongFilters')) { $scopeArgs += @('--scope',$scope) }
        }
        & python (Join-Path $PSScriptRoot 'audit_mod_ui_localization.py') @uiSnapshots --output $uiAudit --fail-on-findings @scopeArgs
        $uiAuditPassed=$LASTEXITCODE-eq0
        $result.checks+=[pscustomobject]@{name='native-ui-untranslated-captions';passed=$uiAuditPassed;detail=$uiAudit}
        $result.passed=$result.passed -and $uiAuditPassed
        if(!$uiAuditPassed){$result.errors+=('Untranslated native UI captions found; see '+$uiAudit)}
    }
    $result | Add-Member -NotePropertyName logIncludesShutdown -NotePropertyValue $true
    $result | Add-Member -NotePropertyName disabledForTest -NotePropertyValue @($DisableForTest)
    $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $report -Encoding utf8NoBOM
    if (!$result.passed) { throw ('Effect mod runtime check failed: '+($result.errors -join "`n")) }
    Write-Output ('Passed '+$result.checks.Count+' checks in '+$result.elapsed+' seconds.')
} finally {
    if ($process) { $process.Refresh(); if (!$process.HasExited) { Stop-Process -Id $process.Id; $null=$process.WaitForExit(10000) }; $process.Dispose() }
    $playerLog=Join-Path $profileRoot 'Player.log'
    if(Test-Path -LiteralPath $playerLog){Copy-Item -LiteralPath $playerLog -Destination (Join-Path $runRoot 'Player.log')}
    if (Test-Path -LiteralPath $plugin) { Remove-Item -LiteralPath $plugin }
    $chartPathFile=Join-Path $runRoot 'reload-chart-path.txt'
    if(Test-Path -LiteralPath $chartPathFile){
        $chartPath=[IO.Path]::GetFullPath([IO.File]::ReadAllText($chartPathFile))
        $chartsRoot=[IO.Path]::GetFullPath((Join-Path $GameRoot 'Beat Saber_Data/CustomLevels'))+[IO.Path]::DirectorySeparatorChar
        if(!$chartPath.StartsWith($chartsRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Reload test recovery path is outside the custom song library.'}
        [IO.File]::WriteAllBytes($chartPath,[IO.File]::ReadAllBytes((Join-Path $runRoot 'reload-chart-original.bin')))
        $timeFile=Join-Path $runRoot 'reload-chart-time.txt'
        if(Test-Path -LiteralPath $timeFile){[IO.File]::SetLastWriteTimeUtc($chartPath,[DateTime]::Parse([IO.File]::ReadAllText($timeFile),[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind))}
    }
    if($FullModUiAudit){
        $originalConfigPaths=@($configFiles|ForEach-Object FullName)
        foreach($createdConfig in Get-ChildItem -LiteralPath $userDataRoot -File -Recurse | Where-Object {$_.Extension-in$configExtensions -and $_.FullName-notin$originalConfigPaths}){
            $resolvedConfig=[IO.Path]::GetFullPath($createdConfig.FullName)
            if(!$resolvedConfig.StartsWith($userDataRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Configuration recovery path is outside UserData.'}
            Remove-Item -LiteralPath $resolvedConfig
        }
    }
    foreach ($file in $configFiles) { Copy-Item -LiteralPath (Join-Path $configBackup ([IO.Path]::GetRelativePath($userDataRoot,$file.FullName))) -Destination $file.FullName }
    foreach ($name in @('NoteMotionControl.json','DensityReducer.json','ActivityEnergyCounter.json','ModUiChinese.json')) {
        $target=Join-Path $GameRoot ('UserData/'+$name)
        if (!(Test-Path -LiteralPath (Join-Path $configBackup $name)) -and (Test-Path -LiteralPath $target)) { Remove-Item -LiteralPath $target }
    }
    foreach ($file in $profileFiles) { Copy-Item -LiteralPath (Join-Path $profileBackup $file.Name) -Destination $file.FullName }
    foreach ($name in $before.Keys) { [Environment]::SetEnvironmentVariable($name,$before[$name],'Process') }
}
