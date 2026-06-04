using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Controls;
using Frosty.Core.Converters;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Flurry.Editor.Patches
{
    /// <summary>
    /// When guid-filtering, unhide all fields of a matched array entry (connection/object).
    /// </summary>
    [HarmonyPatch(typeof(FrostyPropertyGridItemData))]
    [HarmonyPatchCategory("flurry.editor")]
    public class FilterGuidSiblingPatch
    {
        [HarmonyPatch("FilterGuid")]
        [HarmonyPostfix]
        public static void ShowSiblingsOnMatch(FrostyPropertyGridItemData __instance, bool __result, bool doNotHideSubObjects)
        {
            if (doNotHideSubObjects || __result)
                return;
            if (!__instance.IsArrayChild)
                return;

            bool anyVisible = false;
            foreach (var item in __instance.Children.ToList())
            {
                if (!item.IsHidden) { anyVisible = true; break; }
            }
            if (anyVisible)
            {
                foreach (var item in __instance.Children.ToList())
                    UnhideRecursive(item);
            }
        }

        internal static void UnhideRecursive(FrostyPropertyGridItemData item)
        {
            item.IsHidden = false;
            foreach (var child in item.Children.ToList())
                UnhideRecursive(child);
        }
    }

    /// <summary>
    /// Enhances name filter to also match string property VALUES (e.g. SourceField="OnDeactivated").
    /// When a match is found on an array element, unhides all sibling fields.
    /// </summary>
    [HarmonyPatch(typeof(FrostyPropertyGridItemData))]
    [HarmonyPatchCategory("flurry.editor")]
    public class FilterPropertyNameValuePatch
    {
        [HarmonyPatch("FilterPropertyName")]
        [HarmonyPostfix]
        public static void AlsoCheckValues(FrostyPropertyGridItemData __instance, string filterText, ref bool __result, bool doNotHideSubObjects)
        {
            if (doNotHideSubObjects) return;

            // Check searchable value text of each child against filter
            bool anyNewMatch = false;
            foreach (var item in __instance.Children.ToList())
            {
                if (!item.IsHidden) continue;

                string valStr = GetSearchableText(item.Value);
                if (valStr != null && valStr.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    item.IsHidden = false;
                    anyNewMatch = true;
                }
            }
            if (anyNewMatch)
            {
                __instance.IsHidden = false;
                __result = false;
            }

            // Sibling unhide for array elements (show all fields of matched connection/object)
            if (!__result && __instance.IsArrayChild)
            {
                bool anyVisible = false;
                foreach (var item in __instance.Children.ToList())
                {
                    if (!item.IsHidden) { anyVisible = true; break; }
                }
                if (anyVisible)
                {
                    foreach (var item in __instance.Children.ToList())
                        FilterGuidSiblingPatch.UnhideRecursive(item);
                }
            }
        }

        /// <summary>
        /// Extracts searchable text from any EBX field value.
        /// </summary>
        private static string GetSearchableText(object value)
        {
            if (value == null) return null;

            // PointerRef needs special handling — no useful ToString()
            if (value is PointerRef pr)
            {
                if (pr.Type == PointerRefType.External)
                {
                    try
                    {
                        EbxAssetEntry entry = App.AssetManager.GetEbxEntry(pr.External.FileGuid);
                        if (entry != null)
                            return entry.Name + " " + entry.Type;
                    }
                    catch { }
                    return pr.External.FileGuid.ToString() + " " + pr.External.ClassGuid.ToString();
                }
                if (pr.Type == PointerRefType.Internal)
                {
                    try
                    {
                        return value.GetType().Name + " " + ((dynamic)pr.Internal).GetInstanceGuid().ToString();
                    }
                    catch { }
                }
                return null;
            }

            // Skip collections/structs — their fields are searched as children
            if (value is IList) return null;

            // Everything else: CString, numbers, bools, enums, Guid, FileRef, ResourceRef, TypeRef, etc.
            return value.ToString();
        }
    }

    /// <summary>
    /// Auto-detect GUID format in filter box so users don't need "guid:" prefix.
    /// </summary>
    [HarmonyPatch(typeof(FrostyPropertyGrid))]
    [HarmonyPatchCategory("flurry.editor")]
    public class FilterBoxAutoGuidPatch
    {
        private static AccessTools.FieldRef<FrostyPropertyGrid, FrostyWatermarkTextBox> filterBoxRef =
            AccessTools.FieldRefAccess<FrostyPropertyGrid, FrostyWatermarkTextBox>("filterBox");

        private static readonly Regex GuidPattern = new Regex(
            @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.Compiled);

        [HarmonyPatch("FilterBox_LostFocus")]
        [HarmonyPrefix]
        public static void AutoDetectGuid(FrostyPropertyGrid __instance)
        {
            var filterBox = filterBoxRef(__instance);
            string text = filterBox.Text?.Trim();
            if (string.IsNullOrEmpty(text) || text.StartsWith("guid:")) return;

            if (GuidPattern.IsMatch(text))
                filterBox.Text = "guid:" + text;
        }
    }

    [HarmonyPatch(typeof(FrostyPropertyGridItem))]
    [HarmonyPatchCategory("flurry.editor")]
    public class FrostyPropertyGridPatch
    {
        private static AccessTools.FieldRef<FrostyPropertyGrid, FrostyWatermarkTextBox> filterBoxRef =
            AccessTools.FieldRefAccess<FrostyPropertyGrid, FrostyWatermarkTextBox>("filterBox");

        [HarmonyPatch("OnApplyTemplate")]
        [HarmonyPostfix]
        public static void FilterGuidContextMenuButton(FrostyPropertyGridItem __instance)
        {
            FileLog.Debug("[Flurry] Patching FrostyPropertyGridItem context menu for Filter Guid option.");
            FrostyPropertyGridItemData item = (FrostyPropertyGridItemData)__instance.DataContext;
            if (!item.IsPointerRef)
            {
                return;
            }

            for (var i = 0; i < __instance.ContextMenu.Items.Count; i++)
            {
                var menuItem = __instance.ContextMenu.Items[i] as MenuItem;
                if (!(menuItem is MenuItem)) continue;
                FileLog.Debug("[Flurry] Found context menu item: " + menuItem.Header.ToString());
                if (menuItem != null && menuItem.Header.ToString() == "Copy Guid")
                {
                    //menuItem.Visibility = System.Windows.Visibility.Collapsed;
                    MenuItem filterGuidItem = new MenuItem
                    {
                        Header = "Filter Guid",
                        Icon = new Image
                        {
                            Source = StringToBitmapSourceConverter.CopySource,
                            Opacity = 0.5
                        }
                    };
                    filterGuidItem.Click += (s, e) =>
                    {
                        string guidToCopy = "";

                        PointerRef pointerRef = (PointerRef)item.Value;
                        if (pointerRef.Type == PointerRefType.Null)
                            guidToCopy = "";
                        else if (pointerRef.Type == PointerRefType.External)
                            guidToCopy = pointerRef.External.ClassGuid.ToString();
                        else
                        {
                            dynamic obj = pointerRef.Internal;
                            guidToCopy = obj.GetInstanceGuid().ToString();
                        }
                        FrostyPropertyGrid pg = GetPropertyGrid(__instance);
                        FrostyWatermarkTextBox filterBox = filterBoxRef(pg);
                        filterBox.WatermarkText = "";
                        filterBox.Text = "guid:" + guidToCopy;
                        Traverse.Create(pg).Method("FilterBox_LostFocus", new Type[] { typeof(object), typeof(RoutedEventArgs) })
                            .GetValue(new object[] { filterBox, null });
                    };
                    __instance.ContextMenu.Items.Insert(i + 1, filterGuidItem);
                }
            }
        }

        private static FrostyPropertyGrid GetPropertyGrid(FrostyPropertyGridItem item)
        {
            DependencyObject parent = VisualTreeHelper.GetParent(item);
            while (!(parent.GetType().IsSubclassOf(typeof(FrostyPropertyGrid)) || parent is FrostyPropertyGrid))
                parent = VisualTreeHelper.GetParent(parent);
            return (parent as FrostyPropertyGrid);
        }
    }
}
