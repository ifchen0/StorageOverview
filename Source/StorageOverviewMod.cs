using RimWorld;
using UnityEngine;
using Verse;

namespace StorageOverview
{
    /// <summary>Where the table is opened from: one entry point at a time.</summary>
    public class StorageOverviewSettings : ModSettings
    {
        /// <summary>False: main button bar (default). True: Overview inspect tab on storages.</summary>
        public bool openFromInspectTab;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref openFromInspectTab, "openFromInspectTab", false);
        }
    }

    public class StorageOverviewMod : Mod
    {
        public static StorageOverviewSettings Settings { get; private set; }

        public StorageOverviewMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<StorageOverviewSettings>();
        }

        public override string SettingsCategory() => "Storage Overview";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Label("StorageOverview_SettingOpenFrom".Translate());
            if (listing.RadioButton("StorageOverview_OpenFromMainBar".Translate(), !Settings.openFromInspectTab))
            {
                Settings.openFromInspectTab = false;
            }
            if (listing.RadioButton("StorageOverview_OpenFromInspectTab".Translate(), Settings.openFromInspectTab))
            {
                Settings.openFromInspectTab = true;
            }
            listing.End();
            ApplyEntryPoint();
        }

        /// <summary>
        /// Shows the main bar button only when it is the chosen entry point. The inspect tab
        /// is filtered in MainTabWindow_Inspect_CurTabs_Patch, so both switch without restart.
        /// </summary>
        public static void ApplyEntryPoint()
        {
            MainButtonDef def = DefDatabase<MainButtonDef>.GetNamedSilentFail("StorageOverview");
            if (def != null && Settings != null)
            {
                def.buttonVisible = !Settings.openFromInspectTab;
            }
        }
    }
}
