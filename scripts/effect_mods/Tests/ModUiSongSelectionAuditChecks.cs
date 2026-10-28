using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    static void ModUiAuditAddSongSelectionContainer(List<Zenject.DiContainer> containers,
        HashSet<Zenject.DiContainer> seen, Zenject.DiContainer container)
    {
        if (container == null || !seen.Add(container)) return;
        containers.Add(container);
        Zenject.DiContainer[] parents;
        try { parents = container.ParentContainers; }
        catch { return; }
        if (parents == null) return;
        foreach (Zenject.DiContainer parent in parents)
            ModUiAuditAddSongSelectionContainer(containers, seen, parent);
    }

    static List<Zenject.DiContainer> ModUiAuditSongSelectionContainers()
    {
        List<Zenject.DiContainer> result = new();
        HashSet<Zenject.DiContainer> seen = new();
        Zenject.Context[] contexts;
        try { contexts = Resources.FindObjectsOfTypeAll<Zenject.Context>(); }
        catch { return result; }

        // SiraUtil installs Location.Menu bindings while decorating the menu
        // installer.  Depending on the active scene that container can be a
        // SceneContext, SceneDecoratorContext, or a child GameObjectContext;
        // search active contexts first and then their complete parent chain.
        IEnumerable<Zenject.Context> ordered = contexts
            .Where(value => value != null && value.gameObject != null &&
                value.gameObject.scene.IsValid() && value.gameObject.activeInHierarchy)
            .Concat(contexts.Where(value => value != null && value.gameObject != null &&
                value.gameObject.scene.IsValid() && !value.gameObject.activeInHierarchy));
        foreach (Zenject.Context context in ordered)
        {
            try { ModUiAuditAddSongSelectionContainer(result, seen, context.Container); }
            catch { }
        }
        return result;
    }

    static object ModUiAuditTryResolveSongSelectionFromContainer(Zenject.DiContainer container, Type type)
    {
        if (container == null || type == null) return null;
        try
        {
            object direct = container.TryResolve(type);
            if (direct != null && type.IsInstanceOfType(direct)) return direct;
        }
        catch { }

        // Several PlaylistManager menu bindings intentionally expose only
        // updater interfaces (BindInterfacesTo), so resolving the concrete
        // controller can return null even though the live singleton exists.
        Type[] interfaces;
        try { interfaces = type.GetInterfaces(); }
        catch { interfaces = Array.Empty<Type>(); }
        foreach (Type contract in interfaces.OrderBy(value => value.FullName))
        {
            try
            {
                object resolved = container.TryResolve(contract);
                if (resolved != null && type.IsInstanceOfType(resolved)) return resolved;
            }
            catch { }
            if (contract.Assembly == type.Assembly ||
                (contract.Namespace ?? string.Empty).StartsWith("PlaylistManager.", StringComparison.Ordinal))
            {
                try
                {
                    // Updater contracts can have several PlaylistManager
                    // subscribers (for example Folders and playlist details),
                    // so TryResolve may either throw or return the other
                    // subscriber. ResolveAll still returns the registered
                    // production singletons and lets us select the requested
                    // concrete type without resolving every IInitializable
                    // service in the game.
                    IList resolvedAll = container.ResolveAll(contract);
                    if (resolvedAll == null) continue;
                    foreach (object resolved in resolvedAll)
                        if (resolved != null && type.IsInstanceOfType(resolved)) return resolved;
                }
                catch { }
            }
        }
        return null;
    }

    static object ModUiAuditTryFindLoadedSongSelectionInstance(Type type)
    {
        if (type == null) return null;

        foreach (string name in new[] { "Instance", "instance", "Current", "current", "Singleton", "Default" })
        {
            object value = GetStaticMember(type, name);
            if (value != null && type.IsInstanceOfType(value)) return value;
        }

        // ViewController/FlowCoordinator bindings are actual components.  A
        // live inactive component is still the production object that the
        // menu container constructed; never create a replacement host here.
        try
        {
            object[] values = Resources.FindObjectsOfTypeAll(type);
            object value = values.FirstOrDefault(candidate =>
            {
                Component component = candidate as Component;
                return component != null && component.gameObject != null &&
                    component.gameObject.scene.IsValid() && component.gameObject.activeInHierarchy;
            });
            if (value != null) return value;
            return values.FirstOrDefault(candidate =>
            {
                Component component = candidate as Component;
                return component != null && component.gameObject != null && component.gameObject.scene.IsValid();
            });
        }
        catch { return null; }
    }

    static object ModUiAuditFindPlaylistManagerDependency(Type type)
    {
        if (type == null) return null;
        Type dataManagerType = FindTrainingType("PlaylistManager.PlaylistDataManager");
        if (dataManagerType == null || type == dataManagerType) return null;
        object dataManager = null;
        foreach (Zenject.DiContainer container in ModUiAuditSongSelectionContainers())
        {
            dataManager = ModUiAuditTryResolveSongSelectionFromContainer(container, dataManagerType);
            if (dataManager != null) break;
        }
        if (dataManager == null) return null;

        // PlaylistDataManager is the production owner of the updater lists.
        // LevelDetailButtonsViewController is reached through
        // beatmapLevelUpdaters; PlaylistUIManager is reached through
        // levelCollectionsTableUpdaters.  PlaylistUIManager then owns the
        // initialized category updater list and SettingsViewController.
        object beatmapUpdaters = ReadMemberAny(dataManager, "beatmapLevelUpdaters");
        if (beatmapUpdaters is IEnumerable beatmapValues)
            foreach (object value in beatmapValues)
                if (value != null && type.IsInstanceOfType(value)) return value;

        object tableUpdaters = ReadMemberAny(dataManager, "levelCollectionsTableUpdaters");
        if (tableUpdaters is IEnumerable tableValues)
        {
            foreach (object value in tableValues)
            {
                if (value == null) continue;
                if (type.IsInstanceOfType(value)) return value;
                object categoryUpdaters = ReadMemberAny(value, "levelCategoryUpdaters");
                if (categoryUpdaters is IEnumerable categoryValues)
                    foreach (object category in categoryValues)
                        if (category != null && type.IsInstanceOfType(category)) return category;
                object settings = ReadMemberAny(value, "settingsViewController");
                if (settings != null && type.IsInstanceOfType(settings)) return settings;
            }
        }
        return null;
    }

    static string ModUiAuditPlaylistManagerDependencySource(object target)
    {
        if (target == null) return string.Empty;
        try
        {
            Type dataManagerType = FindTrainingType("PlaylistManager.PlaylistDataManager");
            if (dataManagerType == null) return string.Empty;
            foreach (Zenject.DiContainer container in ModUiAuditSongSelectionContainers())
            {
                object dataManager = ModUiAuditTryResolveSongSelectionFromContainer(container, dataManagerType);
                if (dataManager == null) continue;
                foreach (string field in new[] { "beatmapLevelUpdaters", "levelCollectionsTableUpdaters" })
                {
                    object values = ReadMemberAny(dataManager, field);
                    if (values is IEnumerable enumerable && enumerable.Cast<object>().Any(value => ReferenceEquals(value, target)))
                        return "PlaylistDataManager." + field;
                    if (field == "levelCollectionsTableUpdaters" && values is IEnumerable tableValues)
                    {
                        foreach (object manager in tableValues)
                        {
                            object categories = ReadMemberAny(manager, "levelCategoryUpdaters");
                            if (categories is IEnumerable categoryValues && categoryValues.Cast<object>().Any(value => ReferenceEquals(value, target)))
                                return "PlaylistDataManager.levelCollectionsTableUpdaters->PlaylistUIManager.levelCategoryUpdaters";
                            object settings = ReadMemberAny(manager, "settingsViewController");
                            if (ReferenceEquals(settings, target)) return "PlaylistDataManager.levelCollectionsTableUpdaters->PlaylistUIManager.settingsViewController";
                        }
                    }
                }
            }
        }
        catch { }
        return string.Empty;
    }

    static object ModUiAuditTryResolveSongSelection(Type type)
    {
        if (type == null) return null;
        foreach (Zenject.DiContainer container in ModUiAuditSongSelectionContainers())
        {
            object resolved = ModUiAuditTryResolveSongSelectionFromContainer(container, type);
            if (resolved != null) return resolved;
        }
        return ModUiAuditTryFindLoadedSongSelectionInstance(type) ?? ModUiAuditFindPlaylistManagerDependency(type);
    }

    static Component ModUiAuditSongSelectionComponent(object value, params string[] names)
    {
        if (value == null) return null;
        foreach (string name in names)
        {
            object member = ReadMemberAny(value, name);
            if (member is Component component) return component;
            if (member is GameObject gameObject) return gameObject.transform;
        }
        return value as Component;
    }

    static bool ModUiAuditSongSelectionActive(Component component)
    {
        try { return component != null && component.gameObject.activeInHierarchy; }
        catch { return false; }
    }

    static bool ModUiAuditInvokeSongSelection(object target, string name, object[] arguments, out string detail)
    {
        detail = string.Empty;
        if (target == null)
        {
            detail = "target is unavailable";
            return false;
        }
        MethodInfo method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(value => value.Name == name && value.GetParameters().Length == (arguments?.Length ?? 0))
            .OrderBy(value => value.IsPublic ? 0 : 1)
            .FirstOrDefault();
        if (method == null)
        {
            detail = target.GetType().FullName + "." + name + "(" + (arguments?.Length ?? 0) + ") is unavailable";
            return false;
        }
        try
        {
            method.Invoke(target, arguments);
            detail = method.ToString();
            return true;
        }
        catch (Exception error)
        {
            detail = ModUiAuditException(error);
            return false;
        }
    }

    void ModUiAuditSongSelectionBlocked(ModUiAuditRun run, string scope, string target, string reason)
    {
        run.Entrances.Add(new
        {
            kind = "song-selection-extra",
            scope,
            target,
            status = "blocked",
            reason
        });
        run.Block(scope, target, reason);
    }

    static int ModUiAuditActiveSongSelectionModals()
    {
        Type modalType = FindTrainingType("HMUI.ModalView");
        if (modalType == null) return 0;
        try
        {
            return Resources.FindObjectsOfTypeAll(modalType).OfType<Component>()
                .Count(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.activeInHierarchy);
        }
        catch { return 0; }
    }

    static string ModUiAuditSongSelectionLevelId(object level)
    {
        return Convert.ToString(ReadMemberAny(level, "levelID", "levelId", "LevelID", "LevelId"),
            CultureInfo.InvariantCulture) ?? string.Empty;
    }

    static object ModUiAuditSongSelectionLevelFromTable(object table, string wantedId, bool allowFirst)
    {
        if (table == null) return null;
        object values = ReadMemberAny(table, "_beatmapLevels", "beatmapLevels", "BeatmapLevels", "_levels");
        if (!(values is IEnumerable enumerable)) return null;
        object first = null;
        foreach (object value in enumerable)
        {
            if (value == null) continue;
            first ??= value;
            if (!string.IsNullOrEmpty(wantedId) &&
                string.Equals(ModUiAuditSongSelectionLevelId(value), wantedId, StringComparison.Ordinal))
                return value;
        }
        return allowFirst ? first : null;
    }

    static object ModUiAuditSongSelectionRequestedLevel(string wantedId)
    {
        if (string.IsNullOrEmpty(wantedId)) return null;
        Type loader = FindTrainingType("SongCore.Loader");
        if (loader == null) return null;
        MethodInfo method = loader.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(value => value.Name == "GetLevelById" && value.GetParameters().Length == 1 &&
                value.GetParameters()[0].ParameterType == typeof(string));
        try { return method?.Invoke(null, new object[] { wantedId }); }
        catch { return null; }
    }

    static List<object> ModUiAuditSongSelectionPackLevels(object pack)
    {
        List<object> levels = new();
        if (pack == null) return levels;

        // BeatmapLevelPack exposes AllBeatmapLevels() in the production game.
        // Invoke that method first so the selection is based on the pack that
        // SongCore actually registered, rather than on a table that may still
        // contain the base-game song list while the filter is changing.
        try
        {
            MethodInfo allLevels = pack.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(value => value.Name == "AllBeatmapLevels" && value.GetParameters().Length == 0)
                .OrderBy(value => value.IsPublic ? 0 : 1)
                .FirstOrDefault();
            object values = allLevels?.Invoke(pack, null);
            if (values is IEnumerable enumerable)
                foreach (object value in enumerable)
                    if (value != null) levels.Add(value);
        }
        catch { }

        // Keep a field fallback for Beat Saber builds where the inherited
        // accessor is stripped from reflection metadata.  This is the same
        // _allBeatmapLevels field used by SongCore's Accessors helper.
        if (levels.Count == 0)
        {
            object values = ReadMemberAny(pack, "_allBeatmapLevels", "allBeatmapLevels");
            if (values is IEnumerable enumerable)
                foreach (object value in enumerable)
                    if (value != null) levels.Add(value);
        }
        return levels;
    }

    static object ModUiAuditSongSelectionProductionController(string typeName)
    {
        object active = ModUiAuditFindActiveController(typeName);
        if (active != null) return active;
        return ModUiAuditTryResolveSongSelection(FindTrainingType(typeName));
    }

    static bool ModUiAuditSongSelectionAnnotatedContainsPack(object annotated, object pack)
    {
        if (annotated == null || pack == null) return false;
        object values = ReadMemberAny(annotated,
            "_annotatedBeatmapLevelCollections", "annotatedBeatmapLevelCollections");
        if (!(values is IEnumerable enumerable)) return false;
        string packId = Convert.ToString(ReadMemberAny(pack, "packID", "packId", "PackID", "PackId"),
            CultureInfo.InvariantCulture) ?? string.Empty;
        foreach (object value in enumerable)
        {
            if (ReferenceEquals(value, pack)) return true;
            if (!string.IsNullOrEmpty(packId) && string.Equals(
                Convert.ToString(ReadMemberAny(value, "packID", "packId", "PackID", "PackId"),
                    CultureInfo.InvariantCulture), packId, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    static bool ModUiAuditSongSelectionSelectCustomSongsCategory(object filtering,
        out string detail, out string categoryTypeName)
    {
        detail = string.Empty;
        categoryTypeName = string.Empty;
        object category = ModUiAuditSongSelectionProductionController("SelectLevelCategoryViewController");
        if (category == null)
        {
            detail = "SelectLevelCategoryViewController production instance is unavailable.";
            return false;
        }
        MethodInfo setup = category.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(value => value.Name == "Setup" && value.GetParameters().Length == 2)
            .FirstOrDefault();
        if (setup == null)
        {
            detail = category.GetType().FullName + ".Setup(LevelCategory, LevelCategory[]) is unavailable.";
            return false;
        }
        ParameterInfo[] parameters = setup.GetParameters();
        Type categoryType = parameters[0].ParameterType;
        categoryTypeName = categoryType.FullName;
        object customSongs;
        try { customSongs = Enum.Parse(categoryType, "CustomSongs"); }
        catch (Exception error)
        {
            detail = "CustomSongs enum value is unavailable: " + error.GetType().Name;
            return false;
        }

        object enabled = filtering == null ? null :
            ReadMemberAny(filtering, "_enabledLevelCategories", "enabledLevelCategories");
        Type enabledType = parameters[1].ParameterType;
        if (enabled == null || !enabledType.IsInstanceOfType(enabled))
        {
            // Keep the enabled categories owned by the native category
            // controller when the filter field is not reflectable.
            object infos = ReadMemberAny(category, "_levelCategoryInfos", "_allLevelCategoryInfos");
            Type elementType = enabledType.IsArray ? enabledType.GetElementType() : null;
            if (elementType != null && infos is IEnumerable infoValues)
            {
                List<object> values = new();
                foreach (object info in infoValues)
                {
                    object value = ReadMemberAny(info, "levelCategory", "LevelCategory");
                    if (value != null) values.Add(value);
                }
                Array array = Array.CreateInstance(elementType, values.Count);
                for (int index = 0; index < values.Count; index++) array.SetValue(values[index], index);
                enabled = array;
            }
        }
        if (enabled == null || !enabledType.IsInstanceOfType(enabled))
        {
            detail = "Native enabled level-category array is unavailable.";
            return false;
        }
        try
        {
            setup.Invoke(category, new[] { customSongs, enabled });
            detail = setup.ToString();
            return true;
        }
        catch (Exception error)
        {
            detail = ModUiAuditException(error);
            return false;
        }
    }

    static string ModUiAuditSongSelectionInterfaceNames(Type type)
    {
        if (type == null) return string.Empty;
        try
        {
            return string.Join("|", type.GetInterfaces().Select(value => value.FullName ?? value.Name).ToArray());
        }
        catch { return string.Empty; }
    }

    static int ModUiAuditSongSelectionEnumerableCount(object value)
    {
        if (!(value is IEnumerable enumerable)) return -1;
        int count = 0;
        foreach (object ignored in enumerable)
        {
            if (++count >= 512) return count;
        }
        return count;
    }

    static int ModUiAuditSongSelectionTableRow(object table, object level)
    {
        if (table == null || level == null) return -1;
        object values = ReadMemberAny(table, "_beatmapLevels", "beatmapLevels", "BeatmapLevels", "_levels");
        if (!(values is IEnumerable enumerable)) return -1;
        string wantedId = ModUiAuditSongSelectionLevelId(level);
        int index = 0;
        foreach (object value in enumerable)
        {
            if (ReferenceEquals(value, level) || (!string.IsNullOrEmpty(wantedId) &&
                string.Equals(ModUiAuditSongSelectionLevelId(value), wantedId, StringComparison.Ordinal)))
            {
                bool header = ModUiAuditReadBool(ReadMemberAny(table, "_showLevelPackHeader", "showLevelPackHeader"), string.Empty, false);
                return index + (header ? 1 : 0);
            }
            index++;
        }
        return -1;
    }

    static bool ModUiAuditInvokeNativeSongSelectionCallback(object table, object level, out string detail)
    {
        detail = string.Empty;
        if (table == null || level == null)
        {
            detail = "native table or level is unavailable";
            return false;
        }
        object tableView = ReadMemberAny(table, "_tableView", "tableView", "TableView");
        int row = ModUiAuditSongSelectionTableRow(table, level);
        if (tableView == null || row < 0)
        {
            detail = "LevelCollectionTableView table view or selected row is unavailable; row=" + row;
            return false;
        }
        MethodInfo method = table.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(value => value.Name == "HandleDidSelectCellWithIndex" && value.GetParameters().Length == 2)
            .FirstOrDefault();
        if (method == null)
        {
            detail = table.GetType().FullName + ".HandleDidSelectCellWithIndex(TableView, int) is unavailable";
            return false;
        }
        try
        {
            // This is the same callback used by the native HMUI.TableView
            // click path.  Its Harmony prefix raises PlaylistManager's
            // LevelCollectionTableView_HandleDidSelectRowEvent, and the
            // original invokes LevelCollectionViewController's detail handler.
            method.Invoke(table, new[] { tableView, (object)row });
            detail = method.ToString() + ";row=" + row;
            return true;
        }
        catch (Exception error)
        {
            detail = ModUiAuditException(error);
            return false;
        }
    }

    static string ModUiAuditSongSelectionState(object value)
    {
        if (value == null) return "null";
        Type type = value.GetType();
        string levelId = ModUiAuditSongSelectionLevelId(ReadMemberAny(value,
            "selectedBeatmapLevel", "_selectedBeatmapLevel", "beatmapLevel", "_beatmapLevel"));
        string addActive = string.Empty;
        object add = ReadMemberAny(value, "AddActive", "_addActive");
        if (add != null) addActive = Convert.ToString(add, CultureInfo.InvariantCulture);
        Component component = value as Component;
        string componentState = component == null ? string.Empty :
            ";scene=" + component.gameObject.scene.name + ";active=" + component.gameObject.activeInHierarchy;
        return (type.FullName ?? type.Name) + ";selectedLevelId=" + levelId +
            ";addActive=" + addActive + componentState;
    }

    static string ModUiAuditSongSelectionResolutionDiagnostics(Type type)
    {
        if (type == null) return "type=null";
        List<string> rows = new();
        rows.Add("type=" + (type.FullName ?? type.Name));
        rows.Add("interfaces=" + ModUiAuditSongSelectionInterfaceNames(type));
        foreach (Zenject.DiContainer container in ModUiAuditSongSelectionContainers())
        {
            if (rows.Count >= 20) break;
            string direct = "direct=?";
            try
            {
                object resolved = container.TryResolve(type);
                direct = "direct=" + (resolved == null ? "null" : resolved.GetType().FullName);
            }
            catch (Exception error) { direct = "direct-error=" + error.GetType().Name; }
            List<string> contracts = new();
            foreach (Type contract in type.GetInterfaces())
            {
                if (contracts.Count >= 8) break;
                try
                {
                    bool bound = container.HasBinding(contract);
                    string value = contract.FullName + ":bound=" + bound;
                    if (bound)
                    {
                        IList resolvedAll = container.ResolveAll(contract);
                        value += ";all=" + (resolvedAll == null ? "null" :
                            string.Join(",", resolvedAll.Cast<object>().Where(item => item != null)
                                .Select(item => item.GetType().FullName).ToArray()));
                    }
                    contracts.Add(value);
                }
                catch (Exception error) { contracts.Add(contract.FullName + ":error=" + error.GetType().Name); }
            }
            rows.Add("container=" + container.GetHashCode().ToString(CultureInfo.InvariantCulture) + ";" +
                direct + ";" + string.Join("|", contracts.ToArray()));
        }
        return string.Join(" || ", rows.ToArray());
    }

    IEnumerator ModUiAuditSelectExistingSong(ModUiAuditRun run, object addController)
    {
        string wantedId = targetId ?? string.Empty;
        object table = ModUiAuditFindActiveController("LevelCollectionTableView");
        object level = null;
        object requestedLevel = null;
        bool customTarget = wantedId.StartsWith("custom_level_", StringComparison.Ordinal);

        // A custom target must come from SongCore's production pack.  The
        // active table can briefly expose base-game rows during a pack switch;
        // using its first row here previously selected 100 Bills and made the
        // Requirements/Chart UI exercise the wrong object.
        if (customTarget)
        {
            Type loader = FindTrainingType("SongCore.Loader");
            bool areSongsLoaded = loader != null &&
                ModUiAuditReadBool(GetStaticMember(loader, "AreSongsLoaded"), string.Empty, false);
            if (!areSongsLoaded)
            {
                yield return ModUiAuditWait(run, () => loader != null &&
                    ModUiAuditReadBool(GetStaticMember(loader, "AreSongsLoaded"), string.Empty, false),
                    5, 8f);
                areSongsLoaded = loader != null &&
                    ModUiAuditReadBool(GetStaticMember(loader, "AreSongsLoaded"), string.Empty, false);
            }

            object customPack = loader == null ? null : GetStaticMember(loader, "CustomLevelsPack");
            List<object> packLevels = ModUiAuditSongSelectionPackLevels(customPack);
            List<object> customLevels = packLevels
                .Where(value => ModUiAuditSongSelectionLevelId(value).StartsWith("custom_level_", StringComparison.Ordinal))
                .ToList();
            requestedLevel = ModUiAuditSongSelectionRequestedLevel(wantedId);
            level = customLevels.FirstOrDefault(value =>
                string.Equals(ModUiAuditSongSelectionLevelId(value), wantedId, StringComparison.Ordinal)) ??
                customLevels.FirstOrDefault();
            string selectionSource = level == null ? "none" :
                string.Equals(ModUiAuditSongSelectionLevelId(level), wantedId, StringComparison.Ordinal)
                    ? "custom-pack-target" : "custom-pack-first";
            run.Entrances.Add(new
            {
                kind = "song-selection-diagnostic",
                scope = "SongCore",
                target = "NativeCustomSongSelection",
                phase = "custom-pack-selection",
                loaderType = loader?.FullName,
                areSongsLoaded,
                customPackType = customPack?.GetType().FullName,
                customPackCount = packLevels.Count,
                customLevelCount = customLevels.Count,
                requestedId = wantedId,
                getLevelByIdFound = requestedLevel != null,
                chosenId = ModUiAuditSongSelectionLevelId(level),
                selectionSource
            });
            if (!areSongsLoaded || customPack == null || customLevels.Count == 0 || level == null)
                yield break;

            object filtering = ModUiAuditFindActiveController("LevelFilteringNavigationController");
            string filterDetail = string.Empty;
            string categoryDetail;
            string categoryTypeName;
            bool categorySelected = ModUiAuditSongSelectionSelectCustomSongsCategory(filtering,
                out categoryDetail, out categoryTypeName);
            run.Entrances.Add(new
            {
                kind = "song-selection-diagnostic",
                scope = "SongCore",
                target = "NativeCustomSongSelection",
                phase = "select-custom-category",
                categoryType = categoryTypeName,
                categorySelected,
                detail = categoryDetail
            });
            if (!categorySelected)
                yield break;

            // FoldersViewController is an ILevelCollectionsTableUpdater in
            // the live PlaylistManager graph.  Reset its existing root list
            // through the production method before selecting the custom pack,
            // so its previous playlist/folder table cannot leave the native
            // annotated-collection view on the OST list.
            object folders = ModUiAuditSongSelectionProductionController("PlaylistManager.UI.FoldersViewController");
            string folderDetail = string.Empty;
            bool foldersReset = folders != null && ModUiAuditInvokeSongSelection(folders,
                "SetupList", new object[] { null, true }, out folderDetail);
            run.Entrances.Add(new
            {
                kind = "song-selection-diagnostic",
                scope = "PlaylistManager",
                target = "NativeCustomSongSelection",
                phase = "reset-folders-table",
                foldersType = folders?.GetType().FullName,
                foldersReset,
                detail = folders == null ? "FoldersViewController production instance is unavailable." : folderDetail
            });
            if (filtering == null || !ModUiAuditInvokeSongSelection(filtering,
                "SelectAnnotatedBeatmapLevelCollection", new[] { customPack }, out filterDetail))
            {
                run.Entrances.Add(new
                {
                    kind = "song-selection-diagnostic",
                    scope = "SongCore",
                    target = "NativeCustomSongSelection",
                    phase = "select-custom-pack",
                    status = "blocked",
                    reason = filtering == null ? "LevelFilteringNavigationController is unavailable." : filterDetail
                });
                yield break;
            }
            object annotated = ModUiAuditSongSelectionProductionController(
                "AnnotatedBeatmapLevelCollectionsViewController");
            bool annotatedContains = false;
            bool annotatedCallback = false;
            string annotatedDetail = string.Empty;
            if (annotated != null)
            {
                yield return ModUiAuditWait(run,
                    () => ModUiAuditSongSelectionAnnotatedContainsPack(annotated, customPack), 4, 8f);
                annotatedContains = ModUiAuditSongSelectionAnnotatedContainsPack(annotated, customPack);
                annotatedCallback = ModUiAuditInvokeSongSelection(annotated,
                    "HandleDidSelectAnnotatedBeatmapLevelCollection", new[] { customPack }, out annotatedDetail);
            }
            run.Entrances.Add(new
            {
                kind = "song-selection-diagnostic",
                scope = "PlaylistManager",
                target = "NativeCustomSongSelection",
                phase = "annotated-pack-callback",
                annotatedType = annotated?.GetType().FullName,
                annotatedContains,
                annotatedCallback,
                detail = annotatedDetail
            });
            if (!annotatedCallback)
                yield break;
            string chosenId = ModUiAuditSongSelectionLevelId(level);
            yield return ModUiAuditWait(run, () =>
                ModUiAuditSongSelectionLevelFromTable(
                    ModUiAuditFindActiveController("LevelCollectionTableView"), chosenId, false) != null,
                4, 8f);
            table = ModUiAuditFindActiveController("LevelCollectionTableView");
            object tableLevel = ModUiAuditSongSelectionLevelFromTable(table, chosenId, false);
            if (tableLevel == null)
            {
                run.Entrances.Add(new
                {
                    kind = "song-selection-diagnostic",
                    scope = "SongCore",
                    target = "NativeCustomSongSelection",
                    phase = "custom-pack-table",
                    status = "blocked",
                    chosenId,
                    tableType = table?.GetType().FullName,
                    reason = "The production table did not expose the selected custom-pack level after SelectAnnotatedBeatmapLevelCollection."
                });
                yield break;
            }
            level = tableLevel;
        }
        else
        {
            level = ModUiAuditSongSelectionLevelFromTable(table, wantedId, false);
            requestedLevel = level ?? ModUiAuditSongSelectionRequestedLevel(wantedId);
            level ??= ModUiAuditSongSelectionLevelFromTable(table, string.Empty, true);
            level ??= requestedLevel;
        }

        // The configured Solo state can enter the song list without selecting
        // its detail row.  SelectLevel plus the native table callback is the
        // production path that populates StandardLevelDetailViewController and
        // fires PlaylistManager's BeatmapLevelUpdated subscribers.
        if (level == null) yield break;

        object navigation = ModUiAuditFindActiveController("LevelCollectionNavigationController");
        object target = navigation ?? table;
        if (target == null || !ModUiAuditInvokeSongSelection(target, "SelectLevel", new[] { level }, out _)) yield break;
        table ??= ModUiAuditFindActiveController("LevelCollectionTableView");
        bool callback = ModUiAuditInvokeNativeSongSelectionCallback(table, level, out string callbackDetail);
        run.Entrances.Add(new
        {
            kind = "song-selection-diagnostic",
            scope = "PlaylistManager",
            target = "AddPlaylistModal",
            phase = "native-table-callback",
            callback,
            callbackDetail,
            selectedLevelId = ModUiAuditSongSelectionLevelId(level),
            tableType = table?.GetType().FullName
        });
        if (!callback) yield break;

        string expectedId = ModUiAuditSongSelectionLevelId(level);
        yield return ModUiAuditWait(run, () =>
        {
            Component detail = ModUiAuditFindActiveController("StandardLevelDetailViewController") as Component;
            if (!ModUiAuditSongSelectionActive(detail)) return false;
            object beatmapKey = ReadMemberAny(detail, "beatmapKey", "_beatmapKey", "BeatmapKey");
            string selectedId = ModUiAuditSongSelectionLevelId(beatmapKey);
            bool matchingDetail = string.IsNullOrEmpty(expectedId) || string.IsNullOrEmpty(selectedId) ||
                string.Equals(expectedId, selectedId, StringComparison.Ordinal);
            return matchingDetail &&
                (ModUiAuditReadBool(GetMember(addController, "AddActive"), string.Empty, false) ||
                 ModUiAuditReadBool(GetMember(addController, "_addActive"), string.Empty, false));
        }, 8, 12f);
    }

    IEnumerator ModUiAuditCaptureSongSelectionPage(ModUiAuditRun run, Component root, Camera camera,
        string scope, string title, string slug, string host, string action)
    {
        if (root == null)
        {
            ModUiAuditSongSelectionBlocked(run, scope, title, "No live UI root was available after the safe action.");
            yield break;
        }
        yield return ModUiAuditWaitLayout(root, 3, 3f);
        Dictionary<string, object> page = new()
        {
            ["kind"] = "SongSelectionExtra",
            ["title"] = title,
            ["scope"] = scope,
            ["host"] = host,
            ["action"] = action,
            ["status"] = ModUiAuditSongSelectionActive(root) ? "verified" : "blocked",
            ["active"] = ModUiAuditSongSelectionActive(root),
            ["camera"] = camera?.name
        };
        run.Pages.Add(page);
        if (page["status"].ToString() != "verified")
        {
            page["reason"] = "The safe action returned a UI root that is not active.";
            ModUiAuditSongSelectionBlocked(run, scope, title, (string)page["reason"]);
            yield break;
        }

        run.StablePages++;
        ModUiAuditCheckVisibility(run, page, root, camera);
        ModUiAuditCapturePage(run, page, root, camera, slug + "-top");
        ModUiAuditControls(run, page, root);
        yield return ModUiAuditTabs(run, page, root, camera, slug);
        yield return ModUiAuditScroll(run, page, root, camera, slug);
        yield return ModUiAuditRealHoverHints(run, page, root, camera, scope);
        yield return ModUiAuditModals(run, page, root, camera, slug, scope);
    }

    static Component ModUiAuditAdvancedPanelRoot(object panel)
    {
        return ModUiAuditSongSelectionComponent(panel, "panelRoot", "ownedUiRoot", "filterModal");
    }

    IEnumerator ModUiAuditAdvancedPanelSubPage(ModUiAuditRun run, object runtime, object panel,
        Component root, Camera camera, string action, string pageMember, string closeAction)
    {
        bool available = ModUiAuditReadBool(GetMember(runtime, "PresetsAvailable"), string.Empty, false);
        if (!available)
        {
            run.Entrances.Add(new
            {
                kind = "song-selection-extra",
                scope = "AdvancedSongFilters",
                target = action,
                status = "skipped-unavailable",
                reason = "No persisted filter presets are available in this isolated run."
            });
            yield break;
        }
        if (!ModUiAuditInvokeSafeLazyAction(panel, action, out string detail))
        {
            ModUiAuditSongSelectionBlocked(run, "AdvancedSongFilters", action, detail);
            yield break;
        }
        yield return ModUiAuditWaitLayout(root, 3, 1.5f);
        GameObject subPage = ReadMemberAny(panel, pageMember) as GameObject;
        if (subPage == null || !subPage.activeInHierarchy)
        {
            ModUiAuditSongSelectionBlocked(run, "AdvancedSongFilters", action,
                "The safe preset page action returned without an active page.");
        }
        else
        {
            yield return ModUiAuditCaptureSongSelectionPage(run, subPage.transform, camera,
                "AdvancedSongFilters", action, "advanced-filters-" + ModUiAuditSafeSlug(action),
                "AdvancedSongFilters.Panel", action);
        }
        if (!string.IsNullOrEmpty(closeAction) && !ModUiAuditInvokeSafeLazyAction(panel, closeAction, out detail))
            ModUiAuditSongSelectionBlocked(run, "AdvancedSongFilters", closeAction, detail);
    }

    IEnumerator ModUiAuditAdvancedSongFiltersExtra(ModUiAuditRun run, Camera camera)
    {
        Type runtimeType = FindTrainingType("AdvancedSongFilters.Runtime");
        object runtime = GetStaticMember(runtimeType, "Current");
        object panel = GetMember(runtime, "panel");
        if (runtime == null || panel == null)
        {
            ModUiAuditSongSelectionBlocked(run, "AdvancedSongFilters", "Panel.Open",
                "AdvancedSongFilters.Runtime.Current or its private Panel instance is unavailable in native Solo song selection.");
            yield break;
        }
        if (!ModUiAuditReadBool(GetMember(runtime, "MenuActive"), string.Empty, false))
        {
            ModUiAuditSongSelectionBlocked(run, "AdvancedSongFilters", "Panel.Open",
                "AdvancedSongFilters reports MenuActive=false; opening the filter editor was withheld.");
            yield break;
        }

        bool wasOpen = ModUiAuditReadBool(GetMember(panel, "IsOpen"), string.Empty, false);
        if (!wasOpen && !ModUiAuditInvokeSafeLazyAction(panel, "Open", out string openDetail))
        {
            ModUiAuditSongSelectionBlocked(run, "AdvancedSongFilters", "Panel.Open", openDetail);
            yield break;
        }

        Component modal = ModUiAuditSongSelectionComponent(panel, "filterModal");
        yield return ModUiAuditWait(run, () => ModUiAuditSongSelectionActive(modal), 6, 8f);
        Component root = ModUiAuditAdvancedPanelRoot(panel);
        if (!ModUiAuditSongSelectionActive(modal) && !ModUiAuditSongSelectionActive(root))
        {
            ModUiAuditSongSelectionBlocked(run, "AdvancedSongFilters", "Panel.Open",
                "Panel.Open completed without an active filter modal or panel root.");
        }
        else
        {
            root ??= modal;
            run.Entrances.Add(new
            {
                kind = "song-selection-extra",
                scope = "AdvancedSongFilters",
                target = "Panel.Open",
                status = "verified",
                action = "read-only-open"
            });
            yield return ModUiAuditCaptureSongSelectionPage(run, root, camera, "AdvancedSongFilters",
                "AdvancedSongFilters filter editor", "advanced-filters-main", "AdvancedSongFilters.Panel", "Open");
            yield return ModUiAuditAdvancedPanelSubPage(run, runtime, panel, root, camera,
                "OpenPresetSelector", "boundPresetSelectorPage", "ClosePresetSelector");
            yield return ModUiAuditAdvancedPanelSubPage(run, runtime, panel, root, camera,
                "OpenManagePresets", "boundPresetManagePage", "CloseManagePresets");
        }

        if (!wasOpen && !ModUiAuditInvokeSafeLazyAction(panel, "Close", out string closeDetail))
            ModUiAuditSongSelectionBlocked(run, "AdvancedSongFilters", "Panel.Close", closeDetail);
    }

    IEnumerator ModUiAuditBetterSongListDropdown(ModUiAuditRun run, object filterUi, Component hostRoot,
        Camera camera, string fieldName, string title, string slug)
    {
        object dropdownObject = GetMember(filterUi, fieldName);
        Component dropdown = dropdownObject as Component;
        Component modal = ModUiAuditSongSelectionComponent(dropdownObject, "_modalView");
        if (dropdown == null || modal == null)
        {
            ModUiAuditSongSelectionBlocked(run, "BetterSongList", title,
                "FilterUI." + fieldName + " or its DropdownWithTableView modal is unavailable.");
            yield break;
        }
        bool wasShown = ModUiAuditSongSelectionActive(modal);
        if (!wasShown && !ModUiAuditInvokeSafeLazyAction(dropdownObject, "OnButtonClick", out string openDetail))
        {
            ModUiAuditSongSelectionBlocked(run, "BetterSongList", title, openDetail);
            yield break;
        }
        yield return ModUiAuditWait(run, () => ModUiAuditSongSelectionActive(modal), 5, 5f);
        if (!ModUiAuditSongSelectionActive(modal))
        {
            ModUiAuditSongSelectionBlocked(run, "BetterSongList", title,
                "DropdownWithTableView.OnButtonClick did not expose an active options modal.");
            yield break;
        }

        run.Entrances.Add(new
        {
            kind = "song-selection-extra",
            scope = "BetterSongList",
            target = title,
            status = "verified",
            action = "read-only-open"
        });
        Dictionary<string, object> modalRecord = new()
        {
            ["kind"] = "BetterSongList.DropdownModal",
            ["page"] = title,
            ["scope"] = "BetterSongList",
            ["path"] = ModUiAuditPath(modal.transform),
            ["type"] = modal.GetType().FullName,
            ["initialActive"] = wasShown,
            ["status"] = "verified"
        };
        run.Modals.Add(modalRecord);
        Component captureRoot = modal;
        yield return ModUiAuditCaptureSongSelectionPage(run, captureRoot, camera, "BetterSongList", title,
            slug, "BetterSongList.UI.FilterUI", fieldName);
        string hideSignature = string.Empty;
        string hideError = string.Empty;
        bool hidden = wasShown || ModUiAuditInvokeModal(dropdown, "Hide", out hideSignature, out hideError);
        if (!hidden)
        {
            modalRecord["status"] = "partial";
            modalRecord["hideSignature"] = hideSignature;
            modalRecord["hideReason"] = hideError;
            ModUiAuditSongSelectionBlocked(run, "BetterSongList", title, "Dropdown Hide failed: " + hideError);
        }
        else modalRecord["hideSignature"] = hideSignature;
    }

    IEnumerator ModUiAuditBetterSongListExtras(ModUiAuditRun run, Camera camera)
    {
        Type filterType = FindTrainingType("BetterSongList.UI.FilterUI");
        object filterUi = GetStaticMember(filterType, "persistentNuts");
        if (filterUi == null)
        {
            ModUiAuditSongSelectionBlocked(run, "BetterSongList", "FilterUI",
                "BetterSongList.UI.FilterUI.persistentNuts is unavailable in native Solo song selection.");
            yield break;
        }
        Component hostRoot = ModUiAuditSongSelectionComponent(filterUi, "rootTransform");
        if (hostRoot == null) hostRoot = ModUiAuditSongSelectionComponent(filterUi, "_filterDropdown");
        if (hostRoot == null)
        {
            ModUiAuditSongSelectionBlocked(run, "BetterSongList", "FilterUI",
                "FilterUI.rootTransform and _filterDropdown were unavailable.");
            yield break;
        }
        yield return ModUiAuditBetterSongListDropdown(run, filterUi, hostRoot, camera,
            "_filterDropdown", "BetterSongList filter dropdown", "better-song-list-filter");
        yield return ModUiAuditBetterSongListDropdown(run, filterUi, hostRoot, camera,
            "_sortDropdown", "BetterSongList sort dropdown", "better-song-list-sort");
        yield return ModUiAuditBetterSongListSettings(run, filterUi, hostRoot, camera);
        yield return ModUiAuditBetterSongListDeleteConfirm(run, camera);
    }

    static HashSet<int> ModUiAuditBetterSongListActiveModalIds()
    {
        HashSet<int> ids = new();
        try
        {
            foreach (HMUI.ModalView modal in Resources.FindObjectsOfTypeAll<HMUI.ModalView>())
                if (modal != null && modal.gameObject.scene.IsValid() && modal.gameObject.activeInHierarchy)
                    ids.Add(modal.GetInstanceID());
        }
        catch { }
        return ids;
    }

    static Component ModUiAuditFindBetterSongListSettingsModal(Component hostRoot,
        HashSet<int> activeBefore)
    {
        List<HMUI.ModalView> candidates = new();
        try
        {
            if (hostRoot != null)
                candidates.AddRange(hostRoot.GetComponentsInChildren<HMUI.ModalView>(true)
                    .Where(value => value != null && value.gameObject.scene.IsValid() &&
                        value.gameObject.activeInHierarchy));
            candidates.AddRange(Resources.FindObjectsOfTypeAll<HMUI.ModalView>()
                .Where(value => value != null && value.gameObject.scene.IsValid() &&
                    value.gameObject.activeInHierarchy));
        }
        catch { }
        candidates = candidates.Distinct().ToList();
        HMUI.ModalView modal = candidates.FirstOrDefault(value => activeBefore == null ||
            !activeBefore.Contains(value.GetInstanceID()));
        modal ??= candidates.FirstOrDefault(value =>
            ModUiAuditPath(value.transform).IndexOf("BetterSongList", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ModUiAuditPath(value.transform).IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0);
        modal ??= candidates.FirstOrDefault(value => hostRoot != null &&
            value.transform.IsChildOf(hostRoot.transform));
        return modal;
    }

    static Component ModUiAuditFindBetterSongListSettingsRoot(object filterUi, Component hostRoot)
    {
        object settings = GetStaticMember(FindTrainingType("BetterSongList.UI.SplitViews.Settings"), "instance");
        Component sponsorText = GetMember(settings, "sponsorsText") as Component;
        if (sponsorText != null)
        {
            for (Transform current = sponsorText.transform; current != null; current = current.parent)
            {
                if (!current.gameObject.activeInHierarchy) continue;
                string path = ModUiAuditPath(current);
                if (path.IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    current.name.IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0)
                    return current;
            }
            if (sponsorText.gameObject.activeInHierarchy) return sponsorText;
        }

        // Some BSML split-view revisions expose the temporary settings view
        // as an active ViewController while the child ModalView remains
        // inactive.  Locate that live view by its production hierarchy rather
        // than treating the FilterUI host as the settings page.
        try
        {
            Component view = Resources.FindObjectsOfTypeAll<HMUI.ViewController>()
                .FirstOrDefault(value => value != null && value.gameObject.scene.IsValid() &&
                    value.gameObject.activeInHierarchy &&
                    (ModUiAuditPath(value.transform).IndexOf("BetterSongList", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     ModUiAuditPath(value.transform).IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0));
            if (view != null) return view;
        }
        catch { }

        return null;
    }

    static bool ModUiAuditBetterSongListCloseSettings(object filterUi, out string detail)
    {
        detail = string.Empty;
        object parserParams = GetMember(filterUi, "settingsViewParams");
        if (parserParams == null)
        {
            detail = "FilterUI.settingsViewParams is unavailable.";
            return false;
        }
        return ModUiAuditInvokeSongSelection(parserParams, "EmitEvent",
            new object[] { "CloseSettings" }, out detail);
    }

    IEnumerator ModUiAuditBetterSongListSettings(ModUiAuditRun run, object filterUi, Component hostRoot, Camera camera)
    {
        HashSet<int> activeModalIdsBefore = ModUiAuditBetterSongListActiveModalIds();
        if (!ModUiAuditInvokeSafeLazyAction(filterUi, "SettingsOpened", out string detail))
        {
            ModUiAuditSongSelectionBlocked(run, "BetterSongList", "SettingsOpened", detail);
            yield break;
        }
        Component modal = null;
        Component settingsRoot = null;
        yield return ModUiAuditWait(run, () =>
        {
            try
            {
                modal = ModUiAuditFindBetterSongListSettingsModal(hostRoot, activeModalIdsBefore);
                settingsRoot = modal ?? ModUiAuditFindBetterSongListSettingsRoot(filterUi, hostRoot);
            }
            catch { }
            return ModUiAuditSongSelectionActive(modal) || ModUiAuditSongSelectionActive(settingsRoot);
        }, 6, 8f);
        if (!ModUiAuditSongSelectionActive(modal) && !ModUiAuditSongSelectionActive(settingsRoot))
        {
            ModUiAuditSongSelectionBlocked(run, "BetterSongList", "SettingsOpened",
                "FilterUI.SettingsOpened completed without an active BetterSongList settings modal or split-view settings root.");
            yield break;
        }
        run.Entrances.Add(new
        {
            kind = "song-selection-extra",
            scope = "BetterSongList",
            target = "SettingsOpened",
            status = "verified",
            action = "read-only-open",
            captureType = modal?.GetType().FullName ?? settingsRoot?.GetType().FullName,
            captureMode = modal == null ? "split-view-root" : "modal"
        });
        yield return ModUiAuditCaptureSongSelectionPage(run, modal ?? settingsRoot, camera, "BetterSongList",
            "BetterSongList settings", "better-song-list-settings", "BetterSongList.UI.FilterUI", "SettingsOpened");
        if (ModUiAuditSongSelectionActive(modal))
        {
            if (!ModUiAuditInvokeModal(modal, "Hide", out _, out string hideError) &&
                !ModUiAuditBetterSongListCloseSettings(filterUi, out string closeDetail))
                ModUiAuditSongSelectionBlocked(run, "BetterSongList", "SettingsOpened",
                    "Settings modal cancel failed: " + hideError + "; " + closeDetail);
        }
        else if (ModUiAuditSongSelectionActive(settingsRoot) &&
            !ModUiAuditBetterSongListCloseSettings(filterUi, out string splitCloseDetail))
        {
            ModUiAuditSongSelectionBlocked(run, "BetterSongList", "SettingsOpened",
                "Settings split-view close event failed: " + splitCloseDetail);
        }
    }

    IEnumerator ModUiAuditBetterSongListDeleteConfirm(ModUiAuditRun run, Camera camera)
    {
        Type deleteType = FindTrainingType("BetterSongList.HarmonyPatches.UI.SongDeleteButton");
        Button deleteButton = GetStaticMember(deleteType, "deleteButton") as Button;
        if (deleteButton == null || !deleteButton.gameObject.activeInHierarchy || !deleteButton.interactable)
        {
            run.Entrances.Add(new
            {
                kind = "song-selection-extra",
                scope = "BetterSongList",
                target = "SongDeleteConfirm",
                status = "not-applicable",
                reason = "BetterSongList did not expose an active, interactable delete button for the selected song."
            });
            yield break;
        }
        try { deleteButton.onClick.Invoke(); }
        catch (Exception error)
        {
            ModUiAuditSongSelectionBlocked(run, "BetterSongList", "SongDeleteConfirm", ModUiAuditException(error));
            yield break;
        }
        yield return null;
        Component root = ModUiAuditFindActiveController("StandardLevelDetailViewController") as Component;
        if (ModUiAuditActiveSongSelectionModals() == 0)
        {
            ModUiAuditSongSelectionBlocked(run, "BetterSongList", "SongDeleteConfirm",
                "The production delete button did not open its confirmation modal.");
            yield break;
        }
        run.Entrances.Add(new
        {
            kind = "song-selection-extra",
            scope = "BetterSongList",
            target = "SongDeleteConfirm",
            status = "verified",
            action = "read-only-open-cancel"
        });
        // CaptureActiveModalsAfterAction hides the modal without invoking the
        // BSML Confirm action, so the selected song/file remains untouched.
        yield return ModUiAuditCaptureActiveModalsAfterAction(run, root, camera, "BetterSongList", "SongDeleteConfirm (cancel)");
    }

    static bool ModUiAuditSongCoreDiffHasColors(object diffData, out bool known)
    {
        known = false;
        if (diffData == null) return false;
        Type utils = FindTrainingType("SongCore.Utilities.Utils");
        MethodInfo method = utils?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(value => value.Name == "DiffHasColors" && value.GetParameters().Length == 1);
        if (method == null) return false;
        known = true;
        try { return Convert.ToBoolean(method.Invoke(null, new[] { diffData }), CultureInfo.InvariantCulture); }
        catch { return false; }
    }

    static bool ModUiAuditSongCoreSameIcon(object left, object right)
    {
        if (left == null || right == null) return false;
        if (ReferenceEquals(left, right)) return true;
        try
        {
            UnityEngine.Object leftObject = left as UnityEngine.Object;
            UnityEngine.Object rightObject = right as UnityEngine.Object;
            return leftObject != null && rightObject != null && leftObject == rightObject;
        }
        catch { return false; }
    }

    IEnumerator ModUiAuditSongCoreRequirements(ModUiAuditRun run, Camera camera)
    {
        bool expectsCustomSong = (targetId ?? string.Empty).StartsWith("custom_level_", StringComparison.Ordinal);
        if (expectsCustomSong)
        {
            object selectedDetail = ModUiAuditFindActiveController("StandardLevelDetailViewController");
            string selectedId = ModUiAuditSongSelectionLevelId(ReadMemberAny(selectedDetail,
                "beatmapKey", "_beatmapKey", "BeatmapKey"));
            if (!selectedId.StartsWith("custom_level_", StringComparison.Ordinal))
            {
                string reason = string.IsNullOrEmpty(selectedId)
                    ? "No native custom-song detail is active after SongCore pack selection."
                    : "The native detail still points to a base-game level (" + selectedId + ").";
                run.Entrances.Add(new
                {
                    kind = "song-selection-extra",
                    scope = "SongCore",
                    target = "RequirementsModal",
                    status = "not-applicable",
                    reason
                });
                run.Entrances.Add(new
                {
                    kind = "song-selection-extra",
                    scope = "SongCore",
                    target = "ColorsModal",
                    status = "not-applicable",
                    reason = "Requirements/Colors audit requires the selected native custom-song detail."
                });
                yield break;
            }
        }
        Type requirementsType = FindTrainingType("SongCore.UI.RequirementsUI");
        object requirements = GetStaticMember(requirementsType, "instance");
        object diffData = GetMember(requirements, "diffData");
        if (requirements == null || diffData == null)
        {
            run.Entrances.Add(new
            {
                kind = "song-selection-extra",
                scope = "SongCore",
                target = "RequirementsModal",
                status = "not-applicable",
                reason = requirements == null ? "RequirementsUI.instance unavailable" :
                    "selected custom song has no SongCore difficulty metadata"
            });
            run.Entrances.Add(new
            {
                kind = "song-selection-extra",
                scope = "SongCore",
                target = "ColorsModal",
                status = "not-applicable",
                reason = requirements == null ? "RequirementsUI.instance unavailable" :
                    "RequirementsUI.diffData is unavailable"
            });
            yield break;
        }
        if (!ModUiAuditInvokeSafeLazyAction(requirements, "ShowRequirements", out string detail))
        {
            ModUiAuditSongSelectionBlocked(run, "SongCore", "RequirementsModal", detail);
            yield break;
        }
        yield return null;
        Component requirementsModal = ModUiAuditSongSelectionComponent(requirements, "modal");
        yield return ModUiAuditWait(run, () => ModUiAuditSongSelectionActive(requirementsModal), 5, 8f);
        if (!ModUiAuditSongSelectionActive(requirementsModal))
        {
            ModUiAuditSongSelectionBlocked(run, "SongCore", "RequirementsModal",
                "RequirementsUI.ShowRequirements completed without an active requirements modal.");
            yield break;
        }
        run.Entrances.Add(new
        {
            kind = "song-selection-extra",
            scope = "SongCore",
            target = "RequirementsModal",
            status = "verified",
            action = "read-only-open"
        });
        yield return ModUiAuditCaptureSongSelectionPage(run, requirementsModal, camera, "SongCore",
            "SongCore requirements", "songcore-requirements", "SongCore.UI.RequirementsUI", "ShowRequirements");

        // Requirements are a valid production page for every selected
        // difficulty with SongCore metadata.  The Colors row is optional and
        // must not gate the Requirements modal itself.
        bool hasColors = ModUiAuditSongCoreDiffHasColors(diffData, out bool colorCheckKnown);
        if (!colorCheckKnown || !hasColors)
        {
            run.Entrances.Add(new
            {
                kind = "song-selection-extra",
                scope = "SongCore",
                target = "ColorsModal",
                status = "not-applicable",
                reason = !colorCheckKnown ? "SongCore.Utils.DiffHasColors unavailable" :
                    "selected difficulty has no custom color metadata"
            });
            yield break;
        }

        object list = GetMember(requirements, "customListTableData");
        object data = ReadMemberAny(list, "Data", "data");
        object colorsIcon = GetMember(requirements, "ColorsIcon");
        int colorsIndex = -1;
        if (data is IEnumerable rows)
        {
            int index = 0;
            foreach (object row in rows)
            {
                if (ModUiAuditSongCoreSameIcon(ReadMemberAny(row, "Icon", "icon"), colorsIcon))
                {
                    colorsIndex = index;
                    break;
                }
                index++;
            }
        }
        object tableView = ReadMemberAny(list, "TableView", "tableView");
        if (colorsIndex < 0 || tableView == null ||
            !ModUiAuditInvokeSongSelection(requirements, "Select", new[] { tableView, (object)colorsIndex }, out detail))
        {
            ModUiAuditSongSelectionBlocked(run, "SongCore", "ColorsModal", string.IsNullOrEmpty(detail)
                ? "SongCore ColorsIcon row or RequirementsUI.Select callback was unavailable." : detail);
            yield break;
        }
        object colors = ReadMemberAny(requirements, "_colorsUI", "colorsUI");
        Component colorsModal = ModUiAuditSongSelectionComponent(colors, "modal");
        yield return ModUiAuditWait(run, () => ModUiAuditSongSelectionActive(colorsModal), 5, 8f);
        if (!ModUiAuditSongSelectionActive(colorsModal))
        {
            ModUiAuditSongSelectionBlocked(run, "SongCore", "ColorsModal",
                "RequirementsUI.Select(ColorsIcon) did not expose the production ColorsUI modal.");
            yield break;
        }
        run.Entrances.Add(new
        {
            kind = "song-selection-extra",
            scope = "SongCore",
            target = "ColorsModal",
            status = "verified",
            action = "read-only-open"
        });
        yield return ModUiAuditCaptureActiveModalsAfterAction(run, colorsModal, camera, "SongCore", "ColorsModal");
    }

    IEnumerator ModUiAuditPlaylistManagerAddModal(ModUiAuditRun run, Camera camera)
    {
        Type type = FindTrainingType("PlaylistManager.UI.LevelDetailButtonsViewController");
        object controller = ModUiAuditTryResolveSongSelection(type);
        if (controller == null)
        {
            run.Entrances.Add(new
            {
                kind = "song-selection-diagnostic",
                scope = "PlaylistManager",
                target = "AddPlaylistModal",
                phase = "controller-resolution",
                type = type?.FullName,
                resolution = ModUiAuditSongSelectionResolutionDiagnostics(type)
            });
            ModUiAuditSongSelectionBlocked(run, "PlaylistManager", "AddPlaylistModal",
                "LevelDetailButtonsViewController could not be resolved from the active Zenject scene container.");
            yield break;
        }
        object initialTable = ModUiAuditFindActiveController("LevelCollectionTableView");
        run.Entrances.Add(new
        {
            kind = "song-selection-diagnostic",
            scope = "PlaylistManager",
            target = "AddPlaylistModal",
            phase = "before-select",
            controller = ModUiAuditSongSelectionState(controller),
            controllerInterfaces = ModUiAuditSongSelectionInterfaceNames(controller.GetType()),
            controllerSource = ModUiAuditPlaylistManagerDependencySource(controller),
            targetId,
            tableType = initialTable?.GetType().FullName,
            tableLevelCount = ModUiAuditSongSelectionEnumerableCount(ReadMemberAny(initialTable, "_beatmapLevels", "beatmapLevels", "BeatmapLevels")),
            tableFirstLevelId = ModUiAuditSongSelectionLevelId(ModUiAuditSongSelectionLevelFromTable(initialTable, string.Empty, true))
        });
        if (!ModUiAuditReadBool(GetMember(controller, "AddActive"), string.Empty, false) &&
            !ModUiAuditReadBool(GetMember(controller, "_addActive"), string.Empty, false))
            yield return ModUiAuditSelectExistingSong(run, controller);
        object selectedDetail = ModUiAuditFindActiveController("StandardLevelDetailViewController");
        object selectedKey = ReadMemberAny(selectedDetail, "beatmapKey", "_beatmapKey", "BeatmapKey");
        run.Entrances.Add(new
        {
            kind = "song-selection-diagnostic",
            scope = "PlaylistManager",
            target = "AddPlaylistModal",
            phase = "after-select",
            controller = ModUiAuditSongSelectionState(controller),
            controllerSource = ModUiAuditPlaylistManagerDependencySource(controller),
            detailType = selectedDetail?.GetType().FullName,
            detailLevelId = ModUiAuditSongSelectionLevelId(selectedKey),
            detailActive = ModUiAuditSongSelectionActive(selectedDetail as Component),
            navigationType = ModUiAuditFindActiveController("LevelCollectionNavigationController")?.GetType().FullName
        });
        if (!ModUiAuditReadBool(GetMember(controller, "AddActive"), string.Empty, false) &&
            !ModUiAuditReadBool(GetMember(controller, "_addActive"), string.Empty, false))
        {
            ModUiAuditSongSelectionBlocked(run, "PlaylistManager", "AddPlaylistModal",
                "The native selected song did not expose PlaylistManager's read-only add-to-playlist action.");
            yield break;
        }
        if (!ModUiAuditInvokeSafeLazyAction(controller, "OpenAddModal", out string detail))
        {
            ModUiAuditSongSelectionBlocked(run, "PlaylistManager", "AddPlaylistModal", detail);
            yield break;
        }
        yield return null;
        Component root = ModUiAuditFindActiveController("StandardLevelDetailViewController") as Component ??
            ModUiAuditSongSelectionComponent(controller, "rootTransform");
        if (ModUiAuditActiveSongSelectionModals() == 0)
        {
            ModUiAuditSongSelectionBlocked(run, "PlaylistManager", "AddPlaylistModal",
                "OpenAddModal completed without an active PlaylistManager modal.");
            yield break;
        }
        run.Entrances.Add(new
        {
            kind = "song-selection-extra",
            scope = "PlaylistManager",
            target = "AddPlaylistModal",
            status = "verified",
            action = "read-only-open"
        });
        yield return ModUiAuditCaptureActiveModalsAfterAction(run, root, camera, "PlaylistManager", "AddPlaylistModal");
    }

    IEnumerator ModUiAuditPlaylistManagerImageSelectionModal(ModUiAuditRun run,
        object details, Camera camera)
    {
        if (details == null)
        {
            run.Entrances.Add(new
            {
                kind = "song-selection-extra",
                scope = "PlaylistManager",
                target = "ImageSelectionModal",
                status = "not-applicable",
                reason = "PlaylistDetailsViewController production instance is unavailable."
            });
            yield break;
        }
        object selectedPlaylist = GetMember(details, "selectedPlaylist");
        if (selectedPlaylist == null)
        {
            run.Entrances.Add(new
            {
                kind = "song-selection-extra",
                scope = "PlaylistManager",
                target = "ImageSelectionModal",
                status = "not-applicable",
                reason = "PlaylistDetailsViewController has no selected existing playlist."
            });
            yield break;
        }
        bool readOnly = ModUiAuditReadBool(GetMember(details, "PlaylistReadOnly"), string.Empty, false);
        if (readOnly)
        {
            run.Entrances.Add(new
            {
                kind = "song-selection-extra",
                scope = "PlaylistManager",
                target = "ImageSelectionModal",
                status = "not-applicable",
                reason = "The selected existing playlist is read-only; OpenImageSelectionModal production guard withheld the editor."
            });
            yield break;
        }

        if (!ModUiAuditInvokeSongSelection(details, "OpenImageSelectionModal",
            Array.Empty<object>(), out string detail))
        {
            ModUiAuditSongSelectionBlocked(run, "PlaylistManager", "ImageSelectionModal", detail);
            yield break;
        }
        object imageController = GetMember(details, "imageSelectionModalController") ??
            ModUiAuditTryResolveSongSelection(FindTrainingType("PlaylistManager.UI.ImageSelectionModalController"));
        Component modal = ModUiAuditSongSelectionComponent(imageController, "modalView", "modalTransform");
        yield return ModUiAuditWait(run, () =>
        {
            modal = ModUiAuditSongSelectionComponent(imageController, "modalView", "modalTransform");
            return ModUiAuditSongSelectionActive(modal);
        }, 6, 8f);
        if (!ModUiAuditSongSelectionActive(modal))
        {
            ModUiAuditSongSelectionBlocked(run, "PlaylistManager", "ImageSelectionModal",
                "PlaylistDetailsViewController.OpenImageSelectionModal completed without an active production image-selection modal.");
            yield break;
        }
        run.Entrances.Add(new
        {
            kind = "song-selection-extra",
            scope = "PlaylistManager",
            target = "ImageSelectionModal",
            status = "verified",
            action = "read-only-open-cancel",
            controllerType = imageController?.GetType().FullName,
            modalType = modal.GetType().FullName
        });
        yield return ModUiAuditCaptureSongSelectionPage(run, modal, camera, "PlaylistManager",
            "PlaylistManager image selection", "playlist-manager-image-selection",
            "PlaylistManager.UI.ImageSelectionModalController", "OpenImageSelectionModal");

        object parserParams = GetMember(imageController, "parserParams");
        string closeDetail = string.Empty;
        bool closed = parserParams != null && ModUiAuditInvokeSongSelection(parserParams,
            "EmitEvent", new object[] { "close-modal" }, out closeDetail);
        if (!closed)
        {
            closed = ModUiAuditInvokeModal(modal, "Hide", out _, out closeDetail);
        }
        yield return ModUiAuditWait(run, () => !ModUiAuditSongSelectionActive(modal), 4, 5f);
        if (!closed || ModUiAuditSongSelectionActive(modal))
        {
            ModUiAuditSongSelectionBlocked(run, "PlaylistManager", "ImageSelectionModal",
                "Image-selection cancel failed: " + closeDetail);
        }
    }

    IEnumerator ModUiAuditPlaylistManagerChangelogPage(ModUiAuditRun run, Camera camera)
    {
        Type changelogType = FindTrainingType("PlaylistManager.UI.ChangelogViewController");
        object changelog = ModUiAuditTryResolveSongSelection(changelogType);
        Component view = changelog as Component;
        if (view == null || !ModUiAuditSongSelectionActive(view))
        {
            run.Entrances.Add(new
            {
                kind = "song-selection-extra",
                scope = "PlaylistManager",
                target = "ChangelogView",
                status = "not-applicable",
                controllerType = changelogType?.FullName,
                reason = "Current PlaylistManager source binds ChangelogViewController but exposes no production flow/menu entry that presents it in native song selection."
            });
            yield break;
        }
        run.Entrances.Add(new
        {
            kind = "song-selection-extra",
            scope = "PlaylistManager",
            target = "ChangelogView",
            status = "verified",
            action = "read-only-open",
            controllerType = changelog.GetType().FullName
        });
        yield return ModUiAuditCaptureSongSelectionPage(run, view, camera, "PlaylistManager",
            "PlaylistManager changelog", "playlist-manager-changelog",
            "PlaylistManager.UI.ChangelogViewController", "native-entry");
    }

    IEnumerator ModUiAuditPlaylistManagerDetailsModal(ModUiAuditRun run, Camera camera)
    {
        Type buttonsType = FindTrainingType("PlaylistManager.UI.PlaylistDetailViewButtonsController");
        object buttons = ModUiAuditTryResolveSongSelection(buttonsType);
        object selected = GetMember(buttons, "selectedPlaylist");
        object details = ModUiAuditTryResolveSongSelection(FindTrainingType("PlaylistManager.UI.PlaylistDetailsViewController"));
        bool stagedExisting = false;
        if (buttons != null && selected == null)
        {
            Type utilities = FindTrainingType("PlaylistManager.Utilities.PlaylistLibUtils");
            var packs = AccessTools.Method(utilities, "TryGetAllPlaylistsAsLevelPacks")?.Invoke(null, null) as System.Collections.IEnumerable;
            object pack = packs?.Cast<object>().FirstOrDefault();
            object manager = GetStaticMember(FindTrainingType("BeatSaberPlaylistsLib.PlaylistManager"), "DefaultManager");
            if (pack != null && manager != null && details != null)
            {
                string selectionDetail;
                stagedExisting = ModUiAuditInvokeSongSelection(buttons, "LevelCollectionUpdated", new[] { pack, manager }, out selectionDetail) &&
                    ModUiAuditInvokeSongSelection(details, "LevelCollectionUpdated", new[] { pack, manager }, out selectionDetail);
                selected = GetMember(buttons, "selectedPlaylist");
            }
        }
        if (buttons == null || selected == null)
        {
            ModUiAuditSongSelectionBlocked(run, "PlaylistManager", "PlaylistDetailsModal",
                "No selected playlist was exposed by PlaylistDetailViewButtonsController; edit modal was withheld.");
            yield break;
        }
        if (!ModUiAuditInvokeSafeLazyAction(buttons, "ShowDetails", out string detail))
        {
            ModUiAuditSongSelectionBlocked(run, "PlaylistManager", "PlaylistDetailsModal", detail);
            yield break;
        }
        yield return null;
        Component root = ModUiAuditSongSelectionComponent(buttons, "rootTransform") ??
            ModUiAuditSongSelectionComponent(ModUiAuditTryResolveSongSelection(
                FindTrainingType("PlaylistManager.UI.PlaylistDetailsViewController")), "modalTransform");
        if (ModUiAuditActiveSongSelectionModals() == 0)
        {
            ModUiAuditSongSelectionBlocked(run, "PlaylistManager", "PlaylistDetailsModal",
                "ShowDetails completed without an active PlaylistManager edit modal.");
            yield break;
        }
        run.Entrances.Add(new
        {
            kind = "song-selection-extra",
            scope = "PlaylistManager",
            target = "PlaylistDetailsModal",
            status = "verified",
            action = "read-only-open"
        });
        yield return ModUiAuditCaptureActiveModalsAfterAction(run, root, camera, "PlaylistManager", "PlaylistDetailsModal");
        yield return ModUiAuditPlaylistManagerImageSelectionModal(run, details, camera);
        // OnDelete only opens the confirmation; its DeleteButtonPressed callback is never invoked.
        if (ModUiAuditInvokeSafeLazyAction(buttons, "OnDelete", out detail))
        {
            yield return ModUiAuditWaitLayout(root, 3, 1.5f);
            yield return ModUiAuditCaptureActiveModalsAfterAction(run, root, camera, "PlaylistManager", "DeletePlaylistConfirmation");
        }
        else ModUiAuditSongSelectionBlocked(run, "PlaylistManager", "DeletePlaylistConfirmation", detail);
        if (stagedExisting)
        {
            ModUiAuditInvokeSongSelection(buttons, "LevelCollectionUpdated", new object[] { null, null }, out detail);
            ModUiAuditInvokeSongSelection(details, "LevelCollectionUpdated", new object[] { null, null }, out detail);
        }
    }

    IEnumerator ModUiAuditPlaylistManagerFoldersPage(ModUiAuditRun run, Camera camera)
    {
        Type foldersType = FindTrainingType("PlaylistManager.UI.FoldersViewController");
        object folders = ModUiAuditTryResolveSongSelection(foldersType);
        Type managerType = FindTrainingType("BeatSaberPlaylistsLib.PlaylistManager");
        object manager = GetStaticMember(managerType, "DefaultManager");
        if (folders == null || manager == null)
        {
            ModUiAuditSongSelectionBlocked(run, "PlaylistManager", "FoldersView",
                "FoldersViewController or BeatSaberPlaylistsLib.PlaylistManager.DefaultManager is unavailable.");
            yield break;
        }
        object original = ReadMemberAny(folders, "CurrentParentManager", "_currentParentManager");
        Component initialRoot = ModUiAuditSongSelectionComponent(folders, "rootTransform");
        bool wasVisible = initialRoot != null && initialRoot.gameObject.activeSelf;
        string categoryDetail;
        ModUiAuditInvokeSongSelection(folders, "LevelCategoryUpdated", new object[] { SelectLevelCategoryViewController.LevelCategory.CustomSongs, false }, out categoryDetail);
        MethodInfo setup = folders.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(value => value.Name == "SetupList" && value.GetParameters().Length == 2);
        string detail = string.Empty;
        bool setupSucceeded = setup != null && ModUiAuditInvokeSongSelection(folders, "SetupList", new object[] { manager, false }, out detail);
        if (!setupSucceeded)
        {
            ModUiAuditSongSelectionBlocked(run, "PlaylistManager", "FoldersView",
                string.IsNullOrEmpty(detail) ? "FoldersViewController.SetupList(PlaylistManager, false) is unavailable." : detail);
            yield break;
        }
        yield return null;
        Component root = ModUiAuditSongSelectionComponent(folders, "rootTransform");
        run.Entrances.Add(new
        {
            kind = "song-selection-extra",
            scope = "PlaylistManager",
            target = "FoldersView",
            status = "verified",
            action = "read-only-open"
        });
        yield return ModUiAuditCaptureSongSelectionPage(run, root, camera, "PlaylistManager",
            "PlaylistManager folders", "playlist-manager-folders", "PlaylistManager.UI.FoldersViewController", "SetupList");
        if (original != null)
            ModUiAuditInvokeSongSelection(folders, "SetupList", new object[] { original, false }, out detail);
        if (initialRoot != null && !wasVisible) initialRoot.gameObject.SetActive(false);
    }

    IEnumerator ModUiAuditPlaylistManagerSettingsPage(ModUiAuditRun run, Camera camera)
    {
        object buttons = ModUiAuditTryResolveSongSelection(FindTrainingType("PlaylistManager.UI.PlaylistViewButtonsController"));
        // PlaylistViewButtonsController is constructed with the production
        // MainFlowCoordinator.  During SoloFreePlay the parent MainFlow is a
        // real object but isActivated is false while its child is active, so
        // the general "active main" helper intentionally cannot find it.
        object main = GetMember(buttons, "mainFlowCoordinator") ?? ModUiAuditFindActiveMainFlow();
        if (main == null)
        {
            Type mainType = FindTrainingType("MainFlowCoordinator");
            try
            {
                main = Resources.FindObjectsOfTypeAll(mainType).OfType<Component>()
                    .FirstOrDefault(value => value != null && value.gameObject.scene.IsValid());
            }
            catch { }
        }
        HMUI.FlowCoordinator parent = ModUiAuditYoungestFlow(main) as HMUI.FlowCoordinator;
        object flow = GetMember(buttons, "playlistManagerFlowCoordinator") ??
            ModUiAuditTryResolveSongSelection(FindTrainingType("PlaylistManager.UI.PlaylistManagerFlowCoordinator"));
        object settings = GetMember(flow, "settingsViewController") ??
            ModUiAuditTryResolveSongSelection(FindTrainingType("PlaylistManager.UI.SettingsViewController"));
        run.Entrances.Add(new
        {
            kind = "song-selection-diagnostic",
            scope = "PlaylistManager",
            target = "SettingsFlow",
            phase = "production-resolution",
            mainType = main?.GetType().FullName,
            parentType = parent?.GetType().FullName,
            buttonsType = buttons?.GetType().FullName,
            buttonsState = ModUiAuditSongSelectionState(buttons),
            buttonsInterfaces = ModUiAuditSongSelectionInterfaceNames(buttons?.GetType()),
            buttonsSource = ModUiAuditPlaylistManagerDependencySource(buttons),
            flowType = flow?.GetType().FullName,
            flowState = ModUiAuditSongSelectionState(flow),
            flowSource = ModUiAuditPlaylistManagerDependencySource(flow),
            settingsType = settings?.GetType().FullName,
            settingsSource = ModUiAuditPlaylistManagerDependencySource(settings),
            flowResolution = buttons == null ? ModUiAuditSongSelectionResolutionDiagnostics(FindTrainingType("PlaylistManager.UI.PlaylistViewButtonsController")) : string.Empty
        });
        if (main == null || parent == null || buttons == null || flow == null || settings == null)
        {
            ModUiAuditSongSelectionBlocked(run, "PlaylistManager", "SettingsFlow",
                "PlaylistManager native PlaylistViewButtonsController, its production flow/settings fields, or active Solo parent flow is unavailable.");
            yield break;
        }
        bool wasActive = ModUiAuditFlowActive(flow);
        string detail = string.Empty;
        // This is the exact production [UIAction("flow-click")] path from
        // PlaylistViewButtonsController, which calls
        // PlaylistManagerFlowCoordinator.PresentFlowCoordinator on the live
        // MainFlowCoordinator child.  Do not synthesize a replacement flow.
        bool presented = wasActive || ModUiAuditInvokeSafeLazyAction(buttons, "ShowSettings", out detail);
        if (!presented)
        {
            ModUiAuditSongSelectionBlocked(run, "PlaylistManager", "SettingsFlow", detail);
            yield break;
        }
        yield return ModUiAuditWait(run, () => ModUiAuditFlowActive(flow), 8, 12f);
        Component root = settings as Component ?? ModUiAuditSongSelectionComponent(settings, "rootTransform");
        yield return ModUiAuditCaptureSongSelectionPage(run, root, camera, "PlaylistManager",
            "PlaylistManager settings", "playlist-manager-settings", "PlaylistManager.UI.SettingsViewController", "PresentFlowCoordinator");
        object contributors = GetMember(flow, "contributorsViewController");
        Component contributorsRoot = contributors as Component ?? ModUiAuditSongSelectionComponent(contributors, "rootTransform");
        if (!ModUiAuditSongSelectionActive(contributorsRoot))
        {
            ModUiAuditSongSelectionBlocked(run, "PlaylistManager", "ContributorsSideView",
                "PlaylistManager settings flow did not expose an active production ContributorsViewController side view.");
        }
        else
        {
            run.Entrances.Add(new
            {
                kind = "song-selection-extra",
                scope = "PlaylistManager",
                target = "ContributorsSideView",
                status = "verified",
                action = "read-only-open"
            });
            yield return ModUiAuditCaptureSongSelectionPage(run, contributorsRoot, camera, "PlaylistManager",
                "PlaylistManager contributors", "playlist-manager-contributors", "PlaylistManager.UI.ContributorsViewController", "PresentFlowCoordinator");
        }
        if (!wasActive && ModUiAuditFlowActive(flow) && !ModUiAuditDismissFlow(main, flow, out string dismissDetail))
            ModUiAuditSongSelectionBlocked(run, "PlaylistManager", "SettingsFlow", dismissDetail);
    }

    IEnumerator ModUiAuditPlaylistManagerQueue(ModUiAuditRun run, Camera camera)
    {
        object buttons = ModUiAuditTryResolveSongSelection(FindTrainingType("PlaylistManager.UI.PlaylistViewButtonsController"));
        if (buttons == null)
        {
            ModUiAuditSongSelectionBlocked(run, "PlaylistManager", "DownloadQueueModal",
                "PlaylistViewButtonsController was unavailable for the production queue button.");
            yield break;
        }
        if (!ModUiAuditInvokeSafeLazyAction(buttons, "ShowQueue", out string detail))
        {
            ModUiAuditSongSelectionBlocked(run, "PlaylistManager", "DownloadQueueModal", detail);
            yield break;
        }
        yield return null;
        if (ModUiAuditActiveSongSelectionModals() == 0)
        {
            ModUiAuditSongSelectionBlocked(run, "PlaylistManager", "DownloadQueueModal",
                "PlaylistViewButtonsController.ShowQueue did not expose an active queue modal.");
            yield break;
        }
        run.Entrances.Add(new
        {
            kind = "song-selection-extra",
            scope = "PlaylistManager",
            target = "DownloadQueueModal",
            status = "verified",
            action = "read-only-open"
        });
        yield return ModUiAuditCaptureActiveModalsAfterAction(run,
            ModUiAuditSongSelectionComponent(buttons, "rootTransform"), camera,
            "PlaylistManager", "DownloadQueueModal");
    }

    IEnumerator ModUiAuditPlaylistManagerExtras(ModUiAuditRun run, Camera camera)
    {
        yield return ModUiAuditPlaylistManagerAddModal(run, camera);
        yield return ModUiAuditSongCoreRequirements(run, camera);
        yield return ModUiAuditPlaylistManagerDetailsModal(run, camera);
        yield return ModUiAuditPlaylistManagerFoldersPage(run, camera);
        yield return ModUiAuditPlaylistManagerQueue(run, camera);
        yield return ModUiAuditPlaylistManagerSettingsPage(run, camera);
        yield return ModUiAuditPlaylistManagerChangelogPage(run, camera);
    }

    /// <summary>
    /// Audits read-only settings/filter/playlist surfaces after the caller has
    /// entered native SoloFreePlay song selection.  The selected existing song
    /// may be changed through the native table callback to populate its detail
    /// view; surfaces below only open existing views and restore them.  No save,
    /// download, delete-confirm callback, or user-input callback is invoked.
    /// </summary>
    IEnumerator ModUiAuditSongSelectionExtras(ModUiAuditRun run, Camera camera)
    {
        if (run == null)
            yield break;
        yield return ModUiAuditBetterSongListExtras(run, camera);
        yield return ModUiAuditAdvancedSongFiltersExtra(run, camera);
        yield return ModUiAuditAdvancedFilterSubpages(run, camera);
        yield return ModUiAuditPlaylistManagerExtras(run, camera);
    }
}
