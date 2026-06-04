using Frosty.Core;
using Frosty.Core.Mod;
using HarmonyLib;
using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace Flurry.Manager.Patches
{
    public class ModSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            IFrostyMod mod = value as IFrostyMod;
            if (mod == null) return "N/A";

            try
            {
                if (!File.Exists(mod.Path))
                    return "N/A";

                long bytes = new FileInfo(mod.Path).Length;

                string baseName = mod.Path.Replace(".fbmod", "");
                string dir = Path.GetDirectoryName(mod.Path);
                if (dir != null)
                {
                    foreach (var archiveFile in Directory.GetFiles(dir, Path.GetFileName(baseName) + "*.archive"))
                    {
                        bytes += new FileInfo(archiveFile).Length;
                    }
                }

                if (bytes >= 1073741824)
                    return $"{bytes / 1073741824.0:F2} GB";
                if (bytes >= 1048576)
                    return $"{bytes / 1048576.0:F1} MB";
                if (bytes >= 1024)
                    return $"{bytes / 1024.0:F0} KB";
                return $"{bytes} B";
            }
            catch
            {
                return "N/A";
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    [HarmonyPatch(typeof(FrostyModManager.Controls.FrostyModDescription))]
    [HarmonyPatchCategory("flurry.manager")]
    public class ModDescriptionScreenshotPatch
    {
        [HarmonyPatch("FrostyModDescription_Loaded")]
        [HarmonyPostfix]
        public static void FrostyModDescription_Loaded_Postfix(FrostyModManager.Controls.FrostyModDescription __instance)
        {
            try
            {
                var modProp = __instance.GetType().GetProperty("Mod");
                var mod = modProp?.GetValue(__instance) as IFrostyMod;
                if (mod == null) return;

                bool hasScreenshots = mod.ModDetails.Screenshots != null && mod.ModDetails.Screenshots.Count > 0;

                __instance.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var screenshotPanel = Traverse.Create(__instance).Field("screenshotPanel").GetValue<StackPanel>();
                        if (screenshotPanel != null)
                        {
                            var scrollViewer = screenshotPanel.Parent as ScrollViewer;
                            if (scrollViewer != null)
                            {
                                scrollViewer.Visibility = hasScreenshots ? Visibility.Visible : Visibility.Collapsed;
                            }
                        }
                    }
                    catch { }
                }), DispatcherPriority.ContextIdle);
            }
            catch { }
        }
    }
}
