using Frosty.Core;
using Frosty.Core.Controls;
using FrostySdk;
using FrostySdk.Managers;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Flurry.Editor.Patches
{
    [HarmonyPatch(typeof(FrostyDataExplorer))]
    [HarmonyPatchCategory("flurry.editor")]
    public class DataExplorer_TypeFilterPatch
    {
        private static AccessTools.FieldRef<FrostyDataExplorer, TextBox> filterTextBoxRef =
            AccessTools.FieldRefAccess<FrostyDataExplorer, TextBox>("filterTextBox");

        // Track the selected type per explorer instance
        private static readonly Dictionary<FrostyDataExplorer, string> selectedTypeMap =
            new Dictionary<FrostyDataExplorer, string>();

        // Track which instances have already been patched
        private static readonly HashSet<FrostyDataExplorer> patchedInstances =
            new HashSet<FrostyDataExplorer>();

        [HarmonyPatch("OnApplyTemplate")]
        [HarmonyPostfix]
        public static void InjectTypeFilterComboBox(FrostyDataExplorer __instance)
        {
            if (!patchedInstances.Add(__instance))
                return; // already patched this instance

            TextBox filterTextBox = filterTextBoxRef(__instance);
            if (filterTextBox == null)
                return;

            Border filterBorder = filterTextBox.Parent as Border;
            if (filterBorder == null)
                return;

            DockPanel filterDockPanel = filterBorder.Parent as DockPanel;
            if (filterDockPanel == null)
                return;

            Grid parentGrid = filterDockPanel.Parent as Grid;
            if (parentGrid == null)
                return;

            // Insert a new row between the filter textbox (row 1) and tree view (row 2)
            parentGrid.RowDefinitions.Insert(2, new RowDefinition { Height = GridLength.Auto });

            foreach (UIElement child in parentGrid.Children)
            {
                int row = Grid.GetRow(child);
                if (row >= 2)
                    Grid.SetRow(child, row + 1);
            }

            List<string> allTypes = GetAllAssetTypes();

            // Build a custom searchable dropdown
            Grid typeFilterGrid = new Grid
            {
                Margin = new Thickness(1, 0, 1, 1),
                Height = 22
            };
            typeFilterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            typeFilterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBox searchBox = new TextBox
            {
                Height = 22,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 0, 0, 0),
                Text = "All Types"
            };
            searchBox.SetResourceReference(Control.ForegroundProperty, "FontColor");
            searchBox.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
            searchBox.SetResourceReference(Control.BorderBrushProperty, "ControlBackground");
            Grid.SetColumn(searchBox, 0);
            Grid.SetColumnSpan(searchBox, 2);

            ToggleButton dropdownButton = new ToggleButton
            {
                Width = 18,
                Height = 22,
                Content = "\u25BC",
                FontSize = 8,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Focusable = false
            };
            Grid.SetColumn(dropdownButton, 1);

            ListBox typeList = new ListBox
            {
                MaxHeight = 300,
                MinWidth = 150
            };
            typeList.SetResourceReference(Control.ForegroundProperty, "FontColor");
            typeList.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
            typeList.SetResourceReference(Control.BorderBrushProperty, "ControlBackground");

            typeList.Items.Add("All Types");
            foreach (string type in allTypes)
                typeList.Items.Add(type);

            Popup popup = new Popup
            {
                PlacementTarget = typeFilterGrid,
                Placement = PlacementMode.Bottom,
                StaysOpen = true,
                AllowsTransparency = true,
                Width = 250,
                Child = new Border
                {
                    BorderThickness = new Thickness(1),
                    Child = typeList
                }
            };
            (popup.Child as Border).SetResourceReference(Border.BorderBrushProperty, "ControlBackground");
            (popup.Child as Border).SetResourceReference(Border.BackgroundProperty, "WindowBackground");

            typeFilterGrid.SizeChanged += (s, e) =>
            {
                popup.Width = typeFilterGrid.ActualWidth;
            };

            // Helper to repopulate the list with optional search filter
            Action<string> repopulateList = (string search) =>
            {
                typeList.Items.Clear();
                typeList.Items.Add("All Types");

                if (string.IsNullOrEmpty(search))
                {
                    foreach (string type in allTypes)
                        typeList.Items.Add(type);
                }
                else
                {
                    foreach (string type in allTypes)
                    {
                        if (type.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                            typeList.Items.Add(type);
                    }
                }
            };

            // Helper to open the dropdown fresh (shows all types, clears search text)
            bool isOpening = false;
            Action openDropdown = () =>
            {
                isOpening = true;
                searchBox.Text = "";
                isOpening = false;
                repopulateList(null);
                popup.IsOpen = true;
                dropdownButton.IsChecked = true;
                searchBox.Focus();
            };

            // Helper to restore display text to current selection
            Action restoreDisplayText = () =>
            {
                isOpening = true;
                searchBox.Text = selectedTypeMap.ContainsKey(__instance)
                    ? selectedTypeMap[__instance]
                    : "All Types";
                isOpening = false;
            };

            dropdownButton.Checked += (s, e) => openDropdown();
            dropdownButton.Unchecked += (s, e) =>
            {
                popup.IsOpen = false;
                restoreDisplayText();
            };

            // Close popup when user clicks elsewhere in the application
            EventHandler lostFocusHandler = null;
            lostFocusHandler = (s, e) =>
            {
                if (!searchBox.IsKeyboardFocusWithin && !typeList.IsKeyboardFocusWithin && !dropdownButton.IsMouseOver)
                {
                    popup.IsOpen = false;
                    dropdownButton.IsChecked = false;
                    restoreDisplayText();
                }
            };
            searchBox.LostKeyboardFocus += (s, e) => lostFocusHandler(s, e);
            typeList.LostKeyboardFocus += (s, e) => lostFocusHandler(s, e);

            searchBox.GotFocus += (s, e) =>
            {
                if (!popup.IsOpen)
                    openDropdown();
            };

            searchBox.TextChanged += (s, e) =>
            {
                if (isOpening || !searchBox.IsFocused)
                    return;

                repopulateList(searchBox.Text);
            };

            // Selection applies the type filter independently of the search box
            Action<string> applySelection = (string selected) =>
            {
                popup.IsOpen = false;
                dropdownButton.IsChecked = false;

                isOpening = true;
                searchBox.Text = selected ?? "All Types";
                isOpening = false;

                if (selected == null || selected == "All Types")
                    selectedTypeMap.Remove(__instance);
                else
                    selectedTypeMap[__instance] = selected;

                __instance.RefreshAll();
            };

            typeList.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (typeList.SelectedItem != null)
                    applySelection(typeList.SelectedItem as string);
            };

            typeList.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && typeList.SelectedItem != null)
                {
                    applySelection(typeList.SelectedItem as string);
                    e.Handled = true;
                }
            };

            searchBox.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    string pick = typeList.Items.Count > 1
                        ? typeList.Items[1] as string
                        : "All Types";
                    applySelection(pick);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    popup.IsOpen = false;
                    dropdownButton.IsChecked = false;
                    restoreDisplayText();
                    e.Handled = true;
                }
                else if (e.Key == Key.Down && typeList.Items.Count > 0)
                {
                    typeList.SelectedIndex = 0;
                    typeList.Focus();
                    e.Handled = true;
                }
            };

            typeFilterGrid.Children.Add(searchBox);
            typeFilterGrid.Children.Add(dropdownButton);
            typeFilterGrid.Children.Add(popup);

            Grid.SetRow(typeFilterGrid, 2);
            parentGrid.Children.Add(typeFilterGrid);
        }

        /// <summary>
        /// Postfix on FilterText — if a type is selected in our dropdown,
        /// additionally reject entries whose type is not the selected type
        /// or a subclass of it.
        /// </summary>
        [HarmonyPatch("FilterText")]
        [HarmonyPostfix]
        public static void FilterBySelectedType(FrostyDataExplorer __instance, AssetEntry inEntry, ref bool __result)
        {
            if (!__result)
                return;

            if (selectedTypeMap.TryGetValue(__instance, out string selectedType))
            {
                string entryType = inEntry.Type ?? "";
                if (!TypeLibrary.IsSubClassOf(entryType, selectedType))
                    __result = false;
            }
        }

        private static List<string> GetAllAssetTypes()
        {
            HashSet<string> types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (App.AssetManager != null)
            {
                foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
                {
                    if (string.IsNullOrEmpty(entry.Type))
                        continue;

                    if (types.Contains(entry.Type))
                        continue;

                    types.Add(entry.Type);

                    // Walk up the inheritance chain to include parent types
                    Type type = TypeLibrary.GetType(entry.Type);
                    if (type == null)
                        continue;

                    type = type.BaseType;
                    while (type != null && type != typeof(object))
                    {
                        string name = type.Name;
                        if (types.Contains(name))
                            break; // Already walked this chain
                        types.Add(name);
                        type = type.BaseType;
                    }
                }
            }

            return types.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
