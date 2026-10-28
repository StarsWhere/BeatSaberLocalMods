[CmdletBinding()]
param(
    [string]$GameRoot=(Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$GameRoot=[IO.Path]::GetFullPath($GameRoot)
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory)|Out-Null
Add-Type -Path (Join-Path $GameRoot 'Libs/Mono.Cecil.dll')

$script:DynamicCaptionCandidates=[Collections.Generic.List[object]]::new()
$script:DynamicCaptionSeen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$script:UiValueGetterMethods=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

function Get-CecilMethodName($Operand) {
    if($null -eq $Operand -or $Operand -isnot [Mono.Cecil.MethodReference]){return ''}
    try{return [string]$Operand.FullName}catch{return ''}
}

function Test-DynamicCaptionLiteral([string]$Value) {
    if([string]::IsNullOrWhiteSpace($Value)){return $false}
    $trimmed=$Value.Trim()
    if($trimmed -match '[\p{IsCJKUnifiedIdeographs}]'){return $false}
    $visible=[regex]::Replace($trimmed,'<[^>]*>','')
    $visible=[regex]::Replace($visible,'\{\d+(?::[^}]*)?\}','')
    if($visible -notmatch '[A-Za-z]'){return $false}
    if($trimmed.StartsWith('~',[StringComparison]::Ordinal) -or $trimmed.StartsWith('#',[StringComparison]::Ordinal)){return $false}
    if($trimmed -notmatch '[A-Za-z]'){return $false}
    if($trimmed -match '(?i)^(?:https?|file|jar|res|resource)://'){return $false}
    if($trimmed -match '(?i)(?:^|[\\/])[^\\/\r\n]+\.(?:dll|exe|json|bsml|png|jpg|jpeg|gif|apng|wav|ogg|zip|mp3)(?:$|[?#])'){return $false}
    if($trimmed -match '(?i)^(?:assets?|resources?|manifest)[\\/:]'){return $false}
    if($trimmed -match '^\.[A-Za-z0-9]{1,8}$'){return $false}
    if($trimmed -match '^(?:[FNDPGfndpg]\d*|[FNDPGfndpg]\d*\{\d+(?::[^}]*)?\})$' -or $trimmed -match '^\{\d+(?::[^}]*)?\}$'){return $false}
    if($trimmed -match '^(?:<[^>]*>|\[[^]]*\]|[\[\]]|[\s|,;:.\-])+$'){return $false}
    if($trimmed -match '^[A-Z0-9_]{3,}$' -and $trimmed -match '_'){return $false}
    if($trimmed -match '^(?i:NPS|BPM|NJS|HUD|UI|ID|URL|URI|API|PB|SS|Hz|kHz|kJ|kcal|MiB|MB|GB|ms|m/s|/s)$'){return $false}
    if($trimmed -match '^(?:get|set|on|off|true|false|id|name|value|text|title|key|path|source|status|error|message|options|formatter)$'){return $false}
    return $true
}

function Test-LogMethod([string]$MethodName) {
    return $MethodName -match '(?i)(?:^|[.:])(?:Log|Logger|Debug|Trace|Console|UnityEngine\.Debug)::?(?:Info|Warn|Warning|Error|Log|LogWarning|LogError|WriteLine)?'
}

function Test-FormattingMethod([string]$MethodName) {
    return $MethodName -match '(?i)(?:String::(?:Format|Concat|Join)|Text(?:MeshPro)?::SetText|::(?:Translate|Format|Message|SourceFormat|T|ToString|Concat|Join|Replace|Trim|Substring|Create|Add|Insert)\()'
}

function Test-BindingOrNonCaptionMethod([string]$MethodName) {
    return $MethodName -match '(?i)(?:EmitEvent|NotifyPropertyChanged|Find|GetComponent|GetTransform|SetActive|AddListener|Register|Unregister|Invoke|MoveNext|LoadResource|GetResource|ReadAll|WriteAll|Path::|File::|Directory::)'
}

function Test-UiTypeName([string]$TypeName) {
    return $TypeName -match '(?i)(?:\.UI(?:\.|$)|ViewController|FlowCoordinator|Settings|Menu|Modal|Popup|Dialog|Counter|Text|Cell|Panel|Screen|Host)'
}

function Test-UiSink([string]$MethodName,[string]$FullName) {
    if($MethodName -match '(?i)^set_text$' -and $FullName -match '(?i)(?:TMP_Text|TextMeshPro|UnityEngine\.UI\.Text|CurvedTextMeshPro)'){return 'text-setter'}
    if($MethodName -match '(?i)^SetText$'){return 'set-text'}
    if($MethodName -match '(?i)^SetTitle$'){return 'set-title'}
    if($FullName -match '(?i)HoverHint' -and $MethodName -match '(?i)(?:set_|Set|Show|Add)'){return 'hover-hint'}
    if($FullName -match '(?i)(?:MessageBox|ModalView)' -and $MethodName -match '(?i)(?:MessageBox|Show|Open|SetMessage|SetTitle|Create)'){return 'message-box'}
    if($MethodName -match '(?i)^(?:SetStatus|SetError|SetMessage|SetNotice|SetDescription|SetSubtitle|SetLabel|SetCaption|ShowMessage|ShowError|ShowWarning|ShowConfirmation|DisplayMessage|DisplayError|DisplayWarning)$'){return 'ui-message-method'}
    return $null
}

function Add-DynamicCaption([string]$Scope,[string]$Source,[string]$Site,[string]$Kind) {
    if(!(Test-DynamicCaptionLiteral $Source)){return}
    # This adapter inserts controls into Camera2's existing settings surface.
    if($Scope -eq 'Camera2UrpFix' -and $Site -match 'CameraSettingsControls::'){$Scope='Camera2'}
    if($Kind-eq'uivalue-bound-store' -and $Source.Trim()-match '^[a-z][A-Za-z0-9_]*$'){return}
    $key=$Scope+"`n"+$Source+"`n"+$Site+"`n"+$Kind
    if($script:DynamicCaptionSeen.Add($key)){
        $script:DynamicCaptionCandidates.Add([ordered]@{scope=$Scope;source=$Source;site=$Site;kind=$Kind})
    }
}

function Get-LdstrContext([Mono.Cecil.MethodDefinition]$Method,[int]$Index,[int]$LookAhead=5) {
    $instructions=@($Method.Body.Instructions)
    $end=[Math]::Min($instructions.Count-1,$Index+$LookAhead)
    for($i=$Index+1;$i-le$end;$i++){
        $ins=$instructions[$i]
        $code=$ins.OpCode.Code.ToString()
        if($code-eq'Ldstr'){break}
        if($code-in @('Br','Br_S','Brfalse','Brfalse_S','Brtrue','Brtrue_S','Beq','Beq_S','Bne_Un','Bne_Un_S','Switch')){return 'id'}
        if($code-notin @('Call','Callvirt','Newobj')){continue}
        $name=Get-CecilMethodName $ins.Operand
        if(Test-LogMethod $name){return 'log'}
        if($name -match '(?i)(?:EmitEvent|NotifyPropertyChanged|SetActive|AddListener)'){return 'binding'}
        if($name -match '(?i)::(?:set_text|SetText|set_title|SetTitle|set_hoverHint|SetMessage|ShowMessage|MessageBox)\('){return 'caption'}
        if($name -match '(?i)::(?:SetStatus|SetError|SetMessage|SetNotice|SetDescription|SetSubtitle|SetLabel|SetCaption|ShowMessage|ShowError|ShowWarning|ShowConfirmation|DisplayMessage|DisplayError|DisplayWarning)\('){return 'caption'}
        if($name -match '(?i)(?:String::(?:Equals|Compare|op_Equality|op_Inequality)|StartsWith|EndsWith|Contains|IsNullOrEmpty)'){return 'id'}
        if(!(Test-FormattingMethod $name) -and $name -notmatch '(?i)::(?:GetReadableChartCaption|GetCurrentChartKey)\('){return 'other'}
    }
    return 'caption'
}

function Get-ContextCaptionLiterals([Mono.Cecil.MethodDefinition]$Method,[int]$SinkIndex,[string]$Scope,[string]$Kind,[int]$LookBack=28) {
    if(!$Method.HasBody){return}
    $instructions=@($Method.Body.Instructions)
    $start=[Math]::Max(0,$SinkIndex-$LookBack)
    for($i=$SinkIndex-1;$i-ge$start;$i--){
        $ins=$instructions[$i]
        $code=$ins.OpCode.Code.ToString()
        if($code-eq'Ldstr'){
            if((Get-LdstrContext $Method $i)-eq'caption'){Add-DynamicCaption $Scope ([string]$ins.Operand) ($Method.Module.Assembly.Name.Name+'.dll#'+$Method.FullName) $Kind}
            continue
        }
        if($code -in @('Br','Br_S','Brfalse','Brfalse_S','Brtrue','Brtrue_S','Leave','Leave_S','Ret','Throw')){break}
        if($code -notin @('Call','Callvirt','Newobj')){continue}
        $name=Get-CecilMethodName $ins.Operand
        if((Test-LogMethod $name) -or (Test-BindingOrNonCaptionMethod $name)){break}
        if(Test-FormattingMethod $name){continue}
        if($name -match '(?i)(?:SetStatus|SetError|SetMessage|SetNotice|SetDescription|SetTitle|SetSubtitle|SetLabel|SetCaption|ShowMessage|ShowError|ShowWarning|ShowConfirmation|DisplayMessage|DisplayError|DisplayWarning)$'){continue}
        break
    }
}

function Get-AssignedCaptionLiterals([Mono.Cecil.MethodDefinition]$Method,[int]$StoreIndex,[string]$Scope,[string]$Kind,[int]$LookBack=18) {
    if(!$Method.HasBody){return}
    $instructions=@($Method.Body.Instructions)
    $start=[Math]::Max(0,$StoreIndex-$LookBack)
    for($i=$StoreIndex-1;$i-ge$start;$i--){
        $ins=$instructions[$i]
        $code=$ins.OpCode.Code.ToString()
        if($code-eq'Ldstr'){
            if((Get-LdstrContext $Method $i)-eq'caption'){Add-DynamicCaption $Scope ([string]$ins.Operand) ($Method.Module.Assembly.Name.Name+'.dll#'+$Method.FullName) $Kind}
            break
        }
        if($code-in @('Br','Br_S','Brfalse','Brfalse_S','Brtrue','Brtrue_S','Beq','Beq_S','Bne_Un','Bne_Un_S','Switch','Ret','Throw')){break}
        if($code-notin @('Call','Callvirt','Newobj')){continue}
        $name=Get-CecilMethodName $ins.Operand
        if((Test-LogMethod $name) -or (Test-BindingOrNonCaptionMethod $name)){break}
        if(Test-FormattingMethod $name){continue}
        break
    }
}

function Get-UiValueName($Definition) {
    foreach($attribute in @($Definition.CustomAttributes)){
        if([string]$attribute.AttributeType.FullName -notmatch '(?i)UIValue(?:Attribute)?$'){continue}
        if(@($attribute.ConstructorArguments).Count -gt 0){
            try{return [string]$attribute.ConstructorArguments[0].Value}catch{return [string]$Definition.Name}
        }
        return [string]$Definition.Name
    }
    return $null
}

function Get-ResolvedSameAssemblyMethod([Mono.Cecil.MethodReference]$Reference,[string]$AssemblyName) {
    if($null -eq $Reference){return $null}
    try{
        $resolved=$Reference.Resolve()
        if($resolved -and $resolved.Module -and $resolved.Module.Assembly -and $resolved.Module.Assembly.Name.Name-eq$AssemblyName){return $resolved}
    }catch{}
    return $null
}

function Collect-MethodLiterals([Mono.Cecil.MethodDefinition]$Method,[string]$Scope,[string]$Kind,[int]$Depth,[Collections.Generic.HashSet[string]]$Visited,[string]$AssemblyName) {
    if(!$Method -or !$Method.HasBody -or $Depth -gt 2){return}
    if(!$Visited.Add($Method.FullName)){return}
    $instructions=@($Method.Body.Instructions)
    for($index=0;$index-lt$instructions.Count;$index++){
        $instruction=$instructions[$index]
        if($instruction.OpCode.Code.ToString()-eq'Ldstr'){
            if((Get-LdstrContext $Method $index)-eq'caption'){
                Add-DynamicCaption $Scope ([string]$instruction.Operand) ($Method.Module.Assembly.Name.Name+'.dll#'+$Method.FullName) $Kind
            }
            continue
        }
        if($instruction.OpCode.Code.ToString()-notin @('Call','Callvirt','Newobj')){continue}
        $reference=$instruction.Operand
        if($reference -isnot [Mono.Cecil.MethodReference]){continue}
        $name=Get-CecilMethodName $reference
        if((Test-LogMethod $name) -or (Test-BindingOrNonCaptionMethod $name)){continue}
        $resolved=Get-ResolvedSameAssemblyMethod $reference $AssemblyName
        if($resolved -and !$script:UiValueGetterMethods.Contains($resolved.FullName) -and $resolved.DeclaringType.FullName-eq$Method.DeclaringType.FullName){
            Collect-MethodLiterals $resolved $Scope ($Kind+'-helper') ($Depth+1) $Visited $AssemblyName
        }
    }
}

function Get-CecilFieldKey($Operand) {
    if($null -eq $Operand -or $Operand -isnot [Mono.Cecil.FieldReference]){return $null}
    try{return [string]$Operand.DeclaringType.FullName+'::'+[string]$Operand.Name}catch{return $null}
}

function Collect-MethodFieldKeys([Mono.Cecil.MethodDefinition]$Method,[int]$Depth,[Collections.Generic.HashSet[string]]$Visited,[string]$AssemblyName,[Collections.Generic.HashSet[string]]$FieldKeys) {
    if(!$Method -or !$Method.HasBody -or $Depth -gt 2){return}
    if(!$Visited.Add($Method.FullName)){return}
    foreach($instruction in @($Method.Body.Instructions)){
        $code=$instruction.OpCode.Code.ToString()
        if($code-in @('Ldfld','Ldsfld','Stfld','Stsfld')){
            $fieldKey=Get-CecilFieldKey $instruction.Operand
            $field=$instruction.Operand
            if($fieldKey -and $field.FieldType.FullName-eq'System.String' -and $field.Name -match '(?i)(?:status|error|message|notice|result|caption|label|description|text|reason|warning)'){$FieldKeys.Add($fieldKey)|Out-Null}
            continue
        }
        if($code-notin @('Call','Callvirt','Newobj')){continue}
        $reference=$instruction.Operand
        if($reference -isnot [Mono.Cecil.MethodReference]){continue}
        $resolved=Get-ResolvedSameAssemblyMethod $reference $AssemblyName
        if($resolved -and !$script:UiValueGetterMethods.Contains($resolved.FullName) -and $resolved.DeclaringType.FullName-eq$Method.DeclaringType.FullName){
            Collect-MethodFieldKeys $resolved ($Depth+1) $Visited $AssemblyName $FieldKeys
        }
    }
}

function Get-ModTypes($Types) {
    foreach($type in $Types){$type; if($type.HasNestedTypes){Get-ModTypes $type.NestedTypes}}
}
$inventory=@(foreach($file in Get-ChildItem -LiteralPath (Join-Path $GameRoot 'Plugins') -File -Filter '*.dll' | Sort-Object Name){
    $assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($file.FullName)
    try{
        $metadata=$null
        $hasDevSampleBsml=$false
        $captionBindings=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $resources=@(foreach($resource in $assembly.MainModule.Resources){
            if($resource -isnot [Mono.Cecil.EmbeddedResource]){continue}
            if($resource.Name -match '(?i)manifest\.json$'){
                $reader=[IO.StreamReader]::new($resource.GetResourceStream())
                try{$metadata=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
            }
            if($resource.Name -notmatch '(?i)\.bsml$'){continue}
            if($resource.Name -match '(?i)(?:^|[._-])tests?\.bsml$'){$hasDevSampleBsml=$true}
            $directory=Join-Path $OutputDirectory ('resources/'+$assembly.Name.Name)
            [IO.Directory]::CreateDirectory($directory)|Out-Null
            $path=Join-Path $directory ($resource.Name-replace'[^A-Za-z0-9_.-]','_')
            $bytes=$resource.GetResourceData()
            $bsmlText=[Text.Encoding]::UTF8.GetString($bytes)
            foreach($binding in [regex]::Matches($bsmlText,'(?is)(?:text|hover-hint|tab-name|placeholder)\s*=\s*([''\"])\s*~([^''\"]+?)\1')){
                $captionBindings.Add(([string]$binding.Groups[2].Value).Trim())|Out-Null
            }
            [IO.File]::WriteAllBytes($path,$bytes)
            [pscustomobject]@{name=$resource.Name;path=[IO.Path]::GetRelativePath($OutputDirectory,$path).Replace('\','/');sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
        })
        $types=@(Get-ModTypes $assembly.MainModule.Types)
        $scope=[string]$assembly.Name.Name
        $dynamicTypes=@($types|Where-Object { !($scope-eq'BSML' -and $hasDevSampleBsml -and $_.FullName -match '(?i)Test') })
        $allMethods=@($dynamicTypes|ForEach-Object Methods)
        $script:UiValueGetterMethods=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($type in $dynamicTypes){
            foreach($property in @($type.Properties)){
                if($null -ne (Get-UiValueName $property) -and $null -ne $property.GetMethod){$script:UiValueGetterMethods.Add($property.GetMethod.FullName)|Out-Null}
            }
        }
        $uiValueFieldKeys=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($type in $dynamicTypes){
            $typeName=[string]$type.FullName
            foreach($property in @($type.Properties)){
                $uiValueName=Get-UiValueName $property
                if($null -eq $uiValueName){continue}
                if(!$captionBindings.Contains($uiValueName)){continue}
                $getter=$property.GetMethod
                if($null -eq $getter -or !$getter.HasBody){continue}
                if($getter.ReturnType.FullName -notmatch '(?i)(?:System\.String(?:\[\])?|System\.Collections|System\.Object|IEnumerable|IReadOnly|List|Array)'){continue}
                $visited=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                Collect-MethodLiterals $getter $scope 'uivalue-getter' 0 $visited $scope
                $fieldVisited=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                Collect-MethodFieldKeys $getter 0 $fieldVisited $scope $uiValueFieldKeys
            }
        }
        foreach($method in $allMethods){
                if(!$method.HasBody){continue}
                $declaringTypeName=[string]$method.DeclaringType.FullName
                $instructions=$method.Body.Instructions
                for($index=0;$index-lt$instructions.Count;$index++){
                    $instruction=$instructions[$index]
                    if($instruction.OpCode.Code.ToString()-notin @('Call','Callvirt','Newobj')){continue}
                    $fullName=Get-CecilMethodName $instruction.Operand
                    if([string]::IsNullOrEmpty($fullName)){continue}
                    $methodName=[string]$instruction.Operand.Name
                    $kind=Test-UiSink $methodName $fullName
                    if($null -eq $kind){continue}
                    if($kind-eq'ui-message-method' -and (!(Test-UiTypeName $declaringTypeName)) -and $fullName-notmatch'(?i)(?:MessageBox|ModalView|ViewController|FlowCoordinator)'){continue}
                    Get-ContextCaptionLiterals $method $index $scope $kind
                }
                for($index=0;$index-lt$instructions.Count;$index++){
                    $instruction=$instructions[$index]
                    if($instruction.OpCode.Code.ToString()-notin @('Stfld','Stsfld')){continue}
                    $fieldKey=Get-CecilFieldKey $instruction.Operand
                    if($fieldKey -and $uiValueFieldKeys.Contains($fieldKey)){Get-AssignedCaptionLiterals $method $index $scope 'uivalue-bound-store'}
                }
        }
        $uiTypes=@($types|Where-Object {$_.FullName-match'(^|\.)(UI|Menu|Views|ViewControllers|Settings)(\.|/)' -or ($_.BaseType -and $_.BaseType.FullName-match'(ViewController|FlowCoordinator|ModalView|Counter)$')}|ForEach-Object FullName)
        $registrations=@(foreach($type in $types){foreach($method in $type.Methods){
            if(!$method.HasBody){continue}
            $instructions=$method.Body.Instructions
            for($index=0;$index-lt$instructions.Count;$index++){
                $instruction=$instructions[$index]
                if($instruction.OpCode.Code.ToString()-notin@('Call','Callvirt','Newobj')){continue}
                if($instruction.Operand.FullName-notmatch'(AddSettingsMenu|RegisterButton|MenuButton::\.ctor|CustomCounter::\.ctor|AddTab|RegisterGameplaySetupMenu)'){continue}
                $literals=@(for($previous=[Math]::Max(0,$index-12);$previous-lt$index;$previous++){if($instructions[$previous].OpCode.Code.ToString()-eq'Ldstr'){[string]$instructions[$previous].Operand}})
                [pscustomobject]@{method=$method.FullName;call=$instruction.Operand.FullName;literals=$literals}
            }
        }})
        [pscustomobject]@{
            file=$file.Name;assembly=$assembly.Name.Name;assemblyVersion=$assembly.Name.Version.ToString()
            pluginId=$(if($metadata -and $metadata.PSObject.Properties['id']){$metadata.id}else{$null})
            pluginName=$(if($metadata -and $metadata.PSObject.Properties['name']){$metadata.name}else{$null})
            pluginVersion=$(if($metadata -and $metadata.PSObject.Properties['version']){$metadata.version}else{$null})
            sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            resources=$resources;uiTypes=$uiTypes;registrations=$registrations
        }
    }finally{$assembly.Dispose()}
})
[ordered]@{schemaVersion=1;gameBuild=(Get-Content -LiteralPath (Join-Path $GameRoot 'BeatSaberVersion.txt') -Raw).Trim();plugins=$inventory}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $OutputDirectory 'installed-mod-ui.json') -Encoding utf8NoBOM
$dynamicPath=Join-Path $OutputDirectory 'dynamic-captions.json'
@($script:DynamicCaptionCandidates|Sort-Object scope,source,site,kind)|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $dynamicPath -Encoding utf8NoBOM
Write-Output ('Installed DLLs: '+$inventory.Count+'; BSML resources: '+@($inventory|ForEach-Object resources).Count)
Write-Output ('Dynamic UI caption candidates: '+$script:DynamicCaptionCandidates.Count+'; output: '+$dynamicPath)
