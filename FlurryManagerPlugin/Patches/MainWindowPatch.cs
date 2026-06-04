using Frosty.Controls;
using Frosty.Core.Mod;
using Flurry.Manager.Windows;
using HarmonyLib;
using MM = FrostyModManager;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Flurry.Manager.Patches
{
    [HarmonyPatch(typeof(MM.MainWindow))]
    [HarmonyPatchCategory("flurry.manager")]
    public class MainWindow_ManagerUIPatches
    {
        private static AccessTools.FieldRef<MM.MainWindow, Button> removeButtonRef = AccessTools.FieldRefAccess<MM.MainWindow, Button>("removeButton");
        private static AccessTools.FieldRef<MM.MainWindow, ListBox> appliedModsListRef = AccessTools.FieldRefAccess<MM.MainWindow, ListBox>("appliedModsList");
        public static AccessTools.FieldRef<MM.MainWindow, MM.FrostyPack> selectedPackRef = AccessTools.FieldRefAccess<MM.MainWindow, MM.FrostyPack>("selectedPack");
        private static AccessTools.FieldRef<MM.MainWindow, FrostyWatermarkTextBox> availableModsFilterTextBoxRef = AccessTools.FieldRefAccess<MM.MainWindow, FrostyWatermarkTextBox>("availableModsFilterTextBox");
        private static AccessTools.FieldRef<MM.MainWindow, ListView> availableModsListRef = AccessTools.FieldRefAccess<MM.MainWindow, ListView>("availableModsList");
        private static AccessTools.FieldRef<MM.MainWindow, Button> installModButtonRef = AccessTools.FieldRefAccess<MM.MainWindow, Button>("installModButton");
        private static readonly Dictionary<MM.MainWindow, ModConflictWindow> conflictWindows = new Dictionary<MM.MainWindow, ModConflictWindow>();

        public static Button invertSelectionButton = new Button()
        {
            Content = new Image()
            {
                Source = new ImageSourceConverter().ConvertFromString("pack://application:,,,/FlurryManagerPlugin;component/Images/InvertSelect.png") as ImageSource,
                Margin = new System.Windows.Thickness(0, 0, 2, 0),
            },
            IsEnabled = false,
            ToolTip = "Toggle enabled for all selected mods\nShift click to disable all selected\nCtrl Shift click to enable all selected",
        };

        private static DockPanel searchPanelContainer = new DockPanel()
        {
            VerticalAlignment = VerticalAlignment.Center,
            Width = Double.NaN,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        private static ToggleButton appliedModsFilterButton = new ToggleButton()
        {
            ToolTip = "Filter applied mods",
            Content = new Image()
            {
                Source = new ImageSourceConverter().ConvertFromString("pack://application:,,,/FlurryManagerPlugin;component/Images/CircleCheck.png") as ImageSource,
                Height = 16,
                Width = 16,
                Margin = new Thickness(0, 0, 2, 0),
            }
        };
        private static ToggleButton notAppliedModsFilterButton = new ToggleButton()
        {
            ToolTip = "Filter not applied mods",
            Content = new Image()
            {
                Source = new ImageSourceConverter().ConvertFromString("pack://application:,,,/FlurryManagerPlugin;component/Images/CircleX.png") as ImageSource,
                Height = 16,
                Width = 16,
                Margin = new Thickness(0, 0, 2, 0),
            }
        };
        private static Grid availableModsTabGrid;

        #region Helper Methods

        /// <summary>
        /// Finds all children of a specified type in the visual tree.
        /// </summary>
        /// <typeparam name="T">The type of the elements to find.</typeparam>
        /// <param name="depObj">The parent dependency object to start the search from.</param>
        /// <returns>An enumerable collection of matching elements.</returns>
        public static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
        {
            if (depObj == null) yield break;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(depObj, i);
                if (child != null && child is T tChild)
                {
                    yield return tChild;
                }

                foreach (T childOfChild in FindVisualChildren<T>(child))
                {
                    yield return childOfChild;
                }
            }
        }

        public static bool HasProperty(object obj, string propertyName)
        {
            if (obj == null)
            {
                return false;
            }

            Type type = obj.GetType();
            // Use GetProperty to search for a public instance property
            PropertyInfo propInfo = type.GetProperty(propertyName);

            // If propInfo is not null, the property exists
            return propInfo != null;
        }

        private static void PrintVisualTree(int depth, DependencyObject obj)
        {
            FileLog.Debug(new string(' ', depth * 2) + obj.GetType().Name + ":" + (HasProperty(obj, "Name") ? ((obj as dynamic).Name) : "_"));
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                PrintVisualTree(depth + 1, VisualTreeHelper.GetChild(obj, i));
            }
        }

        #endregion

        #region Applied Mods Filter Refresh
        [HarmonyPatch("removeButton_Click")]
        [HarmonyPostfix]
        public static void RefreshOnRemoveButtonClick(MM.MainWindow __instance) {
            RefreshFilter(__instance);
        }
        [HarmonyPatch("addModButton_Click")]
        [HarmonyPostfix]
        public static void RefreshOnAddButtonClick(MM.MainWindow __instance)
        {
            RefreshFilter(__instance);
        }
        [HarmonyPatch("uninstallModButton_Click")]
        [HarmonyPostfix]
        public static void RefreshOnUninstallButtonClick(MM.MainWindow __instance)
        {
            RefreshFilter(__instance);
        }
        [HarmonyPatch("availableModsList_MouseDoubleClick")]
        [HarmonyPostfix]
        public static void RefreshOnApplyViaDoubleClick(MM.MainWindow __instance)
        {
            RefreshFilter(__instance);
        }
        [HarmonyPatch("collectionModsList_MouseDoubleClick")]
        [HarmonyPostfix]
        public static void RefreshOnApplyViaDoubleClick_Collection(MM.MainWindow __instance)
        {
            RefreshFilter(__instance);
        }
        [HarmonyPatch("InstallMods")]
        [HarmonyPostfix]
        public static void RefreshOnInstallMods(MM.MainWindow __instance)
        {
            RefreshFilter(__instance);
        }
        #endregion

        [HarmonyPatch("LoadMenuExtensions")]
        [HarmonyPostfix]
        public static void AddUIElements(MM.MainWindow __instance) {
            #region Invert Button
            // Invert button
            FileLog.Debug("Within PostFix");
            Frosty.Core.App.Logger.Log("Adding Invert Selection Button to Mod Manager UI");
            Button removeButton = removeButtonRef(__instance);
            StackPanel parentPanel = (StackPanel)removeButton.Parent;
            
            invertSelectionButton.Click += (s, e) =>
            {
                ListBox appliedModsList = appliedModsListRef(__instance);
                foreach (MM.FrostyAppliedMod mod in appliedModsList.SelectedItems)
                {
                    bool targetValue = !mod.IsEnabled;
                    if (Keyboard.IsKeyDown(Key.LeftShift))
                    {
                        if (Keyboard.IsKeyDown(Key.LeftCtrl))
                        {
                            // ctrl + shift = enable all
                            targetValue = true;
                        }
                        else
                        {
                            // just shift = disable all
                            targetValue = false;
                        }
                    }
                    mod.IsEnabled = targetValue;
                }
                appliedModsList.Items.Refresh();
                selectedPackRef(__instance).Refresh();
            };
            parentPanel.Children.Add(invertSelectionButton);

            Button conflictExplorerButton = new Button()
            {
                Content = "Conflicts",
                Margin = new Thickness(2, 0, 0, 0),
                MinWidth = 85,
                ToolTip = "Open a fast conflict explorer for applied mods",
            };
            conflictExplorerButton.Click += (s, e) => OpenConflictWindow(__instance);
            parentPanel.Children.Add(conflictExplorerButton);
            // End of Invert button
            #endregion

            #region Applied Mods Filter
            // Applied mods filter stuff
            __instance.Width = 1150;

            FileLog.Debug("Pre applied mods changes");
            ListView availableModsList = availableModsListRef(__instance);
            availableModsTabGrid = availableModsList.Parent as Grid;
            FrostyWatermarkTextBox availableModsFilterTextBox = availableModsFilterTextBoxRef(__instance);
            FileLog.Debug("Got references;");
            searchPanelContainer.Children.Add(appliedModsFilterButton);
            FileLog.Debug("Added appliedModsFilterButton");
            searchPanelContainer.Children.Add(notAppliedModsFilterButton);
            FileLog.Debug("Added notAppliedModsFilterButton");

            availableModsTabGrid.Children.Remove(availableModsFilterTextBox);
            FileLog.Debug("Removed original filter textbox");
            searchPanelContainer.Children.Add(availableModsFilterTextBox);
            FileLog.Debug("Added filter textbox to new container");

            availableModsTabGrid.Children.Insert(availableModsTabGrid.Children.IndexOf(availableModsList), searchPanelContainer);
            FileLog.Debug("Inserted new container into grid");
            Grid.SetRow(searchPanelContainer, 1);
            Grid.SetRow(availableModsList, 2);
            availableModsFilterTextBox.ClearValue(Grid.RowProperty);

            appliedModsFilterButton.Click += (o, e) =>
            {
                // NotApplied + Applied both checked makes no sense, same as neither checked.
                if (appliedModsFilterButton.IsChecked.GetValueOrDefault())
                {
                    notAppliedModsFilterButton.IsChecked = false;
                }

                RefreshFilter(__instance);
            };
            notAppliedModsFilterButton.Click += (o, e) => {
                // NotApplied + Applied both checked makes no sense, same as neither checked.
                if (notAppliedModsFilterButton.IsChecked.GetValueOrDefault())
                {
                    appliedModsFilterButton.IsChecked = false;
                }

                RefreshFilter(__instance);
            };

            RefreshFilter(__instance);
            dynamic installModButton = installModButtonRef(__instance);

            
            FrostyDockablePanel somethingSomethingAvailableMods = installModButton.Parent.Parent.Parent.Parent.Parent.Parent.HeaderControl.Parent.Parent;

            IEnumerable<FrostyTabItem> tabs = FindVisualChildren<FrostyTabItem>(somethingSomethingAvailableMods);

            FrostyTabItem availableModsTab = null;
            foreach (FrostyTabItem tab in tabs)
            {
                if (tab.Header.ToString().Contains("Available Mods"))
                {
                    availableModsTab = tab;
                    break;
                }
            }
            if (availableModsTab == null)
            {
                PrintVisualTree(2, somethingSomethingAvailableMods);
                throw new Exception("No available mods tab? Printing visual tree to debug log.");
            }

            //availableModsTab.Header = "Available Mods (" + availableModsList.Items.Count + ")";

            FrameworkElementFactory factory = new FrameworkElementFactory(typeof(Image));
            factory.SetValue(Image.SourceProperty, new ImageSourceConverter().ConvertFromString("pack://application:,,,/FlurryManagerPlugin;component/Images/CircleCheck.png") as ImageSource);
            factory.SetValue(Image.HeightProperty, 16.0d);
            factory.SetValue(Image.WidthProperty, 16.0d);
            factory.SetValue(Image.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            factory.SetValue(Image.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.SetBinding(Image.VisibilityProperty, new Binding("ModDetails.Title")
            {
                Converter = new ModAppliedConverter(),
                ConverterParameter = __instance,
                Mode = BindingMode.OneWay,
            });
            DataTemplate dt = new DataTemplate();
            dt.VisualTree = factory;
            GridView gridView = availableModsList.View as GridView;
            gridView.Columns.Add(new GridViewColumn() { Header = "Applied" });
            GridViewColumn appliedBindingColumn = gridView.Columns[2];
            appliedBindingColumn.CellTemplate = dt;

            // Size column
            var sizeFactory = new FrameworkElementFactory(typeof(TextBlock));
            sizeFactory.SetBinding(TextBlock.TextProperty, new Binding(".")
            {
                Converter = new Flurry.Manager.Patches.ModSizeConverter()
            });
            sizeFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            sizeFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            var sizeDt = new DataTemplate { VisualTree = sizeFactory };
            gridView.Columns.Add(new GridViewColumn() { Header = "Size", Width = 80, CellTemplate = sizeDt });
            // End of Size column

            // End of Applied mods filter stuff
            #endregion
        }

        private static string ResolveAppliedModDisplayName(MM.FrostyAppliedMod appliedMod)
        {
            if (appliedMod == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(appliedMod.ModName))
            {
                return appliedMod.ModName;
            }

            IFrostyMod mod = appliedMod.Mod;
            if (mod != null && mod.ModDetails != null && !string.IsNullOrWhiteSpace(mod.ModDetails.Title))
            {
                return mod.ModDetails.Title;
            }

            return string.Empty;
        }

        private static void RefreshAppliedModsUi(MM.MainWindow mainWindow)
        {
            MM.FrostyPack selectedPack = selectedPackRef(mainWindow);
            if (selectedPack != null)
            {
                selectedPack.Refresh();
            }

            ListBox appliedModsList = appliedModsListRef(mainWindow);
            if (appliedModsList != null)
            {
                appliedModsList.Items.Refresh();
            }
        }

        private static bool MoveAppliedModInLoadOrder(MM.MainWindow mainWindow, string modDisplayName, int offset)
        {
            if (string.IsNullOrWhiteSpace(modDisplayName))
            {
                return false;
            }

            MM.FrostyPack selectedPack = selectedPackRef(mainWindow);
            if (selectedPack == null || selectedPack.AppliedMods == null || selectedPack.AppliedMods.Count == 0)
            {
                return false;
            }

            int sourceIndex = -1;
            for (int i = 0; i < selectedPack.AppliedMods.Count; i++)
            {
                string displayName = ResolveAppliedModDisplayName(selectedPack.AppliedMods[i]);
                if (string.Equals(displayName, modDisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    sourceIndex = i;
                    break;
                }
            }

            if (sourceIndex < 0)
            {
                return false;
            }

            int targetIndex = sourceIndex + offset;
            if (targetIndex < 0 || targetIndex >= selectedPack.AppliedMods.Count)
            {
                return false;
            }

            MM.FrostyAppliedMod movingMod = selectedPack.AppliedMods[sourceIndex];
            selectedPack.AppliedMods.RemoveAt(sourceIndex);
            selectedPack.AppliedMods.Insert(targetIndex, movingMod);
            RefreshAppliedModsUi(mainWindow);
            return true;
        }

        private static bool SetAppliedModAsActive(MM.MainWindow mainWindow, string modDisplayName)
        {
            if (string.IsNullOrWhiteSpace(modDisplayName))
            {
                return false;
            }

            MM.FrostyPack selectedPack = selectedPackRef(mainWindow);
            if (selectedPack == null || selectedPack.AppliedMods == null || selectedPack.AppliedMods.Count == 0)
            {
                return false;
            }

            int sourceIndex = -1;
            for (int i = 0; i < selectedPack.AppliedMods.Count; i++)
            {
                string displayName = ResolveAppliedModDisplayName(selectedPack.AppliedMods[i]);
                if (string.Equals(displayName, modDisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    sourceIndex = i;
                    break;
                }
            }

            if (sourceIndex < 0)
            {
                return false;
            }

            MM.FrostyAppliedMod movingMod = selectedPack.AppliedMods[sourceIndex];
            selectedPack.AppliedMods.RemoveAt(sourceIndex);
            selectedPack.AppliedMods.Add(movingMod);
            RefreshAppliedModsUi(mainWindow);
            return true;
        }

        private static void OpenConflictWindow(MM.MainWindow mainWindow)
        {
            MM.FrostyPack selectedPack = selectedPackRef(mainWindow);
            if (selectedPack == null || selectedPack.AppliedMods == null || selectedPack.AppliedMods.Count == 0)
            {
                Frosty.Core.App.Logger.LogWarning("No applied mods in the selected pack. Add mods first, then open Conflicts.");
                return;
            }

            ModConflictWindow existingWindow;
            if (conflictWindows.TryGetValue(mainWindow, out existingWindow))
            {
                if (existingWindow.IsVisible)
                {
                    if (!string.Equals(existingWindow.PackName, selectedPack.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        existingWindow.Close();
                        conflictWindows.Remove(mainWindow);
                    }
                    else
                    {
                        existingWindow.RefreshFromSource();
                        existingWindow.Activate();
                        return;
                    }
                }
                else
                {
                    conflictWindows.Remove(mainWindow);
                }
            }

            ModConflictWindow window = new ModConflictWindow(
                mainWindow,
                selectedPack.Name,
                () =>
                {
                    MM.FrostyPack currentPack = selectedPackRef(mainWindow);
                    if (currentPack == null || currentPack.AppliedMods == null)
                    {
                        return new List<MM.FrostyAppliedMod>();
                    }

                    return new List<MM.FrostyAppliedMod>(currentPack.AppliedMods);
                },
                (modDisplayName, offset) => MoveAppliedModInLoadOrder(mainWindow, modDisplayName, offset),
                modDisplayName => SetAppliedModAsActive(mainWindow, modDisplayName));

            conflictWindows[mainWindow] = window;
            window.Closed += (sender, args) => conflictWindows.Remove(mainWindow);
            window.Show();
        }

        [HarmonyPatch("updateAppliedModButtons")]
        [HarmonyPostfix]
        public static void UpdateInvertButtonState(MM.MainWindow __instance)
        {
            ListBox appliedModsList = appliedModsListRef(__instance);
            if (appliedModsList.SelectedItem == null)
            {
                invertSelectionButton.IsEnabled = false;
            } else
            {
                invertSelectionButton.IsEnabled = true;
            }
        }

        [HarmonyPatch("availableModsFilter_LostFocus")]
        [HarmonyPrefix]
        public static void RefreshFilter(MM.MainWindow __instance)
        {
            //
            MM.FrostyPack selectedPack = selectedPackRef(__instance);
            TextBox availableModsFilterTextBox = availableModsFilterTextBoxRef(__instance);
            ListView availableModsList = availableModsListRef(__instance);

            Func<IFrostyMod, bool> nameFilter = a =>
            {
                if (availableModsFilterTextBox.Text != "")
                {
                    return (a).ModDetails.Title.ToLower().Contains(availableModsFilterTextBox.Text.ToLower());
                }

                return true;
            };

            Func<IFrostyMod, bool> appliedOrNotFilter = a =>
            {
                if (appliedModsFilterButton.IsChecked.GetValueOrDefault())
                {
                    return selectedPack.AppliedMods.Exists(x => x.ModName == ((IFrostyMod)a).ModDetails.Title);
                }
                else if (notAppliedModsFilterButton.IsChecked.GetValueOrDefault())
                {
                    return !selectedPack.AppliedMods.Exists(x => x.ModName == ((IFrostyMod)a).ModDetails.Title);
                }

                return true;
            };

            availableModsList.Items.Filter = new Predicate<object>((object a) => appliedOrNotFilter((IFrostyMod)a) && nameFilter((IFrostyMod)a));
            /*
            if (availableModsFilterTextBox.Text != "" || appliedModsFilterButton.IsChecked.GetValueOrDefault() || notAppliedModsFilterButton.IsChecked.GetValueOrDefault())
            {
                availableModsStatusBar.Text = string.Format("{0} mods pass filter.", availableModsList.Items.Count);
            }
            else
            {
                availableModsStatusBar.Text = string.Format("{0} mods available.", availableModsList.Items.Count);
            } */

            // Skips original method.
        }

    }

    public class ModAppliedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            String title = (String)value;
            MM.MainWindow mainWindow = (MM.MainWindow)parameter;
            MM.FrostyPack selectedPack = MainWindow_ManagerUIPatches.selectedPackRef(mainWindow);

            if (selectedPack != null)
            {
                if (selectedPack.AppliedMods.Exists(x => x.ModName == title))
                {
                    return Visibility.Visible;
                }
            }

            return Visibility.Hidden;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
