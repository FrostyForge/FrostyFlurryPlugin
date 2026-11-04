using Flurry.Editor.DataExplorer;
using Frosty.Core.Controls;
using FrostySdk.Managers;
using HarmonyLib;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Flurry.Editor.Patches
{
    internal static class FlurryDataExplorerFastMode
    {
        internal static bool Enabled;
    }

    [HarmonyPatch(typeof(FrostyDataExplorer))]
    [HarmonyPatchCategory("flurry.dataexplorer")]
    internal static class FrostyDataExplorerFastPatches
    {
        [HarmonyPatch("OnItemsSourceChanged")]
        [HarmonyPrefix]
        static bool OnItemsSourceChanged_Prefix(DependencyObject o, DependencyPropertyChangedEventArgs e)
        {
            if (!FlurryDataExplorerFastMode.Enabled) return true;

            FrostyDataExplorer ctrl = (FrostyDataExplorer)o;
            FlurryExplorerState state = FlurryExplorerEngine.GetState(ctrl);
            state.ReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> readyTcs = state.ReadyTcs;
            state.Generation++;
            state.SelectedNode = null;
            state.Root = null;
            state.TypeFilter = null;
            state.TypeFilterFromText = false;
            ctrl.SelectedAsset = null;

            ListView assetListView = FlurryExplorerEngine.AssetListViewRef(ctrl);
            if (assetListView != null)
                assetListView.ItemsSource = null;

            FlurryExplorerEngine.ShowProgress(state, ctrl);

            var populateTypes = FlurryExplorerEngine.PopulateAssetTypesComboBox(ctrl, state);
            var buildMapping = FlurryExplorerEngine.CreateAssetPathMappingAsync(ctrl, state);
            _ = Task.Factory.ContinueWhenAll(new Task[] { populateTypes, buildMapping }, tasks =>
            {
                readyTcs.TrySetResult(true);
                FlurryExplorerEngine.HideProgress(state, ctrl);
            });

            return false;
        }

        [HarmonyPatch("OnShowOnlyModifiedChanged")]
        [HarmonyPrefix]
        static bool OnShowOnlyModifiedChanged_Prefix(DependencyObject o, DependencyPropertyChangedEventArgs e)
        {
            if (!FlurryDataExplorerFastMode.Enabled) return true;

            FrostyDataExplorer ctrl = (FrostyDataExplorer)o;
            FlurryExplorerState state = FlurryExplorerEngine.GetState(ctrl);
            if (state.SuppressModified)
                return false;

            if ((bool)e.NewValue && FlurryExplorerBehaviors.GetShowOnlyUnModified(ctrl))
            {
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
            if (!FlurryDataExplorerFastMode.Enabled) return true;

            FlurryExplorerState state = FlurryExplorerEngine.GetState(__instance);
            _ = FlurryExplorerEngine.UpdateTreeViewAsync(__instance, state);
            return false;
        }

        [HarmonyPatch("SelectAsset")]
        [HarmonyPrefix]
        static bool SelectAsset_Prefix(FrostyDataExplorer __instance, AssetEntry entry)
        {
            if (!FlurryDataExplorerFastMode.Enabled) return true;

            FlurryExplorerState state = FlurryExplorerEngine.GetState(__instance);
            _ = FlurryExplorerEngine.SelectAssetAsync(__instance, state, entry);
            return false;
        }

        [HarmonyPatch("RefreshAll")]
        [HarmonyPrefix]
        static bool RefreshAll_Prefix(FrostyDataExplorer __instance)
        {
            if (!FlurryDataExplorerFastMode.Enabled) return true;

            FlurryExplorerState state = FlurryExplorerEngine.GetState(__instance);
            _ = FlurryExplorerEngine.UpdateTreeViewAsync(__instance, state, false, true, true, true);

            ListView assetListView = FlurryExplorerEngine.AssetListViewRef(__instance);
            if (__instance.SelectedAssets != null && assetListView != null)
            {
                foreach (var item in __instance.SelectedAssets)
                {
                    assetListView.SelectedItems.Add(item);
                }
            }
            return false;
        }

        [HarmonyPatch("assetTreeView_SelectedItemChanged")]
        [HarmonyPrefix]
        static bool AssetTreeView_SelectedItemChanged_Prefix(FrostyDataExplorer __instance, object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (!FlurryDataExplorerFastMode.Enabled) return true;

            FlurryExplorerState state = FlurryExplorerEngine.GetState(__instance);
            if (!state.SuppressSelection)
                state.SelectGeneration++;
            if (state.SuppressModified) return false;

            TreeView assetTreeView = FlurryExplorerEngine.AssetTreeViewRef(__instance);
            if (assetTreeView == null) return false;

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
            if (!FlurryDataExplorerFastMode.Enabled) return;

            FlurryExplorerState state = FlurryExplorerEngine.GetState(__instance);
            if (!state.SuppressSelection)
                state.SelectGeneration++;
        }

        [HarmonyPatch("BuildFilterData")]
        [HarmonyPrefix]
        static bool BuildFilterData_Prefix(FrostyDataExplorer __instance, string filterText)
        {
            if (!FlurryDataExplorerFastMode.Enabled) return true;

            FlurryExplorerState state = FlurryExplorerEngine.GetState(__instance);
            FlurryExplorerEngine.BuildFilterData(state, filterText, __instance);
            return false;
        }

        [HarmonyPatch("OnApplyTemplate")]
        [HarmonyPostfix]
        static void OnApplyTemplate_Postfix(FrostyDataExplorer __instance)
        {
            if (!FlurryDataExplorerFastMode.Enabled) return;

            FlurryExplorerState state = FlurryExplorerEngine.GetState(__instance);

            state.FilterProgressBar = __instance.Template.FindName("PART_FilterProgressBar", __instance) as ProgressBar;
            state.FilterInProgressBorder = __instance.Template.FindName("PART_FilterInProgressBorder", __instance) as Border;

            TextBox filterBox = FlurryExplorerEngine.FilterTextBoxRef(__instance);
            if (filterBox != null)
            {
                filterBox.AddHandler(System.Windows.UIElement.KeyDownEvent, new System.Windows.Input.KeyEventHandler((sender, e) =>
                {
                    if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None)
                        return;

                    if (e.Key == System.Windows.Input.Key.Up)
                    {
                        filterBox.CaretIndex = 0;
                        e.Handled = true;
                    }
                    else if (e.Key == System.Windows.Input.Key.Down)
                    {
                        filterBox.CaretIndex = filterBox.Text.Length;
                        e.Handled = true;
                    }
                }), true);
            }

            ComboBox typeComboBox = __instance.Template.FindName("PART_TypeComboBox", __instance) as ComboBox;
            if (typeComboBox != null)
            {
                state.TypeComboBox = typeComboBox;
                FlurryExplorerEngine.WireTypeComboBox(__instance, state);
                _ = FlurryExplorerEngine.PopulateAssetTypesComboBox(__instance, state);
            }

            TreeView assetTreeView = FlurryExplorerEngine.AssetTreeViewRef(__instance);
            if (assetTreeView != null)
            {
                assetTreeView.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
                assetTreeView.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
                assetTreeView.SetValue(ScrollViewer.CanContentScrollProperty, true);
            }

            ListView assetListView = FlurryExplorerEngine.AssetListViewRef(__instance);
            if (assetListView != null)
            {
                assetListView.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
                assetListView.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
                assetListView.SetValue(ScrollViewer.CanContentScrollProperty, true);
            }

            if (state.Root == null && __instance.ItemsSource != null)
            {
                _ = FlurryExplorerEngine.CreateAssetPathMappingAsync(__instance, state);
            }
        }
    }
}
