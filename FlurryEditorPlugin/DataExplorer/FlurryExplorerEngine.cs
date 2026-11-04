using Frosty.Core;
using Frosty.Core.Controls;
using FrostySdk;
using FrostySdk.Managers;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Flurry.Editor.DataExplorer
{
    /// <summary>
    /// Per-control mutable state for the fast Data Explorer. Instances live in a
    /// <see cref="ConditionalWeakTable"/> so they never outlive their control.
    /// </summary>
    internal class FlurryExplorerState
    {
        public FlurryAssetPath Root;
        public FlurryAssetPath SelectedNode;
        public readonly List<FlurryFilterData> Filter = new List<FlurryFilterData>();
        public TaskCompletionSource<bool> ReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SuppressModified;
        public int Generation;
        public ComboBox TypeComboBox;
        public TextBox TypeSearchBox;
        public ProgressBar FilterProgressBar;
        public Border FilterInProgressBorder;
        public string TypeFilter;
        public bool TypeFilterFromText;
        public List<string> AllTypes = new List<string>();
        public readonly ConcurrentDictionary<string, HashSet<string>> TypeDescendants = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        public bool UpdatingTypeCombo;
        public readonly HashSet<AssetEntry> BaseEntries = new HashSet<AssetEntry>();
        public int BaseEntryCount;
        public readonly object BaseLock = new object();
        public int SelectGeneration;
        public bool SuppressSelection;

        // Delegates retained so template re-application can unsubscribe stale handlers.
        public KeyEventHandler FilterKeyDownHandler;
        public TextChangedEventHandler TypeSearchTextChangedHandler;
        public KeyEventHandler TypeSearchKeyDownHandler;
        public EventHandler TypeComboDropDownOpenedHandler;
        public EventHandler TypeComboDropDownClosedHandler;
        public SelectionChangedEventHandler TypeComboSelectionChangedHandler;
    }

    /// <summary>
    /// Tree building, filtering, and selection plumbing for the fast Data Explorer mode.
    /// The base tree is built on background threads and is never bound to the UI; the UI
    /// only ever sees filter clones produced by <see cref="FlurryAssetPath.Search"/>. All
    /// UI-bound mutations happen on the owning control's dispatcher.
    /// </summary>
    internal static class FlurryExplorerEngine
    {
        internal static bool FastModeEnabled;

        internal const string AllTypesItem = "All";
        internal const string RootKeyName = "/";
        internal const string RootDisplayName = "![root]";
        internal const string LoadingDisplayName = "[Loading...]";
        internal const string TypeSearchPartName = "PART_TypeSearchBox";

        internal static readonly AccessTools.FieldRef<FrostyDataExplorer, TreeView> AssetTreeViewRef = AccessTools.FieldRefAccess<FrostyDataExplorer, TreeView>("assetTreeView");
        internal static readonly AccessTools.FieldRef<FrostyDataExplorer, ListView> AssetListViewRef = AccessTools.FieldRefAccess<FrostyDataExplorer, ListView>("assetListView");
        internal static readonly AccessTools.FieldRef<FrostyDataExplorer, TextBox> FilterTextBoxRef = AccessTools.FieldRefAccess<FrostyDataExplorer, TextBox>("filterTextBox");
        internal static readonly AccessTools.FieldRef<FrostyDataExplorer, string> PrevFilterTextRef = AccessTools.FieldRefAccess<FrostyDataExplorer, string>("prevFilterText");

        private static readonly ConditionalWeakTable<FrostyDataExplorer, FlurryExplorerState> states = new ConditionalWeakTable<FrostyDataExplorer, FlurryExplorerState>();

        internal static FlurryExplorerState GetState(FrostyDataExplorer ctrl)
        {
            return states.GetOrCreateValue(ctrl);
        }

        #region Progress indicator

        internal static void SetProgressVisible(FlurryExplorerState state, FrostyDataExplorer ctrl, bool visible)
        {
            ctrl.Dispatcher.InvokeAsync(() => ApplyProgressVisibility(state, visible));
        }

        internal static void SetProgressVisible(FlurryExplorerState state, FrostyDataExplorer ctrl, int generation, bool visible)
        {
            ctrl.Dispatcher.InvokeAsync(() =>
            {
                // A superseded run must not hide the progress a newer run has shown.
                if (state.Generation == generation)
                    ApplyProgressVisibility(state, visible);
            });
        }

        private static void ApplyProgressVisibility(FlurryExplorerState state, bool visible)
        {
            if (state.FilterProgressBar == null || state.FilterInProgressBorder == null)
                return;

            Visibility visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            state.FilterProgressBar.Visibility = visibility;
            state.FilterInProgressBorder.Visibility = visibility;
        }

        #endregion

        #region Filter plumbing

        internal static HashSet<string> GetTypeDescendants(FlurryExplorerState state, string baseType)
        {
            return state.TypeDescendants.GetOrAdd(baseType, _ =>
            {
                string canonical = state.AllTypes.FirstOrDefault(t => t.Equals(baseType, StringComparison.OrdinalIgnoreCase)) ?? baseType;

                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string typeName in state.AllTypes)
                {
                    if (typeName.Equals(canonical, StringComparison.OrdinalIgnoreCase) || TypeLibrary.IsSubClassOf(typeName, canonical))
                        set.Add(typeName);
                }
                return set;
            });
        }

        internal static void BuildFilterData(FlurryExplorerState state, string filterText, FrostyDataExplorer ctrl)
        {
            List<FlurryFilterData> tokens;
            List<List<string>> consumed;
            if (!FlurryFilterEngine.TryTokenize(filterText, out tokens, out consumed))
            {
                TextBox filterTextBox = FilterTextBoxRef(ctrl);
                if (filterTextBox != null)
                    filterTextBox.Text = "";
            }

            state.Filter.Clear();
            state.Filter.AddRange(tokens);
            PrevFilterTextRef(ctrl) = filterText;
            SyncTypeComboFromFilter(ctrl, state);
        }

        internal static void SyncTypeComboFromFilter(FrostyDataExplorer ctrl, FlurryExplorerState state)
        {
            string firstType = null;
            int typeCount = 0;
            var kept = new List<FlurryFilterData>();

            foreach (FlurryFilterData token in state.Filter)
            {
                if (token.Command == FlurryFilterCommandType.Type && !token.Not)
                {
                    typeCount++;
                    if (typeCount == 1)
                        firstType = token.Text;
                }
                else
                {
                    kept.Add(token);
                }
            }

            if (typeCount == 1)
            {
                state.Filter.Clear();
                state.Filter.AddRange(kept);
                state.TypeFilter = firstType;
                state.TypeFilterFromText = true;
                SelectTypeComboItem(state, firstType);
            }
            else if (typeCount > 1)
            {
                state.TypeFilter = null;
                state.TypeFilterFromText = false;
                SelectTypeComboItem(state, AllTypesItem);
            }
            else if (state.TypeFilterFromText)
            {
                state.TypeFilter = null;
                state.TypeFilterFromText = false;
                SelectTypeComboItem(state, AllTypesItem);
            }
        }

        internal static void StripPlainTypeTokens(FrostyDataExplorer ctrl, FlurryExplorerState state)
        {
            TextBox filterTextBox = FilterTextBoxRef(ctrl);
            string filterText = filterTextBox?.Text ?? "";
            if (filterText == "")
                return;

            List<FlurryFilterData> tokens;
            List<List<string>> consumed;
            if (!FlurryFilterEngine.TryTokenize(filterText, out tokens, out consumed))
                return;

            bool hasPlainType = tokens.Any(t => t.Command == FlurryFilterCommandType.Type && !t.Not);
            if (!hasPlainType)
                return;

            var keptWords = new List<string>();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (!(tokens[i].Command == FlurryFilterCommandType.Type && !tokens[i].Not))
                    keptWords.AddRange(consumed[i]);
            }

            while (keptWords.Count > 0 && keptWords[0].Equals(FlurryFilterEngine.AndToken, StringComparison.OrdinalIgnoreCase))
                keptWords.RemoveAt(0);

            string newText = string.Join(" ", keptWords);
            if (newText == filterText)
                return;

            filterTextBox.Text = newText;
            BuildFilterData(state, newText, ctrl);
        }

        #endregion

        #region Type combo box

        internal static List<string> BuildTypeList(FlurryExplorerState state, string searchText)
        {
            var result = new List<string> { AllTypesItem };

            if (string.IsNullOrWhiteSpace(searchText) || searchText.Equals(AllTypesItem, StringComparison.OrdinalIgnoreCase))
            {
                result.AddRange(state.AllTypes);
                return result;
            }

            var startsWith = new List<string>();
            var contains = new List<string>();
            foreach (string type in state.AllTypes)
            {
                int index = type.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
                if (index == 0)
                    startsWith.Add(type);
                else if (index > 0)
                    contains.Add(type);
            }

            startsWith.Sort(StringComparer.OrdinalIgnoreCase);
            contains.Sort(StringComparer.OrdinalIgnoreCase);
            result.AddRange(startsWith);
            result.AddRange(contains);
            return result;
        }

        private static bool SelectTypeComboItem(FlurryExplorerState state, string target)
        {
            ComboBox combo = state.TypeComboBox;
            if (combo == null)
                return false;

            state.UpdatingTypeCombo = true;
            try
            {
                if (combo.SelectedItem as string == target)
                    return true;

                bool present = false;
                foreach (object item in combo.Items)
                {
                    if (string.Equals(item as string, target, StringComparison.OrdinalIgnoreCase))
                    {
                        present = true;
                        break;
                    }
                }

                if (!present)
                    combo.ItemsSource = BuildTypeList(state, "");

                combo.SelectedItem = target;
                return true;
            }
            finally
            {
                state.UpdatingTypeCombo = false;
            }
        }

        internal static void ClearTypeFilter(FlurryExplorerState state)
        {
            state.TypeFilter = null;
            state.TypeFilterFromText = false;
            SelectTypeComboItem(state, AllTypesItem);
        }

        internal static void WireTypeComboBox(FrostyDataExplorer ctrl, FlurryExplorerState state)
        {
            ComboBox combo = state.TypeComboBox;
            if (combo == null)
                return;

            // DropDownOpened/DropDownClosed are field-like CLR events (null-tolerant -=);
            // SelectionChanged is routed and must go through Unsubscribe.
            combo.DropDownOpened -= state.TypeComboDropDownOpenedHandler;
            combo.DropDownClosed -= state.TypeComboDropDownClosedHandler;
            Unsubscribe(combo, Selector.SelectionChangedEvent, state.TypeComboSelectionChangedHandler);

            state.TypeComboDropDownOpenedHandler = (sender, e) =>
            {
                if (state.UpdatingTypeCombo)
                    return;
                InitTypeSearchBox(state);
                ResetTypeSearch(state);
            };

            state.TypeComboDropDownClosedHandler = (sender, e) =>
            {
                if (state.UpdatingTypeCombo)
                    return;
                state.UpdatingTypeCombo = true;
                try
                {
                    object selected = combo.SelectedItem;
                    combo.ItemsSource = BuildTypeList(state, "");
                    if (selected != null)
                        combo.SelectedItem = selected;
                }
                finally
                {
                    state.UpdatingTypeCombo = false;
                }
            };

            state.TypeComboSelectionChangedHandler = (sender, e) =>
            {
                if (state.UpdatingTypeCombo)
                    return;

                string selected = combo.SelectedItem as string;
                if (selected == null)
                    return;

                state.TypeFilter = selected == AllTypesItem ? null : selected;
                state.TypeFilterFromText = false;
                StripPlainTypeTokens(ctrl, state);
                UpdateTreeViewAsync(ctrl, state);
            };

            combo.DropDownOpened += state.TypeComboDropDownOpenedHandler;
            combo.DropDownClosed += state.TypeComboDropDownClosedHandler;
            combo.SelectionChanged += state.TypeComboSelectionChangedHandler;
        }

        private static void InitTypeSearchBox(FlurryExplorerState state)
        {
            ComboBox combo = state.TypeComboBox;
            TextBox searchBox = combo?.Template?.FindName(TypeSearchPartName, combo) as TextBox;
            if (searchBox == null || ReferenceEquals(state.TypeSearchBox, searchBox))
                return;

            if (state.TypeSearchBox != null)
            {
                Unsubscribe(state.TypeSearchBox, TextBoxBase.TextChangedEvent, state.TypeSearchTextChangedHandler);
                Unsubscribe(state.TypeSearchBox, UIElement.PreviewKeyDownEvent, state.TypeSearchKeyDownHandler);
            }

            state.TypeSearchBox = searchBox;
            state.TypeSearchTextChangedHandler = (sender, e) => SearchTypeList(state);
            state.TypeSearchKeyDownHandler = (sender, e) => HandleTypeSearchKeyDown(combo, searchBox, e);

            searchBox.TextChanged += state.TypeSearchTextChangedHandler;
            searchBox.PreviewKeyDown += state.TypeSearchKeyDownHandler;
        }

        private static void ResetTypeSearch(FlurryExplorerState state)
        {
            ComboBox combo = state.TypeComboBox;
            TextBox searchBox = state.TypeSearchBox;
            if (combo == null || searchBox == null)
                return;

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

        private static void SearchTypeList(FlurryExplorerState state)
        {
            ComboBox combo = state.TypeComboBox;
            TextBox searchBox = state.TypeSearchBox;
            if (combo == null || searchBox == null || state.UpdatingTypeCombo)
                return;

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

        private static void HandleTypeSearchKeyDown(ComboBox combo, TextBox searchBox, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down:
                case Key.Up:
                    MoveTypeComboSelection(combo, searchBox, e.Key == Key.Down ? 1 : -1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (combo.SelectedItem != null)
                        combo.IsDropDownOpen = false;
                    e.Handled = true;
                    break;
                case Key.Escape:
                    combo.IsDropDownOpen = false;
                    e.Handled = true;
                    break;
            }
        }

        private static void MoveTypeComboSelection(ComboBox combo, TextBox searchBox, int delta)
        {
            int count = combo.Items.Count;
            if (count == 0)
                return;

            bool hasSearchText = !string.IsNullOrEmpty(searchBox.Text) && !searchBox.Text.Equals(AllTypesItem, StringComparison.OrdinalIgnoreCase);
            int index = combo.SelectedIndex;
            if (index < 0)
                index = delta > 0 && hasSearchText && count > 1 ? 1 : 0;
            else
                index += delta;

            index = Math.Max(0, Math.Min(index, count - 1));
            combo.SelectedIndex = index;

            ComboBoxItem container = combo.ItemContainerGenerator.ContainerFromIndex(index) as ComboBoxItem;
            container?.BringIntoView();
        }

        /// <summary>
        /// Removes a previously added routed-event handler. WPF's routed-event remove
        /// accessors delegate to <see cref="UIElement.RemoveHandler"/>, which throws on a
        /// null handler - unlike field-like CLR events whose -= is null-tolerant. First-time
        /// wiring passes null (nothing to remove), so this must guard.
        /// </summary>
        private static void Unsubscribe(UIElement element, RoutedEvent routedEvent, Delegate handler)
        {
            if (handler != null)
                element.RemoveHandler(routedEvent, handler);
        }

        internal static void WireFilterTextBox(TextBox filterBox, FlurryExplorerState state)
        {
            Unsubscribe(filterBox, UIElement.KeyDownEvent, state.FilterKeyDownHandler);

            state.FilterKeyDownHandler = (sender, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.None)
                    return;

                if (e.Key == Key.Up)
                {
                    filterBox.CaretIndex = 0;
                    e.Handled = true;
                }
                else if (e.Key == Key.Down)
                {
                    filterBox.CaretIndex = filterBox.Text.Length;
                    e.Handled = true;
                }
            };

            filterBox.AddHandler(UIElement.KeyDownEvent, state.FilterKeyDownHandler, true);
        }

        #endregion

        #region Tree building and filtering

        private static Func<AssetEntry, bool> CreateAssetFilter(FrostyDataExplorer ctrl, FlurryExplorerState state)
        {
            // Capture UI state here (UI thread); the returned predicate is evaluated on background threads.
            bool showOnlyModified = ctrl.ShowOnlyModified;
            bool showOnlyUnmodified = FlurryExplorerBehaviors.GetShowOnlyUnModified(ctrl);
            bool showOnlyUnsavedModified = FlurryExplorerBehaviors.GetShowOnlyUnsavedModified(ctrl);
            bool showOnlyAdded = FlurryExplorerBehaviors.GetShowOnlyAdded(ctrl);
            HashSet<string> typeFilterSet = string.IsNullOrEmpty(state.TypeFilter) ? null : GetTypeDescendants(state, state.TypeFilter);
            List<FlurryFilterData> filterSnapshot = state.Filter.ToList();

            return entry =>
            {
                if (entry == null)
                    return false;

                if (showOnlyModified && !entry.IsModified)
                    return false;

                if (showOnlyUnmodified && entry.IsModified)
                    return false;

                if (showOnlyUnsavedModified && (!entry.IsModified || !entry.DisplayName.EndsWith("*")))
                    return false;

                if (showOnlyAdded && !entry.IsAdded)
                    return false;

                if (typeFilterSet != null && !typeFilterSet.Contains(entry.Type ?? ""))
                    return false;

                return FlurryFilterEngine.Matches(filterSnapshot, entry.Name, entry, state);
            };
        }

        internal static Task CreateAssetPathMappingAsync(FrostyDataExplorer ctrl, FlurryExplorerState state)
        {
            TreeView treeView = AssetTreeViewRef(ctrl);
            IEnumerable items = ctrl.ItemsSource;
            if (treeView == null || items == null)
                return Task.CompletedTask;

            // Show a placeholder immediately so the tree does not appear frozen while building.
            var placeholder = new FlurryAssetPath("", "", null);
            placeholder.VisibleChildren.Add(new FlurryAssetPath(LoadingDisplayName, "", null));
            state.Root = placeholder;
            treeView.ItemsSource = placeholder.VisibleChildren;

            return Task.Run(async () =>
            {
                int generation = state.Generation;
                try
                {
                    lock (state.BaseLock)
                    {
                        BuildBaseTree(state, items);
                    }

                    if (state.Generation != generation)
                        return;

                    await ctrl.Dispatcher.InvokeAsync(async () =>
                    {
                        if (state.Generation != generation)
                            return;

                        state.Root.LazyLoadChildren();
                        await UpdateTreeViewAsync(ctrl, state, true, false, false);
                    }).Task.Unwrap();
                }
                catch (Exception ex)
                {
                    App.Logger.LogError(ex.ToString());
                }
            });
        }

        private static void BuildBaseTree(FlurryExplorerState state, IEnumerable items)
        {
            FlurryAssetPath oldBase = state.Root;

            var newBase = new FlurryAssetPath("", "", null);
            var rootPath = new FlurryAssetPath(RootDisplayName, "", null, true);
            newBase.Children.Add(RootKeyName, rootPath);

            var seenEntries = new HashSet<AssetEntry>();
            foreach (AssetEntry entry in items)
            {
                seenEntries.Add(entry);
                InsertEntry(state, newBase, rootPath, entry);
            }

            GraftCloneLinks(oldBase, newBase);

            state.Root = newBase;
            state.BaseEntries.Clear();
            foreach (AssetEntry entry in seenEntries)
                state.BaseEntries.Add(entry);
            state.BaseEntryCount = seenEntries.Count;
        }

        private static void InsertEntry(FlurryExplorerState state, FlurryAssetPath root, FlurryAssetPath rootPath, AssetEntry entry)
        {
            string[] parts = entry.Path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            FlurryAssetPath current = parts.Length > 0 ? root : rootPath;

            foreach (string part in parts)
            {
                if (!current.Children.TryGetValue(part, out FlurryAssetPath next))
                {
                    next = new FlurryAssetPath(part, "", current);
                    current.Children[part] = next;

                    if (current.VisibleChildren.Count == 0)
                        current.VisibleChildren.Add(new FlurryAssetPath(LoadingDisplayName, "", null));
                }
                else if (part.Any(char.IsUpper))
                {
                    next.UpdatePathName(part);
                }

                current = next;
            }

            current.Entries.Add(entry);
        }

        /// <summary>
        /// Carries the previous base tree's clone links onto a freshly built base tree so
        /// expanded/selected visual state survives full rebuilds.
        /// </summary>
        private static void GraftCloneLinks(FlurryAssetPath oldNode, FlurryAssetPath newNode)
        {
            if (oldNode == null)
                return;

            newNode.Clone = oldNode.Clone;
            foreach (KeyValuePair<string, FlurryAssetPath> pair in newNode.Children)
            {
                if (oldNode.Children.TryGetValue(pair.Key, out FlurryAssetPath oldChild))
                    GraftCloneLinks(oldChild, pair.Value);
            }
        }

        private static void SyncBaseTree(FlurryExplorerState state, IEnumerable items, bool forceFullRebuild)
        {
            if (state.Root == null || forceFullRebuild)
            {
                BuildBaseTree(state, items);
                return;
            }

            List<AssetEntry> newEntries = null;
            int count = 0;
            foreach (AssetEntry entry in items)
            {
                count++;
                if (!state.BaseEntries.Contains(entry))
                {
                    if (newEntries == null)
                        newEntries = new List<AssetEntry>();
                    newEntries.Add(entry);
                }
            }

            if (count < state.BaseEntryCount)
            {
                BuildBaseTree(state, items);
                return;
            }

            if (newEntries == null)
                return;

            if (!state.Root.Children.TryGetValue(RootKeyName, out FlurryAssetPath rootPath))
            {
                rootPath = new FlurryAssetPath(RootDisplayName, "", null, true);
                state.Root.Children.Add(RootKeyName, rootPath);
            }

            foreach (AssetEntry entry in newEntries)
            {
                InsertEntry(state, state.Root, rootPath, entry);
                state.BaseEntries.Add(entry);
            }
            state.BaseEntryCount = count;
        }

        internal static Task UpdateTreeViewAsync(FrostyDataExplorer ctrl, FlurryExplorerState state, bool keepVisualState = true, bool showProgressBar = true, bool rebuildBase = true, bool forceFullRebuild = false)
        {
            TreeView treeView = AssetTreeViewRef(ctrl);
            if (treeView == null || ctrl.ItemsSource == null)
                return Task.CompletedTask;

            if (!rebuildBase && (state.Root == null || !state.Root.IsLazyLoaded))
                return Task.CompletedTask;

            if (!keepVisualState && state.SelectedNode != null)
                state.SelectedNode.IsSelected = false;

            IEnumerable items = ctrl.ItemsSource;
            Func<AssetEntry, bool> shouldDisplayAsset = CreateAssetFilter(ctrl, state);
            int generation = ++state.Generation;

            return Task.Run(async () =>
            {
                try
                {
                    SetProgressVisible(state, ctrl, generation, true);

                    FlurryAssetPath newRoot;
                    lock (state.BaseLock)
                    {
                        if (rebuildBase)
                            SyncBaseTree(state, items, forceFullRebuild);
                        newRoot = state.Root?.Search(shouldDisplayAsset, keepVisualState);
                    }

                    newRoot?.LazyLoadChildren();

                    if (generation != state.Generation)
                        return;

                    await ctrl.Dispatcher.InvokeAsync(() => ApplyTreeViewResult(ctrl, state, treeView, newRoot, generation));
                }
                catch (Exception ex)
                {
                    App.Logger.LogError(ex.ToString());
                }
                finally
                {
                    SetProgressVisible(state, ctrl, generation, false);
                }
            });
        }

        private static void ApplyTreeViewResult(FrostyDataExplorer ctrl, FlurryExplorerState state, TreeView treeView, FlurryAssetPath newRoot, int generation)
        {
            if (generation != state.Generation)
                return;

            string selectedFullPath = state.SelectedNode != null && !state.SelectedNode.IsRoot ? state.SelectedNode.FullPath : null;

            state.SuppressSelection = true;
            try
            {
                treeView.SelectedValuePath = string.Empty;
                treeView.ItemsSource = newRoot?.VisibleChildren;
                treeView.Items.Refresh();

                FlurryAssetPath selectedNode = FindNodeByPath(newRoot, selectedFullPath);
                if (selectedNode != null)
                {
                    state.SelectedNode = selectedNode;
                    selectedNode.IsSelected = true;
                    ctrl.SelectedPath = selectedNode.IsRoot ? "" : selectedNode.FullPath;
                }
                else
                {
                    state.SelectedNode = null;
                    ctrl.SelectedPath = "";
                }

                UpdateListViewAsync(ctrl, state, selectedNode, true);
            }
            finally
            {
                state.SuppressSelection = false;
            }
        }

        private static FlurryAssetPath FindNodeByPath(FlurryAssetPath root, string fullPath)
        {
            if (root == null || string.IsNullOrEmpty(fullPath))
                return null;

            if (fullPath == RootDisplayName)
                return root.Children.TryGetValue(RootKeyName, out FlurryAssetPath rootPath) ? rootPath : null;

            FlurryAssetPath current = root;
            foreach (string segment in fullPath.Split('/'))
            {
                if (!current.Children.TryGetValue(segment, out current))
                    return null;
            }
            return current;
        }

        internal static Task UpdateListViewAsync(FrostyDataExplorer ctrl, FlurryExplorerState state, FlurryAssetPath path = null, bool suppressSelectionEvents = false)
        {
            ListView assetListView = AssetListViewRef(ctrl);
            if (assetListView == null)
                return Task.CompletedTask;

            if (path == null)
            {
                assetListView.ItemsSource = null;
                return Task.CompletedTask;
            }

            return ctrl.Dispatcher.InvokeAsync(() =>
            {
                if (suppressSelectionEvents)
                    state.SuppressSelection = true;
                try
                {
                    assetListView.ItemsSource = path.Entries;

                    if (ctrl.SelectedAsset != null)
                    {
                        assetListView.SelectedItem = ctrl.SelectedAsset;
                    }
                    else if (ctrl.SelectedAssets != null)
                    {
                        foreach (AssetEntry entry in ctrl.SelectedAssets)
                            assetListView.SelectedItems.Add(entry);
                    }
                }
                finally
                {
                    if (suppressSelectionEvents)
                        state.SuppressSelection = false;
                }
            }, DispatcherPriority.Background).Task;
        }

        internal static void ConfigureVirtualization(FrostyDataExplorer ctrl)
        {
            TreeView assetTreeView = AssetTreeViewRef(ctrl);
            if (assetTreeView != null)
            {
                assetTreeView.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
                assetTreeView.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
                assetTreeView.SetValue(ScrollViewer.CanContentScrollProperty, true);
            }

            ListView assetListView = AssetListViewRef(ctrl);
            if (assetListView != null)
            {
                assetListView.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
                assetListView.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
                assetListView.SetValue(ScrollViewer.CanContentScrollProperty, true);
            }
        }

        #endregion

        #region Selection

        internal static async Task SelectAssetAsync(FrostyDataExplorer ctrl, FlurryExplorerState state, AssetEntry entry, bool clearFilters = true)
        {
            state.SelectGeneration++;
            int selectGen = state.SelectGeneration;

            await state.ReadyTcs.Task;
            if (state.SelectGeneration != selectGen)
                return;

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
                    SetProgressVisible(state, ctrl, true);

                    bool anyShowFlags =
                        ctrl.ShowOnlyModified ||
                        FlurryExplorerBehaviors.GetShowOnlyUnModified(ctrl) ||
                        FlurryExplorerBehaviors.GetShowOnlyUnsavedModified(ctrl) ||
                        FlurryExplorerBehaviors.GetShowOnlyAdded(ctrl);

                    if (clearFilters && (anyShowFlags || PrevFilterTextRef(ctrl) != ""))
                    {
                        await ClearFiltersForSelectionAsync(ctrl, state, selectGen);
                        if (state.SelectGeneration != selectGen)
                            return;
                    }

                    List<FlurryAssetPath> traversalStack = ResolveTraversalStack(state, entry);
                    TreeViewItem container = await RevealPathAsync(ctrl, state, treeView, traversalStack, selectGen);
                    if (state.SelectGeneration != selectGen)
                        return;

                    if (container == null && traversalStack.Count > 1)
                        container = WalkRealizedContainers(state, treeView, traversalStack);
                    if (state.SelectGeneration != selectGen)
                        return;

                    if (container != null)
                    {
                        container.UpdateLayout();
                        state.SuppressSelection = true;
                        try { container.IsSelected = true; }
                        finally { state.SuppressSelection = false; }
                        container.BringIntoView();
                    }

                    FlurryAssetPath current = traversalStack.Count > 0 ? traversalStack[traversalStack.Count - 1] : null;
                    if (current != null)
                        current.IsSelected = true;

                    if (state.SelectedNode == null || !string.Equals(state.SelectedNode.FullPath, current?.FullPath ?? "", StringComparison.OrdinalIgnoreCase))
                    {
                        state.SelectedNode = current;
                        ctrl.SelectedPath = current == null || current.IsRoot ? "" : current.FullPath;
                    }

                    if (ctrl.AssetListVisible)
                    {
                        await UpdateListViewAsync(ctrl, state, current, true);
                        if (state.SelectGeneration != selectGen)
                            return;
                        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        if (state.SelectGeneration != selectGen)
                            return;

                        await ForceRealizeItem(AssetListViewRef(ctrl), entry, state, selectGen);
                        if (state.SelectGeneration != selectGen)
                            return;

                        ListView assetListView = AssetListViewRef(ctrl);
                        state.SuppressSelection = true;
                        try
                        {
                            assetListView.SelectedItem = entry;
                            assetListView.ScrollIntoView(entry);
                        }
                        finally
                        {
                            state.SuppressSelection = false;
                        }
                    }

                    if (state.SelectGeneration != selectGen)
                        return;
                    ctrl.SelectedAsset = entry;
                }
                catch (Exception e)
                {
                    App.Logger.LogError(e.ToString());
                }
                finally
                {
                    state.SuppressModified = false;
                    SetProgressVisible(state, ctrl, false);
                }
            }).Task.Unwrap();
        }

        private static async Task ClearFiltersForSelectionAsync(FrostyDataExplorer ctrl, FlurryExplorerState state, int selectGen)
        {
            ctrl.SetValue(FrostyDataExplorer.ShowOnlyModifiedProperty, false);
            FlurryExplorerBehaviors.SetShowOnlyUnModified(ctrl, false);
            FlurryExplorerBehaviors.SetShowOnlyUnsavedModified(ctrl, false);
            FlurryExplorerBehaviors.SetShowOnlyAdded(ctrl, false);

            if (PrevFilterTextRef(ctrl) != "")
            {
                TextBox filterTextBox = FilterTextBoxRef(ctrl);
                if (filterTextBox != null)
                    filterTextBox.Text = "";
                PrevFilterTextRef(ctrl) = "";
                BuildFilterData(state, "", ctrl);
            }

            ClearTypeFilter(state);

            await UpdateTreeViewAsync(ctrl, state, false, false);
            if (state.SelectGeneration != selectGen)
                return;

            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        }

        private static List<FlurryAssetPath> ResolveTraversalStack(FlurryExplorerState state, AssetEntry entry)
        {
            string[] pathSegments = entry.Path.Length > 0
                ? entry.Path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                : new[] { RootKeyName };

            var stack = new List<FlurryAssetPath>();
            lock (state.BaseLock)
            {
                FlurryAssetPath cursor = state.Root;
                if (cursor != null)
                    stack.Add(cursor);

                foreach (string segment in pathSegments)
                {
                    if (cursor == null || !cursor.Children.TryGetValue(segment, out FlurryAssetPath next))
                        break;
                    cursor = next;
                    stack.Add(cursor);
                }
            }
            return stack;
        }

        private static async Task<TreeViewItem> RevealPathAsync(FrostyDataExplorer ctrl, FlurryExplorerState state, TreeView treeView, List<FlurryAssetPath> traversalStack, int selectGen)
        {
            TreeViewItem container = null;
            foreach (FlurryAssetPath node in traversalStack.Skip(1))
            {
                FlurryAssetPath target;
                lock (state.BaseLock)
                {
                    target = node.Clone ?? node;
                    target.LazyLoadChildren();
                }

                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                if (state.SelectGeneration != selectGen)
                    return null;

                if (container != null)
                {
                    container.IsExpanded = true;
                    container.UpdateLayout();
                    await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                    if (state.SelectGeneration != selectGen)
                        return null;

                    await ForceRealizeItem(container, target, state, selectGen);
                    if (state.SelectGeneration != selectGen)
                        return null;

                    container = await GetContainerFromItemAsync<TreeViewItem>(container, target, state, selectGen);
                }
                else if (node.Parent == state.Root)
                {
                    await ForceRealizeItem(treeView, target, state, selectGen);
                    if (state.SelectGeneration != selectGen)
                        return null;

                    container = await GetContainerFromItemAsync<TreeViewItem>(treeView, target, state, selectGen);
                }

                if (state.SelectGeneration != selectGen)
                    return null;
            }
            return container;
        }

        /// <summary>
        /// Fallback for fully realized trees (no virtualizing panel found): walks already
        /// generated containers instead of forcing realization.
        /// </summary>
        private static TreeViewItem WalkRealizedContainers(FlurryExplorerState state, TreeView treeView, List<FlurryAssetPath> traversalStack)
        {
            TreeViewItem container = null;
            foreach (FlurryAssetPath node in traversalStack.Skip(1))
            {
                FlurryAssetPath target;
                lock (state.BaseLock)
                {
                    target = node.Clone ?? node;
                }

                ItemsControl parent = container ?? (ItemsControl)treeView;
                TreeViewItem child = parent.ItemContainerGenerator.ContainerFromItem(target) as TreeViewItem;
                if (child == null)
                    break;

                child.IsExpanded = true;
                child.UpdateLayout();
                container = child;
            }
            return container;
        }

        private static async Task<T> GetContainerFromItemAsync<T>(ItemsControl parent, object item, FlurryExplorerState state, int selectGen)
            where T : DependencyObject
        {
            int retries = 40;
            while (parent.ItemContainerGenerator.ContainerFromItem(item) as T == null && retries-- > 0)
            {
                if (state.SelectGeneration != selectGen)
                    return null;
                await Task.Delay(50);
                parent.UpdateLayout();
            }

            if (state.SelectGeneration != selectGen)
                return null;
            return parent.ItemContainerGenerator.ContainerFromItem(item) as T;
        }

        private static async Task<bool> ForceRealizeItem(ItemsControl parent, object item, FlurryExplorerState state, int selectGen)
        {
            int index = FindIndexOfNode(parent, item);
            if (index < 0)
                return false;

            int retries = 20;
            VirtualizingStackPanel panel = FindVirtualizingStackPanel(parent);
            while (panel == null && retries-- > 0)
            {
                if (state.SelectGeneration != selectGen)
                    return false;
                await Task.Delay(50);
                panel = FindVirtualizingStackPanel(parent);
            }

            if (panel == null)
                return false;

            panel.BringIndexIntoViewPublic(index);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            return true;
        }

        private static int FindIndexOfNode(ItemsControl parent, object item)
        {
            if (parent == null || item == null)
                return -1;

            if (!(parent.ItemsSource is IList items))
                return -1;

            for (int i = 0; i < items.Count; i++)
            {
                if (ReferenceEquals(items[i], item))
                    return i;
            }

            return -1;
        }

        private static VirtualizingStackPanel FindVirtualizingStackPanel(DependencyObject parent)
        {
            if (parent == null)
                return null;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                if (child is VirtualizingStackPanel panel)
                    return panel;

                VirtualizingStackPanel result = FindVirtualizingStackPanel(child);
                if (result != null)
                    return result;
            }

            return null;
        }

        #endregion

        #region Asset types

        internal static Task PopulateAssetTypesComboBox(FrostyDataExplorer ctrl, FlurryExplorerState state)
        {
            if (state.TypeComboBox == null || ctrl.ItemsSource == null)
                return Task.CompletedTask;

            IEnumerable entries = ctrl.ItemsSource;
            return Task.Run(() =>
            {
                try
                {
                    var assetTypes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (AssetEntry asset in entries)
                    {
                        if (!string.IsNullOrEmpty(asset.Type))
                            assetTypes.Add(asset.Type);
                    }

                    // Include SDK base types so filtering by a base type matches concrete entries.
                    foreach (string typeName in new List<string>(assetTypes))
                    {
                        Type sdkType = TypeLibrary.GetType(typeName);
                        if (sdkType == null)
                            continue;

                        for (Type baseType = sdkType.BaseType; baseType != null && baseType != typeof(object); baseType = baseType.BaseType)
                            assetTypes.Add(baseType.Name);
                    }

                    List<string> types = new List<string>(assetTypes);
                    ctrl.Dispatcher.InvokeAsync(() =>
                    {
                        state.AllTypes = types;
                        state.TypeDescendants.Clear();
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
                }
                catch (Exception ex)
                {
                    App.Logger.LogError(ex.ToString());
                }
            });
        }

        #endregion
    }
}