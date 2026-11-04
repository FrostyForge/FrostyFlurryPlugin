using System;

namespace Flurry.Editor.DataExplorer
{
    internal static class FlurryExplorerTheme
    {
        private static bool applied;

        public static void Apply()
        {
            if (applied) return;
            var uri = new Uri("pack://application:,,,/FlurryEditorPlugin;component/DataExplorer/FlurryExplorerResources.xaml");
            var dict = new System.Windows.ResourceDictionary { Source = uri };
            System.Windows.Application.Current.Resources.MergedDictionaries.Add(dict);
            applied = true;
        }
    }
}
