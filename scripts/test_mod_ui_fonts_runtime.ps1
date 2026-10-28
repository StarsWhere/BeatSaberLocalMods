[CmdletBinding()]
param(
    [string]$GameRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ModUiChinesePath,
    [string]$LevelId,
    [int]$FiniteWaitSeconds = 260,
    [switch]$MenuOnly,
    [switch]$ReturnToMenu,
    [switch]$CompileOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$GameRoot = [IO.Path]::GetFullPath($GameRoot)
if ($FiniteWaitSeconds -lt 30 -or $FiniteWaitSeconds -gt 600) { throw 'FiniteWaitSeconds must be between 30 and 600.' }
if (!$CompileOnly -and @(Get-Process -Name 'Beat Saber' -ErrorAction SilentlyContinue).Count) {
    throw 'Beat Saber is already running; the font check will not attach to or stop it.'
}
if (!$ModUiChinesePath) { $ModUiChinesePath = Join-Path $GameRoot 'Plugins/ModUiChinese.dll' }
$ModUiChinesePath = [IO.Path]::GetFullPath($ModUiChinesePath)
if (!(Test-Path -LiteralPath $ModUiChinesePath -PathType Leaf)) { throw "Font candidate is missing: $ModUiChinesePath" }
$source = Join-Path $PSScriptRoot 'mod_ui_chinese/Tests'
$directory = Join-Path $GameRoot ('.tmp/font-glyph-fix/runtime-' + [Guid]::NewGuid().ToString('N'))
$destination = Join-Path $GameRoot 'Plugins/ModUiFontSmoke.dll'
if (Test-Path -LiteralPath $destination) { throw 'ModUiFontSmoke.dll already exists; refusing to replace it.' }
[IO.Directory]::CreateDirectory($directory) | Out-Null
$references = @(Get-ChildItem -LiteralPath (Join-Path $GameRoot 'Beat Saber_Data/Managed') -Filter '*.dll' -File | ForEach-Object FullName)
$references += $ModUiChinesePath
$references += @('Plugins/SongCore.dll','Plugins/BSML.dll') | ForEach-Object { Join-Path $GameRoot $_ }
$helper = Join-Path $directory 'ModUiFontSmoke.dll'
$arguments = @('/nologo','/noconfig','/nostdlib+','/target:library','/codepage:65001','/langversion:5',('/out:' + $helper),
    ('/resource:' + (Join-Path $source 'FontRuntimeSmoke.manifest.json') + ',ModUiFontSmoke.manifest.json'),
    (Join-Path $source 'FontRuntimeSmoke.cs'))
$arguments += $references | ForEach-Object { '/r:' + $_ }
& 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe' @arguments
if ($LASTEXITCODE -ne 0) { throw 'Font runtime helper compilation failed.' }
if ($CompileOnly) { Write-Output "Font runtime helper compiled: $helper"; return }
$installed = Join-Path $GameRoot 'Plugins/ModUiChinese.dll'
if (!(Test-Path -LiteralPath $installed -PathType Leaf) -or
    (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $ModUiChinesePath -Algorithm SHA256).Hash) {
    throw 'The installed ModUiChinese.dll does not match the selected candidate. Install the reviewed candidate before running this check.'
}
$process = $null
$ownsHelper = $false
$exitCode = $null
$logsBefore = @(Get-ChildItem -LiteralPath (Join-Path $GameRoot 'Logs') -File -Filter '*.log' | ForEach-Object FullName)
try {
    # Compilation is read-only with respect to Plugins. Only a real run installs this owned helper.
    Copy-Item -LiteralPath $helper -Destination $destination
    $ownsHelper = $true
    $launch = [Diagnostics.ProcessStartInfo]::new()
    $launch.FileName = Join-Path $GameRoot 'Beat Saber.exe'
    $playerLog = Join-Path $directory 'Player.log'
    $launch.Arguments = '--fpfc fpfc -logFile "' + $playerLog + '"'
    $launch.WorkingDirectory = $GameRoot
    $launch.UseShellExecute = $false
    $launch.CreateNoWindow = $true
    $launch.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $launch.EnvironmentVariables['MOD_UI_FONT_SMOKE_DIR'] = $directory
    $launch.EnvironmentVariables['MOD_UI_FONT_SMOKE_SECONDS'] = [string]($FiniteWaitSeconds - 10)
    if ($LevelId) { $launch.EnvironmentVariables['MOD_UI_FONT_SMOKE_LEVEL'] = $LevelId }
    if ($MenuOnly) { $launch.EnvironmentVariables['MOD_UI_FONT_SMOKE_MENU_ONLY'] = '1' }
    if ($ReturnToMenu) { $launch.EnvironmentVariables['MOD_UI_FONT_SMOKE_RETURN_TO_MENU'] = '1' }
    $process = [Diagnostics.Process]::Start($launch)
    Write-Output ("Font runtime process {0}; report {1}" -f $process.Id,(Join-Path $directory 'report.json'))
    $deadline = [DateTime]::UtcNow.AddSeconds($FiniteWaitSeconds)
    while (!$process.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 500; $process.Refresh() }
    if (!$process.HasExited) { throw 'Font runtime check exceeded its finite deadline.' }
    $exitCode = $process.ExitCode
}
finally {
    if ($null -ne $process) {
        if (!$process.HasExited) { Stop-Process -Id $process.Id }
        $process.Dispose()
    }
    if ($ownsHelper -and (Test-Path -LiteralPath $destination)) { Remove-Item -LiteralPath $destination }
}
$report = Join-Path $directory 'report.json'
$preExitReport = Join-Path $directory 'pre-exit-report.json'
if (!(Test-Path -LiteralPath $preExitReport -PathType Leaf)) { throw "Font runtime report missing: $preExitReport" }
$result = Get-Content -LiteralPath $preExitReport -Raw | ConvertFrom-Json
$sessionLogs = @(Get-ChildItem -LiteralPath (Join-Path $GameRoot 'Logs') -File -Filter '*.log' | Where-Object { $_.Name -ne '_latest.log' -and $_.FullName -notin $logsBefore })
if ($sessionLogs.Count -ne 1) { throw 'Expected one new IPA session log for this font check.' }
Copy-Item -LiteralPath $sessionLogs[0].FullName -Destination (Join-Path $directory 'game.log')
$fontErrors = @(Select-String -LiteralPath $sessionLogs[0].FullName -Pattern '^\[(ERROR|CRITICAL).*\| Mod UI Language\]' | ForEach-Object Line)
$playerMissing = @()
if (Test-Path -LiteralPath $playerLog -PathType Leaf) {
    $playerMissing = @(Select-String -LiteralPath $playerLog -Pattern 'The character with Unicode value|Unicode.*not found.*font' | ForEach-Object Line)
}
$playerMissing | ConvertTo-Json -AsArray | Set-Content -LiteralPath (Join-Path $directory 'missing-glyph-warnings.json') -Encoding utf8NoBOM
$result | Add-Member -NotePropertyName completeLogMissingGlyphCount -NotePropertyValue $playerMissing.Count
$result | Add-Member -NotePropertyName completeLogFontErrors -NotePropertyValue $fontErrors
$result | Add-Member -NotePropertyName processExitCode -NotePropertyValue $exitCode
$result.passed = $result.passed -and $exitCode -eq 0 -and $playerMissing.Count -eq 0 -and $fontErrors.Count -eq 0
$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $report -Encoding utf8NoBOM
if (!$result.passed) { throw "Font runtime check failed: $report; missing glyphs: $($playerMissing.Count); font errors: $($fontErrors.Count)" }
Write-Output ("Passed {0} font checks in {1:N1}s; missing glyphs: 0; font errors: 0. Report: {2}" -f $result.checks.Count,$result.elapsed,$report)
