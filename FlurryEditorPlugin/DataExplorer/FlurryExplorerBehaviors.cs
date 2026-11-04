using System.Windows;
using Frosty.Core.Controls;

namespace Flurry.Editor.DataExplorer
{
    public static class FlurryExplorerBehaviors
    {
        public static readonly DependencyProperty ShowOnlyUnModifiedProperty = DependencyProperty.RegisterAttached(
            "ShowOnlyUnModified", typeof(bool), typeof(FlurryExplorerBehaviors),
            new FrameworkPropertyMetadata(false, OnShowOnlyChanged));
        public static bool GetShowOnlyUnModified(DependencyObject d) { return (bool)d.GetValue(ShowOnlyUnModifiedProperty); }
        public static void SetShowOnlyUnModified(DependencyObject d, bool value) { d.SetValue(ShowOnlyUnModifiedProperty, value); }

        public static readonly DependencyProperty ShowOnlyUnsavedModifiedProperty = DependencyProperty.RegisterAttached(
            "ShowOnlyUnsavedModified", typeof(bool), typeof(FlurryExplorerBehaviors),
            new FrameworkPropertyMetadata(false, OnShowOnlyChanged));
        public static bool GetShowOnlyUnsavedModified(DependencyObject d) { return (bool)d.GetValue(ShowOnlyUnsavedModifiedProperty); }
        public static void SetShowOnlyUnsavedModified(DependencyObject d, bool value) { d.SetValue(ShowOnlyUnsavedModifiedProperty, value); }

        public static readonly DependencyProperty ShowOnlyAddedProperty = DependencyProperty.RegisterAttached(
            "ShowOnlyAdded", typeof(bool), typeof(FlurryExplorerBehaviors),
            new FrameworkPropertyMetadata(false, OnShowOnlyChanged));
        public static bool GetShowOnlyAdded(DependencyObject d) { return (bool)d.GetValue(ShowOnlyAddedProperty); }
        public static void SetShowOnlyAdded(DependencyObject d, bool value) { d.SetValue(ShowOnlyAddedProperty, value); }

        private static void OnShowOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            FrostyDataExplorer ctrl = d as FrostyDataExplorer;
            if (ctrl == null) return;
            if (!Patches.FlurryDataExplorerFastMode.Enabled) return;

            FlurryExplorerState state = FlurryExplorerEngine.GetState(ctrl);
            if (state.SuppressModified) return;

            if ((bool)e.NewValue)
            {
                state.SuppressModified = true;
                try
                {
                    if (e.Property == ShowOnlyUnModifiedProperty)
                    {
                        if (ctrl.ShowOnlyModified)
                            ctrl.SetValue(FrostyDataExplorer.ShowOnlyModifiedProperty, false);

                        SetShowOnlyUnsavedModified(ctrl, false);
                        SetShowOnlyAdded(ctrl, false);
                    }
                    else
                    {
                        SetShowOnlyUnModified(ctrl, false);
                    }
                }
                finally { state.SuppressModified = false; }
            }

            FlurryExplorerEngine.UpdateTreeViewAsync(ctrl, state);
        }
    }
}
