using Frosty.Core;
using Frosty.Core.Controls;
using Frosty.Hash;
using FrostySdk.Managers;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Flurry.Editor.DataExplorer
{
    internal enum FlurryFilterCommandType
    {
        Contains,
        StartsWith,
        EndsWith,
        RegEx,
        Type,
        Id,
        Hash
    }

    internal enum FlurryFilterCombineType
    {
        Or,
        And
    }

    internal struct FlurryFilterData
    {
        public string Text;
        public FlurryFilterCommandType Command;
        public FlurryFilterCombineType Combine;
        public bool Not;
    }

    internal class FlurryExplorerState
    {
        public FlurryAssetPath Root;
        public FlurryAssetPath SelectedNode;
        public string PrevFilterText = "";
        public readonly List<FlurryFilterData> Filter = new List<FlurryFilterData>();
        public TaskCompletionSource<bool> ReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SuppressModified;
        public int Generation;
        public Func<AssetEntry, bool> EntryFilterFunction = null;
        public ComboBox TypeComboBox;
        public TextBox TypeSearchBox;
        public ProgressBar FilterProgressBar;
        public Border FilterInProgressBorder;
        public string TypeFilter;
        public List<string> AllTypes = new List<string>();
        public bool UpdatingTypeCombo;
    }

    internal static class FlurryExplorerEngine
    {
        internal static readonly AccessTools.FieldRef<FrostyDataExplorer, TreeView> AssetTreeViewRef = AccessTools.FieldRefAccess<FrostyDataExplorer, TreeView>("assetTreeView");
        internal static readonly AccessTools.FieldRef<FrostyDataExplorer, ListView> AssetListViewRef = AccessTools.FieldRefAccess<FrostyDataExplorer, ListView>("assetListView");
        internal static readonly AccessTools.FieldRef<FrostyDataExplorer, TextBox> FilterTextBoxRef = AccessTools.FieldRefAccess<FrostyDataExplorer, TextBox>("filterTextBox");
        internal static readonly AccessTools.FieldRef<FrostyDataExplorer, string> PrevFilterTextRef = AccessTools.FieldRefAccess<FrostyDataExplorer, string>("prevFilterText");

        private static readonly ConditionalWeakTable<FrostyDataExplorer, FlurryExplorerState> states = new ConditionalWeakTable<FrostyDataExplorer, FlurryExplorerState>();

        internal static FlurryExplorerState GetState(FrostyDataExplorer ctrl)
        {
            return states.GetOrCreateValue(ctrl);
        }

        internal static void ShowProgress(FlurryExplorerState state, FrostyDataExplorer ctrl)
        {
            if (state.FilterProgressBar == null || state.FilterInProgressBorder == null) return;
            ctrl.Dispatcher.Invoke(() =>
            {
                state.FilterProgressBar.Visibility = Visibility.Visible;
                state.FilterInProgressBorder.Visibility = Visibility.Visible;
            });
        }

        internal static void HideProgress(FlurryExplorerState state, FrostyDataExplorer ctrl)
        {
            if (state.FilterProgressBar == null || state.FilterInProgressBorder == null) return;
            ctrl.Dispatcher.Invoke(() =>
            {
                state.FilterProgressBar.Visibility = Visibility.Collapsed;
                state.FilterInProgressBorder.Visibility = Visibility.Collapsed;
            });
        }

        private static bool FilterText(FlurryExplorerState state, string inText, AssetEntry inEntry)
        {
            string type = inEntry.Type ?? "";

            if (state.Filter.Count == 0)
                return true;

            bool retCode = false;
            foreach (FlurryFilterData filterData in state.Filter)
            {
                bool nextRetCode = false;
                switch (filterData.Command)
                {
                    case FlurryFilterCommandType.Contains: nextRetCode = inText.IndexOf(filterData.Text, StringComparison.OrdinalIgnoreCase) >= 0; break;
                    case FlurryFilterCommandType.StartsWith: nextRetCode = inText.StartsWith(filterData.Text, StringComparison.OrdinalIgnoreCase); break;
                    case FlurryFilterCommandType.EndsWith: nextRetCode = inText.EndsWith(filterData.Text, StringComparison.OrdinalIgnoreCase); break;
                    case FlurryFilterCommandType.RegEx: nextRetCode = Regex.IsMatch(inText, filterData.Text); break;
                    case FlurryFilterCommandType.Type: nextRetCode = type.Equals(filterData.Text, StringComparison.OrdinalIgnoreCase); break;
                    case FlurryFilterCommandType.Id:
                        if (inEntry is EbxAssetEntry entry)
                        {
                            nextRetCode = entry.Guid.Equals(new Guid(filterData.Text));
                        }
                        else
                        {
                            if (ulong.TryParse(filterData.Text, System.Globalization.NumberStyles.HexNumber, null, out ulong resRid))
                                nextRetCode = (inEntry as ResAssetEntry).ResRid == resRid;
                        }
                        break;
                    case FlurryFilterCommandType.Hash:
                        {
                            if (int.TryParse(filterData.Text, System.Globalization.NumberStyles.HexNumber, null, out int hash))
                            {
                                if (inEntry is EbxAssetEntry || inEntry is ResAssetEntry)
                                {
                                    nextRetCode = Fnv1.HashString(inEntry.Name.ToLower()) == hash;
                                }
                            }
                        }
                        break;
                }

                if (filterData.Not)
                    nextRetCode = !nextRetCode;

                switch (filterData.Combine)
                {
                    case FlurryFilterCombineType.And: retCode &= nextRetCode; break;
                    case FlurryFilterCombineType.Or: retCode |= nextRetCode; break;
                }
            }

            return retCode;
        }

        internal static void BuildFilterData(FlurryExplorerState state, string filterText, FrostyDataExplorer ctrl)
        {
            state.Filter.Clear();
            if (filterText != "")
            {
                string[] subStr = filterText.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (subStr.Length == 1 && !subStr[0].Contains(":"))
                {
                    state.Filter.Add(new FlurryFilterData()
                    {
                        Text = subStr[0],
                        Command = FlurryFilterCommandType.Contains,
                        Combine = FlurryFilterCombineType.Or,
                        Not = false
                    });
                    PrevFilterTextRef(ctrl) = filterText;
                    SyncTypeComboFromFilter(ctrl, state);
                    return;
                }

                try
                {
                    for (int i = 0; i < subStr.Length; i++)
                    {
                        FlurryFilterCommandType command = FlurryFilterCommandType.Contains;
                        FlurryFilterCombineType combine = FlurryFilterCombineType.Or;
                        bool not = false;

                        if (state.Filter.Count != 0)
                            combine = (subStr[i++] == "AND") ? FlurryFilterCombineType.And : FlurryFilterCombineType.Or;

                        if (subStr[i] == "NOT")
                        {
                            not = true;
                            i++;
                        }

                        string[] cmdArr = subStr[i].Split(new char[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
                        string cmdString = subStr[i];
                        string remaining = "";

                        if (cmdArr.Length > 1)
                        {
                            cmdString = cmdArr[0];
                            remaining = cmdArr[1];
                        }
                        else
                            remaining = subStr[++i];

                        foreach (string value in Enum.GetNames(typeof(FlurryFilterCommandType)))
                        {
                            if (cmdString.StartsWith(value.ToLower()))
                            {
                                command = (FlurryFilterCommandType)Enum.Parse(typeof(FlurryFilterCommandType), value);
                                break;
                            }
                        }

                        state.Filter.Add(new FlurryFilterData()
                        {
                            Text = remaining,
                            Command = command,
                            Combine = combine,
                            Not = not
                        });
                    }
                }
                catch (Exception)
                {
                    FilterTextBoxRef(ctrl).Text = "";
                    state.Filter.Clear();
                }
            }
            PrevFilterTextRef(ctrl) = filterText;
            SyncTypeComboFromFilter(ctrl, state);
        }

        internal static void SyncTypeComboFromFilter(FrostyDataExplorer ctrl, FlurryExplorerState state)
        {
            string parsedType = null;
            List<FlurryFilterData> kept = new List<FlurryFilterData>();

            foreach (FlurryFilterData f in state.Filter)
            {
                if (f.Command == FlurryFilterCommandType.Type && !f.Not)
                {
                    if (parsedType == null)
                        parsedType = f.Text;
                }
                else
                {
                    kept.Add(f);
                }
            }

            state.Filter.Clear();
            state.Filter.AddRange(kept);
            state.TypeFilter = parsedType;

            ComboBox combo = state.TypeComboBox;
            if (combo == null)
                return;

            string target = parsedType ?? "All";
            state.UpdatingTypeCombo = true;
            try
            {
                if (combo.SelectedItem as string != target)
                    combo.SelectedItem = target;
            }
            finally
            {
                state.UpdatingTypeCombo = false;
            }
        }

        private static Func<AssetEntry, bool> CreateAssetFilter(FrostyDataExplorer ctrl, FlurryExplorerState state)
        {
            bool showOnlyModified = ctrl.ShowOnlyModified;
            string typeFilter = state.TypeFilter;

            return (entry) =>
            {
                if (entry == null)
                    return false;

                if (showOnlyModified && !entry.IsModified)
                    return false;

                if (!string.IsNullOrEmpty(typeFilter) && !string.Equals(entry.Type ?? "", typeFilter, StringComparison.OrdinalIgnoreCase))
                    return false;

                return FilterText(state, entry.Name, entry);
            };
        }

        internal static Task CreateAssetPathMappingAsync(FrostyDataExplorer ctrl, FlurryExplorerState state)
        {
            TreeView treeView = AssetTreeViewRef(ctrl);
            if (treeView == null) return Task.CompletedTask;
            IEnumerable items = ctrl.ItemsSource;
            if (items == null) return Task.CompletedTask;

            FlurryAssetPath dummyNode = new FlurryAssetPath("[Loading...]", "", null);
            state.Root = new FlurryAssetPath("", "", null);
            state.Root.VisibleChildren.Add(dummyNode);
            treeView.ItemsSource = state.Root.VisibleChildren;

            return Task.Run(async () =>
            {
                try
                {
                    FlurryAssetPath rootPath = new FlurryAssetPath("![root]", "", null, true);
                    state.Root.Children.Add("/", rootPath);

                    foreach (AssetEntry entry in items)
                    {
                        if (state.EntryFilterFunction != null && !state.EntryFilterFunction(entry)) { continue; }

                        var parts = entry.Path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                        FlurryAssetPath current = parts.Length > 0 ? state.Root : rootPath;

                        foreach (var part in parts)
                        {
                            if (!current.Children.TryGetValue(part, out var next))
                            {
                                next = new FlurryAssetPath(part, "", current);
                                current.Children[part] = next;

                                if (current.VisibleChildren.Count == 0)
                                    current.VisibleChildren.Add(dummyNode);
                            }
                            else
                            {
                                if (part.ToCharArray().Any(char.IsUpper))
                                    next.UpdatePathName(part);

                                if (next == state.SelectedNode)
                                    state.SelectedNode.IsSelected = true;
                            }

                            current = next;
                        }

                        current.Entries.Add(entry);
                    }

                    await ctrl.Dispatcher.InvokeAsync(async () =>
                    {
                        state.Root.LazyLoadChildren();
                        await UpdateTreeViewAsync(ctrl, state, true, false);
                    }).Task.Unwrap();
                }
                catch (Exception ex)
                {
                    App.Logger.LogError(ex.ToString());
                }
            });
        }

        internal static Task UpdateTreeViewAsync(FrostyDataExplorer ctrl, FlurryExplorerState state, bool keepVisualState = true, bool showProgressBar = true)
        {
            TreeView treeView = AssetTreeViewRef(ctrl);
            if (treeView == null || state.Root == null || state.Root.IsLazyLoaded == false)
                return Task.CompletedTask;

            if (!keepVisualState && state.SelectedNode != null)
                state.SelectedNode.IsSelected = false;

            if (ctrl.ItemsSource == null)
                return Task.CompletedTask;

            var ShouldDisplayAsset = CreateAssetFilter(ctrl, state);
            int generation = ++state.Generation;

            return Task.Run(async () =>
            {
                try
                {
                    if (showProgressBar)
                    {
                        ShowProgress(state, ctrl);
                    }

                    FlurryAssetPath newRoot = state.Root.Search(ShouldDisplayAsset, keepVisualState);

                    newRoot?.LazyLoadChildren();

                    if (generation != state.Generation)
                        return;

                    await ctrl.Dispatcher.InvokeAsync(() =>
                    {
                        string selectedFullPath = state.SelectedNode != null && !state.SelectedNode.IsRoot ? state.SelectedNode.FullPath : null;

                        treeView.SelectedValuePath = string.Empty;
                        treeView.ItemsSource = newRoot?.VisibleChildren;
                        treeView.Items.Refresh();

                        FlurryAssetPath selectedNode = null;
                        if (newRoot != null && !string.IsNullOrEmpty(selectedFullPath))
                        {
                            if (selectedFullPath == "![root]")
                            {
                                FlurryAssetPath rootPath;
                                if (newRoot.Children.TryGetValue("/", out rootPath))
                                    selectedNode = rootPath;
                            }
                            else
                            {
                                FlurryAssetPath cur = newRoot;
                                foreach (string seg in selectedFullPath.Split('/'))
                                {
                                    FlurryAssetPath next;
                                    if (cur == null || !cur.Children.TryGetValue(seg, out next))
                                    {
                                        cur = null;
                                        break;
                                    }
                                    cur = next;
                                }
                                selectedNode = cur;
                            }
                        }

                        if (selectedNode != null)
                        {
                            state.SelectedNode = selectedNode;
                            selectedNode.IsSelected = true;
                            ctrl.SelectedPath = selectedNode.IsRoot ? "" : selectedNode.FullPath;
                            UpdateListViewAsync(ctrl, state, selectedNode);
                        }
                        else
                        {
                            state.SelectedNode = null;
                            ctrl.SelectedPath = "";
                            UpdateListViewAsync(ctrl, state, null);
                        }
                    });
                }
                catch (Exception ex)
                {
                    App.Logger.LogError(ex.ToString());
                }
                finally
                {
                    if (showProgressBar)
                    {
                        HideProgress(state, ctrl);
                    }
                }
            });
        }

        internal static Task UpdateListViewAsync(FrostyDataExplorer ctrl, FlurryExplorerState state, FlurryAssetPath path = null)
        {
            ListView assetListView = AssetListViewRef(ctrl);
            if (assetListView == null)
            {
                return Task.CompletedTask;
            }
            if (path == null)
            {
                assetListView.ItemsSource = null;
                return Task.CompletedTask;
            }

            return ctrl.Dispatcher.InvokeAsync(() =>
            {
                assetListView.ItemsSource = path.Entries;

                if (ctrl.SelectedAsset != null)
                {
                    assetListView.SelectedItem = ctrl.SelectedAsset;
                }
                else if (ctrl.SelectedAssets != null)
                {
                    foreach (var item in ctrl.SelectedAssets)
                    {
                        assetListView.SelectedItems.Add(item);
                    }
                }
            }, DispatcherPriority.Background).Task;
        }

        internal static async Task SelectAssetAsync(FrostyDataExplorer ctrl, FlurryExplorerState state, AssetEntry entry, bool clearFilters = true)
        {
            await state.ReadyTcs.Task;

            TreeView treeView = AssetTreeViewRef(ctrl);
            if (treeView == null)
            {
                ctrl.SelectedAsset = entry;
                return;
            }

            if (entry == null)
            {
                ctrl.SelectedAsset = null;
                AssetListViewRef(ctrl).SelectedItem = null;
                return;
            }

            await ctrl.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    state.SuppressModified = true;
                    ShowProgress(state, ctrl);

                    if (clearFilters &&
                        (ctrl.ShowOnlyModified || state.PrevFilterText != "")
                        )
                    {
                        ctrl.SetValue(FrostyDataExplorer.ShowOnlyModifiedProperty, false);

                        TextBox filterTextBox = FilterTextBoxRef(ctrl);
                        if (state.PrevFilterText != "")
                        {
                            filterTextBox.Text = "";
                            state.PrevFilterText = "";
                            BuildFilterData(state, "", ctrl);
                        }

                        await UpdateTreeViewAsync(ctrl, state, false, false);
                        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ContextIdle);
                    }

                    string[] pathSegments = entry.Path.Length > 0
                        ? entry.Path.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                        : new string[1] { "/" };

                    FlurryAssetPath current = state.Root;
                    List<FlurryAssetPath> traversalStack = new List<FlurryAssetPath> { current };

                    foreach (string segment in pathSegments)
                    {
                        if (current != null && current.Children.TryGetValue(segment, out var next))
                        {
                            current = next;
                            traversalStack.Add(current);
                        }
                        else
                        {
                            break;
                        }
                    }

                    TreeViewItem tvi = null;
                    foreach (var node in traversalStack.Skip(1))
                    {
                        FlurryAssetPath target = node;

                        if (node.Clone != null)
                        {
                            target = node.Clone;
                        }

                        target.LazyLoadChildren();
                        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ContextIdle);

                        if (tvi != null)
                        {
                            tvi.IsExpanded = true;
                            tvi.UpdateLayout();
                            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ContextIdle);

                            await ForceRealizeItem(tvi, target);
                            tvi = await GetContainerFromItemAsync<TreeViewItem>(tvi, target);
                        }
                        else if (node.Parent == state.Root)
                        {
                            await ForceRealizeItem(treeView, target);
                            tvi = await GetContainerFromItemAsync<TreeViewItem>(treeView, target);
                        }
                    }

                    if (tvi != null)
                    {
                        tvi.UpdateLayout();
                        tvi.IsSelected = true;
                        tvi.BringIntoView();
                        tvi.Focus();
                    }

                    if (current != null)
                        current.IsSelected = true;

                    if (ctrl.AssetListVisible)
                    {
                        await UpdateListViewAsync(ctrl, state, current);
                        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        await ForceRealizeItem(AssetListViewRef(ctrl), entry);
                        AssetListViewRef(ctrl).SelectedItem = entry;
                        AssetListViewRef(ctrl).ScrollIntoView(entry);
                    }

                    ctrl.SelectedAsset = entry;
                }
                catch (Exception e)
                {
                    App.Logger.LogError(e.ToString());
                }
                finally
                {
                    state.SuppressModified = false;
                    HideProgress(state, ctrl);
                }
            }).Task.Unwrap();
        }

        private static async Task<T> GetContainerFromItemAsync<T>(ItemsControl parent, object item)
            where T : DependencyObject
        {
            int retries = 40;

            while (parent.ItemContainerGenerator.ContainerFromItem(item) as T == null && retries-- > 0)
            {
                await Task.Delay(50);
                parent.UpdateLayout();
            }

            return parent.ItemContainerGenerator.ContainerFromItem(item) as T;
        }

        private static async Task<bool> ForceRealizeItem(ItemsControl parent, object item)
        {
            int index = FindIndexOfNode(parent, item);
            if (index < 0) return false;

            int retries = 20;
            var panel = FindVirtualizingStackPanel(parent);
            while (panel == null && retries-- > 0)
            {
                await Task.Delay(50);
                panel = FindVirtualizingStackPanel(parent);
            }
            if (panel != null)
            {
                panel.BringIndexIntoViewPublic(index);
                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ContextIdle);
                return true;
            }

            return false;
        }

        private static int FindIndexOfNode(ItemsControl parent, object item)
        {
            if (parent == null || item == null)
                return -1;

            var items = parent.ItemsSource as IList;
            if (items == null) return -1;

            for (int i = 0; i < items.Count; i++)
            {
                if (ReferenceEquals(items[i], item))
                    return i;
            }

            return -1;
        }

        private static VirtualizingStackPanel FindVirtualizingStackPanel(DependencyObject parent)
        {
            if (parent == null) return null;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                if (child is VirtualizingStackPanel vsp)
                    return vsp;

                VirtualizingStackPanel result = FindVirtualizingStackPanel(child);
                if (result != null)
                    return result;
            }

            return null;
        }

        internal static Task PopulateAssetTypesComboBox(FrostyDataExplorer ctrl, FlurryExplorerState state)
        {
            if (state.TypeComboBox == null || ctrl.ItemsSource == null) return Task.CompletedTask;
            var entries = ctrl.ItemsSource;

            return Task.Run(() =>
            {
                var assetTypes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (AssetEntry asset in entries)
                {
                    if (!string.IsNullOrEmpty(asset.Type))
                        assetTypes.Add(asset.Type);
                }

                List<string> list = new List<string>(assetTypes);
                list.Sort(StringComparer.OrdinalIgnoreCase);

                ctrl.Dispatcher.Invoke(() =>
                {
                    state.AllTypes = list;
                    state.UpdatingTypeCombo = true;
                    try
                    {
                        state.TypeComboBox.ItemsSource = BuildTypeList(state, "");
                        state.TypeComboBox.SelectedIndex = 0;
                    }
                    finally
                    {
                        state.UpdatingTypeCombo = false;
                    }
                });
            });
        }

        internal static List<string> BuildTypeList(FlurryExplorerState state, string searchText)
        {
            var result = new List<string>();
            result.Add("All");

            if (string.IsNullOrWhiteSpace(searchText) || searchText.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                result.AddRange(state.AllTypes);
                return result;
            }

            List<string> startsWith = new List<string>();
            List<string> contains = new List<string>();
            foreach (string type in state.AllTypes)
            {
                int idx = type.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
                if (idx == 0)
                    startsWith.Add(type);
                else if (idx > 0)
                    contains.Add(type);
            }

            startsWith.Sort(StringComparer.OrdinalIgnoreCase);
            contains.Sort(StringComparer.OrdinalIgnoreCase);

            result.AddRange(startsWith);
            result.AddRange(contains);
            return result;
        }

        internal static void WireTypeComboBox(FrostyDataExplorer ctrl, FlurryExplorerState state)
        {
            ComboBox combo = state.TypeComboBox;
            if (combo == null) return;

            combo.DropDownOpened += (sender, e) =>
            {
                if (state.UpdatingTypeCombo) return;
                InitTypeSearchBox(ctrl, state);
                ResetTypeSearch(state);
            };

            combo.SelectionChanged += (sender, e) =>
            {
                if (state.UpdatingTypeCombo) return;

                string selected = combo.SelectedItem as string;
                if (selected == null) return;

                state.TypeFilter = selected == "All" ? null : selected;
                UpdateTreeViewAsync(ctrl, state);
            };
        }

        private static void InitTypeSearchBox(FrostyDataExplorer ctrl, FlurryExplorerState state)
        {
            if (state.TypeSearchBox != null) return;

            ComboBox combo = state.TypeComboBox;
            TextBox searchBox = combo.Template != null ? combo.Template.FindName("PART_TypeSearchBox", combo) as TextBox : null;
            if (searchBox == null) return;

            state.TypeSearchBox = searchBox;
            searchBox.TextChanged += (sender, e) => SearchTypeList(ctrl, state);
            searchBox.PreviewKeyDown += (sender, e) => TypeSearchBox_PreviewKeyDown(combo, searchBox, e);
        }

        private static void ResetTypeSearch(FlurryExplorerState state)
        {
            ComboBox combo = state.TypeComboBox;
            TextBox searchBox = state.TypeSearchBox;
            if (combo == null || searchBox == null) return;

            state.UpdatingTypeCombo = true;
            try
            {
                searchBox.Text = "";
                combo.ItemsSource = BuildTypeList(state, "");
                searchBox.Focus();
                searchBox.CaretIndex = 0;
            }
            finally
            {
                state.UpdatingTypeCombo = false;
            }
        }

        private static void SearchTypeList(FrostyDataExplorer ctrl, FlurryExplorerState state)
        {
            ComboBox combo = state.TypeComboBox;
            TextBox searchBox = state.TypeSearchBox;
            if (combo == null || searchBox == null || state.UpdatingTypeCombo) return;

            state.UpdatingTypeCombo = true;
            try
            {
                combo.ItemsSource = BuildTypeList(state, searchBox.Text);
            }
            finally
            {
                state.UpdatingTypeCombo = false;
            }
        }

        private static void TypeSearchBox_PreviewKeyDown(ComboBox combo, TextBox searchBox, System.Windows.Input.KeyEventArgs e)
        {
            switch (e.Key)
            {
                case System.Windows.Input.Key.Down:
                case System.Windows.Input.Key.Up:
                    {
                        int count = combo.Items.Count;
                        if (count == 0)
                        {
                            e.Handled = true;
                            return;
                        }

                        bool hasSearchText = !string.IsNullOrEmpty(searchBox.Text) && !searchBox.Text.Equals("All", StringComparison.OrdinalIgnoreCase);
                        int idx = combo.SelectedIndex;
                        if (idx < 0)
                        {
                            idx = e.Key == System.Windows.Input.Key.Down ? (hasSearchText && count > 1 ? 1 : 0) : count - 1;
                        }
                        else
                        {
                            idx += e.Key == System.Windows.Input.Key.Down ? 1 : -1;
                        }

                        if (idx >= count) idx = count - 1;
                        if (idx < 0) idx = 0;

                        combo.SelectedIndex = idx;
                        ComboBoxItem cbi = combo.ItemContainerGenerator.ContainerFromIndex(idx) as ComboBoxItem;
                        if (cbi != null)
                            cbi.BringIntoView();
                        e.Handled = true;
                        break;
                    }
                case System.Windows.Input.Key.Enter:
                    if (combo.SelectedItem != null)
                        combo.IsDropDownOpen = false;
                    e.Handled = true;
                    break;
                case System.Windows.Input.Key.Escape:
                    combo.IsDropDownOpen = false;
                    e.Handled = true;
                    break;
            }
        }
    }
}

