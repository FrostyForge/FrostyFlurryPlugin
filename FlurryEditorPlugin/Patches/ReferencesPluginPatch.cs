using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Controls;
using FrostySdk.Managers;
using HarmonyLib;
using ReferencesPlugin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;

namespace Flurry.Editor.Patches
{
    [HarmonyPatch(typeof(ReferenceTabItem))]
    [HarmonyPatchCategory("flurry.editor")]
    public class ReferencesPluginPatch
    {
        private static AccessTools.FieldRef<ReferenceTabItem, MenuItem> explorerToFindItem_ref = AccessTools.FieldRefAccess<ReferenceTabItem, MenuItem>("refExplorerToFindItem");
        private static AccessTools.FieldRef<ReferenceTabItem, MenuItem> explorerFromFindItem_ref = AccessTools.FieldRefAccess<ReferenceTabItem, MenuItem>("refExplorerFromFindItem");
        private static AccessTools.FieldRef<ReferenceTabItem, FrostyAssetListView> refToList_ref = AccessTools.FieldRefAccess<ReferenceTabItem, FrostyAssetListView>("refExplorerToList");
        private static AccessTools.FieldRef<ReferenceTabItem, FrostyAssetListView> refFromList_ref = AccessTools.FieldRefAccess<ReferenceTabItem, FrostyAssetListView>("refExplorerFromList");

        private static CheckBox hideMvdbCheckBox;
        private static CheckBox hideNetRegCheckBox;

        [HarmonyPatch("RefreshReferences")]
        [HarmonyPostfix]
        public static void ReferenceCounter(ReferenceTabItem __instance)
        {
            FlurryEditorConfig config = new FlurryEditorConfig();
            config.Load();

            if (!config.ReferencesTabTweaks)
            {
                return;
            }

            FrostyAssetListView refExplorerToList = refToList_ref(__instance);
            FrostyAssetListView refExplorerFromList = refFromList_ref(__instance);

            // Apply MVDB/NetReg filter based on UI checkboxes
            bool hideMvdb = hideMvdbCheckBox?.IsChecked == true;
            bool hideNetReg = hideNetRegCheckBox?.IsChecked == true;

            if (refExplorerToList.ItemsSource != null)
            {
                ICollectionView view = CollectionViewSource.GetDefaultView(refExplorerToList.ItemsSource);
                if (view != null)
                {
                    if (hideMvdb || hideNetReg)
                    {
                        view.Filter = obj =>
                        {
                            if (obj is EbxAssetEntry entry)
                            {
                                if (hideMvdb && entry.Type == "MeshVariationDatabase")
                                    return false;
                                if (hideNetReg && entry.Type == "NetworkRegistryAsset")
                                    return false;
                            }
                            return true;
                        };
                    }
                    else
                    {
                        view.Filter = null;
                    }
                }
            }

            foreach (var item in App.EditorWindow.MiscTabControl.Items)
            {
                if (item is FrostyTabItem)
                {
                    FrostyTabItem tab = item as FrostyTabItem;
                    if (tab.Header.ToString().Contains("References"))
                    {
                        if (refExplorerToList.ItemsSource == null || refExplorerFromList.ItemsSource == null)
                        {
                            tab.Header = "References (0 & 0)";
                            return;
                        }

                        // Count visible items (respecting filter)
                        int toCount;
                        ICollectionView toView = CollectionViewSource.GetDefaultView(refExplorerToList.ItemsSource);
                        if (toView != null && toView.Filter != null)
                            toCount = toView.Cast<object>().Count();
                        else
                            toCount = Enumerable.Count((IEnumerable<EbxAssetEntry>)refExplorerToList.ItemsSource);

                        int fromCount = Enumerable.Count((IEnumerable<EbxAssetEntry>)refExplorerFromList.ItemsSource);
                        tab.Header = $"References ({toCount} & {fromCount})";
                    }
                }
            }
        }

