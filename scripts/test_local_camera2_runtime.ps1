[CmdletBinding()]
param(
    [string]$GameRoot = (Split-Path -Parent $PSScriptRoot),
    [ValidateRange(30,100)][int]$FiniteWaitSeconds = 100,
    [string]$TaskDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$GameRoot = [IO.Path]::GetFullPath($GameRoot)
$versionPath = Join-Path $GameRoot 'BeatSaberVersion.txt'
if ((Get-Content -LiteralPath $versionPath -Raw).Trim() -ne '1.45.2_28848') {
    throw 'This runtime test targets Beat Saber 1.45.2_28848.'
}

$camera2Path = Join-Path $GameRoot 'Plugins/Camera2.dll'
$camera2Version = [Reflection.AssemblyName]::GetAssemblyName($camera2Path).Version.ToString()
if ($camera2Version -ne '0.6.119.0') {
    throw "Expected Camera2 0.6.119.0, found $camera2Version"
}
$adapterManifestPath = Join-Path $PSScriptRoot 'camera2_urp_fix/Camera2UrpFix.manifest.json'
$adapterManifest = Get-Content -LiteralPath $adapterManifestPath -Raw | ConvertFrom-Json
if ($adapterManifest.version -ne '0.3.1') {
    throw "Expected Camera2UrpFix 0.3.1, found $($adapterManifest.version)"
}

if (@(Get-Process -Name 'Beat Saber' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'An existing Beat Saber process is running.'
}

$pluginDirectory = Join-Path $GameRoot 'Plugins'
$pluginName = 'Camera2RegressionRuntime'
$pluginPath = Join-Path $pluginDirectory ($pluginName + '.dll')
if (Test-Path -LiteralPath $pluginPath -PathType Leaf) {
    throw "An existing test plugin must be resolved first: $pluginPath"
}
$otherTestPlugins = @(Get-ChildItem -LiteralPath $pluginDirectory -Filter '*.dll' -File |
    Where-Object { $_.BaseName -match 'Smoke|Regression|RuntimeChecks' -and $_.BaseName -ne $pluginName })
if ($otherTestPlugins.Count -gt 0) {
    throw ('Another smoke or regression plugin is installed: ' +
        (($otherTestPlugins | ForEach-Object { $_.Name }) -join ', '))
}

$taskRoot = if ($TaskDirectory) { [IO.Path]::GetFullPath($TaskDirectory) } else { Join-Path $GameRoot '.tmp/camera2-fix' }
$runtimeDirectory = Join-Path $taskRoot 'runtime'
$authorRoot = Join-Path $taskRoot 'tests-author'
$backupDirectory = Join-Path $authorRoot ([Guid]::NewGuid().ToString('N'))
$helperPath = Join-Path $runtimeDirectory ($pluginName + '.dll')
$reportPath = Join-Path $runtimeDirectory 'report.json'
$sourceDirectory = Join-Path $PSScriptRoot 'camera2_urp_fix/Tests'
$sourcePath = Join-Path $sourceDirectory 'RuntimeChecks.cs'
$manifestPath = Join-Path $sourceDirectory 'RuntimeChecks.manifest.json'
$managedDirectory = Join-Path $GameRoot 'Beat Saber_Data/Managed'
$compiler = 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$process = $null
$records = @()
$cameraOriginalTargets = @()

function New-BackupRecord([string]$Target, [string]$BackupRelativePath, [string]$Kind) {
    $exists = Test-Path -LiteralPath $Target -PathType Leaf
    $backup = Join-Path $backupDirectory ($BackupRelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
    if ($exists) {
        [IO.Directory]::CreateDirectory((Split-Path -Parent $backup)) | Out-Null
        Copy-Item -LiteralPath $Target -Destination $backup
    }
    [pscustomobject]@{
        Target = $Target
        Backup = $backup
        Kind = $Kind
        Existed = $exists
        Sha256 = if ($exists) { (Get-FileHash -LiteralPath $Target -Algorithm SHA256).Hash } else { $null }
    }
}

function Restore-Backups([object[]]$Items, [string[]]$OriginalCameraTargets) {
    $cameraRoot = Join-Path $GameRoot 'UserData/Camera2'
    if (Test-Path -LiteralPath $cameraRoot -PathType Container) {
        foreach ($file in @(Get-ChildItem -LiteralPath $cameraRoot -Filter '*.json' -File -Recurse)) {
            if ($file.BaseName.StartsWith('__Camera2Regression', [StringComparison]::OrdinalIgnoreCase)) {
                Remove-Item -LiteralPath $file.FullName
            }
        }
    }
    foreach ($item in $Items) {
        if ($item.Existed) {
            [IO.Directory]::CreateDirectory((Split-Path -Parent $item.Target)) | Out-Null
            Copy-Item -LiteralPath $item.Backup -Destination $item.Target
        } elseif (Test-Path -LiteralPath $item.Target -PathType Leaf) {
            Remove-Item -LiteralPath $item.Target
        }
    }
    foreach ($item in $Items) {
        if ($item.Existed) {
            if (-not (Test-Path -LiteralPath $item.Target -PathType Leaf)) {
                throw "Restore missing file: $($item.Target)"
            }
            $actual = (Get-FileHash -LiteralPath $item.Target -Algorithm SHA256).Hash
            if ($actual -ne $item.Sha256) { throw "Restore byte verification failed: $($item.Target)" }
        } elseif (Test-Path -LiteralPath $item.Target) {
            throw "Restore left a file that was originally absent: $($item.Target)"
        }
    }
}

try {
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw "Missing compiler: $compiler" }
    [IO.Directory]::CreateDirectory($runtimeDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
    foreach ($name in @('report.json', 'obstacle-urp.png', 'desktop-bars.png', 'settings-first-person.png', 'settings-third-person.png', 'settings-display.png')) {
        $path = Join-Path $runtimeDirectory $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path }
    }

    $saveDirectory = Join-Path $env:USERPROFILE 'AppData/LocalLow/Hyperbolic Magnetism/Beat Saber'
    foreach ($name in @('PlayerData.dat', 'PlayerData.dat.bak', 'settings.ini', 'settings.ini.bak')) {
        $target = Join-Path $saveDirectory $name
        $records += New-BackupRecord $target ('original/user-settings/' + $name) 'user'
    }

    $cameraRoot = Join-Path $GameRoot 'UserData/Camera2'
    if (Test-Path -LiteralPath $cameraRoot -PathType Container) {
        foreach ($file in @(Get-ChildItem -LiteralPath $cameraRoot -Filter '*.json' -File -Recurse)) {
            $gameRootPrefix = $GameRoot.TrimEnd('\', '/') + '\'
            $relative = $file.FullName.Substring($gameRootPrefix.Length).TrimStart('\', '/').Replace('\', '/')
            $cameraOriginalTargets += $file.FullName
            $records += New-BackupRecord $file.FullName ('original/' + $relative) 'camera'
        }
    }

    $references = @(
        'IPA.Loader.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.UIModule.dll',
        'UnityEngine.UI.dll', 'UnityEngine.ImageConversionModule.dll',
        'UnityEngine.ScreenCaptureModule.dll', 'Unity.RenderPipelines.Core.Runtime.dll',
        'Unity.RenderPipelines.Universal.Runtime.dll', 'netstandard.dll'
    ) | ForEach-Object { Join-Path $managedDirectory $_ }
    $references += Join-Path $GameRoot 'Libs/0Harmony.dll'
    foreach ($reference in $references) {
        if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) { throw "Missing reference: $reference" }
    }

    $arguments = @(
        '/nologo', '/target:library', '/codepage:65001', '/warnaserror', '/langversion:5',
        ('/out:' + $helperPath),
        ('/resource:' + $manifestPath + ',' + $pluginName + '.manifest.json')
    )
    $arguments += @($references | ForEach-Object { '/r:' + $_ })
    $arguments += @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' -File | Sort-Object Name | ForEach-Object { $_.FullName })
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Camera2 runtime checks compilation failed.' }

    Copy-Item -LiteralPath $helperPath -Destination $pluginPath
    $startOptions = @{
        FilePath = Join-Path $GameRoot 'Beat Saber.exe'
        WorkingDirectory = $GameRoot
        WindowStyle = 'Hidden'
        PassThru = $true
        ArgumentList = @('--fpfc', 'fpfc')
    }
    $previousOutput = [Environment]::GetEnvironmentVariable('BEATSABER_CAMERA2_TEST_OUTPUT', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('BEATSABER_CAMERA2_TEST_OUTPUT', $runtimeDirectory, 'Process')
        $process = Start-Process @startOptions
    }
    finally {
        [Environment]::SetEnvironmentVariable('BEATSABER_CAMERA2_TEST_OUTPUT', $previousOutput, 'Process')
    }
    $deadline = [DateTime]::UtcNow.AddSeconds($FiniteWaitSeconds)
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
    }
    if (-not $process.HasExited) { throw "Beat Saber runtime checks exceeded $FiniteWaitSeconds seconds." }
    $process.WaitForExit()
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
        throw "Runtime checks produced no report: $reportPath"
    }
    $result = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if (-not $result.passed -or $process.ExitCode -ne 0) {
        throw "Camera2 runtime checks failed. See $reportPath"
    }
    Write-Output "Camera2 runtime checks passed: $reportPath"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $pluginPath -PathType Leaf) { Remove-Item -LiteralPath $pluginPath }
    if ($records.Count -gt 0) { Restore-Backups $records $cameraOriginalTargets }
    if (Test-Path -LiteralPath $helperPath -PathType Leaf) { Remove-Item -LiteralPath $helperPath }
    if (Test-Path -LiteralPath $backupDirectory -PathType Container) {
        $resolvedBackup = (Resolve-Path -LiteralPath $backupDirectory).Path
        $expectedParent = [IO.Path]::GetFullPath($authorRoot).TrimEnd('\') + '\'
        if (-not $resolvedBackup.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Backup cleanup path is outside this test workspace.'
        }
        Remove-Item -LiteralPath $resolvedBackup -Recurse
    }
}
