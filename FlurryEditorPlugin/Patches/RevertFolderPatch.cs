using App = Frosty.Core.App;
using Frosty.Controls;
using Frosty.Core.Controls;
using Frosty.Core.Windows;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Flurry.Editor.Patches
{
    // =========================================================================
    //  FEATURE: Revert entire folder in Data Explorer (#6)
    //
    //  Adds a context menu to the folder tree with "Revert Folder",
    //  "Revert Folder + Subfolders", and reference-inclusive variants.
    //  Collects all modified assets under the selected path, confirms with
    //  the user, closes open tabs, and reverts them.
    // =========================================================================

    [HarmonyPatch(typeof(FrostyDataExplorer), "OnApplyTemplate")]
    [HarmonyPatchCategory("flurry.editor")]
    public class RevertFolderPatch
    {
        private static readonly FieldInfo assetTreeViewField
            = AccessTools.Field(typeof(FrostyDataExplorer), "assetTreeView");

        // AssetPath reflection (internal class)
        private static readonly Type assetPathType
            = typeof(FrostyDataExplorer).Assembly.GetType("Frosty.Core.Controls.AssetPath");
        private static readonly PropertyInfo fullPathProp
            = assetPathType?.GetProperty("FullPath");

        [HarmonyPostfix]
        public static void Postfix(FrostyDataExplorer __instance)
        {
            var treeView = assetTreeViewField?.GetValue(__instance) as TreeView;
            if (treeView == null)
                return;

            var folderContextMenu = new ContextMenu();

            var revertFolderItem = new MenuItem { Header = "Revert Folder" };
            revertFolderItem.Click += (s, e) => RevertFolder(__instance, treeView, includeSubfolders: false, includeReferences: false);

            var revertSubfoldersItem = new MenuItem { Header = "Revert Folder + Subfolders" };
            revertSubfoldersItem.Click += (s, e) => RevertFolder(__instance, treeView, includeSubfolders: true, includeReferences: false);

            var revertRefsItem = new MenuItem { Header = "Revert Folder (including references)" };
            revertRefsItem.Click += (s, e) => RevertFolder(__instance, treeView, includeSubfolders: false, includeReferences: true);

            var revertSubfoldersRefsItem = new MenuItem { Header = "Revert Folder + Subfolders (including references)" };
            revertSubfoldersRefsItem.Click += (s, e) => RevertFolder(__instance, treeView, includeSubfolders: true, includeReferences: true);

            // Add revert icon if available
            try
            {
                var converter = new ImageSourceConverter();
                var revertIcon = converter.ConvertFromString("pack://application:,,,/FrostyEditor;component/Images/Revert.png") as ImageSource;
                if (revertIcon != null)
                {
                    foreach (var item in new[] { revertFolderItem, revertSubfoldersItem, revertRefsItem, revertSubfoldersRefsItem })
                    {
                        item.Icon = new Image { Source = revertIcon, Width = 16, Height = 16 };
                        RenderOptions.SetBitmapScalingMode(item.Icon as Image, BitmapScalingMode.Fant);
                    }
                }
            }
            catch { /* icon is optional */ }

            folderContextMenu.Items.Add(revertFolderItem);
            folderContextMenu.Items.Add(revertSubfoldersItem);
            folderContextMenu.Items.Add(new Separator());
            folderContextMenu.Items.Add(revertRefsItem);
            folderContextMenu.Items.Add(revertSubfoldersRefsItem);

            // Only enable items when a folder is selected
            folderContextMenu.Opened += (s, e) =>
            {
                var selectedItem = treeView.SelectedItem;
                bool hasSelection = selectedItem != null && assetPathType != null && assetPathType.IsInstanceOfType(selectedItem);
                revertFolderItem.IsEnabled = hasSelection;
                revertSubfoldersItem.IsEnabled = hasSelection;
                revertRefsItem.IsEnabled = hasSelection;
                revertSubfoldersRefsItem.IsEnabled = hasSelection;
            };

            // Apply context menu to tree view items via style, preserving the existing theme style
            var existingStyle = treeView.ItemContainerStyle;
            var itemStyle = new Style(typeof(TreeViewItem));
            if (existingStyle != null)
                itemStyle.BasedOn = existingStyle;
            itemStyle.Setters.Add(new Setter(FrameworkElement.ContextMenuProperty, folderContextMenu));
            treeView.ItemContainerStyle = itemStyle;
        }

        private static void RevertFolder(FrostyDataExplorer explorer, TreeView treeView, bool includeSubfolders, bool includeReferences)
        {
            var selectedItem = treeView.SelectedItem;
            if (selectedItem == null || fullPathProp == null)
                return;

            string folderPath = (fullPathProp.GetValue(selectedItem) as string)?.Trim('/') ?? "";
            if (string.IsNullOrEmpty(folderPath))
                return;

            // Collect all modified assets in this folder (and optionally subfolders)
            var folderAssets = new HashSet<AssetEntry>();

            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx(modifiedOnly: true))
            {
                if (MatchesFolder(entry.Path, folderPath, includeSubfolders))
                    folderAssets.Add(entry);
            }
            foreach (ResAssetEntry entry in App.AssetManager.EnumerateRes(modifiedOnly: true))
            {
                if (MatchesFolder(entry.Path, folderPath, includeSubfolders))
                    folderAssets.Add(entry);
            }
            foreach (ChunkAssetEntry entry in App.AssetManager.EnumerateChunks(modifiedOnly: true))
            {
                if (MatchesFolder(entry.Path ?? "", folderPath, includeSubfolders))
                    folderAssets.Add(entry);
            }

            if (folderAssets.Count == 0)
            {
                FrostyMessageBox.Show("No modified assets in this folder.", "Revert Folder");
                return;
            }

            // Expand with reverse references if requested
            var toRevert = folderAssets;
            if (includeReferences)
            {
                var reverseIndex = BuildReverseReferenceIndex();
                var expanded = new HashSet<AssetEntry>();
                foreach (var asset in folderAssets)
                    CollectReverseDependencies(asset, expanded, reverseIndex);
                toRevert = expanded;
            }

            string scope = includeSubfolders ? "folder and all subfolders" : "folder";
            string refNote = includeReferences ? $" (+ {toRevert.Count - folderAssets.Count} referencing asset(s))" : "";
            var result = FrostyMessageBox.Show(
                $"Revert {toRevert.Count} modified asset(s) in this {scope}?{refNote}\n\nFolder: {folderPath}\n\nThis cannot be undone.",
                "Revert Folder",
                MessageBoxButton.YesNo);

            if (result != MessageBoxResult.Yes)
                return;

            // Close any open tabs for assets being reverted
            CloseTabsForAssets(toRevert);

            // Revert all — suppress per-asset OnModify events to avoid
            // rebuilding the data explorer on every single revert
            int total = toRevert.Count;
            FrostyTaskWindow.Show("Reverting Folder", folderPath, (task) =>
            {
                int count = 0;
                foreach (var entry in toRevert)
                {
                    App.AssetManager.RevertAsset(entry, suppressOnModify: true);
                    count++;
                    task.Update($"Reverted {count}/{total}");
                }
            });

            // Refresh both explorers once at the end, matching normal editor behavior
            RefreshAllExplorers();
            App.Logger.Log($"Reverted {toRevert.Count} asset(s) in {folderPath}");
        }

        #region Reverse Reference Walking

        private static Dictionary<AssetEntry, HashSet<AssetEntry>> BuildReverseReferenceIndex()
        {
            var index = new Dictionary<AssetEntry, HashSet<AssetEntry>>();

            foreach (var ebx in App.AssetManager.EnumerateEbx(modifiedOnly: true))
            {
                var dataObject = ebx.ModifiedEntry?.DataObject;
                if (dataObject == null)
                    continue;

                foreach (var referenced in ExtractReferencedAssets(dataObject))
                {
                    if (!index.TryGetValue(referenced, out var list))
                    {
                        list = new HashSet<AssetEntry>();
                        index[referenced] = list;
                    }
                    list.Add(ebx);
                }
            }

            return index;
        }

        private static void CollectReverseDependencies(
            AssetEntry asset,
            HashSet<AssetEntry> result,
            Dictionary<AssetEntry, HashSet<AssetEntry>> reverseIndex)
        {
            if (!result.Add(asset))
                return;

            if (reverseIndex.TryGetValue(asset, out var dependents))
            {
                foreach (var dep in dependents)
                    CollectReverseDependencies(dep, result, reverseIndex);
            }
        }

        private static IEnumerable<AssetEntry> ExtractReferencedAssets(object root)
        {
            var result = new HashSet<AssetEntry>();
            var visited = new HashSet<object>();

            void Walk(object obj)
            {
                if (obj == null || visited.Contains(obj))
                    return;
                visited.Add(obj);

                if (obj is PointerRef pointer && pointer.Type == PointerRefType.External)
                {
                    var entry = App.AssetManager.GetEbxEntry(pointer.External.FileGuid);
                    if (entry != null)
                        result.Add(entry);
                    return;
                }

                if (obj is IEnumerable enumerable && !(obj is string))
                {
                    foreach (var item in enumerable)
                        Walk(item);
                    return;
                }

                var type = obj.GetType();
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanRead) continue;
                    try { Walk(prop.GetValue(obj)); }
                    catch { /* ignore broken getters */ }
                }
            }

            Walk(root);
            return result;
        }

        #endregion

        private static void RefreshAllExplorers()
        {
            try
            {
                var mainWindow = Application.Current.MainWindow;
                if (mainWindow == null) return;

                var mainWindowType = mainWindow.GetType();

                var dataExplorerField = AccessTools.Field(mainWindowType, "dataExplorer");
                var legacyExplorerField = AccessTools.Field(mainWindowType, "legacyExplorer");

                var dataExplorer = dataExplorerField?.GetValue(mainWindow) as FrostyDataExplorer;
                var legacyExplorer = legacyExplorerField?.GetValue(mainWindow) as FrostyDataExplorer;

                dataExplorer?.RefreshAll();
                legacyExplorer?.RefreshAll();
            }
            catch { /* non-critical */ }
        }

        private static bool MatchesFolder(string assetPath, string folderPath, bool includeSubfolders)
        {
            if (includeSubfolders)
                return assetPath.Equals(folderPath, StringComparison.OrdinalIgnoreCase)
                    || assetPath.StartsWith(folderPath + "/", StringComparison.OrdinalIgnoreCase);
            else
                return assetPath.Equals(folderPath, StringComparison.OrdinalIgnoreCase);
        }

        private static void CloseTabsForAssets(IEnumerable<AssetEntry> assets)
        {
            try
            {
                var mainWindow = Application.Current.MainWindow;
                if (mainWindow == null) return;

                var mainWindowType = mainWindow.GetType();
                var tabControlField = AccessTools.Field(mainWindowType, "tabControl");
                var shutdownMethod = AccessTools.Method(mainWindowType, "ShutdownEditorAndRemoveTab");
                var removeTabMethod = AccessTools.Method(mainWindowType, "RemoveTab");
                var tabControl = tabControlField?.GetValue(mainWindow) as TabControl;

                if (tabControl == null) return;

                var assetNames = new HashSet<string>(assets.Select(a => a.Name));
                var tabsToClose = new List<FrostyTabItem>();

                for (int i = 1; i < tabControl.Items.Count; i++)
                {
                    var tabItem = tabControl.Items[i] as FrostyTabItem;
                    if (tabItem != null && tabItem.TabId != null && assetNames.Contains(tabItem.TabId))
                        tabsToClose.Add(tabItem);
                }

                foreach (var tab in tabsToClose)
                {
                    if (tab.Content is FrostyAssetEditor assetEditor && shutdownMethod != null)
                        shutdownMethod.Invoke(mainWindow, new object[] { assetEditor, tab });
                    else if (removeTabMethod != null)
                        removeTabMethod.Invoke(mainWindow, new object[] { tab });
                }
            }
            catch { /* non-critical — tabs just stay open */ }
        }
    }
}