        [HarmonyPatch("OnApplyTemplate")]
        [HarmonyPostfix]
        public static void PatchContextMenu(ReferenceTabItem __instance)
        {
            FlurryEditorConfig config = new FlurryEditorConfig();
            config.Load();
            if (!config.ReferencesTabTweaks)
            {
                return;
            }

            ImageSourceConverter converter = new ImageSourceConverter();

            MenuItem refExplorerToFindItem = explorerToFindItem_ref(__instance);
            MenuItem refExploreFromFindItem = explorerFromFindItem_ref(__instance);
            FrostyAssetListView refExplorerToList = refToList_ref(__instance);
            FrostyAssetListView refExplorerFromList = refFromList_ref(__instance);

            // Inject filter checkboxes into the references tab grid
            Grid parentGrid = VisualTreeHelper.GetParent(refExplorerToList) as Grid;
            if (parentGrid != null && hideMvdbCheckBox == null)
            {
                Brush fgBrush = Application.Current?.Resources.Contains("FontColor") == true
                    ? Application.Current.Resources["FontColor"] as Brush
                    : new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF8));

                // Add a new row for filter checkboxes
                parentGrid.RowDefinitions.Insert(1, new RowDefinition { Height = GridLength.Auto });

                // Shift existing row 1+ children down by one
                foreach (UIElement child in parentGrid.Children)
                {
                    int row = Grid.GetRow(child);
                    if (row >= 1)
                        Grid.SetRow(child, row + 1);
                }

                var filterPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(4, 2, 4, 2)
                };
                Grid.SetRow(filterPanel, 1);
                Grid.SetColumn(filterPanel, 0);

