using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace StorageOverview
{
    /// <summary>
    /// Inspect tab button next to the vanilla Storage tab. It never shows a panel: opening
    /// it opens the overview table focused on the selected storage.
    /// </summary>
    public class ITab_StorageOverview : ITab
    {
        public ITab_StorageOverview()
        {
            size = new Vector2(10f, 10f);
            labelKey = "StorageOverview_TabLabel";
        }

        /// <summary>
        /// Runs on the first frame the tab is open. The inspect pane sets its open tab after
        /// OnOpen, so the tab is closed here rather than there.
        /// </summary>
        protected override void FillTab()
        {
            IStoreSettingsParent focus = OwnerOf(SelObject);
            if (MainButtonDefOf.Inspect.TabWindow is MainTabWindow_Inspect inspect)
            {
                inspect.OpenTabType = null;
            }
            MainButtonDef def = DefDatabase<MainButtonDef>.GetNamed("StorageOverview");
            if (def.TabWindow is MainTabWindow_StorageOverview window)
            {
                window.FocusOn(focus);
            }
            Find.MainTabsRoot.SetCurrentTab(def);
        }

        /// <summary>The settings owner the overview shows as a column for this selection.</summary>
        public static IStoreSettingsParent OwnerOf(object selected)
        {
            switch (selected)
            {
                case IStorageGroupMember member when member.Group != null:
                    return member.Group;
                case IStoreSettingsParent parent:
                    return parent;
                default:
                    return null;
            }
        }
    }

    /// <summary>Adds the overview tab wherever the vanilla Storage tab is shown.</summary>
    [StaticConstructorOnStartup]
    public static class StorageOverviewStartup
    {
        static StorageOverviewStartup()
        {
            InspectTabBase tab = InspectTabManager.GetSharedInstance(typeof(ITab_StorageOverview));
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def.inspectorTabsResolved == null || def.inspectorTabsResolved.Contains(tab)
                    || !def.inspectorTabsResolved.Any(t => t is ITab_Storage))
                {
                    continue;
                }
                def.inspectorTabs ??= new System.Collections.Generic.List<System.Type>();
                def.inspectorTabs.Add(typeof(ITab_StorageOverview));
                def.inspectorTabsResolved.Add(tab);
            }
            new Harmony("ifchen0.storageoverview").PatchAll();
            StorageOverviewMod.ApplyEntryPoint();
        }
    }

    /// <summary>Stockpile zones list their tabs in code, so the overview tab is appended here.</summary>
    [HarmonyPatch(typeof(Zone_Stockpile), nameof(Zone_Stockpile.GetInspectTabs))]
    public static class Zone_Stockpile_GetInspectTabs_Patch
    {
        private static InspectTabBase[] tabs;

        public static void Postfix(ref System.Collections.Generic.IEnumerable<InspectTabBase> __result)
        {
            tabs ??= __result.Append(InspectTabManager.GetSharedInstance(typeof(ITab_StorageOverview))).ToArray();
            __result = tabs;
        }
    }

    /// <summary>
    /// Hides the overview tab unless it is the chosen entry point, and otherwise keeps it
    /// leftmost (tabs are drawn right to left in list order) so it never takes the place of
    /// a tab players already click by habit, whatever other mods add and whenever they add
    /// it. Only rebuilds the list when needed, into a reused buffer.
    /// </summary>
    [HarmonyPatch(typeof(MainTabWindow_Inspect), nameof(MainTabWindow_Inspect.CurTabs), MethodType.Getter)]
    public static class MainTabWindow_Inspect_CurTabs_Patch
    {
        private static readonly System.Collections.Generic.List<InspectTabBase> buffer =
            new System.Collections.Generic.List<InspectTabBase>();

        public static void Postfix(ref System.Collections.Generic.IEnumerable<InspectTabBase> __result)
        {
            if (__result == null)
            {
                return;
            }
            InspectTabBase overview = null;
            InspectTabBase last = null;
            foreach (InspectTabBase tab in __result)
            {
                if (tab is ITab_StorageOverview)
                {
                    overview = tab;
                }
                last = tab;
            }
            bool show = StorageOverviewMod.Settings?.openFromInspectTab ?? false;
            if (overview == null || (show && last == overview))
            {
                return;
            }
            buffer.Clear();
            foreach (InspectTabBase tab in __result)
            {
                if (tab != overview)
                {
                    buffer.Add(tab);
                }
            }
            if (show)
            {
                buffer.Add(overview);
            }
            __result = buffer;
        }
    }
}
