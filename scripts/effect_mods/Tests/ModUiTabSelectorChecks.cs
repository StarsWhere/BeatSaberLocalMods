using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    IEnumerator ModUiAuditBsmlSelector(ModUiAuditRun run, Dictionary<string, object> page,
        Component view, Component selector, Camera camera, string slug)
    {
        var state = ModUiGameplayReadSelectorState(selector, ModUiGameplayEnumerable(GetMember(selector, "tabs")));
        var record = new Dictionary<string, object>
        {
            ["path"] = ModUiAuditPath(selector.transform), ["type"] = selector.GetType().FullName,
            ["count"] = state.VisibleTabs.Count, ["pageSize"] = state.PageSize, ["status"] = "pending"
        };
        if (!page.TryGetValue("tabs", out object existing) || !(existing is List<object>)) page["tabs"] = new List<object>();
        ((List<object>)page["tabs"]).Add(record);
        var cells = new List<object>();
        record["cells"] = cells;
        string scope = ModUiAuditString(page["scope"]);
        for (int index = 0; index < state.VisibleTabs.Count; index++)
        {
            if (!ModUiGameplaySelectGlobal(state, index, out string detail))
            { run.Block(scope, slug, detail); cells.Add(new { index, status = "blocked", detail }); continue; }
            Component tab = state.VisibleTabs[index] as Component;
            yield return ModUiAuditWaitLayout(view, 3, 1.5f);
            yield return new WaitForSecondsRealtime(0.3f);
            if (tab == null || !tab.gameObject.activeInHierarchy)
            { run.Block(scope, slug, "Native selector did not activate tab " + index); cells.Add(new { index, status = "blocked" }); continue; }
            string label = slug + "-page-" + index;
            ModUiAuditCapturePage(run, page, tab, camera, label);
            yield return ModUiAuditScroll(run, page, tab, camera, label);
            yield return ModUiAuditRealHoverHints(run, page, tab, camera, scope);
            yield return ModUiAuditModals(run, page, tab, camera, label, scope);
            cells.Add(new { index, status = "verified", name = GetMember(tab, "TabName") });
        }
        record["status"] = cells.Count == state.VisibleTabs.Count && state.VisibleTabs.Count > 0 &&
            cells.All(value => ModUiAuditString(GetMember(value, "status")) == "verified") ? "verified" : "partial";
        if (state.OriginalGlobal >= 0)
        {
            if (!ModUiGameplaySelectGlobal(state, state.OriginalGlobal, out string detail)) run.Block(scope, slug, "Tab restoration failed: " + detail);
        }
        else run.Block(scope, slug, "Original native tab selection was unavailable for restoration.");
    }
}
