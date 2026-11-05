using Flurry.Editor.DataExplorer;
using Frosty.Core.Controls;
using FrostySdk.Managers;
using HarmonyLib;
using System.Collections;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Flurry.Editor.Patches
{
    /// <summary>
    /// Redirects FrostyDataExplorer internals to the fast-mode engine. Prefixes suppress the
    /// stock synchronous implementation; postfixes wire template parts Frosty does not know
    /// about. All patches are inert unless <see cref="FlurryExplorerEngine.FastModeEnabled"/>
    /// is set during plugin startup.
    /// </summary>
    [HarmonyPatch(typeof(FrostyDataExplorer))]
    [HarmonyPatchCategory("flurry.dataexplorer")]
    internal static class FrostyDataExplorerFastPatches
    {
        private const string PartTypeComboBoxName = "PART_TypeComboBox";
        private const string PartFilterProgressBarName = "PART_FilterProgressBar";
        private const string PartFilterInProgressBorderName = "PART_FilterInProgressBorder";

        [HarmonyPatch("OnItemsSourceChanged")]
        [HarmonyPrefix]
        static bool OnItemsSourceChanged_Prefix(DependencyObject o, DependencyPropertyChangedEventArgs e)
        {
            if (!FlurryExplorerEngine.FastModeEnabled)
                return true;

            FrostyDataExplorer ctrl = (FrostyDataExplorer)o;
            FlurryExplorerState state = FlurryExplorerEngine.GetState(ctrl);

            state.SelectGeneration++;
            state.ReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> readyTcs = state.ReadyTcs;
            state.BaseTreeDisplayed = false;
            state.DeferredItemsSource = null;
            state.Generation++;
            state.SelectedNode = null;
            state.Root = null;
            state.TypeFilter = null;
            state.TypeFilterFromText = false;
            ctrl.SelectedAsset = null;

            ListView assetListView = FlurryExplorerEngine.AssetListViewRef(ctrl);
            if (assetListView != null)
                assetListView.ItemsSource = null;

            FlurryExplorerEngine.SetProgressVisible(state, ctrl, true);

            Task populateTypes = FlurryExplorerEngine.PopulateAssetTypesComboBox(ctrl, state);
            Task buildMapping = FlurryExplorerEngine.CreateAssetPathMappingAsync(ctrl, state);
            int generation = state.Generation;
            _ = buildMapping.ContinueWith(_ =>
            {
                readyTcs.TrySetResult(true);
                FlurryExplorerEngine.SetProgressVisible(state, ctrl, generation, false);
            }, TaskScheduler.Default);

            return false;
        }

        [HarmonyPatch("OnShowOnlyModifiedChanged")]
        [HarmonyPrefix]
        static bool OnShowOnlyModifiedChanged_Prefix(DependencyObject o, DependencyPropertyChangedEventArgs e)
        {
            if (!FlurryExplorerEngine.FastModeEnabled)
                return true;

            FrostyDataExplorer ctrl = (FrostyDataExplorer)o;
            FlurryExplorerState state = FlurryExplorerEngine.GetState(ctrl);
            if (state.SuppressModified)
                return false;

            if ((bool)e.NewValue && FlurryExplorerBehaviors.GetShowOnlyUnModified(ctrl))
            {
                // Mutually exclusive with the "Unmodified" checkbox. Suppress the behavior's
                // own rebuild: Frosty's original callback runs right after and rebuilds once.
                state.SuppressModified = true;
                try { FlurryExplorerBehaviors.SetShowOnlyUnModified(ctrl, false); }
                finally { state.SuppressModified = false; }
            }

            return true;
        }

        [HarmonyPatch("UpdateTreeView")]
        [HarmonyPrefix]
        static bool UpdateTreeView_Prefix(FrostyDataExplorer __instance)
        {
            if (!FlurryExplorerEngine.FastModeEnabled)
                return true;

            FlurryExplorerState state = FlurryExplorerEngine.GetState(__instance);
            _ = FlurryExplorerEngine.UpdateTreeViewAsync(__instance, state);
            return false;
        }

        [HarmonyPatch("SelectAsset")]
        [HarmonyPrefix]
        static bool SelectAsset_Prefix(FrostyDataExplorer __instance, AssetEntry entry)
        {
            if (!FlurryExplorerEngine.FastModeEnabled)
                return true;

            FlurryExplorerState state = FlurryExplorerEngine.GetState(__instance);
            _ = FlurryExplorerEngine.SelectAssetAsync(__instance, state, entry);
            return false;
        }

        [HarmonyPatch("RefreshAll")]
        [HarmonyPrefix]
        static bool RefreshAll_Prefix(FrostyDataExplorer __instance)
        {
            if (!FlurryExplorerEngine.FastModeEnabled)
                return true;

            FlurryExplorerState state = FlurryExplorerEngine.GetState(__instance);
            _ = FlurryExplorerEngine.UpdateTreeViewAsync(__instance, state, false, true, true, true);
            return false;
        }

        [HarmonyPatch("assetTreeView_SelectedItemChanged")]
        [HarmonyPrefix]
        static bool AssetTreeView_SelectedItemChanged_Prefix(FrostyDataExplorer __instance, object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (!FlurryExplorerEngine.FastModeEnabled)
                return true;

            FlurryExplorerState state = FlurryExplorerEngine.GetState(__instance);
            if (!state.SuppressSelection)
            {
                state.SelectGeneration++;
                var selNode = e.NewValue as DataExplorer.FlurryAssetPath;
            }
            if (e.NewValue != null && state.DesiredSelectionPath != null)
            {
                state.DesiredSelectionPath = null;
            }
            if (state.SuppressModified)
                return false;

            TreeView assetTreeView = FlurryExplorerEngine.AssetTreeViewRef(__instance);
            if (assetTreeView == null)
                return false;

            var newItem = assetTreeView.SelectedItem as FlurryAssetPath;
            if (state.SelectedNode != null && !ReferenceEquals(state.SelectedNode, newItem))
                state.SelectedNode.IsSelected = false;

            state.SelectedNode = newItem;
            __instance.SelectedPath = newItem == null || newItem.IsRoot ? "" : newItem.FullPath;

            _ = FlurryExplorerEngine.UpdateListViewAsync(__instance, state, newItem);
            return false;
        }

        [HarmonyPatch("assetListView_SelectionChanged")]
        [HarmonyPostfix]
        static void AssetListView_SelectionChanged_Postfix(FrostyDataExplorer __instance)
        {
            if (!FlurryExplorerEngine.FastModeEnabled)
                return;

            FlurryExplorerState state = FlurryExplorerEngine.GetState(__instance);
            if (!state.SuppressSelection)
            {
                state.SelectGeneration++;
            }
        }

        [HarmonyPatch("BuildFilterData")]
        [HarmonyPrefix]
        static bool BuildFilterData_Prefix(FrostyDataExplorer __instance, string filterText)
        {
            if (!FlurryExplorerEngine.FastModeEnabled)
                return true;

            FlurryExplorerState state = FlurryExplorerEngine.GetState(__instance);
            FlurryExplorerEngine.BuildFilterData(state, filterText, __instance);
            return false;
        }

        [HarmonyPatch("OnApplyTemplate")]
        [HarmonyPostfix]
        static void OnApplyTemplate_Postfix(FrostyDataExplorer __instance)
        {
            if (!FlurryExplorerEngine.FastModeEnabled)
                return;

            FlurryExplorerState state = FlurryExplorerEngine.GetState(__instance);

            // Release SelectAsset waiters only when no ItemsSource exists at all; when an
            // ItemsSource is set, a build is in flight (or about to start) and completing
            // the gate here would let selection run against an empty tree.
            if (__instance.ItemsSource == null)
                state.ReadyTcs.TrySetResult(true);

            state.FilterProgressBar = __instance.Template.FindName(PartFilterProgressBarName, __instance) as ProgressBar;
            state.FilterInProgressBorder = __instance.Template.FindName(PartFilterInProgressBorderName, __instance) as Border;

            TextBox filterBox = FlurryExplorerEngine.FilterTextBoxRef(__instance);
            if (filterBox != null)
                FlurryExplorerEngine.WireFilterTextBox(filterBox, state);

            ComboBox typeComboBox = __instance.Template.FindName(PartTypeComboBoxName, __instance) as ComboBox;
            if (typeComboBox != null)
            {
                state.TypeComboBox = typeComboBox;
                FlurryExplorerEngine.WireTypeComboBox(__instance, state);
                _ = FlurryExplorerEngine.PopulateAssetTypesComboBox(__instance, state);
            }

            FlurryExplorerEngine.ConfigureVirtualization(__instance);

            if (state.DeferredItemsSource != null && state.Root == null)
            {
                // The build was deferred because the template was not yet applied when
                // ItemsSource was assigned. Run it now and complete its gate on finish.
                IEnumerable deferredItems = state.DeferredItemsSource;
                state.DeferredItemsSource = null;

                Task deferredBuild = FlurryExplorerEngine.RunDeferredMapping(__instance, state, deferredItems);
                TaskCompletionSource<bool> deferredTcs = state.DeferredBuildTcs;
                _ = deferredBuild.ContinueWith(_ => deferredTcs.TrySetResult(true), TaskScheduler.Default);
            }
            else if (state.Root == null && __instance.ItemsSource != null)
                _ = FlurryExplorerEngine.CreateAssetPathMappingAsync(__instance, state);
        }
    }
}
