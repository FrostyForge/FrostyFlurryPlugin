using Flurry.Editor.Editors;
using Frosty.Controls;
using Frosty.Core;
using FrostySdk.Interfaces;
using HarmonyLib;
using System;
using System.Windows;
using System.Windows.Media;

namespace Flurry.Editor
{
    public class HarmonyPatcherAction : StartupAction
    {
        public override Action<ILogger> Action => logger =>
        {
            FlurryEditorConfig config = new FlurryEditorConfig();
            config.Load();
            Harmony.DEBUG = config.HarmonyDebug;

            FileLog.Debug("ManagerType: " + App.PluginManager.ManagerType);
            switch (App.PluginManager.ManagerType)
            {
                case PluginManagerType.Editor:
                    ApplyEditorOnlyPatches(logger);
                    break;
            }
        };

        private void ApplyEditorOnlyPatches(ILogger taskLogger)
        {
            taskLogger.Log("[Flurry] Applying editor patches...");
            var harmony = new Harmony("io.github.adamraichu.frosty.flurry.editor");
            harmony.PatchCategory("flurry.editor");

            FlurryEditorConfig config = new FlurryEditorConfig();
            config.Load();
            if (config.DataExplorerFastMode)
            {
                taskLogger.Log("[Flurry] Enabling fast Data Explorer...");
                Patches.FlurryDataExplorerFastMode.Enabled = true;
                DataExplorer.FlurryExplorerTheme.Apply();
                harmony.PatchCategory("flurry.dataexplorer");
            }
        }
    }

    public class LoadOrderEditorMenuExt : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Kyber Tooling";
        public override string MenuItemName => "Open Launch Overrides Editor";
        public ImageSource ParentIcon => Icon;
        public override ImageSource Icon => new ImageSourceConverter().ConvertFromString("pack://application:,,,/FlurryEditorPlugin;component/Images/KyberCog.png") as ImageSource;
        public override RelayCommand MenuItemClicked => new RelayCommand((object o) => {
            FlurryEditorConfig config = new FlurryEditorConfig();
            config.Load();
            if (!config.KyberIntegration)
            {
                FrostyMessageBox.Show("Kyber Integration is not enabled in the Flurry Editor Plugin settings. Please enable it to use the Load Order Editor.", "Kyber Integration Disabled", System.Windows.MessageBoxButton.OK);
                return;
            }
            App.EditorWindow.OpenEditor("[Flurry] Kyber Overrides Editor", new KyberLaunchOverridesEditor());
        });
    }

    public class ExportBinaryFileHashesExt : MenuExtension
    {
        public override string TopLevelMenuName => "Debug";
        public override string SubLevelMenuName => "Flurry";
        public override string MenuItemName => "Copy Binary Hash List";

        public override RelayCommand MenuItemClicked => new RelayCommand((object o) =>
        {
            string output = FlurryEditorUtils.GetBinaryFileHashes();
            try
            {
                Clipboard.SetText(output);
                App.Logger.Log("Copied binary hash list to clipboard.");
            }
            catch (Exception ex)
            {
                App.Logger.LogWarning("Failed to copy binary hash list to clipboard: " + ex.ToString());
                App.Logger.LogWarning("Logging the output below:");
                App.Logger.LogWarning(output);
            }
        });
    }
}
