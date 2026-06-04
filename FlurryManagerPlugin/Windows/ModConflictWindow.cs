using Frosty.Core.Mod;
using Frosty.Core;
using FrostySdk.Managers;
using MM = FrostyModManager;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Flurry.Manager.Windows
{
    internal sealed class ModConflictViewEntry
    {
        public string ResourceKey { get; set; }
        public string ResourceName { get; set; }
        public string ResourceType { get; set; }
        public int ModCount { get; set; }
        public string RowSubtitle { get; set; }
        public string UsedInGameMod { get; set; }
        public int ActiveModIndex { get; set; }
        public bool IsRuleOverride { get; set; }
        public int OverriddenCount { get; set; }
        public string OverriddenPreview { get; set; }
        public string OverriddenModsDisplay { get; set; }
        public IReadOnlyList<string> OverriddenMods { get; set; }
        public string LoadOrderPath { get; set; }
        public string ConflictStatus { get; set; }
        public IReadOnlyList<string> OrderedMods { get; set; }
    }

    internal sealed class ModLoadOrderViewItem
    {
        public string ModName { get; set; }
        public bool IsActive { get; set; }
        public string Label { get; set; }
        public Brush ForegroundBrush { get; set; }
    }

    internal sealed class ModConflictFileEntry
    {
        public string DisplayName { get; set; }
        public string ResourceName { get; set; }
        public string ResourceType { get; set; }
        public string Outcome { get; set; }
        public string SearchBlob { get; set; }
        public ModConflictViewEntry SourceConflict { get; set; }
    }

    internal sealed class ModConflictPairEntry
    {
        public string WinnerMod { get; set; }
        public string OtherMod { get; set; }
        public int SharedAssetCount { get; set; }
        public int OverwrittenCount { get; set; }
        public int MergedCount { get; set; }
        public int DisabledCount { get; set; }
        public string RowTitle { get; set; }
        public string RowSubtitle { get; set; }
        public string BadgeText { get; set; }
        public string SearchBlob { get; set; }
        public IReadOnlyList<ModConflictFileEntry> Files { get; set; }
    }

    internal static class ModConflictAnalyzer
    {
        private enum ModActionKind
        {
            None,
            Modify,
            Add,
            Merge
        }

        private sealed class ConflictBucket
        {
            public string ResourceKey;
            public string ResourceName;
            public string ResourceType;
            public List<string> OrderedModNames = new List<string>();
            public List<ModActionKind> OrderedActions = new List<ModActionKind>();
            public HashSet<string> SeenModNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool IncludesDisabledMods;
        }

        private sealed class ResourceDescriptor
        {
            public string Key;
            public string DisplayName;
            public string DisplayType;
        }

        private static readonly object propertyCacheLock = new object();
        private static readonly Dictionary<Type, PropertyInfo> resourcesPropertyCache = new Dictionary<Type, PropertyInfo>();

        public static List<ModConflictViewEntry> Build(
            IList<MM.FrostyAppliedMod> appliedMods,
            bool enabledOnly,
            IDictionary<string, string> assetOverrideRules,
            out int scannedMods,
            out int scannedAssets)
        {
            scannedMods = 0;
            scannedAssets = 0;

            if (appliedMods == null || appliedMods.Count == 0)
            {
                return new List<ModConflictViewEntry>();
            }

            Dictionary<string, ConflictBucket> bucketsByResource = new Dictionary<string, ConflictBucket>(StringComparer.OrdinalIgnoreCase);

            foreach (MM.FrostyAppliedMod appliedMod in appliedMods)
            {
                if (appliedMod == null || !appliedMod.IsFound || appliedMod.Mod == null)
                {
                    continue;
                }

                if (enabledOnly && !appliedMod.IsEnabled)
                {
                    continue;
                }

                scannedMods++;
                HashSet<string> seenResourceKeysInMod = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string modDisplayName = ResolveModDisplayName(appliedMod);

                foreach (BaseModResource resource in EnumerateResources(appliedMod.Mod))
                {
                    if (resource == null || resource.Type == ModResourceType.Embedded)
                    {
                        continue;
                    }

                    bool hasAction = resource.HasHandler || resource.IsAdded || resource.IsModified;
                    if (!hasAction)
                    {
                        continue;
                    }

                    ResourceDescriptor descriptor = BuildResourceDescriptor(resource);
                    if (!seenResourceKeysInMod.Add(descriptor.Key))
                    {
                        continue;
                    }

                    scannedAssets++;

                    ConflictBucket bucket;
                    if (!bucketsByResource.TryGetValue(descriptor.Key, out bucket))
                    {
                        bucket = new ConflictBucket
                        {
                            ResourceKey = descriptor.Key,
                            ResourceName = descriptor.DisplayName,
                            ResourceType = descriptor.DisplayType
                        };
                        bucketsByResource.Add(descriptor.Key, bucket);
                    }

                    ModActionKind actionKind = DetermineActionKind(resource);
                    if (bucket.SeenModNames.Add(modDisplayName))
                    {
                        bucket.OrderedModNames.Add(modDisplayName);
                        bucket.OrderedActions.Add(actionKind);
                    }
                    if (!appliedMod.IsEnabled)
                    {
                        bucket.IncludesDisabledMods = true;
                    }
                }
            }

            return bucketsByResource.Values
                .Where(bucket => bucket.OrderedModNames.Count > 1)
                .Select(bucket =>
                {
                    int activeIndex = bucket.OrderedModNames.Count - 1;
                    bool isRuleOverride = TryResolveRuleOverrideIndex(bucket, assetOverrideRules, out int overrideIndex)
                        && overrideIndex >= 0
                        && overrideIndex < bucket.OrderedModNames.Count;
                    if (isRuleOverride)
                    {
                        activeIndex = overrideIndex;
                    }

                    List<string> overriddenMods = bucket.OrderedModNames
                        .Where((modName, index) => index != activeIndex)
                        .ToList();

                    return new ModConflictViewEntry
                    {
                        ResourceKey = bucket.ResourceKey,
                        ResourceName = bucket.ResourceName,
                        ResourceType = bucket.ResourceType,
                        ModCount = bucket.OrderedModNames.Count,
                        RowSubtitle = BuildRowSubtitle(bucket, activeIndex, isRuleOverride),
                        UsedInGameMod = bucket.OrderedModNames[activeIndex],
                        ActiveModIndex = activeIndex,
                        IsRuleOverride = isRuleOverride,
                        OverriddenCount = overriddenMods.Count,
                        OverriddenPreview = BuildOverriddenPreview(overriddenMods),
                        OverriddenModsDisplay = string.Join(", ", overriddenMods),
                        OverriddenMods = overriddenMods,
                        LoadOrderPath = string.Join(" -> ", bucket.OrderedModNames),
                        ConflictStatus = BuildConflictStatus(bucket, activeIndex),
                        OrderedMods = bucket.OrderedModNames.ToList()
                    };
                })
                .OrderByDescending(entry => entry.ModCount)
                .ThenBy(entry => entry.ResourceType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.ResourceName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool TryResolveRuleOverrideIndex(ConflictBucket bucket, IDictionary<string, string> assetOverrideRules, out int index)
        {
            index = -1;
            if (bucket == null || bucket.OrderedModNames == null || bucket.OrderedModNames.Count == 0)
            {
                return false;
            }

            if (!ConflictAssetOverrideRules.TryGetPreferredMod(assetOverrideRules, bucket.ResourceKey, out string preferredMod))
            {
                return false;
            }

            for (int i = bucket.OrderedModNames.Count - 1; i >= 0; i--)
            {
                if (string.Equals(bucket.OrderedModNames[i], preferredMod, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    return true;
                }
            }

            return false;
        }

        private static string BuildOverriddenPreview(IList<string> overriddenModNames)
        {
            int overriddenCount = overriddenModNames?.Count ?? 0;
            if (overriddenCount <= 0)
            {
                return string.Empty;
            }

            const int previewCount = 2;
            List<string> overridden = overriddenModNames.Take(overriddenCount).ToList();
            if (overriddenCount <= previewCount)
            {
                return string.Join(", ", overridden);
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}, {1} (+{2} more)",
                overridden[0],
                overridden[1],
                overriddenCount - previewCount);
        }

        private static string BuildConflictStatus(ConflictBucket bucket, int activeIndex)
        {
            if (bucket == null)
            {
                return "Unknown";
            }

            if (bucket.IncludesDisabledMods)
            {
                return "Includes disabled mods";
            }

            ModActionKind finalAction = (bucket.OrderedActions.Count > activeIndex && activeIndex >= 0)
                ? bucket.OrderedActions[activeIndex]
                : ModActionKind.None;

            if (finalAction == ModActionKind.Merge)
            {
                return "Merged";
            }

            if (finalAction == ModActionKind.Add || finalAction == ModActionKind.Modify)
            {
                return "Replaced";
            }

            return "Conflict";
        }

        private static string BuildRowSubtitle(ConflictBucket bucket, int activeIndex, bool isRuleOverride)
        {
            if (bucket == null || bucket.OrderedModNames.Count == 0)
            {
                return "No load-order data";
            }

            string activeMod = bucket.OrderedModNames[Math.Max(0, Math.Min(activeIndex, bucket.OrderedModNames.Count - 1))];
            int replacedCount = Math.Max(0, bucket.OrderedModNames.Count - 1);
            return string.Format(
                CultureInfo.InvariantCulture,
                "Active: {0}  |  Overridden mods: {1}{2}",
                activeMod,
                replacedCount,
                isRuleOverride ? "  |  Rule Override" : string.Empty);
        }

        private static ModActionKind DetermineActionKind(BaseModResource resource)
        {
            if (resource == null)
            {
                return ModActionKind.None;
            }

            if (resource.HasHandler)
            {
                // Legacy merge hash used by Frosty's actions tab.
                if ((uint)resource.Handler == 0xBD9BFB65)
                {
                    return ModActionKind.Merge;
                }

                try
                {
                    ICustomActionHandler handler = null;
                    if (resource.Type == ModResourceType.Ebx)
                    {
                        handler = App.PluginManager.GetCustomHandler((uint)resource.Handler);
                    }
                    else if (resource.Type == ModResourceType.Res)
                    {
                        ResResource resResource = resource as ResResource;
                        if (resResource != null)
                        {
                            handler = App.PluginManager.GetCustomHandler((ResourceType)resResource.ResType);
                        }
                    }

                    if (handler != null && handler.Usage == HandlerUsage.Merge)
                    {
                        return ModActionKind.Merge;
                    }
                }
                catch
                {
                    // Fall back to modify when handler lookup fails.
                }

                return ModActionKind.Modify;
            }

            if (resource.IsAdded)
            {
                return ModActionKind.Add;
            }

            if (resource.IsModified)
            {
                return ModActionKind.Modify;
            }

            return ModActionKind.None;
        }

        private static string ResolveModDisplayName(MM.FrostyAppliedMod appliedMod)
        {
            if (!string.IsNullOrWhiteSpace(appliedMod.ModName))
            {
                return appliedMod.ModName;
            }

            IFrostyMod mod = appliedMod.Mod;
            if (mod != null && mod.ModDetails != null && !string.IsNullOrWhiteSpace(mod.ModDetails.Title))
            {
                return mod.ModDetails.Title;
            }

            return "(Unknown Mod)";
        }

        private static IEnumerable<BaseModResource> EnumerateResources(IFrostyMod mod)
        {
            if (mod == null)
            {
                yield break;
            }

            FrostyModCollection collection = mod as FrostyModCollection;
            if (collection != null)
            {
                foreach (FrostyMod collectionMod in collection.Mods)
                {
                    foreach (BaseModResource resource in EnumerateResourcesFromSingleMod(collectionMod))
                    {
                        yield return resource;
                    }
                }

                yield break;
            }

            foreach (BaseModResource resource in EnumerateResourcesFromSingleMod(mod))
            {
                yield return resource;
            }
        }

        private static IEnumerable<BaseModResource> EnumerateResourcesFromSingleMod(IFrostyMod mod)
        {
            FrostyMod frostyMod = mod as FrostyMod;
            if (frostyMod != null)
            {
                foreach (BaseModResource resource in frostyMod.Resources)
                {
                    yield return resource;
                }

                yield break;
            }

            PropertyInfo resourcesProperty = GetResourcesProperty(mod.GetType());
            if (resourcesProperty == null)
            {
                yield break;
            }

            IEnumerable resources = resourcesProperty.GetValue(mod, null) as IEnumerable;
            if (resources == null)
            {
                yield break;
            }

            foreach (object resource in resources)
            {
                BaseModResource baseResource = resource as BaseModResource;
                if (baseResource != null)
                {
                    yield return baseResource;
                }
            }
        }

        private static PropertyInfo GetResourcesProperty(Type modType)
        {
            lock (propertyCacheLock)
            {
                PropertyInfo property;
                if (resourcesPropertyCache.TryGetValue(modType, out property))
                {
                    return property;
                }

                property = modType.GetProperty("Resources", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                resourcesPropertyCache[modType] = property;
                return property;
            }
        }

        private static ResourceDescriptor BuildResourceDescriptor(BaseModResource resource)
        {
            string resourceType = resource.Type.ToString();
            string resourceName = resource.Name;

            if (!string.IsNullOrWhiteSpace(resource.UserData))
            {
                string[] parts = resource.UserData.Split(';');
                if (parts.Length >= 2)
                {
                    resourceType = parts[0];
                    resourceName = parts[1];
                }
            }

            if (string.IsNullOrWhiteSpace(resourceType))
            {
                resourceType = resource.Type.ToString();
            }

            if (string.IsNullOrWhiteSpace(resourceName))
            {
                resourceName = string.Format(CultureInfo.InvariantCulture, "(resource index {0})", resource.ResourceIndex);
            }

            return new ResourceDescriptor
            {
                Key = (resourceType.Trim().ToLowerInvariant() + "/" + resourceName.Trim().ToLowerInvariant()),
                DisplayName = resourceName,
                DisplayType = resourceType
            };
        }
    }

    public class ModConflictWindow : Window
    {
        private sealed class PairBucket
        {
            public string WinnerMod;
            public string OtherMod;
            public readonly List<ModConflictFileEntry> Files = new List<ModConflictFileEntry>();
            public readonly HashSet<string> SeenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public int OverwrittenCount;
            public int MergedCount;
            public int DisabledCount;
        }

        private readonly Func<List<MM.FrostyAppliedMod>> appliedModsProvider;
        private readonly Func<string, int, bool> moveModInLoadOrder;
        private readonly Func<string, bool> setModAsActiveInLoadOrder;
        private readonly string packName;
        private Dictionary<string, string> assetOverrideRules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly ObservableCollection<ModConflictPairEntry> entries = new ObservableCollection<ModConflictPairEntry>();
        private readonly ObservableCollection<ModConflictFileEntry> affectedFiles = new ObservableCollection<ModConflictFileEntry>();
        private readonly ObservableCollection<ModLoadOrderViewItem> loadOrderItems = new ObservableCollection<ModLoadOrderViewItem>();

        private readonly TextBlock summaryText;
        private readonly TextBlock statusText;
        private readonly TextBox searchTextBox;
        private readonly CheckBox enabledOnlyCheckBox;
        private readonly ListView conflictListView;
        private readonly TextBlock selectedPairText;
        private readonly TextBlock affectedFilesHeaderText;
        private readonly ListView affectedFilesListView;
        private readonly TextBlock selectedAssetText;
        private readonly TextBlock selectedStatusText;
        private readonly TextBlock selectedUsedInGameText;
        private readonly TextBlock selectedImpactText;
        private readonly TextBlock selectedOverriddenPreviewText;
        private readonly ListView loadOrderListView;
        private readonly TextBlock loadOrderHintText;
        private readonly Button moveEarlierButton;
        private readonly Button moveLaterButton;
        private readonly Button setActiveButton;
        private readonly Button resetRuleButton;
        private readonly Brush advancedTextBrush;
        private readonly Brush activeFlowBrush;
        private readonly Brush mergedFlowBrush;
        private readonly Brush replacedFlowBrush;
        private readonly Brush disabledFlowBrush;
        public string PackName => packName;

        public ModConflictWindow(
            MM.MainWindow owner,
            string packName,
            Func<List<MM.FrostyAppliedMod>> appliedModsProvider,
            Func<string, int, bool> moveModInLoadOrder,
            Func<string, bool> setModAsActiveInLoadOrder)
        {
            this.appliedModsProvider = appliedModsProvider;
            this.moveModInLoadOrder = moveModInLoadOrder;
            this.setModAsActiveInLoadOrder = setModAsActiveInLoadOrder;
            this.packName = string.IsNullOrWhiteSpace(packName) ? "Default" : packName;
            assetOverrideRules = ConflictAssetOverrideRules.LoadPackRules(this.packName);

            Title = "Applied Mod Conflicts";
            Width = 1220;
            Height = 760;
            MinWidth = 960;
            MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = owner;

            Brush windowBackground = TryFindBrush("WindowBackground") ?? new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
            Brush fontColor = TryFindBrush("FontColor") ?? new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC));
            Brush controlBackground = TryFindBrush("ControlBackground") ?? new SolidColorBrush(Color.FromRgb(0x3B, 0x3B, 0x3B));
            Brush listBackground = TryFindBrush("ListBackground") ?? controlBackground;
            Brush alternateRowBackground = TryFindBrush("ControlBackground") ?? new SolidColorBrush(Color.FromRgb(0x34, 0x34, 0x34));
            Brush rowBorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
            Brush selectedRowBackground = new SolidColorBrush(Color.FromRgb(0x2D, 0x3F, 0x50));

            advancedTextBrush = fontColor;
            activeFlowBrush = new SolidColorBrush(Color.FromRgb(0x89, 0xE0, 0x9D));
            mergedFlowBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xD2, 0x7A));
            replacedFlowBrush = new SolidColorBrush(Color.FromRgb(0xEE, 0x98, 0x98));
            disabledFlowBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xB8, 0x9A));

            Background = windowBackground;
            Foreground = fontColor;

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            StackPanel topPanel = new StackPanel
            {
                Margin = new Thickness(12, 10, 12, 10)
            };
            Grid.SetRow(topPanel, 0);
            root.Children.Add(topPanel);

            summaryText = new TextBlock
            {
                Text = "Scanning mod resources...",
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 8)
            };
            topPanel.Children.Add(summaryText);

            WrapPanel controlsRow = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                ItemHeight = 28
            };
            topPanel.Children.Add(controlsRow);

            enabledOnlyCheckBox = new CheckBox
            {
                Content = "Enabled mods only",
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            };
            enabledOnlyCheckBox.Click += (s, e) => RefreshFromSource();
            controlsRow.Children.Add(enabledOnlyCheckBox);

            Button refreshButton = new Button
            {
                Content = "Refresh",
                Width = 90,
                Margin = new Thickness(0, 0, 8, 0)
            };
            refreshButton.Click += (s, e) => RefreshFromSource();
            controlsRow.Children.Add(refreshButton);

            Button copyButton = new Button
            {
                Content = "Copy Report",
                Width = 120
            };
            copyButton.Click += CopySelectedConflict;
            controlsRow.Children.Add(copyButton);

            Button copyAffectedFilesButton = new Button
            {
                Content = "Copy Affected Files",
                Width = 150,
                Margin = new Thickness(8, 0, 0, 0)
            };
            copyAffectedFilesButton.Click += CopyAffectedFilesList;
            controlsRow.Children.Add(copyAffectedFilesButton);

            StackPanel legendRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 0)
            };
            topPanel.Children.Add(legendRow);
            legendRow.Children.Add(new TextBlock
            {
                Text = "Legend:",
                Margin = new Thickness(0, 0, 8, 0),
                Opacity = 0.9
            });
            legendRow.Children.Add(new TextBlock
            {
                Text = "Overwritten",
                Foreground = replacedFlowBrush,
                Margin = new Thickness(0, 0, 10, 0)
            });
            legendRow.Children.Add(new TextBlock
            {
                Text = "Merged",
                Foreground = mergedFlowBrush,
                Margin = new Thickness(0, 0, 10, 0)
            });
            legendRow.Children.Add(new TextBlock
            {
                Text = "Disabled",
                Foreground = disabledFlowBrush,
                Margin = new Thickness(0, 0, 10, 0)
            });
            legendRow.Children.Add(new TextBlock
            {
                Text = "Active",
                Foreground = activeFlowBrush
            });

            Grid contentGrid = new Grid
            {
                Margin = new Thickness(12, 0, 12, 0)
            };
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(560) });
            Grid.SetRow(contentGrid, 1);
            root.Children.Add(contentGrid);

            Border listBorder = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = rowBorderBrush,
                Background = controlBackground,
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(listBorder, 0);
            contentGrid.Children.Add(listBorder);

            Grid listGrid = new Grid();
            listGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            listGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            listBorder.Child = listGrid;

            listGrid.Children.Add(new TextBlock
            {
                Text = "Mods With Shared Assets",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 0, 0, 6)
            });

            conflictListView = new ListView
            {
                Margin = new Thickness(0),
                ItemsSource = entries,
                Background = listBackground
            };
            conflictListView.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
            conflictListView.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(conflictListView, true);
            conflictListView.AlternationCount = 2;
            conflictListView.SelectionChanged += (s, e) => UpdateSelectedPair();
            conflictListView.ItemTemplate = CreateConflictCardTemplate();

            Style rowStyle = new Style(typeof(ListViewItem));
            rowStyle.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(0)));
            rowStyle.Setters.Add(new Setter(ListViewItem.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
            rowStyle.Setters.Add(new Setter(ListViewItem.BorderBrushProperty, rowBorderBrush));
            rowStyle.Setters.Add(new Setter(ListViewItem.BackgroundProperty, listBackground));
            rowStyle.Setters.Add(new Setter(ListViewItem.ForegroundProperty, fontColor));
            rowStyle.Setters.Add(new Setter(ListViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

            Trigger oddRowTrigger = new Trigger { Property = ItemsControl.AlternationIndexProperty, Value = 1 };
            oddRowTrigger.Setters.Add(new Setter(ListViewItem.BackgroundProperty, alternateRowBackground));
            rowStyle.Triggers.Add(oddRowTrigger);

            Trigger selectedTrigger = new Trigger { Property = ListViewItem.IsSelectedProperty, Value = true };
            selectedTrigger.Setters.Add(new Setter(ListViewItem.BackgroundProperty, selectedRowBackground));
            rowStyle.Triggers.Add(selectedTrigger);

            conflictListView.ItemContainerStyle = rowStyle;
            Grid.SetRow(conflictListView, 1);
            listGrid.Children.Add(conflictListView);

            Border detailsBorder = new Border
            {
                Margin = new Thickness(0),
                Padding = new Thickness(10, 8, 10, 8),
                BorderThickness = new Thickness(1),
                BorderBrush = rowBorderBrush,
                Background = controlBackground
            };
            Grid.SetColumn(detailsBorder, 1);
            contentGrid.Children.Add(detailsBorder);

            Grid detailsGrid = new Grid();
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // pair summary
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // files label
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.3, GridUnitType.Star) }); // files list
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // selected file header
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // asset
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // used in game
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // status
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // summary
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // replaced mods
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // resolution header
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // hint
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // load order
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // buttons
            detailsBorder.Child = detailsGrid;

            detailsGrid.Children.Add(new TextBlock
            {
                Text = "Selected Conflict",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });

            selectedPairText = new TextBlock
            {
                Text = "(select a mod pair)",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
                Opacity = 0.95
            };
            Grid.SetRow(selectedPairText, 1);
            detailsGrid.Children.Add(selectedPairText);

            DockPanel filesHeaderPanel = new DockPanel
            {
                LastChildFill = false,
                Margin = new Thickness(0, 0, 0, 3)
            };
            Grid.SetRow(filesHeaderPanel, 2);
            detailsGrid.Children.Add(filesHeaderPanel);

            searchTextBox = new TextBox
            {
                Width = 240,
                VerticalAlignment = VerticalAlignment.Center,
                Background = controlBackground,
                Foreground = fontColor,
                CaretBrush = fontColor,
                ToolTip = "Filter by mod names, file names, resource type, or status"
            };
            searchTextBox.TextChanged += (s, e) => RefreshViewFilter();
            DockPanel.SetDock(searchTextBox, Dock.Right);
            filesHeaderPanel.Children.Add(searchTextBox);

            affectedFilesHeaderText = new TextBlock
            {
                Text = "Affected Files",
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold
            };
            filesHeaderPanel.Children.Add(affectedFilesHeaderText);

            Border filesBorder = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = rowBorderBrush,
                Padding = new Thickness(6),
                Background = listBackground,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Grid.SetRow(filesBorder, 3);
            detailsGrid.Children.Add(filesBorder);

            affectedFilesListView = new ListView
            {
                Background = listBackground,
                BorderThickness = new Thickness(0),
                ItemsSource = affectedFiles
            };
            affectedFilesListView.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
            affectedFilesListView.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
            affectedFilesListView.AlternationCount = 2;
            affectedFilesListView.ItemTemplate = CreateAffectedFileTemplate();
            affectedFilesListView.SelectionChanged += (s, e) => UpdateSelectedDetails();

            Style affectedFileRowStyle = new Style(typeof(ListViewItem));
            affectedFileRowStyle.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(4, 3, 4, 3)));
            affectedFileRowStyle.Setters.Add(new Setter(ListViewItem.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
            affectedFileRowStyle.Setters.Add(new Setter(ListViewItem.BorderBrushProperty, rowBorderBrush));
            affectedFileRowStyle.Setters.Add(new Setter(ListViewItem.BackgroundProperty, Brushes.Transparent));
            Trigger oddFileRowTrigger = new Trigger { Property = ItemsControl.AlternationIndexProperty, Value = 1 };
            oddFileRowTrigger.Setters.Add(new Setter(ListViewItem.BackgroundProperty, alternateRowBackground));
            affectedFileRowStyle.Triggers.Add(oddFileRowTrigger);
            Trigger selectedFileTrigger = new Trigger { Property = ListViewItem.IsSelectedProperty, Value = true };
            selectedFileTrigger.Setters.Add(new Setter(ListViewItem.BackgroundProperty, selectedRowBackground));
            affectedFileRowStyle.Triggers.Add(selectedFileTrigger);
            affectedFilesListView.ItemContainerStyle = affectedFileRowStyle;
            filesBorder.Child = affectedFilesListView;

            TextBlock selectedFileHeader = new TextBlock
            {
                Text = "Selected File Details",
                Margin = new Thickness(0, 2, 0, 4),
                FontWeight = FontWeights.SemiBold
            };
            Grid.SetRow(selectedFileHeader, 4);
            detailsGrid.Children.Add(selectedFileHeader);

            selectedAssetText = new TextBlock
            {
                Text = "Asset: (select a file)",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 3)
            };
            Grid.SetRow(selectedAssetText, 5);
            detailsGrid.Children.Add(selectedAssetText);

            selectedUsedInGameText = new TextBlock
            {
                Text = "Active mod: -",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 3),
                FontWeight = FontWeights.SemiBold,
                Foreground = activeFlowBrush
            };
            Grid.SetRow(selectedUsedInGameText, 6);
            detailsGrid.Children.Add(selectedUsedInGameText);

            selectedStatusText = new TextBlock
            {
                Text = "Status: -",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 3),
                Opacity = 0.95
            };
            Grid.SetRow(selectedStatusText, 7);
            detailsGrid.Children.Add(selectedStatusText);

            selectedImpactText = new TextBlock
            {
                Text = "Summary: -",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 3)
            };
            Grid.SetRow(selectedImpactText, 8);
            detailsGrid.Children.Add(selectedImpactText);

            selectedOverriddenPreviewText = new TextBlock
            {
                Text = string.Empty,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
                Opacity = 0.95,
                Foreground = replacedFlowBrush,
                Visibility = Visibility.Collapsed
            };
            Grid.SetRow(selectedOverriddenPreviewText, 9);
            detailsGrid.Children.Add(selectedOverriddenPreviewText);

            TextBlock resolutionHeaderText = new TextBlock
            {
                Text = "Choose Active Mod",
                Margin = new Thickness(0, 4, 0, 3),
                FontWeight = FontWeights.SemiBold
            };
            Grid.SetRow(resolutionHeaderText, 10);
            detailsGrid.Children.Add(resolutionHeaderText);

            loadOrderHintText = new TextBlock
            {
                Text = "Lower items override higher items. Select a mod below and apply it for this asset.",
                Margin = new Thickness(0, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap,
                Foreground = advancedTextBrush,
                Opacity = 0.95
            };
            Grid.SetRow(loadOrderHintText, 11);
            detailsGrid.Children.Add(loadOrderHintText);

            Border flowBorder = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = rowBorderBrush,
                Padding = new Thickness(6),
                Background = listBackground,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Grid.SetRow(flowBorder, 12);
            detailsGrid.Children.Add(flowBorder);

            loadOrderListView = new ListView
            {
                Background = listBackground,
                BorderThickness = new Thickness(0),
                ItemsSource = loadOrderItems
            };
            loadOrderListView.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
            loadOrderListView.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
            loadOrderListView.AlternationCount = 2;
            loadOrderListView.ItemTemplate = CreateLoadOrderTemplate();
            loadOrderListView.SelectionChanged += (s, e) => UpdateResolutionButtonsState();

            Style loadOrderRowStyle = new Style(typeof(ListViewItem));
            loadOrderRowStyle.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(4, 3, 4, 3)));
            loadOrderRowStyle.Setters.Add(new Setter(ListViewItem.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
            loadOrderRowStyle.Setters.Add(new Setter(ListViewItem.BorderBrushProperty, rowBorderBrush));
            loadOrderRowStyle.Setters.Add(new Setter(ListViewItem.BackgroundProperty, Brushes.Transparent));
            Trigger oddLoadOrderTrigger = new Trigger { Property = ItemsControl.AlternationIndexProperty, Value = 1 };
            oddLoadOrderTrigger.Setters.Add(new Setter(ListViewItem.BackgroundProperty, alternateRowBackground));
            loadOrderRowStyle.Triggers.Add(oddLoadOrderTrigger);
            Trigger selectedLoadOrderTrigger = new Trigger { Property = ListViewItem.IsSelectedProperty, Value = true };
            selectedLoadOrderTrigger.Setters.Add(new Setter(ListViewItem.BackgroundProperty, selectedRowBackground));
            loadOrderRowStyle.Triggers.Add(selectedLoadOrderTrigger);
            loadOrderListView.ItemContainerStyle = loadOrderRowStyle;
            flowBorder.Child = loadOrderListView;

            StackPanel resolutionButtonsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };
            Grid.SetRow(resolutionButtonsRow, 13);
            detailsGrid.Children.Add(resolutionButtonsRow);

            moveEarlierButton = new Button
            {
                Content = "Move Earlier",
                MinWidth = 100,
                Margin = new Thickness(0, 0, 6, 0),
                IsEnabled = false
            };
            moveEarlierButton.Click += (s, e) => ResolveMoveSelected(-1);
            resolutionButtonsRow.Children.Add(moveEarlierButton);

            moveLaterButton = new Button
            {
                Content = "Move Later",
                MinWidth = 100,
                Margin = new Thickness(0, 0, 6, 0),
                IsEnabled = false
            };
            moveLaterButton.Click += (s, e) => ResolveMoveSelected(1);
            resolutionButtonsRow.Children.Add(moveLaterButton);

            setActiveButton = new Button
            {
                Content = "Use This Mod for Asset",
                MinWidth = 170,
                IsEnabled = false
            };
            setActiveButton.Click += (s, e) => ResolveSetAsActive();
            resolutionButtonsRow.Children.Add(setActiveButton);

            resetRuleButton = new Button
            {
                Content = "Reset to Load Order",
                MinWidth = 145,
                Margin = new Thickness(6, 0, 0, 0),
                IsEnabled = false
            };
            resetRuleButton.Click += (s, e) => ResolveResetRule();
            resolutionButtonsRow.Children.Add(resetRuleButton);

            statusText = new TextBlock
            {
                Margin = new Thickness(12, 8, 12, 10),
                Opacity = 0.8
            };
            Grid.SetRow(statusText, 2);
            root.Children.Add(statusText);

            Loaded += (s, e) => RefreshFromSource();
        }

        public void RefreshFromSource()
        {
            RefreshFromSourceInternal(GetSelectedPairIdentity(), GetSelectedFileIdentity(), null);
        }

        private void RefreshFromSourceInternal(string preferredPairIdentity, string preferredFileIdentity, string preferredModName)
        {
            List<MM.FrostyAppliedMod> appliedMods = appliedModsProvider != null
                ? appliedModsProvider.Invoke()
                : new List<MM.FrostyAppliedMod>();

            int scannedMods;
            int scannedAssets;
            assetOverrideRules = ConflictAssetOverrideRules.LoadPackRules(packName);
            List<ModConflictViewEntry> assetEntries = ModConflictAnalyzer.Build(
                appliedMods,
                enabledOnlyCheckBox.IsChecked.GetValueOrDefault(),
                assetOverrideRules,
                out scannedMods,
                out scannedAssets);

            List<ModConflictPairEntry> pairEntries = BuildPairEntries(assetEntries);

            entries.Clear();
            foreach (ModConflictPairEntry entry in pairEntries)
            {
                entries.Add(entry);
            }

            RefreshViewFilter();
            UpdateSummary(scannedMods, scannedAssets, assetEntries.Count);

            if (!string.IsNullOrWhiteSpace(preferredPairIdentity))
            {
                TrySelectPairByIdentity(preferredPairIdentity);
            }

            if (conflictListView.SelectedItem == null && conflictListView.Items.Count > 0)
            {
                conflictListView.SelectedIndex = 0;
            }
            else
            {
                UpdateSelectedPair();
            }

            if (!string.IsNullOrWhiteSpace(preferredFileIdentity))
            {
                TrySelectAffectedFileByIdentity(preferredFileIdentity);
            }

            if (!string.IsNullOrWhiteSpace(preferredModName))
            {
                ModLoadOrderViewItem matchingLoadOrderItem = loadOrderItems
                    .FirstOrDefault(item => string.Equals(item.ModName, preferredModName, StringComparison.OrdinalIgnoreCase));
                if (matchingLoadOrderItem != null)
                {
                    loadOrderListView.SelectedItem = matchingLoadOrderItem;
                    loadOrderListView.ScrollIntoView(matchingLoadOrderItem);
                }
            }

            UpdateSelectedDetails();
        }

        private static List<ModConflictPairEntry> BuildPairEntries(IEnumerable<ModConflictViewEntry> assetEntries)
        {
            Dictionary<string, PairBucket> buckets = new Dictionary<string, PairBucket>(StringComparer.OrdinalIgnoreCase);

            foreach (ModConflictViewEntry assetEntry in assetEntries ?? Enumerable.Empty<ModConflictViewEntry>())
            {
                if (assetEntry == null || assetEntry.OrderedMods == null || assetEntry.OrderedMods.Count < 2)
                {
                    continue;
                }

                string winnerMod = string.IsNullOrWhiteSpace(assetEntry.UsedInGameMod)
                    ? assetEntry.OrderedMods[assetEntry.OrderedMods.Count - 1]
                    : assetEntry.UsedInGameMod;

                IEnumerable<string> overriddenMods = (assetEntry.OverriddenMods ?? Array.Empty<string>())
                    .Where(modName => !string.IsNullOrWhiteSpace(modName))
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                string fileIdentity = GetConflictIdentity(assetEntry);
                string displayName = FormatResourceNameForDisplay(assetEntry.ResourceName);
                string outcome = BuildOutcomeLabel(assetEntry.ConflictStatus);

                foreach (string overriddenMod in overriddenMods)
                {
                    string key = (winnerMod ?? string.Empty) + "|" + overriddenMod;
                    PairBucket bucket;
                    if (!buckets.TryGetValue(key, out bucket))
                    {
                        bucket = new PairBucket
                        {
                            WinnerMod = winnerMod,
                            OtherMod = overriddenMod
                        };
                        buckets.Add(key, bucket);
                    }

                    if (!bucket.SeenFiles.Add(fileIdentity))
                    {
                        continue;
                    }

                    bucket.Files.Add(new ModConflictFileEntry
                    {
                        DisplayName = displayName,
                        ResourceName = assetEntry.ResourceName,
                        ResourceType = assetEntry.ResourceType,
                        Outcome = outcome,
                        SearchBlob = string.Join(" ", assetEntry.OrderedMods),
                        SourceConflict = assetEntry
                    });

                    if (string.Equals(outcome, "Merged", StringComparison.OrdinalIgnoreCase))
                    {
                        bucket.MergedCount++;
                    }
                    else if (string.Equals(outcome, "Disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        bucket.DisabledCount++;
                    }
                    else
                    {
                        bucket.OverwrittenCount++;
                    }
                }
            }

            return buckets.Values
                .Select(bucket =>
                {
                    string badgeText;
                    if (bucket.DisabledCount > 0)
                    {
                        badgeText = "Disabled";
                    }
                    else if (bucket.MergedCount > 0 && bucket.OverwrittenCount > 0)
                    {
                        badgeText = "Mixed";
                    }
                    else if (bucket.MergedCount > 0)
                    {
                        badgeText = "Merged";
                    }
                    else
                    {
                        badgeText = "Overwritten";
                    }

                    return new ModConflictPairEntry
                    {
                        WinnerMod = bucket.WinnerMod,
                        OtherMod = bucket.OtherMod,
                        SharedAssetCount = bucket.Files.Count,
                        OverwrittenCount = bucket.OverwrittenCount,
                        MergedCount = bucket.MergedCount,
                        DisabledCount = bucket.DisabledCount,
                        RowTitle = "Active: " + (bucket.WinnerMod ?? "(Unknown)") + "  |  Overridden: " + (bucket.OtherMod ?? "(Unknown)"),
                        RowSubtitle = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} shared files  |  Overwritten {1}  |  Merged {2}",
                            bucket.Files.Count,
                            bucket.OverwrittenCount,
                            bucket.MergedCount),
                        BadgeText = badgeText,
                        SearchBlob = (bucket.WinnerMod ?? string.Empty) + " " + (bucket.OtherMod ?? string.Empty) + " " +
                            string.Join(" ", bucket.Files.Select(file => (file.ResourceName ?? string.Empty) + " " + (file.Outcome ?? string.Empty))),
                        Files = bucket.Files
                            .OrderBy(file => IsChunkLike(file.ResourceType, file.ResourceName, file.DisplayName) ? 1 : 0)
                            .ThenBy(file => file.DisplayName, StringComparer.OrdinalIgnoreCase)
                            .ToList()
                    };
                })
                .OrderByDescending(entry => entry.SharedAssetCount)
                .ThenBy(entry => entry.WinnerMod, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.OtherMod, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string BuildOutcomeLabel(string conflictStatus)
        {
            if (string.Equals(conflictStatus, "Merged", StringComparison.OrdinalIgnoreCase))
            {
                return "Merged";
            }

            if (string.Equals(conflictStatus, "Includes disabled mods", StringComparison.OrdinalIgnoreCase))
            {
                return "Disabled";
            }

            return "Overwritten";
        }

        private void RefreshViewFilter()
        {
            ICollectionView view = CollectionViewSource.GetDefaultView(entries);
            if (view == null)
            {
                return;
            }

            string filter = (searchTextBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(filter))
            {
                view.Filter = null;
            }
            else
            {
                view.Filter = obj =>
                {
                    ModConflictPairEntry entry = obj as ModConflictPairEntry;
                    if (entry == null)
                    {
                        return false;
                    }

                    if (ContainsIgnoreCase(entry.RowTitle, filter)
                        || ContainsIgnoreCase(entry.RowSubtitle, filter)
                        || ContainsIgnoreCase(entry.BadgeText, filter)
                        || ContainsIgnoreCase(entry.SearchBlob, filter))
                    {
                        return true;
                    }

                    return entry.Files != null && entry.Files.Any(file =>
                        ContainsIgnoreCase(file.DisplayName, filter)
                        || ContainsIgnoreCase(file.ResourceType, filter)
                        || ContainsIgnoreCase(file.Outcome, filter));
                };
            }

            view.Refresh();
            int shownCount = view.Cast<object>().Count();
            statusText.Text = string.IsNullOrEmpty(filter)
                ? string.Format(CultureInfo.InvariantCulture, "{0} mod pair rows shown.", shownCount)
                : string.Format(CultureInfo.InvariantCulture, "{0} mod pair rows shown for \"{1}\".", shownCount, filter);

            if (conflictListView.SelectedItem != null && !conflictListView.Items.Contains(conflictListView.SelectedItem))
            {
                conflictListView.SelectedItem = null;
            }
            if (conflictListView.SelectedItem == null && conflictListView.Items.Count > 0)
            {
                conflictListView.SelectedIndex = 0;
            }

            UpdateSelectedPair();
        }

        private void UpdateSummary(int scannedMods, int scannedAssets, int overlappingAssetCount)
        {
            if (entries.Count == 0)
            {
                summaryText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "No shared assets found across {0} scanned mods ({1} touched assets).",
                    scannedMods,
                    scannedAssets);
                return;
            }

            summaryText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0} mod pair(s) share {1} unique assets across {2} scanned mods ({3} touched assets).",
                entries.Count,
                overlappingAssetCount,
                scannedMods,
                scannedAssets);
        }

        private void CopySelectedConflict(object sender, RoutedEventArgs e)
        {
            ModConflictPairEntry selectedPair = conflictListView.SelectedItem as ModConflictPairEntry;
            if (selectedPair == null)
            {
                statusText.Text = "Select a mod pair first.";
                return;
            }

            ModConflictFileEntry selectedFile = affectedFilesListView.SelectedItem as ModConflictFileEntry;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Active Mod: " + (selectedPair.WinnerMod ?? "(Unknown)"));
            sb.AppendLine("Overridden Mod: " + (selectedPair.OtherMod ?? "(Unknown)"));
            sb.AppendLine("Shared Files: " + selectedPair.SharedAssetCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Overwritten: " + selectedPair.OverwrittenCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Merged: " + selectedPair.MergedCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("Affected Files:");
            foreach (ModConflictFileEntry file in selectedPair.Files ?? Array.Empty<ModConflictFileEntry>())
            {
                sb.AppendLine("- [" + file.Outcome + "] " + file.DisplayName);
            }

            if (selectedFile != null && selectedFile.SourceConflict != null)
            {
                sb.AppendLine();
                sb.AppendLine("Selected File:");
                sb.AppendLine(selectedFile.DisplayName);
                sb.AppendLine("Type: " + selectedFile.ResourceType);
                sb.AppendLine("Load Order: " + selectedFile.SourceConflict.LoadOrderPath);
            }

            if (TrySetClipboardText(sb.ToString()))
            {
                statusText.Text = "Copied selected conflict report to clipboard.";
            }
            else
            {
                statusText.Text = "Could not copy report right now (clipboard is busy).";
                App.Logger.LogWarning("Conflict report copy failed because clipboard could not be opened.");
            }
        }

        private void CopyAffectedFilesList(object sender, RoutedEventArgs e)
        {
            ModConflictPairEntry selectedPair = conflictListView.SelectedItem as ModConflictPairEntry;
            if (selectedPair == null)
            {
                statusText.Text = "Select a mod pair first.";
                return;
            }

            IReadOnlyList<ModConflictFileEntry> files = selectedPair.Files ?? Array.Empty<ModConflictFileEntry>();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Active Mod: " + (selectedPair.WinnerMod ?? "(Unknown)"));
            sb.AppendLine("Overridden Mod: " + (selectedPair.OtherMod ?? "(Unknown)"));
            sb.AppendLine("Affected Files (" + files.Count.ToString(CultureInfo.InvariantCulture) + "):");

            for (int i = 0; i < files.Count; i++)
            {
                ModConflictFileEntry file = files[i];
                sb.AppendLine((i + 1).ToString(CultureInfo.InvariantCulture) + ". [" + file.Outcome + "] " + file.DisplayName);
            }

            if (TrySetClipboardText(sb.ToString()))
            {
                statusText.Text = "Copied affected files list to clipboard.";
            }
            else
            {
                statusText.Text = "Could not copy list right now (clipboard is busy).";
                App.Logger.LogWarning("Affected files list copy failed because clipboard could not be opened.");
            }
        }

        private void UpdateSelectedPair()
        {
            ModConflictPairEntry selectedPair = conflictListView.SelectedItem as ModConflictPairEntry;
            affectedFiles.Clear();
            loadOrderItems.Clear();
            loadOrderListView.SelectedItem = null;

            if (selectedPair == null)
            {
                selectedPairText.Text = "(select a mod pair)";
                affectedFilesHeaderText.Text = "Affected Files";
                UpdateSelectedDetails();
                return;
            }

            selectedPairText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "Active Mod: {0}  |  Overridden Mod: {1}  |  Shared files: {2}",
                selectedPair.WinnerMod,
                selectedPair.OtherMod,
                selectedPair.SharedAssetCount);

            foreach (ModConflictFileEntry fileEntry in selectedPair.Files ?? Array.Empty<ModConflictFileEntry>())
            {
                affectedFiles.Add(fileEntry);
            }

            affectedFilesHeaderText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "Affected Files ({0})",
                affectedFiles.Count);

            if (affectedFilesListView.SelectedItem == null && affectedFilesListView.Items.Count > 0)
            {
                affectedFilesListView.SelectedIndex = 0;
            }
            else
            {
                UpdateSelectedDetails();
            }
        }

        private void UpdateSelectedDetails()
        {
            ModConflictPairEntry selectedPair = conflictListView.SelectedItem as ModConflictPairEntry;
            ModConflictFileEntry selectedFile = affectedFilesListView.SelectedItem as ModConflictFileEntry;

            loadOrderItems.Clear();
            loadOrderListView.SelectedItem = null;

            if (selectedPair == null || selectedFile == null || selectedFile.SourceConflict == null)
            {
                selectedAssetText.Text = "Asset: (select a file)";
                selectedUsedInGameText.Text = "Active mod: -";
                selectedStatusText.Text = "Status: -";
                selectedStatusText.Foreground = advancedTextBrush;
                selectedImpactText.Text = "Summary: -";
                selectedOverriddenPreviewText.Text = string.Empty;
                loadOrderHintText.Text = "Select a file to inspect and choose which mod should be active for this asset.";
                UpdateResolutionButtonsState();
                return;
            }

            ModConflictViewEntry source = selectedFile.SourceConflict;
            selectedAssetText.Text = "Asset: " + selectedFile.DisplayName;
            selectedUsedInGameText.Text = source.IsRuleOverride
                ? "Active mod: " + source.UsedInGameMod + " (Rule Override)"
                : "Active mod: " + source.UsedInGameMod + " (Load Order)";
            selectedStatusText.Text = "Status: " + selectedFile.Outcome + (source.IsRuleOverride ? " | Rule Override" : " | Load Order");

            if (string.Equals(selectedFile.Outcome, "Merged", StringComparison.OrdinalIgnoreCase))
            {
                selectedStatusText.Foreground = mergedFlowBrush;
                selectedImpactText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "Summary: {0} is active and this asset merges changes.",
                    source.UsedInGameMod);
            }
            else if (string.Equals(selectedFile.Outcome, "Disabled", StringComparison.OrdinalIgnoreCase))
            {
                selectedStatusText.Foreground = disabledFlowBrush;
                selectedImpactText.Text = "Summary: This file includes disabled-mod involvement in the conflict chain.";
            }
            else
            {
                selectedStatusText.Foreground = replacedFlowBrush;
                selectedImpactText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "Summary: {0} overrides {1} for this asset.",
                    selectedPair.WinnerMod,
                    selectedPair.OtherMod);
            }

            selectedOverriddenPreviewText.Text = string.Empty;
            loadOrderHintText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "Type: {0} | \"Use This Mod for Asset\" saves a per-asset rule. Move buttons still change full pack load order.",
                source.ResourceType);

            IReadOnlyList<string> orderedMods = source.OrderedMods ?? Array.Empty<string>();
            for (int i = 0; i < orderedMods.Count; i++)
            {
                bool isActive = (i == source.ActiveModIndex);
                loadOrderItems.Add(new ModLoadOrderViewItem
                {
                    ModName = orderedMods[i],
                    IsActive = isActive,
                    Label = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}. {1} {2}",
                        i + 1,
                        orderedMods[i],
                        isActive ? "[ACTIVE]" : "[OVERRIDDEN]"),
                    ForegroundBrush = isActive ? activeFlowBrush : replacedFlowBrush
                });
            }

            if (loadOrderItems.Count > 0)
            {
                int selectedIndex = source.ActiveModIndex;
                if (selectedIndex < 0 || selectedIndex >= loadOrderItems.Count)
                {
                    selectedIndex = loadOrderItems.Count - 1;
                }
                loadOrderListView.SelectedIndex = selectedIndex;
            }

            UpdateResolutionButtonsState();
        }

        private void ResolveMoveSelected(int offset)
        {
            ModConflictPairEntry selectedPair = conflictListView.SelectedItem as ModConflictPairEntry;
            ModConflictFileEntry selectedFile = affectedFilesListView.SelectedItem as ModConflictFileEntry;
            ModLoadOrderViewItem selectedMod = loadOrderListView.SelectedItem as ModLoadOrderViewItem;
            if (selectedPair == null || selectedFile == null || selectedMod == null)
            {
                statusText.Text = "Select a mod pair, file, and load-order mod first.";
                return;
            }

            if (moveModInLoadOrder == null)
            {
                statusText.Text = "Load-order editing is unavailable in this build.";
                return;
            }

            bool moved;
            try
            {
                moved = moveModInLoadOrder(selectedMod.ModName, offset);
            }
            catch (Exception ex)
            {
                App.Logger.LogWarning("Conflict load-order move failed: " + ex.Message);
                statusText.Text = "Unable to move that mod in the current load order.";
                return;
            }

            if (!moved)
            {
                statusText.Text = "Unable to move that mod in the current load order.";
                return;
            }

            statusText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0} moved {1}.",
                selectedMod.ModName,
                offset < 0 ? "earlier" : "later");
            RefreshFromSourceInternal(GetPairIdentity(selectedPair), GetFileIdentity(selectedFile), selectedMod.ModName);
        }

        private void ResolveSetAsActive()
        {
            ModConflictPairEntry selectedPair = conflictListView.SelectedItem as ModConflictPairEntry;
            ModConflictFileEntry selectedFile = affectedFilesListView.SelectedItem as ModConflictFileEntry;
            ModLoadOrderViewItem selectedMod = loadOrderListView.SelectedItem as ModLoadOrderViewItem;
            if (selectedPair == null || selectedFile == null || selectedFile.SourceConflict == null || selectedMod == null)
            {
                statusText.Text = "Select a mod pair, file, and load-order mod first.";
                return;
            }

            string resourceKey = GetRuleKey(selectedFile.SourceConflict);
            if (string.IsNullOrWhiteSpace(resourceKey))
            {
                statusText.Text = "Unable to determine rule key for this asset.";
                return;
            }

            bool hasExistingRule = ConflictAssetOverrideRules.TryGetPreferredMod(assetOverrideRules, resourceKey, out string existingPreferredMod);
            if (selectedMod.IsActive
                && hasExistingRule
                && string.Equals(existingPreferredMod, selectedMod.ModName, StringComparison.OrdinalIgnoreCase))
            {
                statusText.Text = "That asset rule is already set.";
                return;
            }

            bool changed = ConflictAssetOverrideRules.SetRule(packName, resourceKey, selectedMod.ModName);
            if (!changed)
            {
                statusText.Text = "That asset rule is already set.";
                return;
            }

            assetOverrideRules[resourceKey] = selectedMod.ModName;
            statusText.Text = selectedMod.ModName + " is now set as active for this asset.";
            RefreshFromSourceInternal(GetPairIdentity(selectedPair), GetFileIdentity(selectedFile), selectedMod.ModName);
        }

        private void ResolveResetRule()
        {
            ModConflictPairEntry selectedPair = conflictListView.SelectedItem as ModConflictPairEntry;
            ModConflictFileEntry selectedFile = affectedFilesListView.SelectedItem as ModConflictFileEntry;
            if (selectedPair == null || selectedFile == null || selectedFile.SourceConflict == null)
            {
                statusText.Text = "Select a mod pair and file first.";
                return;
            }

            string resourceKey = GetRuleKey(selectedFile.SourceConflict);
            if (string.IsNullOrWhiteSpace(resourceKey))
            {
                statusText.Text = "Unable to determine rule key for this asset.";
                return;
            }

            bool changed = ConflictAssetOverrideRules.ClearRule(packName, resourceKey);
            if (!changed)
            {
                statusText.Text = "No asset rule exists for this file.";
                return;
            }

            assetOverrideRules.Remove(resourceKey);
            statusText.Text = "Asset rule cleared. Load order now decides the active mod.";
            RefreshFromSourceInternal(GetPairIdentity(selectedPair), GetFileIdentity(selectedFile), null);
        }

        private void UpdateResolutionButtonsState()
        {
            ModLoadOrderViewItem selectedMod = loadOrderListView.SelectedItem as ModLoadOrderViewItem;
            ModConflictFileEntry selectedFile = affectedFilesListView.SelectedItem as ModConflictFileEntry;
            bool hasSelectedSource = selectedFile != null && selectedFile.SourceConflict != null;
            if (selectedMod == null)
            {
                moveEarlierButton.IsEnabled = false;
                moveLaterButton.IsEnabled = false;
                setActiveButton.IsEnabled = false;
                resetRuleButton.IsEnabled = hasSelectedSource && ConflictAssetOverrideRules.TryGetPreferredMod(assetOverrideRules, GetRuleKey(selectedFile.SourceConflict), out _);
                return;
            }

            int index = loadOrderItems.IndexOf(selectedMod);
            moveEarlierButton.IsEnabled = moveModInLoadOrder != null && index > 0;
            moveLaterButton.IsEnabled = moveModInLoadOrder != null && index >= 0 && index < loadOrderItems.Count - 1;
            string existingPreferredMod = string.Empty;
            bool hasExistingRule = hasSelectedSource && ConflictAssetOverrideRules.TryGetPreferredMod(assetOverrideRules, GetRuleKey(selectedFile.SourceConflict), out existingPreferredMod);
            bool activeAlreadyPinned = hasExistingRule && string.Equals(existingPreferredMod, selectedMod.ModName, StringComparison.OrdinalIgnoreCase);
            setActiveButton.IsEnabled = hasSelectedSource && !activeAlreadyPinned;
            resetRuleButton.IsEnabled = hasSelectedSource && ConflictAssetOverrideRules.TryGetPreferredMod(assetOverrideRules, GetRuleKey(selectedFile.SourceConflict), out _);
        }

        private string GetSelectedPairIdentity()
        {
            return GetPairIdentity(conflictListView.SelectedItem as ModConflictPairEntry);
        }

        private string GetSelectedFileIdentity()
        {
            return GetFileIdentity(affectedFilesListView.SelectedItem as ModConflictFileEntry);
        }

        private static string GetPairIdentity(ModConflictPairEntry entry)
        {
            if (entry == null)
            {
                return null;
            }

            return (entry.WinnerMod ?? string.Empty) + "|" + (entry.OtherMod ?? string.Empty);
        }

        private static string GetConflictIdentity(ModConflictViewEntry entry)
        {
            if (entry == null)
            {
                return null;
            }

            return (entry.ResourceType ?? string.Empty) + "|" + (entry.ResourceName ?? string.Empty);
        }

        private static string GetRuleKey(ModConflictViewEntry entry)
        {
            if (entry == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(entry.ResourceKey))
            {
                return entry.ResourceKey;
            }

            return ConflictAssetOverrideRules.BuildResourceKey(entry.ResourceType, entry.ResourceName);
        }

        private static string GetFileIdentity(ModConflictFileEntry entry)
        {
            return entry == null || entry.SourceConflict == null ? null : GetConflictIdentity(entry.SourceConflict);
        }

        private void TrySelectPairByIdentity(string pairIdentity)
        {
            if (string.IsNullOrWhiteSpace(pairIdentity))
            {
                return;
            }

            foreach (object item in conflictListView.Items)
            {
                ModConflictPairEntry pairEntry = item as ModConflictPairEntry;
                if (pairEntry != null && string.Equals(GetPairIdentity(pairEntry), pairIdentity, StringComparison.Ordinal))
                {
                    conflictListView.SelectedItem = pairEntry;
                    conflictListView.ScrollIntoView(pairEntry);
                    break;
                }
            }
        }

        private void TrySelectAffectedFileByIdentity(string fileIdentity)
        {
            if (string.IsNullOrWhiteSpace(fileIdentity))
            {
                return;
            }

            foreach (object item in affectedFilesListView.Items)
            {
                ModConflictFileEntry fileEntry = item as ModConflictFileEntry;
                if (fileEntry != null && string.Equals(GetFileIdentity(fileEntry), fileIdentity, StringComparison.Ordinal))
                {
                    affectedFilesListView.SelectedItem = fileEntry;
                    affectedFilesListView.ScrollIntoView(fileEntry);
                    break;
                }
            }
        }

        private static bool TrySetClipboardText(string text)
        {
            const int maxAttempts = 5;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    return true;
                }
                catch (COMException ex) when ((uint)ex.ErrorCode == 0x800401D0)
                {
                    Thread.Sleep(25 + (attempt * 40));
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static DataTemplate CreateLoadOrderTemplate()
        {
            FrameworkElementFactory textFactory = new FrameworkElementFactory(typeof(TextBlock));
            textFactory.SetBinding(TextBlock.TextProperty, new Binding("Label"));
            textFactory.SetBinding(TextBlock.ToolTipProperty, new Binding("ModName"));
            textFactory.SetBinding(TextBlock.ForegroundProperty, new Binding("ForegroundBrush"));
            textFactory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);

            return new DataTemplate
            {
                VisualTree = textFactory
            };
        }

        private DataTemplate CreateAffectedFileTemplate()
        {
            FrameworkElementFactory root = new FrameworkElementFactory(typeof(DockPanel));
            root.SetValue(DockPanel.LastChildFillProperty, true);
            root.SetValue(DockPanel.MarginProperty, new Thickness(0, 1, 0, 1));

            FrameworkElementFactory outcomeText = new FrameworkElementFactory(typeof(TextBlock));
            outcomeText.Name = "OutcomeText";
            outcomeText.SetBinding(TextBlock.TextProperty, new Binding("Outcome"));
            outcomeText.SetValue(DockPanel.DockProperty, Dock.Right);
            outcomeText.SetValue(TextBlock.MarginProperty, new Thickness(8, 0, 0, 0));
            outcomeText.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            outcomeText.SetValue(TextBlock.FontSizeProperty, 11.0);
            outcomeText.SetValue(TextBlock.ForegroundProperty, replacedFlowBrush);
            root.AppendChild(outcomeText);

            FrameworkElementFactory infoStack = new FrameworkElementFactory(typeof(StackPanel));
            root.AppendChild(infoStack);

            FrameworkElementFactory title = new FrameworkElementFactory(typeof(TextBlock));
            title.SetBinding(TextBlock.TextProperty, new Binding("DisplayName"));
            title.SetBinding(TextBlock.ToolTipProperty, new Binding("ResourceName"));
            title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            title.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 0, 1));
            infoStack.AppendChild(title);

            FrameworkElementFactory subtitle = new FrameworkElementFactory(typeof(TextBlock));
            Binding resourceTypeBinding = new Binding("ResourceType")
            {
                StringFormat = "Type: {0}"
            };
            subtitle.SetBinding(TextBlock.TextProperty, resourceTypeBinding);
            subtitle.SetValue(TextBlock.OpacityProperty, 0.9);
            subtitle.SetValue(TextBlock.FontSizeProperty, 11.0);
            infoStack.AppendChild(subtitle);

            DataTemplate template = new DataTemplate
            {
                VisualTree = root
            };

            DataTrigger mergedTrigger = new DataTrigger
            {
                Binding = new Binding("Outcome"),
                Value = "Merged"
            };
            mergedTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, mergedFlowBrush, "OutcomeText"));
            template.Triggers.Add(mergedTrigger);

            DataTrigger disabledTrigger = new DataTrigger
            {
                Binding = new Binding("Outcome"),
                Value = "Disabled"
            };
            disabledTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, disabledFlowBrush, "OutcomeText"));
            template.Triggers.Add(disabledTrigger);

            return template;
        }

        private DataTemplate CreateConflictCardTemplate()
        {
            FrameworkElementFactory card = new FrameworkElementFactory(typeof(Border));
            card.SetValue(Border.PaddingProperty, new Thickness(0));
            card.SetValue(Border.MarginProperty, new Thickness(0));
            card.SetValue(Border.BackgroundProperty, Brushes.Transparent);

            FrameworkElementFactory dock = new FrameworkElementFactory(typeof(DockPanel));
            dock.SetValue(DockPanel.LastChildFillProperty, true);
            card.AppendChild(dock);

            FrameworkElementFactory accentBar = new FrameworkElementFactory(typeof(Border));
            accentBar.Name = "AccentBar";
            accentBar.SetValue(DockPanel.DockProperty, Dock.Left);
            accentBar.SetValue(Border.WidthProperty, 4.0);
            accentBar.SetValue(Border.MarginProperty, new Thickness(0, 2, 8, 2));
            accentBar.SetValue(Border.BackgroundProperty, replacedFlowBrush);
            dock.AppendChild(accentBar);

            FrameworkElementFactory badge = new FrameworkElementFactory(typeof(Border));
            badge.Name = "StatusBadge";
            badge.SetValue(DockPanel.DockProperty, Dock.Right);
            badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            badge.SetValue(Border.PaddingProperty, new Thickness(8, 2, 8, 2));
            badge.SetValue(Border.MarginProperty, new Thickness(8, 7, 2, 0));
            badge.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x57, 0x34, 0x34)));
            dock.AppendChild(badge);

            FrameworkElementFactory badgeText = new FrameworkElementFactory(typeof(TextBlock));
            badgeText.Name = "StatusBadgeText";
            badgeText.SetBinding(TextBlock.TextProperty, new Binding("BadgeText"));
            badgeText.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            badgeText.SetValue(TextBlock.FontSizeProperty, 11.0);
            badgeText.SetValue(TextBlock.ForegroundProperty, replacedFlowBrush);
            badge.AppendChild(badgeText);

            FrameworkElementFactory contentStack = new FrameworkElementFactory(typeof(StackPanel));
            contentStack.SetValue(StackPanel.MarginProperty, new Thickness(0, 4, 0, 4));
            dock.AppendChild(contentStack);

            FrameworkElementFactory title = new FrameworkElementFactory(typeof(TextBlock));
            title.SetBinding(TextBlock.TextProperty, new Binding("RowTitle"));
            title.SetBinding(TextBlock.ToolTipProperty, new Binding("RowTitle"));
            title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            contentStack.AppendChild(title);

            FrameworkElementFactory subtitle = new FrameworkElementFactory(typeof(TextBlock));
            subtitle.SetBinding(TextBlock.TextProperty, new Binding("RowSubtitle"));
            subtitle.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            subtitle.SetValue(TextBlock.OpacityProperty, 0.9);
            subtitle.SetValue(TextBlock.MarginProperty, new Thickness(0, 1, 0, 0));
            contentStack.AppendChild(subtitle);

            DataTemplate template = new DataTemplate
            {
                VisualTree = card
            };

            DataTrigger mergedTrigger = new DataTrigger
            {
                Binding = new Binding("BadgeText"),
                Value = "Merged"
            };
            mergedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, mergedFlowBrush, "AccentBar"));
            mergedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x53, 0x4A, 0x2D)), "StatusBadge"));
            mergedTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, mergedFlowBrush, "StatusBadgeText"));
            template.Triggers.Add(mergedTrigger);

            DataTrigger mixedTrigger = new DataTrigger
            {
                Binding = new Binding("BadgeText"),
                Value = "Mixed"
            };
            mixedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xE6, 0xB5, 0x87)), "AccentBar"));
            mixedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x5D, 0x46, 0x2F)), "StatusBadge"));
            mixedTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xE6, 0xB5, 0x87)), "StatusBadgeText"));
            template.Triggers.Add(mixedTrigger);

            DataTrigger disabledTrigger = new DataTrigger
            {
                Binding = new Binding("BadgeText"),
                Value = "Disabled"
            };
            disabledTrigger.Setters.Add(new Setter(Border.BackgroundProperty, disabledFlowBrush, "AccentBar"));
            disabledTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x4F, 0x47, 0x3D)), "StatusBadge"));
            disabledTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, disabledFlowBrush, "StatusBadgeText"));
            template.Triggers.Add(disabledTrigger);

            return template;
        }

        private static string FormatResourceNameForDisplay(string resourceName)
        {
            if (string.IsNullOrWhiteSpace(resourceName))
            {
                return "(unknown asset)";
            }

            string normalized = resourceName.Replace('\\', '/').Trim();
            if (normalized.Length == 0)
            {
                return "(unknown asset)";
            }

            string[] segments = normalized
                .Split(new[] { '/', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return normalized;
            }

            int chunkSegmentIndex = Array.FindIndex(
                segments,
                segment => IsChunkSegment(segment));

            if (chunkSegmentIndex < 0)
            {
                return normalized;
            }

            string chunkInfo = string.Join("/", segments.Skip(chunkSegmentIndex));
            string primary = string.Join("/", segments.Take(chunkSegmentIndex));
            if (!string.IsNullOrWhiteSpace(primary))
            {
                return primary + " [chunk: " + chunkInfo + "]";
            }

            string friendly = segments
                .Skip(chunkSegmentIndex + 1)
                .Reverse()
                .FirstOrDefault(segment =>
                    !IsChunkSegment(segment)
                    && !IsLikelyChunkIdentifier(segment));

            if (string.IsNullOrWhiteSpace(friendly))
            {
                friendly = segments.LastOrDefault();
            }

            if (string.IsNullOrWhiteSpace(friendly)
                || IsChunkSegment(friendly))
            {
                friendly = "(chunk resource)";
            }

            return friendly + " [chunk: " + chunkInfo + "]";
        }

        private static bool IsChunkLike(string resourceType, string resourceName, string displayName)
        {
            if (IsChunkSegment(resourceType))
            {
                return true;
            }

            return ContainsChunkToken(resourceName)
                || ContainsChunkToken(displayName);
        }

        private static bool ContainsChunkToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string normalized = value.Replace('\\', '/');
            if (normalized.IndexOf("/chunk/", StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf("/chunks/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            string[] segments = normalized.Split(new[] { '/', ';', ' ', '[', ']', ':', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
            return segments.Any(IsChunkSegment);
        }

        private static bool IsChunkSegment(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                return false;
            }

            string trimmed = segment.Trim();
            if (string.Equals(trimmed, "chunk", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "chunks", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return trimmed.StartsWith("chunk_", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("chunks_", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("chunk-", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("chunks-", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("chunk", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("chunks", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLikelyChunkIdentifier(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment) || segment.Length < 8)
            {
                return false;
            }

            return segment.All(ch =>
                char.IsDigit(ch)
                || (ch >= 'a' && ch <= 'f')
                || (ch >= 'A' && ch <= 'F')
                || ch == '-'
                || ch == '_');
        }

        private static bool ContainsIgnoreCase(string source, string value)
        {
            return source != null && source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Brush TryFindBrush(string key)
        {
            if (Application.Current != null && Application.Current.Resources.Contains(key))
            {
                return Application.Current.Resources[key] as Brush;
            }

            return null;
        }
    }
}
