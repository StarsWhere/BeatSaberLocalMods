[CmdletBinding()]
param(
    [string]$GameRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputDirectory,
    [string]$UiCaptionContracts,
    [string[]]$Focus = @(),
    [string]$InventoryPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$GameRoot = [IO.Path]::GetFullPath($GameRoot)
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $GameRoot '.tmp/all-mod-ui/sweep'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$inventoryPathExplicit = ![string]::IsNullOrWhiteSpace($InventoryPath)
$captionContractsExplicit = ![string]::IsNullOrWhiteSpace($UiCaptionContracts)
if ($inventoryPathExplicit) { $InventoryPath = [IO.Path]::GetFullPath($InventoryPath) }
if ($captionContractsExplicit) { $UiCaptionContracts = [IO.Path]::GetFullPath($UiCaptionContracts) }

$runtimeScript = Join-Path $PSScriptRoot 'test_local_effect_mods_runtime.ps1'
$inventoryScript = Join-Path $PSScriptRoot 'get_installed_mod_ui_inventory.ps1'
if (!(Test-Path -LiteralPath $runtimeScript -PathType Leaf)) { throw "Runtime script not found: $runtimeScript" }
if (!(Test-Path -LiteralPath $inventoryScript -PathType Leaf)) { throw "Inventory script not found: $inventoryScript" }

[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$pwshCommand = Get-Command pwsh -ErrorAction Stop | Select-Object -First 1
$pwsh = [string]$pwshCommand.Source
if ([string]::IsNullOrWhiteSpace($pwsh)) { $pwsh = [string]$pwshCommand.Definition }
if ([string]::IsNullOrWhiteSpace($pwsh)) { throw 'pwsh executable path could not be resolved.' }
$powerShell = $pwsh
$runToken = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff', [Globalization.CultureInfo]::InvariantCulture)

function Read-JsonFile {
    param([string]$Path)
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
    catch { return [pscustomobject]@{ _readError = $_.Exception.Message; _path = $Path } }
}

function Get-StringValue {
    param([object]$Value)
    if ($null -eq $Value) { return '' }
    return [Convert]::ToString($Value, [Globalization.CultureInfo]::InvariantCulture)
}

function Test-JsonReadError {
    param([object]$Value)
    if ($null -eq $Value) { return $false }
    foreach ($property in $Value.PSObject.Properties) {
        if ($property.Name -eq '_readError') { return $true }
    }
    return $false
}

function Get-ObjectProperty {
    param(
        [object]$Object,
        [string]$Name
    )
    if ($null -eq $Object) { return $null }
    foreach ($property in $Object.PSObject.Properties) {
        if ($property.Name -eq $Name) { return $property.Value }
    }
    return $null
}

function Get-SafeSlug {
    param([string]$Value)
    $slug = ($Value -replace '[^A-Za-z0-9_.-]', '_').Trim('_')
    if ([string]::IsNullOrWhiteSpace($slug)) { return 'batch' }
    return $slug.Substring(0, [Math]::Min(80, $slug.Length))
}

function Get-PathFromRuntimeOutput {
    param([string[]]$Lines)
    $joined = $Lines -join "`n"
    $matches = [regex]::Matches($joined, '(?m)^Runtime process\s+(?<pid>\d+);\s+report\s+(?<path>.+?)\s*$')
    if ($matches.Count -eq 0) { return $null }
    return $matches[$matches.Count - 1].Groups['path'].Value.Trim()
}

function Copy-RuntimeDirectory {
    param(
        [string]$Source,
        [string]$Destination
    )
    if ([string]::IsNullOrWhiteSpace($Source)) { return $null }
    $resolved = [IO.Path]::GetFullPath($Source)
    if (!(Test-Path -LiteralPath $resolved -PathType Container)) { return $null }
    $outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
    $outputRootRoot = [IO.Path]::GetPathRoot($outputRoot)
    if ($outputRoot.Length -gt $outputRootRoot.Length) { $outputRoot = $outputRoot.TrimEnd([char[]]@('\', '/')) }
    $destinationFull = [IO.Path]::GetFullPath($Destination)
    $destinationRoot = [IO.Path]::GetPathRoot($destinationFull)
    $insidePrefix = if ($outputRoot.EndsWith('\') -or $outputRoot.EndsWith('/')) { $outputRoot } else { $outputRoot + [IO.Path]::DirectorySeparatorChar }
    if ($destinationFull -eq $outputRoot -or !$destinationFull.StartsWith($insidePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing runtime copy destination outside OutputDirectory: $destinationFull"
    }
    if ($destinationFull -eq $destinationRoot) { throw "Refusing runtime copy destination at filesystem root: $destinationFull" }
    if (Test-Path -LiteralPath $destinationFull) { throw "Refusing to overwrite existing runtime batch: $destinationFull" }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $destinationFull)) | Out-Null
    Copy-Item -LiteralPath $resolved -Destination $destinationFull -Recurse
    return $destinationFull
}

function Get-ActualUiCounts {
    param([object]$Coverage)
    if ($null -eq $Coverage -or (Test-JsonReadError $Coverage)) {
        return [ordered]@{ pages = $null; modals = $null; hints = $null; blocked = $null; blockedPages = $null }
    }
    $pageRows = @(Get-ObjectProperty $Coverage 'pages')
    $modalRows = @(Get-ObjectProperty $Coverage 'modals')
    $hintRows = @(Get-ObjectProperty $Coverage 'hoverHints')
    $pages = @($pageRows | Where-Object { (Get-ObjectProperty $_ 'status') -in @('verified', 'partial') }).Count
    $modals = @($modalRows | Where-Object { (Get-ObjectProperty $_ 'status') -in @('verified', 'partial') }).Count
    $nativeHints = @($hintRows | Where-Object {
        (Get-ObjectProperty $_ 'kind') -eq 'native-hover' -and
        (Get-ObjectProperty $_ 'status') -in @('verified', 'partial')
    })
    $hints = @($nativeHints | Group-Object {
        '{0}`u001f{1}' -f (Get-StringValue (Get-ObjectProperty $_ 'scope')), (Get-StringValue (Get-ObjectProperty $_ 'path'))
    }).Count
    $summary = Get-ObjectProperty $Coverage 'summary'
    $blocked = @(Get-ObjectProperty $Coverage 'blocked').Count
    $blockedPageValue = Get-ObjectProperty $summary 'blockedPages'
    $blockedPages = if ($null -ne $blockedPageValue) { [int]$blockedPageValue } else { @($pageRows | Where-Object { (Get-ObjectProperty $_ 'status') -eq 'blocked' }).Count }
    return [ordered]@{ pages = $pages; modals = $modals; hints = $hints; blocked = $blocked; blockedPages = $blockedPages }
}

function Get-ContractCounts {
    param(
        [object]$CaptionCoverage,
        [object]$LocalizationAudit,
        [object]$Report
    )
    $captionChecks = @()
    if ($null -ne $Report -and !(Test-JsonReadError $Report)) {
        $captionChecks = @(Get-ObjectProperty $Report 'checks' | Where-Object { (Get-ObjectProperty $_ 'name') -like 'all-mod-ui-*' })
    }
    $resourceRows = @()
    if ($null -ne $CaptionCoverage -and !(Test-JsonReadError $CaptionCoverage)) {
        $resourceRows = @(Get-ObjectProperty $CaptionCoverage 'resources')
    }
    $parseFailed = @($resourceRows | Where-Object { (Get-ObjectProperty $_ 'status') -eq 'parse-failed' }).Count
    return [ordered]@{
        captionEntries = if ($null -ne $CaptionCoverage -and $null -ne (Get-ObjectProperty $CaptionCoverage 'captions')) { [int](Get-ObjectProperty $CaptionCoverage 'captions') } else { $null }
        resources = $resourceRows.Count
        parseFailed = $parseFailed
        localizationFindings = if ($null -ne $LocalizationAudit -and $null -ne (Get-ObjectProperty $LocalizationAudit 'findings')) { @(Get-ObjectProperty $LocalizationAudit 'findings').Count } else { $null }
        checks = @($captionChecks | ForEach-Object { [ordered]@{ name = Get-StringValue (Get-ObjectProperty $_ 'name'); passed = [bool](Get-ObjectProperty $_ 'passed'); detail = Get-StringValue (Get-ObjectProperty $_ 'detail') } })
    }
}

function Get-ProcessGone {
    param([int]$Seconds = 30)
    for ($i = 0; $i -lt $Seconds; $i++) {
        if (@(Get-Process -Name 'Beat Saber' -ErrorAction SilentlyContinue).Count -eq 0) { return $true }
        Start-Sleep -Seconds 1
    }
    return @(Get-Process -Name 'Beat Saber' -ErrorAction SilentlyContinue).Count -eq 0
}

if (!$inventoryPathExplicit) {
    $inventoryDirectory = Join-Path $OutputDirectory ('inventory-' + $runToken)
    $inventorySuffix = 0
    while (Test-Path -LiteralPath $inventoryDirectory) {
        $inventorySuffix++
        $inventoryDirectory = Join-Path $OutputDirectory ('inventory-' + $runToken + '-' + $inventorySuffix)
    }
    [IO.Directory]::CreateDirectory($inventoryDirectory) | Out-Null
    $inventoryOutput = @(& $pwsh -NoProfile -File $inventoryScript -GameRoot $GameRoot -OutputDirectory $inventoryDirectory 2>&1 | ForEach-Object { [string]$_ })
    if ($LASTEXITCODE -ne 0) { throw ('Installed UI inventory generation failed: ' + ($inventoryOutput -join "`n")) }
    $InventoryPath = Join-Path $inventoryDirectory 'installed-mod-ui.json'
}
if (!(Test-Path -LiteralPath $InventoryPath -PathType Leaf)) { throw "Installed UI inventory not found: $InventoryPath" }
$InventoryPath = [IO.Path]::GetFullPath($InventoryPath)
if (!$captionContractsExplicit) {
    $UiCaptionContracts = Join-Path (Split-Path -Parent $InventoryPath) 'dynamic-captions.json'
}
$UiCaptionContracts = [IO.Path]::GetFullPath($UiCaptionContracts)
$inventory = Read-JsonFile $InventoryPath
if ($null -eq $inventory -or (Test-JsonReadError $inventory) -or $null -eq (Get-ObjectProperty $inventory 'plugins')) { throw "Installed UI inventory is invalid: $InventoryPath" }

$mainOwners = @('CustomAvatar', 'BetterSongSearch', 'Camera2', 'Counters+', 'CustomSabersLite', 'HitScoreVisualizer', 'MorePlaylists')
$allFocus = @('LocalModUI', 'Settings') + @($mainOwners | ForEach-Object { 'Main:' + $_ }) + @('Gameplay')
$requestedFocus = @(if ($Focus.Count -eq 0) { $allFocus } else { $Focus })
foreach ($item in $requestedFocus) {
    if ($item -notin $allFocus) { throw "Unsupported Focus '$item'. Supported values: $($allFocus -join ', ')" }
}
$installedAssemblies = @((Get-ObjectProperty $inventory 'plugins') | ForEach-Object { Get-StringValue (Get-ObjectProperty $_ 'assembly') } | Where-Object { $_ })

$noIndependentPageAssemblies = @(
    'AudioLink', 'CustomJSONData', 'MappingExtensions', 'NoodleExtensions',
    'SabersCore', 'SongPackageIO', 'Camera2UrpFix'
)
$uiEntryCallPattern = 'GameplaySetup::AddTab|MenuButtons::RegisterButton|BSMLSettings::AddSettingsMenu'
$classifications = @(
    foreach ($plugin in (Get-ObjectProperty $inventory 'plugins')) {
        $assembly = Get-StringValue (Get-ObjectProperty $plugin 'assembly')
        $resources = @(Get-ObjectProperty $plugin 'resources' | Where-Object { $null -ne $_ })
        $uiTypes = @(Get-ObjectProperty $plugin 'uiTypes' | Where-Object { $null -ne $_ })
        $registrations = @(Get-ObjectProperty $plugin 'registrations' | Where-Object { $null -ne $_ })
        $registeredEntries = @($registrations | Where-Object {
            (Get-StringValue (Get-ObjectProperty $_ 'call')) -match $uiEntryCallPattern
        })
        $explicitNoIndependentPage = $assembly -in $noIndependentPageAssemblies
        $independentPage = !$explicitNoIndependentPage -and ($resources.Count -gt 0 -or $registeredEntries.Count -gt 0)
        $reason = if ($explicitNoIndependentPage) {
            'explicitly classified as a backend/library owner with no independent page; emitted hint captions may still be consumed by other UI'
        }
        elseif ($independentPage) {
            'embedded BSML resources or actual registered UI entry calls present; uiTypes alone was not used'
        }
        else {
            'no embedded BSML resources or actual registered UI entry calls; UI namespace/types alone do not establish an independent page'
        }
        [ordered]@{
            assembly = $assembly
            file = Get-StringValue (Get-ObjectProperty $plugin 'file')
            independentPage = $independentPage
            independentUi = $independentPage
            pageClass = if ($explicitNoIndependentPage) { 'no-independent-page' } elseif ($independentPage) { 'resource-or-registered-entry' } else { 'no-independent-page' }
            bsmlResources = $resources.Count
            uiTypes = $uiTypes.Count
            registrations = $registrations.Count
            registeredUiEntries = $registeredEntries.Count
            reason = $reason
        }
    }
)

$batches = [Collections.Generic.List[object]]::new()
$skipped = [Collections.Generic.List[object]]::new()
$aggregatePassed = $true
$startedUtc = [DateTime]::UtcNow

for ($index = 0; $index -lt $requestedFocus.Count; $index++) {
    $focusValue = $requestedFocus[$index]
    if ($focusValue.StartsWith('Main:', [StringComparison]::Ordinal)) {
        $owner = $focusValue.Substring(5)
        if ($owner -notin $installedAssemblies) {
            $skipped.Add([ordered]@{ focus = $focusValue; status = 'skipped-missing-owner'; owner = $owner; reason = 'assembly is absent from installed UI inventory' })
            continue
        }
    }

    $batchToken = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff', [Globalization.CultureInfo]::InvariantCulture)
    $batchName = ('{0:D2}-{1}-{2}' -f $index, (Get-SafeSlug $focusValue), $batchToken)
    $batchDirectory = Join-Path $OutputDirectory $batchName
    $batchSuffix = 0
    while (Test-Path -LiteralPath $batchDirectory) {
        $batchSuffix++
        $batchDirectory = Join-Path $OutputDirectory ('{0:D2}-{1}-{2}-{3}' -f $index, (Get-SafeSlug $focusValue), $batchToken, $batchSuffix)
    }
    [IO.Directory]::CreateDirectory($batchDirectory) | Out-Null
    $invocationLog = Join-Path $batchDirectory 'invocation.log'
    $batchStarted = [DateTime]::UtcNow
    $lines = @()
    $exitCode = $null
    $runtimeSource = $null
    $runtimeCopy = $null
    $batchError = $null
    $isolationPassed = @(Get-Process -Name 'Beat Saber' -ErrorAction SilentlyContinue).Count -eq 0

    if (!$isolationPassed) {
        $batchError = 'Beat Saber is already running before this batch; refusing to overlap processes.'
    }
    else {
        try {
            $runtimeArgs = @('-NoProfile', '-File', $runtimeScript, '-GameRoot', $GameRoot, '-FullModUiAudit', '-ModUiAuditFocus', $focusValue, '-HideAvatarForCapture', '-NeutralSongFilters')
            if (Test-Path -LiteralPath $UiCaptionContracts -PathType Leaf) {
                $runtimeArgs += @('-UiCaptionContracts', $UiCaptionContracts)
            }
            else {
                $lines += 'Caption contract file not found; runtime invocation omitted -UiCaptionContracts: ' + $UiCaptionContracts
            }
            $lines += @(& $powerShell @runtimeArgs 2>&1 | ForEach-Object { [string]$_ })
            $exitCode = $LASTEXITCODE
        }
        catch {
            $batchError = $_.Exception.Message
        }
    }
    $lines | Set-Content -LiteralPath $invocationLog -Encoding utf8
    if ($null -eq $exitCode) { $exitCode = 1 }
    $runtimeSource = Get-PathFromRuntimeOutput $lines
    if ([string]::IsNullOrWhiteSpace($runtimeSource)) {
        $runtimeCandidates = @(Get-ChildItem -LiteralPath (Join-Path $GameRoot '.tmp/effect-mods-1452/runtime') -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTimeUtc -ge $batchStarted.AddSeconds(-2) } | Sort-Object LastWriteTimeUtc -Descending)
        if ($runtimeCandidates.Count -gt 0) { $runtimeSource = $runtimeCandidates[0].FullName }
    }
    if ($runtimeSource) {
        try { $runtimeCopy = Copy-RuntimeDirectory $runtimeSource (Join-Path $batchDirectory 'runtime') }
        catch { $batchError = (($batchError, 'Could not preserve runtime directory: ' + $_.Exception.Message) | Where-Object { $_ }) -join ' | ' }
    }
    $runtimeReadRoot = if ($runtimeCopy) { $runtimeCopy } elseif ($runtimeSource -and (Test-Path -LiteralPath $runtimeSource)) { $runtimeSource } else { $null }
    $reportPath = if ($runtimeReadRoot) { Join-Path $runtimeReadRoot 'report.json' } else { $null }
    $coveragePath = if ($runtimeReadRoot) { Join-Path $runtimeReadRoot 'mod-ui-coverage.json' } else { $null }
    $captionCoveragePath = if ($runtimeReadRoot) { Join-Path $runtimeReadRoot 'mod-ui-caption-coverage.json' } else { $null }
    $localizationAuditPath = if ($runtimeReadRoot) { Join-Path $runtimeReadRoot 'localization-audit.json' } else { $null }
    $report = if ($reportPath) { Read-JsonFile $reportPath } else { $null }
    $coverage = if ($coveragePath) { Read-JsonFile $coveragePath } else { $null }
    $captionCoverage = if ($captionCoveragePath) { Read-JsonFile $captionCoveragePath } else { $null }
    $localizationAudit = if ($localizationAuditPath) { Read-JsonFile $localizationAuditPath } else { $null }
    $actual = Get-ActualUiCounts $coverage
    $contracts = Get-ContractCounts $captionCoverage $localizationAudit $report
    $reportPassed = $null -ne $report -and !(Test-JsonReadError $report) -and [bool](Get-ObjectProperty $report 'passed')
    $batchPassed = $exitCode -eq 0 -and $reportPassed -and ($actual.blockedPages -eq 0) -and ($actual.blocked -eq 0) -and $null -ne $coverage
    if ($batchError) { $batchPassed = $false }
    if (!$isolationPassed) { $batchPassed = $false }
    if (!$batchPassed) { $aggregatePassed = $false }
    if (!(Get-ProcessGone -Seconds 30)) {
        $batchPassed = $false
        $aggregatePassed = $false
        $batchError = (($batchError, 'Beat Saber process remained after batch; later batches were not allowed to overlap.') | Where-Object { $_ }) -join ' | '
    }
    $batches.Add([ordered]@{
        focus = $focusValue
        status = if ($batchPassed) { 'passed' } else { 'failed' }
        passed = $batchPassed
        exitCode = $exitCode
        startedUtc = $batchStarted.ToString('o')
        finishedUtc = [DateTime]::UtcNow.ToString('o')
        invocationLog = $invocationLog
        runtimeSource = $runtimeSource
        runtimeDirectory = $runtimeCopy
        report = $reportPath
        coverage = $coveragePath
        captionCoverage = $captionCoveragePath
        localizationAudit = $localizationAuditPath
        audit = $localizationAuditPath
        actualUi = $actual
        captionContracts = $contracts
        reportPassed = $reportPassed
        isolationPassed = $isolationPassed
        error = $batchError
    })
}

$totals = [ordered]@{
    actualPages = @($batches | ForEach-Object { $_.actualUi.pages } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
    actualModals = @($batches | ForEach-Object { $_.actualUi.modals } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
    actualHints = @($batches | ForEach-Object { $_.actualUi.hints } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
    blocked = @($batches | ForEach-Object { $_.actualUi.blocked } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
    blockedPages = @($batches | ForEach-Object { $_.actualUi.blockedPages } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
    contractEntries = @($batches | ForEach-Object { $_.captionContracts.captionEntries } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
    contractFindings = @($batches | ForEach-Object { $_.captionContracts.localizationFindings } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
    failedBatches = @($batches | Where-Object { !$_.passed }).Count
    skippedBatches = $skipped.Count
}
$aggregate = [ordered]@{
    schemaVersion = 1
    kind = 'all-mod-ui-runtime-sweep'
    gameBuild = (Get-Content -LiteralPath (Join-Path $GameRoot 'BeatSaberVersion.txt') -Raw).Trim()
    startedUtc = $startedUtc.ToString('o')
    finishedUtc = [DateTime]::UtcNow.ToString('o')
    gameRoot = $GameRoot
    outputDirectory = $OutputDirectory
    inventory = $InventoryPath
    captionContracts = $UiCaptionContracts
    requestedFocus = $requestedFocus
    batches = @($batches)
    skipped = @($skipped)
    uiClassification = @($classifications)
    totals = $totals
    passed = $aggregatePassed
}
$aggregatePath = Join-Path $OutputDirectory 'aggregate.json'
$actualSnapshots = @($batches | ForEach-Object {
    if ($_.runtimeDirectory) {
        $candidate = Join-Path $_.runtimeDirectory 'mod-ui-snapshot-all.json'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $candidate }
    }
})
if ($actualSnapshots.Count -gt 0) {
    $resourceAudit = Join-Path $OutputDirectory 'resource-caption-audit.json'
    try {
        $scopeArgs = @()
        if ($requestedFocus.Count -eq 1 -and $requestedFocus[0] -eq 'LocalModUI') {
            foreach ($scope in @('LocalModUI','NoteMotionControl','ActivityEnergyCounter','RoomCalibration','ModUiChinese','VoiceInput','BeatSage','SongDuplicateCleaner','AdvancedSongFilters')) { $scopeArgs += @('--scope',$scope) }
        }
        & python (Join-Path $PSScriptRoot 'audit_mod_ui_localization.py') @actualSnapshots --inventory $InventoryPath --output $resourceAudit @scopeArgs
        if ($LASTEXITCODE -ne 0) { throw 'Installed resource caption inspection failed.' }
        $aggregate['resourceCaptionAudit'] = $resourceAudit
    }
    catch { $aggregate['resourceCaptionAuditError'] = $_.Exception.Message; $aggregate['passed'] = $false; $aggregatePassed = $false }
}
$aggregate | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $aggregatePath -Encoding utf8NoBOM
Write-Output ('Aggregate report: ' + $aggregatePath)
Write-Output ('Batches=' + $batches.Count + '; skipped=' + $skipped.Count + '; failed=' + $totals.failedBatches + '; actual pages=' + $totals.actualPages + '; modals=' + $totals.actualModals + '; hints=' + $totals.actualHints + '; contracts=' + $totals.contractEntries)
if (!$aggregatePassed) { exit 1 }
