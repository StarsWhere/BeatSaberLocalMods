using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    IEnumerator VerifyModUiCaptionContracts()
    {
        Type manager = FindTrainingType("IPA.Loader.PluginManager");
        var plugins = ((IEnumerable)GetStaticMember(manager, "EnabledPlugins")).Cast<object>().ToArray();
        var candidates = new List<(string Scope, string Source, string Site)>();
        var resources = new List<object>();
        foreach (object plugin in plugins)
        {
            Assembly assembly = GetMember(plugin, "Assembly") as Assembly;
            if (assembly == null) continue;
            string scope = assembly.GetName().Name;
            foreach (string resource in assembly.GetManifestResourceNames().Where(value => value.EndsWith(".bsml", StringComparison.OrdinalIgnoreCase)))
            {
                // The shipped BSML development samples have no installed entry point.
                if (scope == "BSML" && Regex.IsMatch(resource, @"(?:\.|-)(?:test|tests)\.bsml$", RegexOptions.IgnoreCase))
                {
                    resources.Add(new { scope, resource, status = "development-sample" });
                    continue;
                }
                string content;
                using (var reader = new StreamReader(assembly.GetManifestResourceStream(resource))) content = reader.ReadToEnd();
                try
                {
                    var xml = XElement.Parse("<audit-root>" + Regex.Replace(content, @"<\?xml.*?\?>", "") + "</audit-root>", LoadOptions.SetLineInfo);
                    int count = 0;
                    foreach (var element in xml.Descendants())
                        foreach (var attribute in element.Attributes().Where(value => value.Name.LocalName == "text" || value.Name.LocalName == "hover-hint" || value.Name.LocalName == "tab-name" || value.Name.LocalName == "placeholder"))
                        {
                            string source = attribute.Value;
                            if (String.IsNullOrWhiteSpace(source) || source.StartsWith("~", StringComparison.Ordinal) || source.StartsWith("#", StringComparison.Ordinal)) continue;
                            if (!Regex.IsMatch(source, "[A-Za-z]")) continue;
                            candidates.Add((scope, source, resource + ":" + ((System.Xml.IXmlLineInfo)attribute).LineNumber + "/@" + attribute.Name.LocalName));
                            count++;
                        }
                    resources.Add(new { scope, resource, status = "literal-captions", count });
                }
                catch (Exception error)
                {
                    resources.Add(new { scope, resource, status = "parse-failed", error = error.ToString() });
                }
            }
        }
        string dynamicPath = Environment.GetEnvironmentVariable("EFFECT_SMOKE_UI_CAPTIONS");
        if (!String.IsNullOrEmpty(dynamicPath))
        {
            var entries = JArray.Parse(File.ReadAllText(dynamicPath));
            foreach (JObject entry in entries)
            {
                if (entry["scope"]?.Type != JTokenType.String || entry["source"]?.Type != JTokenType.String || entry["site"]?.Type != JTokenType.String)
                    throw new InvalidDataException("Invalid dynamic UI caption contract");
                candidates.Add(((string)entry["scope"], (string)entry["source"], (string)entry["site"]));
            }
        }
        var snapshot = new List<object>();
        GameObject probe = new GameObject("ModUiCaptionContractProbe", typeof(RectTransform), typeof(TextMeshProUGUI));
        Type markerType = FindTrainingType("ModUiChinese.UiScope");
        var marker = probe.AddComponent(markerType);
        var scopeField = AccessTools.Field(markerType, "Scope");
        var text = probe.GetComponent<TMP_Text>();
        try
        {
            foreach (var candidate in candidates.Distinct())
            {
                scopeField.SetValue(marker, candidate.Scope);
                text.text = candidate.Source;
                string translated = text.text;
                string kind = "caption-contract";
                if (candidate.Scope == "Camera2" && (candidate.Site.Contains("WinAPI.MessageBox") || candidate.Site.Contains("Camera2.Configuration.ScenesSettings")))
                {
                    Type translator = FindTrainingType("ModUiChinese.UiTranslator");
                    object[] args = { "Camera2", candidate.Source, "Camera2" };
                    translator.GetMethod("TranslateNativeDialog").Invoke(null, args);
                    translated = args[1] as string;
                    kind = "native-dialog-arguments";
                    Type scenes = FindTrainingType("Camera2.Configuration.ScenesSettings");
                    bool hooked = scenes.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                        .SelectMany(value => value.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                        .Where(value => value.Name.StartsWith("<Load>b__", StringComparison.Ordinal) && value.ReturnType == typeof(int))
                        .Any(value => Harmony.GetPatchInfo(value)?.Owners.Contains("local.beatsaber.mod-ui-chinese") == true);
                    Check("all-mod-ui-native-dialog-hook", hooked && translated != candidate.Source, "Camera2 native dialog call and translated arguments");
                }
                snapshot.Add(new { path = candidate.Site, text = translated, source = candidate.Source, scope = candidate.Scope,
                    protectedData = false, localizationEnabled = true, scopeEnabled = true, kind });
            }
        }
        finally { probe.SetActive(false); UnityEngine.Object.Destroy(probe); }
        File.WriteAllText(Path.Combine(directory, "mod-ui-snapshot-caption-contracts.json"), JsonConvert.SerializeObject(snapshot, Formatting.Indented));
        File.WriteAllText(Path.Combine(directory, "mod-ui-caption-coverage.json"), JsonConvert.SerializeObject(new { schemaVersion = 1, resources, captions = snapshot.Count }, Formatting.Indented));
        Check("all-mod-ui-caption-resource-parsing", !resources.Any(value => Convert.ToString(GetMember(value, "status"), CultureInfo.InvariantCulture) == "parse-failed"), "Inspect mod-ui-caption-coverage.json for resource-level details");
        yield return VerifyModUiOwnershipChecks();
        yield return null;
    }
}
