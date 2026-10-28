using System;
using System.Collections.Generic;
using System.Linq;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Components;
using BGLib.Polyglot;
using HarmonyLib;
using HMUI;
using UnityEngine;

namespace LocalModUI
{
    internal sealed class OwnedPage : MonoBehaviour { }

    [HarmonyPatch(typeof(BSMLParser), "Parse", new[] { typeof(string), typeof(GameObject), typeof(object) })]
    internal static class OwnedPageParsing
    {
        private static void Prefix(GameObject __1, object __2)
        {
            if (__1 == null || __2 == null || __1.GetComponent<OwnedPage>() != null) return;
            if (UiRegistry.Entries.Any(entry => entry.Page != null && ReferenceEquals(entry.Page.Host, __2)))
                __1.AddComponent<OwnedPage>();
        }
    }

    [HarmonyPatch(typeof(TabSelector), "SetSegmentedControlTexts")]
    internal static class OwnedTabCaptions
    {
        private static void Postfix(TabSelector __instance, List<Tab> __0)
        {
            if (__instance.GetComponentInParent<OwnedPage>() == null) return;
            // The game's cloned segmented-control cell carries BUTTON_SETTINGS.
            // Polyglot subscribes independently of Behaviour.enabled and can
            // overwrite the BSML caption on Start or a locale change. These
            // cells belong to our pages and their captions belong to BSML.
            foreach (var cell in __instance.TextSegmentedControl.GetComponentsInChildren<TextSegmentedControlCell>(true))
            {
                int index = cell.cellNumber;
                if (index < 0 || index >= __0.Count || !string.IsNullOrEmpty(__0[index].TabKey)) continue;
                foreach (var binding in cell.GetComponentsInChildren<LocalizedTextMeshProUGUI>(true))
                {
                    binding.enabled = false;
                    Localization.Instance.RemoveOnLocalizeEvent(binding);
                    UnityEngine.Object.Destroy(binding);
                }
                cell.text = __0[index].TabName;
            }
        }
    }
}
