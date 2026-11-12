using System;
using System.Windows;

namespace Flurry.Editor.DataExplorer
{
    /// <summary>
    /// Registers the fast-mode Data Explorer template by merging its resource dictionary
    /// into the application resources. The implicit FrostyDataExplorer style then takes
    /// precedence over FrostyControls' own theme style.
    /// </summary>
    internal static class FlurryExplorerTheme
    {
        private const string ResourcesPackUri = "pack://application:,,,/FlurryEditorPlugin;component/DataExplorer/FlurryExplorerResources.xaml";

        private static bool applied;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;

            Application.Current?.Resources.MergedDictionaries.Add(
                new ResourceDictionary { Source = new Uri(ResourcesPackUri) });
        }
    }
}