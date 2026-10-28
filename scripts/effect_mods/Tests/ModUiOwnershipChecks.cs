using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    static string ModUiOwnershipScope(Component value, Type scopeType)
    {
        if (value == null || scopeType == null) return string.Empty;
        for (Transform current = value.transform; current != null; current = current.parent)
        {
            Component marker = current.GetComponent(scopeType);
            string scope = ModUiAuditString(GetMember(marker, "Scope"));
            if (!string.IsNullOrEmpty(scope)) return scope;
        }
        return string.Empty;
    }

    static string ModUiOwnershipResource(Assembly assembly, string suffix)
    {
        if (assembly == null) return string.Empty;
        string resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(value => value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(resource)) return string.Empty;
        using Stream stream = assembly.GetManifestResourceStream(resource);
        if (stream == null) return string.Empty;
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    static bool ModUiOwnershipParse(object parser, string content, GameObject parent, object host, out string detail)
    {
        detail = string.Empty;
        if (parser == null || parent == null || string.IsNullOrEmpty(content))
        {
            detail = "BSMLParser, parent, or BSML content was unavailable.";
            return false;
        }
        MethodInfo parse = parser.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == "Parse" && method.GetParameters().Length == 3)
            .FirstOrDefault(method =>
            {
                ParameterInfo[] parameters = method.GetParameters();
                return parameters[1].ParameterType == typeof(GameObject) &&
                    parameters[2].ParameterType == typeof(object) &&
                    parameters[0].ParameterType.IsAssignableFrom(typeof(XElement));
            });
        if (parse == null)
        {
            detail = "BSMLParser.Parse(XContainer,GameObject,object) was unavailable.";
            return false;
        }
        try
        {
            parse.Invoke(parser, new object[] { XElement.Parse(content), parent, host });
            detail = parse.ToString();
            return true;
        }
        catch (Exception error)
        {
            detail = ModUiAuditException(error);
            return false;
        }
    }

    static object ModUiOwnershipParser()
    {
        Type parserType = FindTrainingType("BeatSaberMarkupLanguage.BSMLParser");
        return GetStaticMember(parserType, "Instance");
    }

    static Component ModUiOwnershipMarker(GameObject value, Type markerType, string scope)
    {
        Component marker = value.AddComponent(markerType);
        FieldInfo field = AccessTools.Field(markerType, "Scope");
        field?.SetValue(marker, scope);
        return marker;
    }

    static Component[] ModUiOwnershipComponents(GameObject root, Type type)
    {
        if (root == null || type == null) return Array.Empty<Component>();
        try { return root.GetComponentsInChildren(type, true).OfType<Component>().ToArray(); }
        catch { return Array.Empty<Component>(); }
    }

    static Component ModUiOwnershipBinding(Transform transform, Type bindingType)
    {
        for (Transform current = transform; current != null; current = current.parent)
        {
            Component binding = current.GetComponent(bindingType);
            if (binding != null) return binding;
        }
        return null;
    }

    void ModUiOwnershipActivityProbe()
    {
        GameObject root = null;
        try
        {
            Type scopeType = FindTrainingType("ModUiChinese.UiScope");
            Type hostType = FindTrainingType("ActivityEnergyCounter.EnergySettingsHost");
            Type parserType = FindTrainingType("BeatSaberMarkupLanguage.BSMLParser");
            Check("mod-ui-ownership-activity-types", scopeType != null && hostType != null && parserType != null,
                "UiScope=" + (scopeType != null) + "; EnergySettingsHost=" + (hostType != null) + "; BSMLParser=" + (parserType != null));

            root = new GameObject("ModUiOwnership.ActivityRoot", typeof(RectTransform));
            root.SetActive(false);
            Component parentScope = ModUiOwnershipMarker(root, scopeType, "Counters+");
            GameObject existingObject = new GameObject("NameText", typeof(RectTransform));
            existingObject.transform.SetParent(root.transform, false);
            TextMeshProUGUI existingText = existingObject.AddComponent<TextMeshProUGUI>();
            existingText.text = "Canvas";
            int parentScopeId = parentScope.GetInstanceID();
            string existingScopeBefore = ModUiAuditString(GetMember(parentScope, "Scope"));
            HashSet<int> existingTransforms = new(root.GetComponentsInChildren<Transform>(true).Select(value => value.GetInstanceID()));

            object host = Activator.CreateInstance(hostType);
            string content = ModUiOwnershipResource(hostType.Assembly, "ActivityEnergyCounter.UI.Settings.bsml");
            bool parsed = ModUiOwnershipParse(GetStaticMember(parserType, "Instance"), content, root, host, out string parseDetail);
            Check("mod-ui-ownership-activity-parser", parsed, parseDetail);

            Component[] scopes = ModUiOwnershipComponents(root, scopeType);
            Component parentAfter = scopes.FirstOrDefault(value => value.GetInstanceID() == parentScopeId);
            Component[] newScopes = scopes.Where(value => !existingTransforms.Contains(value.transform.GetInstanceID())).ToArray();
            bool newScopeAccurate = newScopes.Length > 0 && newScopes.All(value =>
                string.Equals(ModUiAuditString(GetMember(value, "Scope")), "ActivityEnergyCounter", StringComparison.Ordinal));
            Check("mod-ui-ownership-activity-existing-scope", parentAfter != null &&
                string.Equals(ModUiAuditString(GetMember(parentAfter, "Scope")), existingScopeBefore, StringComparison.Ordinal),
                "parentScopeId=" + parentScopeId + "; scope=" + ModUiAuditString(GetMember(parentAfter, "Scope")));
            Check("mod-ui-ownership-activity-new-scope", newScopeAccurate,
                "newMarkers=" + newScopes.Length + "; scopes=" + string.Join(",", newScopes.Select(value => ModUiAuditString(GetMember(value, "Scope"))).ToArray()));

            TMP_Text[] newTexts = root.GetComponentsInChildren<TMP_Text>(true)
                .Where(value => !existingTransforms.Contains(value.transform.GetInstanceID())).ToArray();
            bool translated = newTexts.Any(value => string.Equals(value.text, "显示", StringComparison.Ordinal) &&
                string.Equals(ModUiOwnershipScope(value, scopeType), "ActivityEnergyCounter", StringComparison.Ordinal));
            Check("mod-ui-ownership-activity-caption", translated,
                "newTexts=" + string.Join(" | ", newTexts.Select(value => value.text).ToArray()));
        }
        finally
        {
            if (root != null) UnityEngine.Object.Destroy(root);
        }
    }

    void ModUiOwnershipCameraDataProbe()
    {
        GameObject literalRoot = null;
        try
        {
            Type scopeType = FindTrainingType("ModUiChinese.UiScope");
            Type bindingType = FindTrainingType("ModUiChinese.UiDataBinding");
            Type hostType = FindTrainingType("Camera2.UI.CustomSceneUIEntry");
            Type parserType = FindTrainingType("BeatSaberMarkupLanguage.BSMLParser");
            Check("mod-ui-ownership-camera-types", scopeType != null && bindingType != null && hostType != null && parserType != null,
                "UiScope=" + (scopeType != null) + "; UiDataBinding=" + (bindingType != null) + "; CustomSceneUIEntry=" + (hostType != null) + "; BSMLParser=" + (parserType != null));

            object host = Activator.CreateInstance(hostType, new object[] { null });
            // Parse the production cell binding with its real cell host. The list
            // resource belongs to CamList and needs that controller's list state.
            literalRoot = new GameObject("ModUiOwnership.CameraLiteralRoot", typeof(RectTransform));
            literalRoot.SetActive(false);
            string literal = "<vertical><text text='~name'/><text text='Switch to default scene'/></vertical>";
            bool literalParsed = ModUiOwnershipParse(GetStaticMember(parserType, "Instance"), literal, literalRoot, host, out string literalDetail);
            Check("mod-ui-ownership-camera-literal-parser", literalParsed, literalDetail);

            Component falseBinding = ModUiOwnershipComponents(literalRoot, bindingType)
                .FirstOrDefault(value => string.Equals(ModUiAuditString(GetMember(value, "Binding")), "name", StringComparison.Ordinal) &&
                    !ModUiAuditReadBool(GetMember(value, "IsData"), string.Empty, true));
            TMP_Text[] literalTexts = literalRoot.GetComponentsInChildren<TMP_Text>(true);
            TMP_Text builtinCaption = literalTexts.FirstOrDefault(value => value.text == "切换到默认场景");
            TMP_Text boundCaption = falseBinding == null ? null : falseBinding.GetComponent<TMP_Text>();
            boundCaption ??= literalTexts.FirstOrDefault(value => ModUiOwnershipBinding(value.transform, bindingType) == falseBinding);
            string boundScope = ModUiOwnershipScope(boundCaption, scopeType);
            string builtinScope = ModUiOwnershipScope(builtinCaption, scopeType);
            Check("mod-ui-ownership-camera-isdata-false", falseBinding != null,
                "The null-name Camera2 CustomSceneUIEntry did not produce a UiDataBinding with IsData=false.");
            Check("mod-ui-ownership-camera-data-scope", falseBinding != null && !string.Equals(boundScope, "ModUiChinese", StringComparison.Ordinal) &&
                string.Equals(boundScope, "Camera2", StringComparison.Ordinal),
                "boundScope=" + boundScope);
            Check("mod-ui-ownership-camera-builtin-caption", builtinCaption != null &&
                string.Equals(builtinScope, "Camera2", StringComparison.Ordinal),
                "text=" + (builtinCaption == null ? "<missing>" : builtinCaption.text) + "; scope=" + builtinScope);
        }
        finally
        {
            if (literalRoot != null) UnityEngine.Object.Destroy(literalRoot);
        }
    }

    void ModUiOwnershipOnEnableProbe()
    {
        GameObject root = null;
        try
        {
            Type scopeType = FindTrainingType("ModUiChinese.UiScope");
            Check("mod-ui-ownership-onenable-types", scopeType != null, "UiScope type is unavailable.");
            root = new GameObject("ModUiOwnership.OnEnableRoot", typeof(RectTransform));
            root.SetActive(false);
            TextMeshProUGUI text = root.AddComponent<TextMeshProUGUI>();
            text.text = "Force Zen Mode Walls";
            Component marker = ModUiOwnershipMarker(root, scopeType, "Chroma");
            bool sourceBeforeActivation = string.Equals(text.text, "Force Zen Mode Walls", StringComparison.Ordinal);
            root.SetActive(true);
            Check("mod-ui-ownership-onenable-first-frame", sourceBeforeActivation &&
                string.Equals(text.text, "强制启用禅模式墙", StringComparison.Ordinal) &&
                string.Equals(ModUiAuditString(GetMember(marker, "Scope")), "Chroma", StringComparison.Ordinal),
                "before=" + sourceBeforeActivation + "; after=" + text.text);
        }
        finally
        {
            if (root != null) UnityEngine.Object.Destroy(root);
        }
    }

    static string ModUiOwnershipLocalizedKey(Component component)
    {
        return ModUiAuditString(GetMember(component, "Key"), GetMember(component, "key"));
    }

    static List<Component> ModUiOwnershipDisabledInherited(Type translatorType)
    {
        List<Component> result = new();
        if (translatorType == null) return result;
        FieldInfo field = AccessTools.Field(translatorType, "disabledInheritedLocalization");
        if (field?.GetValue(null) is not IEnumerable values) return result;
        foreach (object value in values)
        {
            Component component = value as Component;
            if (component == null || component.GetType().FullName.IndexOf("LocalizedTextMeshPro", StringComparison.Ordinal) < 0) continue;
            if (!string.Equals(ModUiOwnershipLocalizedKey(component), "BUTTON_SETTINGS", StringComparison.Ordinal)) continue;
            if (component is Behaviour behaviour && !behaviour.enabled) result.Add(component);
        }
        return result;
    }

    static string ModUiOwnershipComponentRecord(Component component)
    {
        if (component == null) return "<missing>";
        TMP_Text text = component.GetComponent<TMP_Text>();
        return component.GetType().FullName + "#" + component.GetInstanceID() +
            "; key=" + ModUiOwnershipLocalizedKey(component) +
            "; enabled=" + (component is Behaviour behaviour && behaviour.enabled) +
            "; caption=" + (text == null ? "<no-tmp>" : text.text) +
            "; path=" + ModUiAuditPath(component.transform);
    }

    static TMP_Text ModUiOwnershipExistingText(Type scopeType, Component excluded)
    {
        try
        {
            return Resources.FindObjectsOfTypeAll<TMP_Text>()
                .Where(value => value != null && value != excluded && value.gameObject.scene.IsValid() &&
                    value.gameObject.activeInHierarchy && !string.IsNullOrEmpty(ModUiOwnershipScope(value, scopeType)))
                .FirstOrDefault();
        }
        catch { return null; }
    }

    static List<object> ModUiOwnershipCapture(Type translatorType)
    {
        if (translatorType == null) return new List<object>();
        MethodInfo capture = translatorType.GetMethod("Capture", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (capture?.Invoke(null, null) is not IEnumerable values) return new List<object>();
        return values.Cast<object>().Where(value => value != null).ToList();
    }

    static object ModUiOwnershipProtectedData(List<object> values, string path = null)
    {
        return values.FirstOrDefault(value => ModUiAuditReadBool(GetMember(value, "protectedData"), string.Empty, false) &&
            (string.IsNullOrEmpty(path) || string.Equals(ModUiAuditString(GetMember(value, "path")), path, StringComparison.Ordinal)));
    }

    static bool ModUiOwnershipSameProtectedData(object before, object after)
    {
        return before != null && after != null &&
            string.Equals(ModUiAuditString(GetMember(before, "path")), ModUiAuditString(GetMember(after, "path")), StringComparison.Ordinal) &&
            string.Equals(ModUiAuditString(GetMember(before, "text")), ModUiAuditString(GetMember(after, "text")), StringComparison.Ordinal) &&
            string.Equals(ModUiAuditString(GetMember(before, "source")), ModUiAuditString(GetMember(after, "source")), StringComparison.Ordinal) &&
            string.Equals(ModUiAuditString(GetMember(before, "scope")), ModUiAuditString(GetMember(after, "scope")), StringComparison.Ordinal) &&
            ModUiAuditReadBool(GetMember(after, "protectedData"), string.Empty, false);
    }

    IEnumerator ModUiOwnershipReloadProbe()
    {
        GameObject root = null;
        try
        {
            Type translatorType = FindTrainingType("ModUiChinese.UiTranslator");
            Type parserType = FindTrainingType("BeatSaberMarkupLanguage.BSMLParser");
            Type scopeType = FindTrainingType("ModUiChinese.UiScope");
            Check("mod-ui-ownership-reload-types", translatorType != null && parserType != null && scopeType != null,
                "UiTranslator=" + (translatorType != null) + "; BSMLParser=" + (parserType != null) + "; UiScope=" + (scopeType != null));

            List<Component> before = ModUiOwnershipDisabledInherited(translatorType);
            HashSet<int> beforeIds = new(before.Select(value => value.GetInstanceID()));
            root = new GameObject("ModUiOwnership.ReloadRoot", typeof(RectTransform));
            root.SetActive(false);
            object parser = GetStaticMember(parserType, "Instance");
            string[] fragments =
            {
                "<vertical><button text='Settings'/><text text='Settings'/></vertical>",
                "<vertical><clickable-text text='Settings'/></vertical>"
            };
            List<string> parseDetails = new();
            foreach (string fragment in fragments)
            {
                if (ModUiOwnershipParse(parser, fragment, root, null, out string detail)) parseDetails.Add(detail);
                else parseDetails.Add("blocked: " + detail);
            }

            List<Component> afterParse = ModUiOwnershipDisabledInherited(translatorType);
            Component inherited = afterParse.FirstOrDefault(value => !beforeIds.Contains(value.GetInstanceID()));
            bool parsedOwnComponent = inherited != null;
            if (!parsedOwnComponent) inherited = before.FirstOrDefault();
            Check("mod-ui-ownership-reload-native-key", inherited != null,
                "before=" + before.Count + "; afterParse=" + afterParse.Count + "; parse=" + string.Join(" | ", parseDetails.ToArray()));
            if (inherited == null) yield break;

            string beforeRecord = ModUiOwnershipComponentRecord(inherited);
            TMP_Text witness = ModUiOwnershipExistingText(scopeType, inherited);
            string witnessScope = ModUiOwnershipScope(witness, scopeType);
            string witnessCaption = witness == null ? string.Empty : witness.text;
            List<object> dataBefore = ModUiOwnershipCapture(translatorType)
                .Where(value => ModUiAuditReadBool(GetMember(value, "protectedData"), string.Empty, false)).ToList();
            object protectedBefore = ModUiOwnershipProtectedData(dataBefore);
            string protectedPath = ModUiAuditString(GetMember(protectedBefore, "path"));
            MethodInfo reload = translatorType.GetMethod("Reload", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Check("mod-ui-ownership-reload-api", reload != null, "UiTranslator.Reload() was unavailable.");
            if (reload == null) yield break;
            reload.Invoke(null, null);
            yield return new WaitForSecondsRealtime(0.65f);
            string afterOne = ModUiOwnershipComponentRecord(inherited);
            bool firstDisabled = inherited is Behaviour firstBehaviour && !firstBehaviour.enabled;
            List<object> dataAfterOne = ModUiOwnershipCapture(translatorType);
            object protectedAfterOne = ModUiOwnershipProtectedData(dataAfterOne, protectedPath);
            reload.Invoke(null, null);
            yield return new WaitForSecondsRealtime(0.65f);
            string afterTwo = ModUiOwnershipComponentRecord(inherited);
            bool secondDisabled = inherited is Behaviour secondBehaviour && !secondBehaviour.enabled;
            List<object> dataAfterTwo = ModUiOwnershipCapture(translatorType);
            object protectedAfterTwo = ModUiOwnershipProtectedData(dataAfterTwo, protectedPath);
            Check("mod-ui-ownership-reload-keeps-disabled", firstDisabled && secondDisabled,
                "parsedOwnComponent=" + parsedOwnComponent + "; before=" + beforeRecord + "; after1=" + afterOne + "; after2=" + afterTwo);
            Check("mod-ui-ownership-reload-keeps-key", string.Equals(ModUiOwnershipLocalizedKey(inherited), "BUTTON_SETTINGS", StringComparison.Ordinal),
                afterTwo);
            Check("mod-ui-ownership-reload-protected-data", protectedBefore != null &&
                ModUiOwnershipSameProtectedData(protectedBefore, protectedAfterOne) &&
                ModUiOwnershipSameProtectedData(protectedBefore, protectedAfterTwo),
                "before=" + ModUiAuditString(GetMember(protectedBefore, "path"), "<missing>") +
                "; after1=" + ModUiAuditString(GetMember(protectedAfterOne, "text"), "<missing>") +
                "; after2=" + ModUiAuditString(GetMember(protectedAfterTwo, "text"), "<missing>"));
            Check("mod-ui-ownership-reload-all-protected-data", dataBefore.Count > 0 &&
                dataBefore.All(value => dataAfterOne.Any(after => ModUiOwnershipSameProtectedData(value, after)) &&
                    dataAfterTwo.Any(after => ModUiOwnershipSameProtectedData(value, after))),
                "Unchanged imported/user records after both reloads=" + dataBefore.Count);
            if (witness != null)
            {
                string afterWitnessScope = ModUiOwnershipScope(witness, scopeType);
                Check("mod-ui-ownership-reload-existing-scope", string.Equals(witnessScope, afterWitnessScope, StringComparison.Ordinal) &&
                    string.Equals(witnessCaption, witness.text, StringComparison.Ordinal),
                    "component=" + witness.GetInstanceID() + "; beforeScope=" + witnessScope + "; afterScope=" + afterWitnessScope +
                    "; beforeCaption=" + witnessCaption + "; afterCaption=" + witness.text);
            }
        }
        finally
        {
            if (root != null) UnityEngine.Object.Destroy(root);
        }
    }

    void ModUiOwnershipPresetNameProbe()
    {
        GameObject root = null;
        try
        {
            Type rowType = FindTrainingType("BetterSongSearch.UI.SplitViews.Presets+FilterPresetRow");
            Type bindingType = FindTrainingType("ModUiChinese.UiDataBinding");
            Check("mod-ui-imported-preset-types", rowType != null && bindingType != null, "Real preset row and data binding types");
            root = new GameObject("ModUiOwnership.PresetRow", typeof(RectTransform));
            root.SetActive(false);
            object host = Activator.CreateInstance(rowType, new object[] { "Default" });
            bool parsed = ModUiOwnershipParse(ModUiOwnershipParser(),
                "<bg><text text='~name' id='label'/><text text='Name'/></bg>", root, host, out string detail);
            Check("mod-ui-imported-preset-parser", parsed, detail);
            TMP_Text[] texts = root.GetComponentsInChildren<TMP_Text>(true);
            TMP_Text data = texts.FirstOrDefault(value => ModUiOwnershipBinding(value.transform, bindingType) != null);
            Check("mod-ui-imported-preset-name", data != null && data.text == "Default", data?.text ?? "missing data binding");
            Check("mod-ui-imported-preset-caption", texts.Any(value => value != data && value.text == "名称"),
                string.Join(" | ", texts.Select(value => value.text)));
        }
        finally { if (root != null) UnityEngine.Object.Destroy(root); }
    }

    void ModUiOwnershipRun(string name, Action probe, ref Exception firstFailure)
    {
        try
        {
            probe();
        }
        catch (Exception error)
        {
            try { Check(name + "-exception", false, ModUiAuditException(error)); }
            catch (Exception checkError) { firstFailure ??= checkError; }
        }
    }

    IEnumerator VerifyModUiOwnershipChecks()
    {
        Exception firstFailure = null;
        ModUiOwnershipRun("mod-ui-ownership-activity", ModUiOwnershipActivityProbe, ref firstFailure);
        ModUiOwnershipRun("mod-ui-ownership-camera", ModUiOwnershipCameraDataProbe, ref firstFailure);
        ModUiOwnershipRun("mod-ui-ownership-onenable", ModUiOwnershipOnEnableProbe, ref firstFailure);
        if (firstFailure != null) throw firstFailure;
        yield return null;
    }

    public IEnumerator VerifyModUiReloadChecks()
    {
        yield return ModUiOwnershipReloadProbe();
    }
}
