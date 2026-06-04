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

        [Category("_General")]
        [DisplayName("Blocked Log Regex Patterns")]
        [Description("Semicolon or newline-separated regex patterns. Matching log lines are hidden.")]
        [Editor(typeof(FrostyStringEditor))]
        [EbxFieldMeta(EbxFieldType.String)]
        public string BlockedLogRegexPatterns { get; set; } = string.Empty;

        [Category("_General")]
        [DisplayName("Use Local Config Directory")]
        [Description("Store Flurry config redirection in a local directory for this instance.")]
        [Editor(typeof(FrostyBooleanEditor))]
        [EbxFieldMeta(EbxFieldType.Boolean)]
        public bool UseLocalConfigDirectory { get; set; } = false;

        [Category("_General")]
        [DisplayName("Local Config Directory")]
        [Description("Directory used when local config mode is enabled.")]
        [Editor(typeof(FrostyStringEditor))]
        [EbxFieldMeta(EbxFieldType.String)]
        public string LocalConfigDirectory { get; set; } = string.Empty;

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
        [DisplayName("Enable Kyber Integration")]
        [Description("If enabled, adds Kyber buttons to the editor UI.")]
        [Editor(typeof(FrostyBooleanEditor))]
        [EbxFieldMeta(EbxFieldType.Boolean)]
        public bool KyberIntegration { get; set; } = true;

        public override void Load()
        {
            HarmonyDebug = Config.Get<bool>("Flurry.HarmonyDebug", false);
            BlockedLogRegexPatterns = Config.Get<string>("Flurry.BlockedLogRegexPatterns", string.Empty);
            UseLocalConfigDirectory = Config.Get<bool>("Flurry.UseLocalConfigDirectory", false);
            LocalConfigDirectory = Config.Get<string>("Flurry.LocalConfigDirectory", string.Empty);

            LocalConfigRedirectState state = FlurryLocalConfigRedirect.LoadState();
            if (state != null && (!string.IsNullOrWhiteSpace(state.DirectoryPath) || state.Enabled))
            {
                UseLocalConfigDirectory = state.Enabled;
                LocalConfigDirectory = state.DirectoryPath ?? string.Empty;
            }

            HasAcknowledgedStartupMessage = Config.Get<bool>("Flurry.HasAcknowledgedStartupMessage", false);
            AutosaveOnExport = Config.Get<bool>("Flurry.AutosaveOnExport", true);
            BlueprintEditorTweaks = Config.Get<bool>("Flurry.BlueprintEditorTweaks", false);
            BookmarksTabTweaks = Config.Get<bool>("Flurry.BookmarksTabTweaks", true);
            ReferencesTabTweaks = Config.Get<bool>("Flurry.ReferencesTabTweaks", true);
            BundlesTabTweaks = Config.Get<bool>("Flurry.BundlesTabTweaks", true);
            KyberIntegration = Config.Get<bool>("Flurry.KyberIntegration", true);
        }

        public override void Save()
        {
            Config.Add("Flurry.HarmonyDebug", HarmonyDebug);
            Config.Add("Flurry.BlockedLogRegexPatterns", BlockedLogRegexPatterns ?? string.Empty);
            Config.Add("Flurry.UseLocalConfigDirectory", UseLocalConfigDirectory);
            Config.Add("Flurry.LocalConfigDirectory", LocalConfigDirectory ?? string.Empty);
            Config.Add("Flurry.HasAcknowledgedStartupMessage", HasAcknowledgedStartupMessage);
            Config.Add("Flurry.AutosaveOnExport", AutosaveOnExport);
            Config.Add("Flurry.BlueprintEditorTweaks", BlueprintEditorTweaks);
            Config.Add("Flurry.BookmarksTabTweaks", BookmarksTabTweaks);
            Config.Add("Flurry.ReferencesTabTweaks", ReferencesTabTweaks);
            Config.Add("Flurry.BundlesTabTweaks", BundlesTabTweaks);
            Config.Add("Flurry.KyberIntegration", KyberIntegration);

            FlurryLocalConfigRedirect.SaveState(UseLocalConfigDirectory, LocalConfigDirectory);
            Config.Save();
        }

        public override bool Validate()
        {
            return true;
        }
    }
}
