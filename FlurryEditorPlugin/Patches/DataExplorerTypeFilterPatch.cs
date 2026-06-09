using Flurry.Editor.Windows;
using Frosty.Core;
using Frosty.Core.Controls;
using FrostySdk.Managers;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace Flurry.Editor.Patches
{
    [HarmonyPatch(typeof(FrostyDataExplorer))]
    [HarmonyPatchCategory("flurry.editor")]
    public static class DataExplorerTypeFilterPatch
    {
        public const string HideTypesEnabledConfigKey = "Flurry.DataExplorerHideConfiguredTypes";
        public const string HiddenTypesConfigKey = "Flurry.DataExplorerHiddenAssetTypes";
        public const string DefaultHiddenTypesText = "WSTeamData; WSSoldierCustomizationKitList; FsUITextDatabase";

        private const string ShowOnlyUnmodifiedName = "FlurryShowOnlyUnmodifiedCheckBox";

        private static readonly AccessTools.FieldRef<FrostyDataExplorer, CheckBox> ShowOnlyModifiedCheckBoxRef =
            AccessTools.FieldRefAccess<FrostyDataExplorer, CheckBox>("showOnlyModifiedCheckBox");

        private static readonly ConditionalWeakTable<FrostyDataExplorer, ExplorerState> ExplorerStates =
            new ConditionalWeakTable<FrostyDataExplorer, ExplorerState>();

        private static string cachedHiddenTypesText;
        private static List<string> cachedHiddenTypes = new List<string>();
        private static HashSet<string> cachedHiddenTypeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private sealed class ExplorerState
        {
            public bool HideConfiguredTypes;
            public bool ShowOnlyUnmodified;
            public CheckBox MenuAnchor;
            public CheckBox ShowOnlyUnmodifiedCheckBox;
            public bool ShowOnlyModifiedHandlerAttached;
        }

        [HarmonyPatch("OnApplyTemplate")]
        [HarmonyPostfix]
        public static void AddHiddenTypesMenu(FrostyDataExplorer __instance)
        {
            if (!IsMainEditorExplorer(__instance))
                return;

            ExplorerState state = GetState(__instance);
            state.HideConfiguredTypes = Config.Get<bool>(HideTypesEnabledConfigKey, false);

            CheckBox showOnlyModifiedCheckBox = ShowOnlyModifiedCheckBoxRef(__instance);
            if (showOnlyModifiedCheckBox == null)
                return;

            state.MenuAnchor = showOnlyModifiedCheckBox;
            AttachQuickAccessMenu(__instance, showOnlyModifiedCheckBox);
            UpdateMenuAnchor(showOnlyModifiedCheckBox);
            AddShowOnlyUnmodifiedToggle(__instance, showOnlyModifiedCheckBox);
        }

        [HarmonyPatch("FilterText")]
        [HarmonyPostfix]
        public static void FilterHiddenAssetTypes(FrostyDataExplorer __instance, AssetEntry inEntry, ref bool __result)
        {
            if (!__result || !IsMainEditorExplorer(__instance))
                return;

            ExplorerState state = GetState(__instance);
            if (state.ShowOnlyUnmodified && inEntry?.IsModified == true)
                __result = false;
            else if (state.HideConfiguredTypes && IsHiddenType(inEntry?.Type))
                __result = false;
        }

        [HarmonyPatch("SelectAsset")]
        [HarmonyPrefix]
        public static void RevealHiddenSelectedAsset(FrostyDataExplorer __instance, AssetEntry entry)
        {
            if (entry == null || !IsMainEditorExplorer(__instance))
                return;

            ExplorerState state = GetState(__instance);
            bool needsRefresh = false;
            if (state.ShowOnlyUnmodified && entry.IsModified)
                needsRefresh |= SetShowOnlyUnmodified(__instance, false, false);

            if (state.HideConfiguredTypes && IsHiddenType(entry.Type))
                needsRefresh |= SetHideConfiguredTypes(__instance, false, false, false);

            if (needsRefresh)
                __instance.RefreshAll();
        }

        public static void RefreshOpenExplorersFromConfig()
        {
            RefreshExplorerFromConfig(App.EditorWindow?.DataExplorer);
            RefreshExplorerFromConfig(App.EditorWindow?.LegacyExplorer);
        }

        public static List<string> GetHiddenTypes()
        {
            EnsureHiddenTypesCache();
            return new List<string>(cachedHiddenTypes);
        }

        private static ExplorerState GetState(FrostyDataExplorer explorer)
        {
            return ExplorerStates.GetValue(explorer, _ => new ExplorerState());
        }

        private static void AddShowOnlyUnmodifiedToggle(FrostyDataExplorer explorer, CheckBox showOnlyModifiedCheckBox)
        {
            if (!(showOnlyModifiedCheckBox.Parent is Panel toolbarPanel))
                return;

            ExplorerState state = GetState(explorer);
            if (!state.ShowOnlyModifiedHandlerAttached)
            {
                showOnlyModifiedCheckBox.Checked += (s, e) => SetShowOnlyUnmodified(explorer, false);
                state.ShowOnlyModifiedHandlerAttached = true;
            }

            foreach (UIElement child in toolbarPanel.Children)
            {
                if (child is CheckBox existing && existing.Name == ShowOnlyUnmodifiedName)
                {
                    state.ShowOnlyUnmodifiedCheckBox = existing;
                    existing.IsChecked = state.ShowOnlyUnmodified;
                    return;
                }
            }

            CheckBox showOnlyUnmodifiedCheckBox = new CheckBox
            {
                Name = ShowOnlyUnmodifiedName,
                IsChecked = state.ShowOnlyUnmodified,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Content = CreateToolbarLabel("Show only unmodified")
            };
            showOnlyUnmodifiedCheckBox.Checked += (s, e) => SetShowOnlyUnmodified(explorer, true);
            showOnlyUnmodifiedCheckBox.Unchecked += (s, e) => SetShowOnlyUnmodified(explorer, false);

            state.ShowOnlyUnmodifiedCheckBox = showOnlyUnmodifiedCheckBox;
            int insertIndex = toolbarPanel.Children.IndexOf(showOnlyModifiedCheckBox) + 1;
            toolbarPanel.Children.Insert(insertIndex, showOnlyUnmodifiedCheckBox);
        }

        private static TextBlock CreateToolbarLabel(string text)
        {
            TextBlock label = new TextBlock
            {
                Text = text,
                Margin = new Thickness(4, -1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            label.SetResourceReference(Control.ForegroundProperty, "FontColor");
            return label;
        }

        private static bool SetShowOnlyUnmodified(FrostyDataExplorer explorer, bool enabled, bool refresh = true)
        {
            ExplorerState state = GetState(explorer);
            if (state.ShowOnlyUnmodified == enabled && state.ShowOnlyUnmodifiedCheckBox?.IsChecked == enabled)
                return false;

            state.ShowOnlyUnmodified = enabled;
            if (enabled && explorer.ShowOnlyModified)
                explorer.ShowOnlyModified = false;

            if (state.ShowOnlyUnmodifiedCheckBox != null && state.ShowOnlyUnmodifiedCheckBox.IsChecked != enabled)
                state.ShowOnlyUnmodifiedCheckBox.IsChecked = enabled;

            if (refresh)
                explorer.RefreshAll();

            return true;
        }

        private static void RefreshExplorerFromConfig(FrostyDataExplorer explorer)
        {
            if (explorer == null)
                return;

            ExplorerState state = GetState(explorer);
            state.HideConfiguredTypes = Config.Get<bool>(HideTypesEnabledConfigKey, false);
            UpdateMenuAnchor(state.MenuAnchor);
            explorer.RefreshAll();
        }

        private static bool SetHideConfiguredTypes(FrostyDataExplorer explorer, bool enabled, bool persist, bool refresh = true)
        {
            ExplorerState state = GetState(explorer);
            bool changed = state.HideConfiguredTypes != enabled;
            state.HideConfiguredTypes = enabled;

            if (persist)
            {
                Config.Add(HideTypesEnabledConfigKey, enabled);
                Config.Save();
            }

            UpdateMenuAnchor(state.MenuAnchor);
            if (refresh && changed)
                explorer.RefreshAll();

            return changed;
        }

        private static void AttachQuickAccessMenu(FrostyDataExplorer explorer, CheckBox toggle)
        {
            toggle.ContextMenu = new ContextMenu();
            toggle.ContextMenuOpening -= Toggle_ContextMenuOpening;
            toggle.ContextMenuOpening += Toggle_ContextMenuOpening;
            toggle.Tag = explorer;
        }

        private static void Toggle_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (!(sender is CheckBox toggle) || !(toggle.Tag is FrostyDataExplorer explorer) || toggle.ContextMenu == null)
                return;

            RebuildQuickAccessMenu(explorer, toggle.ContextMenu);
        }

        private static void RebuildQuickAccessMenu(FrostyDataExplorer explorer, ContextMenu menu)
        {
            menu.Items.Clear();

            List<string> hiddenTypes = GetHiddenTypes();
            string selectedType = GetSelectedAssetType(explorer);
            bool canHideSelectedType = !string.IsNullOrWhiteSpace(selectedType)
                && !hiddenTypes.Any(type => string.Equals(type, selectedType, StringComparison.OrdinalIgnoreCase));

            ExplorerState state = GetState(explorer);
            MenuItem enabledItem = new MenuItem
            {
                Header = $"Hide configured types ({hiddenTypes.Count})",
                IsCheckable = true,
                IsChecked = state.HideConfiguredTypes
            };
            enabledItem.Click += (s, e) => SetHideConfiguredTypes(explorer, enabledItem.IsChecked, true);
            menu.Items.Add(enabledItem);
            menu.Items.Add(new Separator());

            AddMenuItem(menu,
                string.IsNullOrWhiteSpace(selectedType) ? "Hide selected asset type" : $"Hide selected asset type: {selectedType}",
                canHideSelectedType,
                (s, e) =>
                {
                    hiddenTypes.Add(selectedType);
                    SaveHiddenTypes(hiddenTypes, false);
                    SetHideConfiguredTypes(explorer, true, true);
                });

            AddMenuItem(menu, "Add type...", true, (s, e) =>
            {
                string input = SimpleInputDialog.Show(
                    "Add Hidden Asset Type",
                    "Enter an asset type to hide.",
                    selectedType ?? string.Empty,
                    Application.Current.MainWindow);

                if (string.IsNullOrWhiteSpace(input))
                    return;

                hiddenTypes.Add(input.Trim());
                SaveHiddenTypes(hiddenTypes);
                RefreshOpenExplorersFromConfig();
            });

            AddMenuItem(menu, "Edit type list...", true, (s, e) =>
            {
                string input = SimpleInputDialog.Show(
                    "Hidden Asset Types",
                    "Edit hidden asset types. Separate multiple types with semicolons, commas, or new lines.",
                    string.Join("; ", hiddenTypes),
                    Application.Current.MainWindow,
                    true);

                if (input == null)
                    return;

                SaveHiddenTypes(ParseTypes(input));
                RefreshOpenExplorersFromConfig();
            });

            MenuItem removeMenu = new MenuItem
            {
                Header = "Remove type",
                IsEnabled = hiddenTypes.Count != 0
            };
            foreach (string hiddenType in hiddenTypes)
            {
                string capturedType = hiddenType;
                AddMenuItem(removeMenu, capturedType, true, (s, e) =>
                {
                    SaveHiddenTypes(hiddenTypes.Where(type => !string.Equals(type, capturedType, StringComparison.OrdinalIgnoreCase)));
                    RefreshOpenExplorersFromConfig();
                });
            }
            menu.Items.Add(removeMenu);

            menu.Items.Add(new Separator());

            AddMenuItem(menu, "Reset defaults", true, (s, e) =>
            {
                SaveHiddenTypes(ParseTypes(DefaultHiddenTypesText));
                RefreshOpenExplorersFromConfig();
            });

            AddMenuItem(menu, "Clear hidden types", hiddenTypes.Count != 0, (s, e) =>
            {
                SaveHiddenTypes(Enumerable.Empty<string>());
                RefreshOpenExplorersFromConfig();
            });
        }

        private static MenuItem AddMenuItem(ItemsControl parent, string header, bool isEnabled, RoutedEventHandler click)
        {
            MenuItem item = new MenuItem { Header = header, IsEnabled = isEnabled };
            if (click != null)
                item.Click += click;
            parent.Items.Add(item);
            return item;
        }

        private static void SaveHiddenTypes(IEnumerable<string> hiddenTypes, bool save = true)
        {
            List<string> normalizedTypes = NormalizeTypes(hiddenTypes);
            string rawTypes = string.Join("; ", normalizedTypes);

            Config.Add(HiddenTypesConfigKey, rawTypes);
            if (save)
                Config.Save();

            SetHiddenTypesCache(rawTypes, normalizedTypes);
        }

        private static void UpdateMenuAnchor(CheckBox anchor)
        {
            if (anchor == null)
                return;

            List<string> hiddenTypes = GetHiddenTypes();
            int count = hiddenTypes.Count;
            string typeLabel = count == 1 ? "type" : "types";
            bool enabled = anchor.Tag is FrostyDataExplorer explorer
                ? GetState(explorer).HideConfiguredTypes
                : Config.Get<bool>(HideTypesEnabledConfigKey, false);

            anchor.ToolTip = count == 0
                ? "Right-click to configure hidden asset types."
                : $"Right-click to manage hidden asset types. Hidden filter is {(enabled ? "on" : "off")} for {count} {typeLabel}: {string.Join(", ", hiddenTypes)}";
        }

        private static bool IsHiddenType(string assetType)
        {
            if (string.IsNullOrWhiteSpace(assetType))
                return false;

            EnsureHiddenTypesCache();
            return cachedHiddenTypeSet.Contains(assetType);
        }

        private static void EnsureHiddenTypesCache()
        {
            string rawTypes = Config.Get<string>(HiddenTypesConfigKey, DefaultHiddenTypesText) ?? string.Empty;
            if (string.Equals(rawTypes, cachedHiddenTypesText, StringComparison.Ordinal))
                return;

            SetHiddenTypesCache(rawTypes, ParseTypes(rawTypes));
        }

        private static void SetHiddenTypesCache(string rawTypes, List<string> hiddenTypes)
        {
            cachedHiddenTypesText = rawTypes ?? string.Empty;
            cachedHiddenTypes = hiddenTypes ?? new List<string>();
            cachedHiddenTypeSet = new HashSet<string>(cachedHiddenTypes, StringComparer.OrdinalIgnoreCase);
        }

        private static List<string> ParseTypes(string rawTypes)
        {
            if (string.IsNullOrWhiteSpace(rawTypes))
                return new List<string>();

            return NormalizeTypes(rawTypes
                .Split(new[] { ';', ',', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(type => type.Trim()));
        }

        private static List<string> NormalizeTypes(IEnumerable<string> rawTypes)
        {
            if (rawTypes == null)
                return new List<string>();

            return rawTypes
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .Select(type => type.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(type => type, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string GetSelectedAssetType(FrostyDataExplorer explorer)
        {
            return explorer?.SelectedAsset?.Type;
        }

        private static bool IsMainEditorExplorer(FrostyDataExplorer explorer)
        {
            return explorer != null
                && (explorer == App.EditorWindow?.DataExplorer || explorer == App.EditorWindow?.LegacyExplorer);
        }
    }
}
