using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    IEnumerator VerifyModUiImportedSaberNames()
    {
        Component cell = null;
        try
        {
            Type cellType = FindTrainingType("CustomSabersLite.Menu.Components.SaberListCell");
            Type infoType = FindTrainingType("CustomSabersLite.Models.ListInfoCellInfo");
            Type defaultType = FindTrainingType("CustomSabersLite.Models.DefaultSaberValue");
            Type hashType = FindTrainingType("CustomSabersLite.Models.SaberHash");
            cell = AccessTools.Method(cellType, "CreateStandardCell").Invoke(null, null) as Component;
            cell.gameObject.SetActive(false);
            MethodInfo setInfo = AccessTools.Method(cellType, "SetInfo");
            TMP_Text name = AccessTools.Field(cellType, "mainText").GetValue(cell) as TMP_Text;
            TMP_Text author = AccessTools.Field(cellType, "subText").GetValue(cell) as TMP_Text;
            object builtin = Activator.CreateInstance(infoType, new object[] { "Default", "", null, Activator.CreateInstance(defaultType) });
            object imported = Activator.CreateInstance(infoType, new object[] { "Default", "Imported Author", null,
                Activator.CreateInstance(hashType, new object[] { "local-ui-caption-contract" }) });
            setInfo.Invoke(cell, new[] { builtin });
            Check("mod-ui-imported-saber-builtin", name.text == "默认", name.text);
            setInfo.Invoke(cell, new[] { imported });
            Check("mod-ui-imported-saber-recycled-name", name.text == "Default" && author.text == "Imported Author",
                "Imported name=" + name.text + "; author=" + author.text);
            setInfo.Invoke(cell, new[] { builtin });
            Check("mod-ui-imported-saber-recycled-builtin", name.text == "默认", name.text);
        }
        finally
        {
            if (cell != null) { cell.gameObject.SetActive(false); UnityEngine.Object.Destroy(cell.gameObject); }
        }
        yield return null;
    }
}
