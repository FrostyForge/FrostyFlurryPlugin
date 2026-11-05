using Frosty.Core;
using Frosty.Core.Controls.Editors;
using FrostySdk.Attributes;
using FrostySdk.IO;

namespace Flurry.Editor
{
    [DisplayName("Flurry Config (Editor)")]
    public class FlurryEditorConfig : OptionsExtension
    {
        [Category("_General")]
        [DisplayName("Harmony Debug Logging")]
        [Description("Outputs a log file to Desktop/harmony.log.txt.")]
        [Editor(typeof(FrostyBooleanEditor))]
        [EbxFieldMeta(EbxFieldType.Boolean)]
        public bool HarmonyDebug { get; set; } = false;

        [Category("zz__Meta__DoNotEdit")]
        [IsHidden()]
        public bool HasAcknowledgedStartupMessage { get; set; } = false;
        [Category("zz__Meta__DoNotEdit")]
        [IsHidden()]
        public int LastChangelogViewed { get; set; } = -1;

        [Category("_General")]
        [DisplayName("Autosave on Export")]
        [Description("Create a backup of your project file when exporting a mod (includes kyber launch).")]
        [Editor(typeof(FrostyBooleanEditor))]
        [EbxFieldMeta(EbxFieldType.Boolean)]
        public bool AutosaveOnExport { get; set; } = true;

        [Category("Additional Tweaks")]
        [DisplayName("Enable Blueprint Editor Tweaks")]
        [Description("Enable this if using the Graph Editor for blueprints.")]
        [Editor(typeof(FrostyBooleanEditor))]
        [EbxFieldMeta(EbxFieldType.Boolean)]
        //[DependsOn("updateCheck")]
        public bool BlueprintEditorTweaks { get; set; } = false;

        [Category("Additional Tweaks")]
        [DisplayName("Enable Bookmarks Tab Tweaks")]
        [Description("If you're having issues related to the bookmarks menu, disable this.")]
        [Editor(typeof(FrostyBooleanEditor))]
        [EbxFieldMeta(EbxFieldType.Boolean)]
        public bool BookmarksTabTweaks { get; set; } = true;

        [Category("Additional Tweaks")]
        [DisplayName("Enable References Tab Tweaks")]
        [Description("If you're having issues related to the references tab, disable this.")]
        [Editor(typeof(FrostyBooleanEditor))]
        [EbxFieldMeta(EbxFieldType.Boolean)]
        public bool ReferencesTabTweaks { get; set; } = true;

        [Category("Additional Tweaks")]
        [DisplayName("Enable Bundles Tab Tweaks")]
        [Description("If you're having issues related to the bundles tab, disable this.")]
        [Editor(typeof(FrostyBooleanEditor))]
        [EbxFieldMeta(EbxFieldType.Boolean)]
        public bool BundlesTabTweaks { get; set; } = true;

        [Category("Additional Tweaks")]
        [DisplayName("Enable Fast Data Explorer")]
        [Description("Replaces the Data Explorer's tree building and filtering with a virtualized, background-threaded implementation (ported from Frosty-M24). Takes effect after restarting the editor.")]
        [Editor(typeof(FrostyBooleanEditor))]
        [EbxFieldMeta(EbxFieldType.Boolean)]
        public bool DataExplorerFastMode { get; set; } = true;

        [Category("Additional Tweaks")]
        [DisplayName("Enable Kyber Integration")]
        [Description("If enabled, adds Kyber buttons to the editor UI.")]
        [Editor(typeof(FrostyBooleanEditor))]
        [EbxFieldMeta(EbxFieldType.Boolean)]
        public bool KyberIntegration { get; set; } = true;

        public override void Load()
        {
            HarmonyDebug = Config.Get<bool>("Flurry.HarmonyDebug", false);
            HasAcknowledgedStartupMessage = Config.Get<bool>("Flurry.HasAcknowledgedStartupMessage", false);
            AutosaveOnExport = Config.Get<bool>("Flurry.AutosaveOnExport", true);
            BlueprintEditorTweaks = Config.Get<bool>("Flurry.BlueprintEditorTweaks", false);
            BookmarksTabTweaks = Config.Get<bool>("Flurry.BookmarksTabTweaks", true);
            ReferencesTabTweaks = Config.Get<bool>("Flurry.ReferencesTabTweaks", true);
            BundlesTabTweaks = Config.Get<bool>("Flurry.BundlesTabTweaks", true);
            KyberIntegration = Config.Get<bool>("Flurry.KyberIntegration", true);
            DataExplorerFastMode = Config.Get<bool>("Flurry.DataExplorerFastMode", true);
        }

        public override void Save()
        {
            Config.Add("Flurry.HarmonyDebug", HarmonyDebug);
            Config.Add("Flurry.HasAcknowledgedStartupMessage", HasAcknowledgedStartupMessage);
            Config.Add("Flurry.AutosaveOnExport", AutosaveOnExport);
            Config.Add("Flurry.BlueprintEditorTweaks", BlueprintEditorTweaks);
            Config.Add("Flurry.BookmarksTabTweaks", BookmarksTabTweaks);
            Config.Add("Flurry.ReferencesTabTweaks", ReferencesTabTweaks);
            Config.Add("Flurry.BundlesTabTweaks", BundlesTabTweaks);
            Config.Add("Flurry.KyberIntegration", KyberIntegration);
            Config.Add("Flurry.DataExplorerFastMode", DataExplorerFastMode);
            Config.Save();
        }

        public override bool Validate()
        {
            return true;
        }
    }
}
