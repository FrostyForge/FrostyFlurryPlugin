using Flurry.Editor.Editors;
using Frosty.Controls;
using Frosty.Core;
using FrostySdk.Interfaces;
using HarmonyLib;
using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;

namespace Flurry.Editor
{
    public class HarmonyPatcherAction : StartupAction
    {
        public override Action<ILogger> Action => logger =>
        {
            ExtractEmbeddedMappings(logger);

            FlurryLocalConfigRedirect.ApplyRuntimeRedirect(PluginManagerType.Editor);

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

        private void ExtractEmbeddedMappings(ILogger logger)
        {
            string flurryDataDir = Path.Combine(
                Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? AppDomain.CurrentDomain.BaseDirectory,
                "Plugins", "FlurryData");

            string[] mappings = { "FacePoserMappings.json", "WeaponMappings.json" };
            foreach (var mapping in mappings)
            {
                string destPath = Path.Combine(flurryDataDir, mapping);
                if (File.Exists(destPath))
                    continue;

                try
                {
                    string resourceName = $"Flurry.Editor.Data.{mapping}";
                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                    {
                        if (stream == null)
                        {
                            logger.LogWarning($"[Flurry] Embedded resource not found: {resourceName}");
                            continue;
                        }

                        Directory.CreateDirectory(flurryDataDir);
                        using (FileStream fs = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                            stream.CopyTo(fs);

                        logger.Log($"[Flurry] Extracted {mapping} to {destPath}");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"[Flurry] Failed to extract {mapping}: {ex.Message}");
                }
            }
        }

        private void ApplyEditorOnlyPatches(ILogger taskLogger)
        {
            taskLogger.Log("[Flurry] Applying editor patches...");
            var harmony = new Harmony("io.github.adamraichu.frosty.flurry.editor");
            harmony.PatchCategory("flurry.editor");
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