                hideMvdbCheckBox = new CheckBox
                {
                    Content = "Hide MVDBs",
                    Foreground = fgBrush,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0),
                    IsChecked = false
                };

                hideNetRegCheckBox = new CheckBox
                {
                    Content = "Hide NetRegs",
                    Foreground = fgBrush,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    IsChecked = false
                };

                // When toggled, refresh the filter on the current list
                EventHandler refreshFilter = (s, e) =>
                {
                    if (refExplorerToList.ItemsSource == null)
                        return;

                    bool hideMvdb = hideMvdbCheckBox.IsChecked == true;
                    bool hideNetReg = hideNetRegCheckBox.IsChecked == true;

                    ICollectionView view = CollectionViewSource.GetDefaultView(refExplorerToList.ItemsSource);
                    if (view != null)
                    {
                        if (hideMvdb || hideNetReg)
                        {
                            view.Filter = obj =>
                            {
                                if (obj is EbxAssetEntry entry)
                                {
                                    if (hideMvdb && entry.Type == "MeshVariationDatabase")
                                        return false;
                                    if (hideNetReg && entry.Type == "NetworkRegistryAsset")
                                        return false;
                                }
                                return true;
                            };
                        }
                        else
                        {
                            view.Filter = null;
                        }
                        view.Refresh();
                    }

                    // Update tab header count
                    foreach (var item in App.EditorWindow.MiscTabControl.Items)
                    {
                        if (item is FrostyTabItem tab && tab.Header.ToString().Contains("References"))
                        {
                            int toCount;
                            ICollectionView toView = CollectionViewSource.GetDefaultView(refExplorerToList.ItemsSource);
                            if (toView?.Filter != null)
                                toCount = toView.Cast<object>().Count();
                            else
                                toCount = Enumerable.Count((IEnumerable<EbxAssetEntry>)refExplorerToList.ItemsSource);

                            int fromCount = refExplorerFromList.ItemsSource != null
                                ? Enumerable.Count((IEnumerable<EbxAssetEntry>)refExplorerFromList.ItemsSource)
                                : 0;
                            tab.Header = $"References ({toCount} & {fromCount})";
                            break;
                        }
                    }
                };

                hideMvdbCheckBox.Checked += (s, e) => refreshFilter(s, e);
                hideMvdbCheckBox.Unchecked += (s, e) => refreshFilter(s, e);
                hideNetRegCheckBox.Checked += (s, e) => refreshFilter(s, e);
                hideNetRegCheckBox.Unchecked += (s, e) => refreshFilter(s, e);

                filterPanel.Children.Add(hideMvdbCheckBox);
                filterPanel.Children.Add(hideNetRegCheckBox);
                parentGrid.Children.Add(filterPanel);
            }

            ContextMenu cmTo = refExplorerToFindItem.Parent as ContextMenu;
            ContextMenu cmFrom = refExploreFromFindItem.Parent as ContextMenu;

            // Add an icon to the "Open in Explorer" menu items
            refExplorerToFindItem.Icon = new Image()
            {
                Source = converter.ConvertFromString("pack://application:,,,/FrostyEditor;component/Images/Open.png") as ImageSource,
                Opacity = 0.5
            };
            refExploreFromFindItem.Icon = new Image()
            {
                Source = converter.ConvertFromString("pack://application:,,,/FrostyEditor;component/Images/Open.png") as ImageSource,
                Opacity = 0.5
            };


            // Additional icons
            // Open in Blueprint Editor
            if (config.BlueprintEditorTweaks)
            {
                MenuItem blueprintEditorToItem = new MenuItem()
                {
                    Header = "Open in Blueprint Editor",
                    Icon = new Image()
                    {
                        Source = converter.ConvertFromString("pack://application:,,,/FrostyCore;component/Images/Assets/BlueprintFileType.png") as ImageSource,
                        Opacity = 0.5
                    }
                };
                blueprintEditorToItem.Click += (s, e) =>
                {
                    if (refExplorerToList.SelectedItem is EbxAssetEntry)
                        FlurryEditorUtils.OpenInBlueprintEditor(refExplorerToList.SelectedItem as EbxAssetEntry);
                };

                MenuItem blueprintEditorFromItem = new MenuItem()
                {
                    Header = "Open in Blueprint Editor",
                    Icon = new Image()
                    {
                        Source = converter.ConvertFromString("pack://application:,,,/FrostyCore;component/Images/Assets/BlueprintFileType.png") as ImageSource,
                        Opacity = 0.5
                    }
                };
                blueprintEditorFromItem.Click += (s, e) =>
                {
                    if (refExplorerFromList.SelectedItem is EbxAssetEntry)
                        FlurryEditorUtils.OpenInBlueprintEditor(refExplorerFromList.SelectedItem as EbxAssetEntry);
                };

                cmTo.Items.Add(blueprintEditorToItem);
                cmFrom.Items.Add(blueprintEditorFromItem);
            }


            // Copy file GUID
            MenuItem copyGuidToItem = new MenuItem()
            {
                Header = "Copy file GUID",
                Icon = new Image()
                {
                    Source = converter.ConvertFromString("pack://application:,,,/FrostyCore;component/Images/Copy.png") as ImageSource,
                    Opacity = 0.5
                }
            };
            copyGuidToItem.Click += (s, e) =>
            {
                if (refExplorerToList.SelectedItem == null)
                    return;
                if (refExplorerToList.SelectedItem is EbxAssetEntry ebxEntry)
                {
                    Clipboard.SetText(ebxEntry.Guid.ToString());
                }
            };

            MenuItem copyGuidFromItem = new MenuItem()
            {
                Header = "Copy file GUID",
                Icon = new Image()
                {
                    Source = converter.ConvertFromString("pack://application:,,,/FrostyCore;component/Images/Copy.png") as ImageSource,
                    Opacity = 0.5
                }
            };
            copyGuidFromItem.Click += (s, e) =>
            {
                if (refExplorerFromList.SelectedItem == null)
                    return;
                if (refExplorerFromList.SelectedItem is EbxAssetEntry ebxEntry)
                {
                    Clipboard.SetText(ebxEntry.Guid.ToString());
                }
            };

            cmTo.Items.Add(copyGuidToItem);
            cmFrom.Items.Add(copyGuidFromItem);


            // Copy file path
            MenuItem copyPathToItem = new MenuItem()
            {
                Header = "Copy file path",
                Icon = new Image()
                {
                    Source = converter.ConvertFromString("pack://application:,,,/FrostyCore;component/Images/Copy.png") as ImageSource,
                    Opacity = 0.5
                }
            };
            copyPathToItem.Click += (s, e) =>
            {
                EbxAssetEntry entry = refExplorerToList.SelectedItem as EbxAssetEntry;
                if (entry == null)
                    return;
                Clipboard.SetText(entry.Name);
            };

            MenuItem copyPathFromItem = new MenuItem()
            {
                Header = "Copy file path",
                Icon = new Image()
                {
                    Source = converter.ConvertFromString("pack://application:,,,/FrostyCore;component/Images/Copy.png") as ImageSource,
                    Opacity = 0.5
                }
            };
            copyPathFromItem.Click += (s, e) =>
            {
                EbxAssetEntry entry = refExplorerFromList.SelectedItem as EbxAssetEntry;
                if (entry == null)
                    return;
                Clipboard.SetText(entry.Name);
            };

            cmTo.Items.Add(copyPathToItem);
            cmFrom.Items.Add(copyPathFromItem);
        }
    }
}
