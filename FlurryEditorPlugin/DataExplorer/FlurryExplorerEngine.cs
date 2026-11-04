using Frosty.Core;
using Frosty.Core.Controls;
using Frosty.Hash;
using FrostySdk;
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
        public bool TypeFilterFromText;
        public List<string> AllTypes = new List<string>();
        public readonly Dictionary<string, HashSet<string>> TypeDescendants = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        public bool UpdatingTypeCombo;
        public readonly HashSet<AssetEntry> BaseEntries = new HashSet<AssetEntry>();
        public int BaseEntryCount;
        public readonly object BaseLock = new object();
        public int SelectGeneration;
        public bool SuppressSelection;
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

        internal static HashSet<string> GetTypeDescendants(FlurryExplorerState state, string baseType)
        {
            HashSet<string> set;
            if (state.TypeDescendants.TryGetValue(baseType, out set))
                return set;

            string canonical = baseType;
            foreach (string typeName in state.AllTypes)
            {
                if (typeName.Equals(baseType, StringComparison.OrdinalIgnoreCase))
                {
                    canonical = typeName;
                    break;
                }
            }

            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string typeName in state.AllTypes)
            {
                if (typeName.Equals(canonical, StringComparison.OrdinalIgnoreCase) || TypeLibrary.IsSubClassOf(typeName, canonical))
                    set.Add(typeName);
            }
            state.TypeDescendants[baseType] = set;
            return set;
        }

        private static bool FilterText(FlurryExplorerState state, string inText, AssetEntry inEntry)
        {
            string type = inEntry.Type ?? "";

            if (state.Filter.Count == 0)
                return true;

            bool retCode = false;
            for (int i = 0; i < state.Filter.Count; i++)
            {
                FlurryFilterData filterData = state.Filter[i];
                bool nextRetCode = EvaluateFilterToken(state, filterData, inText, inEntry, type);

                if (filterData.Not)
                    nextRetCode = !nextRetCode;

                if (i == 0)
                    retCode = nextRetCode;
                else if (filterData.Combine == FlurryFilterCombineType.And)
                    retCode &= nextRetCode;
                else
                    retCode |= nextRetCode;
            }

            return retCode;
        }

        private static bool EvaluateFilterToken(FlurryExplorerState state, FlurryFilterData filterData, string inText, AssetEntry inEntry, string type)
        {
            bool nextRetCode = false;
            switch (filterData.Command)
            {
                case FlurryFilterCommandType.Contains: nextRetCode = inText.IndexOf(filterData.Text, StringComparison.OrdinalIgnoreCase) >= 0; break;
                case FlurryFilterCommandType.StartsWith: nextRetCode = inText.StartsWith(filterData.Text, StringComparison.OrdinalIgnoreCase); break;
                case FlurryFilterCommandType.EndsWith: nextRetCode = inText.EndsWith(filterData.Text, StringComparison.OrdinalIgnoreCase); break;
                case FlurryFilterCommandType.RegEx: nextRetCode = Regex.IsMatch(inText, filterData.Text); break;
                case FlurryFilterCommandType.Type: nextRetCode = GetTypeDescendants(state, filterData.Text).Contains(type); break;
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
            return nextRetCode;
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
                        string remaining = "";

                        if (cmdArr.Length > 1)
                        {
                            string cmdString = cmdArr[0];
                            remaining = cmdArr[1];
                            foreach (string value in Enum.GetNames(typeof(FlurryFilterCommandType)))
                            {
                                if (cmdString.StartsWith(value.ToLower()))
                                {
                                    command = (FlurryFilterCommandType)Enum.Parse(typeof(FlurryFilterCommandType), value);
                                    break;
                                }
                            }
                        }
                        else
                        {
                            bool isCommandName = false;
                            foreach (string value in Enum.GetNames(typeof(FlurryFilterCommandType)))
                            {
                                if (subStr[i].Equals(value, StringComparison.OrdinalIgnoreCase))
                                {
                                    isCommandName = true;
                                    break;
                                }
                            }

                            if (isCommandName && i + 1 < subStr.Length)
                            {
                                foreach (string value in Enum.GetNames(typeof(FlurryFilterCommandType)))
                                {
                                    if (subStr[i].Equals(value, StringComparison.OrdinalIgnoreCase))
                                    {
                                        command = (FlurryFilterCommandType)Enum.Parse(typeof(FlurryFilterCommandType), value);
                                        break;
                                    }
                                }
                                remaining = subStr[++i];
                            }
                            else
                            {
                                command = FlurryFilterCommandType.Contains;
                                remaining = subStr[i];
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
            string firstType = null;
            int typeCount = 0;
            List<FlurryFilterData> kept = new List<FlurryFilterData>();

            foreach (FlurryFilterData f in state.Filter)
            {
                if (f.Command == FlurryFilterCommandType.Type && !f.Not)
                {
                    typeCount++;
                    if (typeCount == 1)
                        firstType = f.Text;
                }
                else
                {
                    kept.Add(f);
                }
            }

            if (typeCount == 1)
            {
                state.Filter.Clear();
                state.Filter.AddRange(kept);
                state.TypeFilter = firstType;
                state.TypeFilterFromText = true;
                SetTypeComboDisplay(state, firstType);
                return;
            }

            if (typeCount > 1)
            {
                state.TypeFilter = null;
                state.TypeFilterFromText = false;
                SetTypeComboDisplay(state, "All");
                return;
            }

            if (state.TypeFilterFromText)
            {
                state.TypeFilter = null;
                state.TypeFilterFromText = false;
                SetTypeComboDisplay(state, "All");
            }
        }

        internal static void StripPlainTypeTokens(FrostyDataExplorer ctrl, FlurryExplorerState state)
        {
            TextBox filterTextBox = FilterTextBoxRef(ctrl);
            if (filterTextBox == null) return;

            string filterText = filterTextBox.Text ?? "";
            if (filterText == "") return;

            string[] subStr = filterText.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (subStr.Length == 1 && !subStr[0].Contains(":"))
                return;

            var entries = new List<KeyValuePair<FlurryFilterData, List<string>>>();
            try
            {
                for (int i = 0; i < subStr.Length; i++)
                {
                    FlurryFilterCommandType command = FlurryFilterCommandType.Contains;
                    FlurryFilterCombineType combine = FlurryFilterCombineType.Or;
                    bool not = false;
                    List<string> consumed = new List<string>();

                    if (entries.Count != 0)
                    {
                        consumed.Add(subStr[i]);
                        combine = (subStr[i++] == "AND") ? FlurryFilterCombineType.And : FlurryFilterCombineType.Or;
                    }

                    if (subStr[i] == "NOT")
                    {
                        not = true;
                        consumed.Add(subStr[i]);
                        i++;
                    }

                    consumed.Add(subStr[i]);
                    string[] cmdArr = subStr[i].Split(new char[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
                    string remaining = "";

                    if (cmdArr.Length > 1)
                    {
                        string cmdString = cmdArr[0];
                        remaining = cmdArr[1];
                        foreach (string value in Enum.GetNames(typeof(FlurryFilterCommandType)))
                        {
                            if (cmdString.StartsWith(value.ToLower()))
                            {
                                command = (FlurryFilterCommandType)Enum.Parse(typeof(FlurryFilterCommandType), value);
                                break;
                            }
                        }
                    }
                    else
                    {
                        bool isCommandName = false;
                        foreach (string value in Enum.GetNames(typeof(FlurryFilterCommandType)))
                        {
                            if (subStr[i].Equals(value, StringComparison.OrdinalIgnoreCase))
                            {
                                isCommandName = true;
                                break;
                            }
                        }

                        if (isCommandName && i + 1 < subStr.Length)
                        {
                            foreach (string value in Enum.GetNames(typeof(FlurryFilterCommandType)))
                            {
                                if (subStr[i].Equals(value, StringComparison.OrdinalIgnoreCase))
                                {
                                    command = (FlurryFilterCommandType)Enum.Parse(typeof(FlurryFilterCommandType), value);
                                    break;
                                }
                            }
                            i++;
                            remaining = subStr[i];
                            consumed.Add(subStr[i]);
                        }
                        else
                        {
                            command = FlurryFilterCommandType.Contains;
                            remaining = subStr[i];
                        }
                    }

                    entries.Add(new KeyValuePair<FlurryFilterData, List<string>>(
                        new FlurryFilterData { Text = remaining, Command = command, Combine = combine, Not = not },
                        consumed));
                }
            }
            catch (Exception)
            {
                return;
            }

            bool hasPlainType = false;
            foreach (var entry in entries)
            {
                if (entry.Key.Command == FlurryFilterCommandType.Type && !entry.Key.Not)
                {
                    hasPlainType = true;
                    break;
                }
            }
            if (!hasPlainType)
                return;

            var keptTokens = new List<string>();
            foreach (var entry in entries)
            {
                if (!(entry.Key.Command == FlurryFilterCommandType.Type && !entry.Key.Not))
                    keptTokens.AddRange(entry.Value);
            }

            while (keptTokens.Count > 0 && keptTokens[0].Equals("AND", StringComparison.OrdinalIgnoreCase))
                keptTokens.RemoveAt(0);

            string newText = string.Join(" ", keptTokens);
            if (newText == filterText)
                return;

            filterTextBox.Text = newText;
            BuildFilterData(state, newText, ctrl);
        }

        private static void SetTypeComboDisplay(FlurryExplorerState state, string target)
        {
            ComboBox combo = state.TypeComboBox;
            if (combo == null)
                return;

            state.UpdatingTypeCombo = true;
            try
            {
                if (combo.SelectedItem as string != target)
                {
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
                }
            }
            finally
            {
                state.UpdatingTypeCombo = false;
            }
        }

        internal static void ClearTypeFilter(FrostyDataExplorer ctrl, FlurryExplorerState state)
        {
            state.TypeFilter = null;
            state.TypeFilterFromText = false;

            ComboBox combo = state.TypeComboBox;
            if (combo == null)
                return;

            state.UpdatingTypeCombo = true;
            try
            {
                if (combo.SelectedItem as string != "All")
                {
                    bool present = false;
                    foreach (object item in combo.Items)
                    {
                        if (string.Equals(item as string, "All", StringComparison.OrdinalIgnoreCase))
                        {
                            present = true;
                            break;
                        }
                    }
                    if (!present)
                        combo.ItemsSource = BuildTypeList(state, "");

                    combo.SelectedItem = "All";
                }
            }
            finally
            {
                state.UpdatingTypeCombo = false;
            }
        }

        private static Func<AssetEntry, bool> CreateAssetFilter(FrostyDataExplorer ctrl, FlurryExplorerState state)
        {
            bool showOnlyModified = ctrl.ShowOnlyModified;
            bool showOnlyUnmodified = FlurryExplorerBehaviors.GetShowOnlyUnModified(ctrl);
            bool showOnlyUnsavedModified = FlurryExplorerBehaviors.GetShowOnlyUnsavedModified(ctrl);
            bool showOnlyAdded = FlurryExplorerBehaviors.GetShowOnlyAdded(ctrl);
            HashSet<string> typeFilterSet = string.IsNullOrEmpty(state.TypeFilter) ? null : GetTypeDescendants(state, state.TypeFilter);

            return (entry) =>
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
                    int generation = state.Generation;

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

            FlurryAssetPath newBase = new FlurryAssetPath("", "", null);
            FlurryAssetPath rootPath = new FlurryAssetPath("![root]", "", null, true);
            newBase.Children.Add("/", rootPath);

            var seenEntries = new HashSet<AssetEntry>();
            foreach (AssetEntry entry in items)
            {
                if (state.EntryFilterFunction != null && !state.EntryFilterFunction(entry)) { continue; }
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
            var parts = entry.Path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            FlurryAssetPath current = parts.Length > 0 ? root : rootPath;

            foreach (var part in parts)
            {
                FlurryAssetPath next;
                if (!current.Children.TryGetValue(part, out next))
                {
                    next = new FlurryAssetPath(part, "", current);
                    current.Children[part] = next;

                    if (current.VisibleChildren.Count == 0)
                        current.VisibleChildren.Add(new FlurryAssetPath("[Loading...]", "", null));
                }
                else
                {
                    if (part.ToCharArray().Any(char.IsUpper))
                        next.UpdatePathName(part);
                }

                current = next;
            }

            current.Entries.Add(entry);
        }

        private static void GraftCloneLinks(FlurryAssetPath oldNode, FlurryAssetPath newNode)
        {
            if (oldNode == null) return;
            newNode.Clone = oldNode.Clone;
            foreach (var kvp in newNode.Children)
            {
                FlurryAssetPath oldChild;
                if (oldNode.Children.TryGetValue(kvp.Key, out oldChild))
                    GraftCloneLinks(oldChild, kvp.Value);
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
                if (state.EntryFilterFunction != null && !state.EntryFilterFunction(entry)) { continue; }
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

            if (newEntries != null)
            {
                FlurryAssetPath rootPath;
                if (!state.Root.Children.TryGetValue("/", out rootPath))
                {
                    rootPath = new FlurryAssetPath("![root]", "", null, true);
                    state.Root.Children.Add("/", rootPath);
                }

                foreach (AssetEntry entry in newEntries)
                {
                    InsertEntry(state, state.Root, rootPath, entry);
                    state.BaseEntries.Add(entry);
                }
                state.BaseEntryCount = count;
            }
        }

        internal static Task UpdateTreeViewAsync(FrostyDataExplorer ctrl, FlurryExplorerState state, bool keepVisualState = true, bool showProgressBar = true, bool rebuildBase = true, bool forceFullRebuild = false)
        {
            TreeView treeView = AssetTreeViewRef(ctrl);
            if (treeView == null)
                return Task.CompletedTask;

            if (!rebuildBase && (state.Root == null || state.Root.IsLazyLoaded == false))
                return Task.CompletedTask;

            if (!keepVisualState && state.SelectedNode != null)
                state.SelectedNode.IsSelected = false;

            if (ctrl.ItemsSource == null)
                return Task.CompletedTask;

            IEnumerable items = ctrl.ItemsSource;
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

                    FlurryAssetPath newRoot = null;

                    if (rebuildBase)
                    {
                        lock (state.BaseLock)
                        {
                            SyncBaseTree(state, items, forceFullRebuild);
                            newRoot = state.Root.Search(ShouldDisplayAsset, keepVisualState);
                        }
                    }
                    else
                    {
                        lock (state.BaseLock)
                        {
                            newRoot = state.Root.Search(ShouldDisplayAsset, keepVisualState);
                        }
                    }

                    newRoot?.LazyLoadChildren();

                    if (generation != state.Generation)
                        return;

                    await ctrl.Dispatcher.InvokeAsync(() =>
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
                                UpdateListViewAsync(ctrl, state, selectedNode, true);
                            }
                            else
                            {
                                state.SelectedNode = null;
                                ctrl.SelectedPath = "";
                                UpdateListViewAsync(ctrl, state, null, true);
                            }
                        }
                        finally
                        {
                            state.SuppressSelection = false;
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

        internal static Task UpdateListViewAsync(FrostyDataExplorer ctrl, FlurryExplorerState state, FlurryAssetPath path = null, bool suppressSelectionEvents = false)
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
                        foreach (var item in ctrl.SelectedAssets)
                        {
                            assetListView.SelectedItems.Add(item);
                        }
                    }
                }
                finally
                {
                    if (suppressSelectionEvents)
                        state.SuppressSelection = false;
                }
            }, DispatcherPriority.Background).Task;
        }

        internal static async Task SelectAssetAsync(FrostyDataExplorer ctrl, FlurryExplorerState state, AssetEntry entry, bool clearFilters = true)
        {
            state.SelectGeneration++;
            int selectGen = state.SelectGeneration;

            await state.ReadyTcs.Task;
            if (state.SelectGeneration != selectGen) return;

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

                    bool anyShowFlags =
                        ctrl.ShowOnlyModified ||
                        FlurryExplorerBehaviors.GetShowOnlyUnModified(ctrl) ||
                        FlurryExplorerBehaviors.GetShowOnlyUnsavedModified(ctrl) ||
                        FlurryExplorerBehaviors.GetShowOnlyAdded(ctrl);

                    if (clearFilters && (anyShowFlags || state.PrevFilterText != ""))
                    {
                        ctrl.SetValue(FrostyDataExplorer.ShowOnlyModifiedProperty, false);
                        FlurryExplorerBehaviors.SetShowOnlyUnModified(ctrl, false);
                        FlurryExplorerBehaviors.SetShowOnlyUnsavedModified(ctrl, false);
                        FlurryExplorerBehaviors.SetShowOnlyAdded(ctrl, false);

                        TextBox filterTextBox = FilterTextBoxRef(ctrl);
                        if (state.PrevFilterText != "")
                        {
                            filterTextBox.Text = "";
                            state.PrevFilterText = "";
                            BuildFilterData(state, "", ctrl);
                        }

                        ClearTypeFilter(ctrl, state);

                        await UpdateTreeViewAsync(ctrl, state, false, false);
                        if (state.SelectGeneration != selectGen) return;
                        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        if (state.SelectGeneration != selectGen) return;
                    }

                    string[] pathSegments = entry.Path.Length > 0
                        ? entry.Path.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                        : new string[1] { "/" };

                    List<FlurryAssetPath> traversalStack = new List<FlurryAssetPath>();
                    lock (state.BaseLock)
                    {
                        FlurryAssetPath cursor = state.Root;
                        if (cursor != null)
                            traversalStack.Add(cursor);

                        foreach (string segment in pathSegments)
                        {
                            FlurryAssetPath next;
                            if (cursor != null && cursor.Children.TryGetValue(segment, out next))
                            {
                                cursor = next;
                                traversalStack.Add(cursor);
                            }
                            else
                            {
                                break;
                            }
                        }
                    }

                    TreeViewItem tvi = null;
                    foreach (var node in traversalStack.Skip(1))
                    {
                        FlurryAssetPath target;
                        lock (state.BaseLock)
                        {
                            target = node.Clone != null ? node.Clone : node;
                            target.LazyLoadChildren();
                        }

                        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        if (state.SelectGeneration != selectGen) return;

                        if (tvi != null)
                        {
                            tvi.IsExpanded = true;
                            tvi.UpdateLayout();
                            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ContextIdle);
                            if (state.SelectGeneration != selectGen) return;

                            await ForceRealizeItem(tvi, target, state, selectGen);
                            if (state.SelectGeneration != selectGen) return;
                            tvi = await GetContainerFromItemAsync<TreeViewItem>(tvi, target, state, selectGen);
                            if (state.SelectGeneration != selectGen) return;
                        }
                        else if (node.Parent == state.Root)
                        {
                            await ForceRealizeItem(treeView, target, state, selectGen);
                            if (state.SelectGeneration != selectGen) return;
                            tvi = await GetContainerFromItemAsync<TreeViewItem>(treeView, target, state, selectGen);
                            if (state.SelectGeneration != selectGen) return;
                        }
                    }

                    if (tvi == null && traversalStack.Count > 1 && state.SelectGeneration == selectGen)
                    {
                        foreach (var node in traversalStack.Skip(1))
                        {
                            FlurryAssetPath target;
                            lock (state.BaseLock)
                            {
                                target = node.Clone != null ? node.Clone : node;
                            }

                            TreeViewItem container = tvi == null
                                ? treeView.ItemContainerGenerator.ContainerFromItem(target) as TreeViewItem
                                : tvi.ItemContainerGenerator.ContainerFromItem(target) as TreeViewItem;
                            if (container == null)
                                break;

                            container.IsExpanded = true;
                            container.UpdateLayout();
                            tvi = container;
                        }

                        if (tvi != null && state.SelectGeneration == selectGen)
                        {
                            tvi.UpdateLayout();
                            state.SuppressSelection = true;
                            try { tvi.IsSelected = true; }
                            finally { state.SuppressSelection = false; }
                            tvi.BringIntoView();
                        }
                    }

                    if (tvi != null && state.SelectGeneration == selectGen)
                    {
                        tvi.UpdateLayout();
                        state.SuppressSelection = true;
                        try { tvi.IsSelected = true; }
                        finally { state.SuppressSelection = false; }
                        tvi.BringIntoView();
                    }

                    if (state.SelectGeneration != selectGen) return;

                    FlurryAssetPath current = traversalStack.Count > 0 ? traversalStack[traversalStack.Count - 1] : null;

                    if (current != null)
                        current.IsSelected = true;

                    if (state.SelectedNode == null || !string.Equals(state.SelectedNode.FullPath, current == null ? "" : current.FullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        state.SelectedNode = current;
                        ctrl.SelectedPath = current == null || current.IsRoot ? "" : current.FullPath;
                    }

                    if (ctrl.AssetListVisible)
                    {
                        await UpdateListViewAsync(ctrl, state, current, true);
                        if (state.SelectGeneration != selectGen) return;
                        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        if (state.SelectGeneration != selectGen) return;
                        await ForceRealizeItem(AssetListViewRef(ctrl), entry, state, selectGen);
                        if (state.SelectGeneration != selectGen) return;

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

                    if (state.SelectGeneration != selectGen) return;
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

        private static async Task<T> GetContainerFromItemAsync<T>(ItemsControl parent, object item, FlurryExplorerState state, int selectGen)
            where T : DependencyObject
        {
            int retries = 40;

            while (parent.ItemContainerGenerator.ContainerFromItem(item) as T == null && retries-- > 0)
            {
                if (state.SelectGeneration != selectGen) return null;
                await Task.Delay(50);
                parent.UpdateLayout();
            }

            if (state.SelectGeneration != selectGen) return null;
            return parent.ItemContainerGenerator.ContainerFromItem(item) as T;
        }

        private static async Task<bool> ForceRealizeItem(ItemsControl parent, object item, FlurryExplorerState state, int selectGen)
        {
            int index = FindIndexOfNode(parent, item);
            if (index < 0) return false;

            int retries = 20;
            var panel = FindVirtualizingStackPanel(parent);
            while (panel == null && retries-- > 0)
            {
                if (state.SelectGeneration != selectGen) return false;
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
                try
                {
                    var assetTypes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (AssetEntry asset in entries)
                    {
                        if (!string.IsNullOrEmpty(asset.Type))
                            assetTypes.Add(asset.Type);
                    }

                    List<string> concreteTypes = new List<string>(assetTypes);
                    foreach (string typeName in concreteTypes)
                    {
                        Type sdkType = TypeLibrary.GetType(typeName);
                        if (sdkType == null) continue;
                        Type baseType = sdkType.BaseType;
                        while (baseType != null && baseType != typeof(object))
                        {
                            assetTypes.Add(baseType.Name);
                            baseType = baseType.BaseType;
                        }
                    }

                    List<string> list = new List<string>(assetTypes);

                    ctrl.Dispatcher.Invoke(() =>
                    {
                        state.AllTypes = list;
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
                    App.Logger.LogError("Flurry type dropdown population failed: " + ex.ToString());
                }
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

            combo.DropDownClosed += (sender, e) =>
            {
                if (state.UpdatingTypeCombo) return;
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

            combo.SelectionChanged += (sender, e) =>
            {
                if (state.UpdatingTypeCombo) return;

                string selected = combo.SelectedItem as string;
                if (selected == null) return;

                state.TypeFilter = selected == "All" ? null : selected;
                state.TypeFilterFromText = false;
                StripPlainTypeTokens(ctrl, state);
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



