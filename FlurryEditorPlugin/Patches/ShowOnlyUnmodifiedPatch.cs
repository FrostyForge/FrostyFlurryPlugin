using Frosty.Core.Controls;
using FrostySdk.Managers;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Flurry.Editor.Patches
{
    // =========================================================================
    //  FEATURE: Show only UNmodified in Data Explorer
    //
    //  Adds a "Show only unmodified" checkbox next to the existing
    //  "Show only modified" checkbox. Both are mutually exclusive.
    //  Patches UpdateTreeView to skip modified entries (no empty folders)
    //  and UpdateListView to filter the asset list.
    // =========================================================================

    [HarmonyPatch(typeof(FrostyDataExplorer))]
    [HarmonyPatchCategory("flurry.editor")]
    public class ShowOnlyUnmodifiedPatch
    {
        // Per-instance state so dataExplorer and legacyExplorer are independent
        internal static readonly Dictionary<FrostyDataExplorer, bool> ShowOnlyUnmodified
            = new Dictionary<FrostyDataExplorer, bool>();

        private static readonly FieldInfo showOnlyModifiedCheckBoxField
            = AccessTools.Field(typeof(FrostyDataExplorer), "showOnlyModifiedCheckBox");

        // =====================================================
        //  Inject the checkbox after template is applied
        // =====================================================
        [HarmonyPatch("OnApplyTemplate")]
        [HarmonyPostfix]
        public static void OnApplyTemplate_Postfix(FrostyDataExplorer __instance)
        {
            bool existsInDict = ShowOnlyUnmodified.TryGetValue(__instance, out bool _);
            if (existsInDict)
                return; // already patched this instance

            var modifiedCheckBox = showOnlyModifiedCheckBoxField?.GetValue(__instance) as CheckBox;
            if (modifiedCheckBox == null)
                return;

            var parent = modifiedCheckBox.Parent as Panel;
            if (parent == null)
                return;

            // Initialize state
            ShowOnlyUnmodified[__instance] = false;

            var unmodifiedCheckBox = new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                IsChecked = false
            };

            var label = new TextBlock
            {
                Text = "Show only unmodified",
                Margin = new Thickness(4, -1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "FontColor");
            unmodifiedCheckBox.Content = label;

            // Mutual exclusion: uncheck modified when unmodified is checked
            unmodifiedCheckBox.Checked += (s, e) =>
            {
                ShowOnlyUnmodified[__instance] = true;
                if (modifiedCheckBox.IsChecked == true)
                    modifiedCheckBox.IsChecked = false;
                __instance.RefreshAll();
            };
            unmodifiedCheckBox.Unchecked += (s, e) =>
            {
                ShowOnlyUnmodified[__instance] = false;
                __instance.RefreshAll();
            };

            // Mutual exclusion: uncheck unmodified when modified is checked
            modifiedCheckBox.Checked += (s, e) =>
            {
                if (unmodifiedCheckBox.IsChecked == true)
                    unmodifiedCheckBox.IsChecked = false;
            };

            // Insert after the modified checkbox
            int idx = parent.Children.IndexOf(modifiedCheckBox);
            parent.Children.Insert(idx + 1, unmodifiedCheckBox);
        }

        // =====================================================
        //  Reflection refs for UpdateTreeView replacement
        // =====================================================
        private static readonly FieldInfo assetTreeViewField
            = AccessTools.Field(typeof(FrostyDataExplorer), "assetTreeView");
        private static readonly FieldInfo selectedPathField
            = AccessTools.Field(typeof(FrostyDataExplorer), "selectedPath");
        private static readonly FieldInfo assetPathMappingField
            = AccessTools.Field(typeof(FrostyDataExplorer), "assetPathMapping");
        private static readonly PropertyInfo itemsSourceProp
            = typeof(FrostyDataExplorer).GetProperty("ItemsSource");
        private static readonly MethodInfo filterTextMethod
            = AccessTools.Method(typeof(FrostyDataExplorer), "FilterText");
        private static readonly MethodInfo updateListViewMethod
            = AccessTools.Method(typeof(FrostyDataExplorer), "UpdateListView");

        // AssetPath is internal, so we use reflection
        private static readonly Type assetPathType
            = typeof(FrostyDataExplorer).Assembly.GetType("Frosty.Core.Controls.AssetPath");
        private static readonly ConstructorInfo assetPathCtor
            = assetPathType?.GetConstructor(new[] { typeof(string), typeof(string), assetPathType, typeof(bool) });
        private static readonly PropertyInfo assetPathChildrenProp
            = assetPathType?.GetProperty("Children");
        private static readonly PropertyInfo assetPathPathNameProp
            = assetPathType?.GetProperty("PathName");
        private static readonly PropertyInfo assetPathFullPathProp
            = assetPathType?.GetProperty("FullPath");
        private static readonly PropertyInfo assetPathIsSelectedProp
            = assetPathType?.GetProperty("IsSelected");
        private static readonly MethodInfo assetPathUpdatePathNameMethod
            = assetPathType?.GetMethod("UpdatePathName");

        // =====================================================
        //  Replace UpdateTreeView when show-only-unmodified
        // =====================================================
        [HarmonyPatch("UpdateTreeView")]
        [HarmonyPrefix]
        public static bool UpdateTreeView_Prefix(FrostyDataExplorer __instance)
        {
            if (!ShowOnlyUnmodified.TryGetValue(__instance, out bool showUnmod) || !showUnmod)
                return true; // let original run

            var treeView = assetTreeViewField?.GetValue(__instance) as TreeView;
            if (treeView == null)
                return true;

            var selectedPath = selectedPathField?.GetValue(__instance);
            if (selectedPath != null)
                assetPathIsSelectedProp?.SetValue(selectedPath, false);

            var itemsSource = itemsSourceProp?.GetValue(__instance) as IEnumerable;
            if (itemsSource == null)
                return false;

            var assetPathMapping = assetPathMappingField?.GetValue(__instance) as IDictionary;
            if (assetPathMapping == null)
                return true;

            // Build the tree, mirroring the original but skipping modified entries
            object root = assetPathCtor.Invoke(new object[] { "", "", null, false });
            var rootChildren = assetPathChildrenProp.GetValue(root) as IList;

            foreach (AssetEntry entry in itemsSource)
            {
                // Skip modified entries (show only unmodified)
                if (entry.IsModified)
                    continue;

                if (filterTextMethod != null && !(bool)filterTextMethod.Invoke(__instance, new object[] { entry.Name, entry }))
                    continue;

                string[] arr = entry.Path.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                object next = root;

                foreach (string path in arr)
                {
                    var nextChildren = assetPathChildrenProp.GetValue(next) as IList;
                    bool found = false;

                    foreach (object child in nextChildren)
                    {
                        string childPathName = assetPathPathNameProp.GetValue(child) as string;
                        if (childPathName.Equals(path, StringComparison.OrdinalIgnoreCase))
                        {
                            if (path.ToCharArray().Any(char.IsUpper))
                                assetPathUpdatePathNameMethod.Invoke(child, new object[] { path });

                            next = child;
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        string nextFullPath = assetPathFullPathProp.GetValue(next) as string;
                        string fullPath = nextFullPath + "/" + path;
                        object newPath;

                        if (!assetPathMapping.Contains(fullPath))
                        {
                            newPath = assetPathCtor.Invoke(new object[] { path, fullPath, next, false });
                            assetPathMapping.Add(fullPath, newPath);
                        }
                        else
                        {
                            newPath = assetPathMapping[fullPath];
                            var newPathChildren = assetPathChildrenProp.GetValue(newPath) as IList;
                            newPathChildren.Clear();

                            if (newPath == selectedPath)
                                assetPathIsSelectedProp.SetValue(selectedPath, true);
                        }

                        nextChildren.Add(newPath);
                        next = newPath;
                    }
                }
            }

            // Add [root] node
            string rootKey = "/";
            if (!assetPathMapping.Contains(rootKey))
                assetPathMapping.Add(rootKey, assetPathCtor.Invoke(new object[] { "![root]", "", null, true }));
            rootChildren.Insert(0, assetPathMapping[rootKey]);

            treeView.ItemsSource = rootChildren;
            treeView.Items.SortDescriptions.Add(new SortDescription("PathName", ListSortDirection.Ascending));

            updateListViewMethod.Invoke(__instance, new object[] { selectedPathField.GetValue(__instance) });

            return false; // skip original
        }

        // =====================================================
        //  Patch UpdateListView to filter out modified assets
        // =====================================================
        private static readonly FieldInfo assetListViewField
            = AccessTools.Field(typeof(FrostyDataExplorer), "assetListView");
        private static readonly PropertyInfo selectedAssetProp
            = typeof(FrostyDataExplorer).GetProperty("SelectedAsset");

        [HarmonyPatch("UpdateListView")]
        [HarmonyPrefix]
        public static bool UpdateListView_Prefix(FrostyDataExplorer __instance, object path)
        {
            // Only intercept when "show only unmodified" is active
            if (!ShowOnlyUnmodified.TryGetValue(__instance, out bool showUnmod) || !showUnmod)
                return true; // let original run

            var listView = assetListViewField?.GetValue(__instance) as ListView;
            if (listView == null)
                return true;

            if (path == null)
            {
                listView.ItemsSource = null;
                return false;
            }

            var itemsSource = itemsSourceProp?.GetValue(__instance) as IEnumerable;
            if (itemsSource == null)
                return true;

            string fullPath = assetPathFullPathProp?.GetValue(path) as string;
            string key = fullPath?.Trim('/') ?? "";

            var items = new List<AssetEntry>();
            foreach (AssetEntry entry in itemsSource)
            {
                // Skip modified entries (show only unmodified)
                if (entry.IsModified)
                    continue;

                if (!entry.Path.Equals(key, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (filterTextMethod != null && !(bool)filterTextMethod.Invoke(__instance, new object[] { entry.Name, entry }))
                    continue;

                items.Add(entry);
            }

            listView.ItemsSource = items;

            var selected = selectedAssetProp?.GetValue(__instance);
            if (selected != null)
                listView.SelectedItem = selected;

            return false;
        }
    }
}
