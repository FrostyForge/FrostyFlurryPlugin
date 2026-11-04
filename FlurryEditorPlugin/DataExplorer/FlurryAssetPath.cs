using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;

namespace Flurry.Editor.DataExplorer
{
    /// <summary>
    /// A folder node in the Data Explorer hierarchy. The "base" tree holds every entry and
    /// is never bound to the UI; <see cref="Copy"/> produces filter clones which the UI
    /// binds to. <see cref="Clone"/> links a base node to its most recent clone so expanded/
    /// selected visual state can be restored across filter runs and rebuilds.
    /// </summary>
    public class FlurryAssetPath : INotifyPropertyChanged
    {
        public string DisplayName => PathName.Trim('!');
        public string PathName { get; private set; }
        public string FullPath
        {
            get
            {
                if (fullPath.Length > 0) return fullPath;
                var stack = new Stack<string>();
                FlurryAssetPath current = this;

                while (current != null)
                {
                    if (!string.IsNullOrEmpty(current.PathName))
                    {
                        stack.Push(current.PathName);
                    }
                    current = current.Parent;
                }

                fullPath = string.Join("/", stack);
                return fullPath;
            }
        }
        private string fullPath = "";
        public FlurryAssetPath Parent { get; }
        public Dictionary<string, FlurryAssetPath> Children { get; private set; } = new Dictionary<string, FlurryAssetPath>(StringComparer.OrdinalIgnoreCase);
        public ObservableCollection<FlurryAssetPath> VisibleChildren { get; private set; } = new ObservableCollection<FlurryAssetPath>();
        public bool IsLazyLoaded { get; set; } = false;
        public bool IsSelected
        {
            get => selected;
            set
            {
                selected = value;
                OnPropertyChanged();
            }
        }
        private bool selected;
        public bool IsRoot { get; }

        public bool IsExpanded
        {
            get => expanded && Children.Count != 0;
            set
            {
                expanded = value;
                if (value && !IsLazyLoaded)
                    LazyLoadChildren();
                OnPropertyChanged();
            }
        }
        private bool expanded;

        public ObservableCollection<AssetEntry> Entries { get; set; } = new ObservableCollection<AssetEntry>();

        public FlurryAssetPath Clone;

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public FlurryAssetPath(string inName, string path, FlurryAssetPath inParent, bool bInRoot = false)
        {
            PathName = inName;
            IsRoot = bInRoot;
            Parent = inParent;
        }

        public FlurryAssetPath Copy(bool keepVisualState = false)
        {
            FlurryAssetPath path = (FlurryAssetPath)MemberwiseClone();
            path.Children = new Dictionary<string, FlurryAssetPath>(StringComparer.OrdinalIgnoreCase);
            path.VisibleChildren = new ObservableCollection<FlurryAssetPath>();
            path.Entries = new ObservableCollection<AssetEntry>();
            path.IsLazyLoaded = false;

            if (keepVisualState && Clone != null)
            {
                path.IsExpanded = Clone.IsExpanded;
                path.IsSelected = Clone.IsSelected;
            }

            Clone = path;

            return path;
        }

        public void UpdatePathName(string newName)
        {
            PathName = newName;
            fullPath = "";
        }

        public FlurryAssetPath Search(Func<AssetEntry, bool> entryFilterFunction, bool keepVisualState = false)
        {
            Dictionary<string, FlurryAssetPath> filteredChildren = null;
            List<AssetEntry> filteredEntries = null;

            if (Children.Count > 0)
            {
                foreach (var kvp in Children)
                {
                    var filteredChild = kvp.Value.Search(entryFilterFunction, keepVisualState);
                    if (filteredChild != null)
                    {
                        if (filteredChildren == null)
                            filteredChildren = new Dictionary<string, FlurryAssetPath>();

                        filteredChildren[kvp.Key] = filteredChild;
                    }
                }
            }

            if (Entries != null && Entries.Count > 0)
            {
                filteredEntries = Entries.Where(entryFilterFunction).ToList();
            }

            bool hasChildNodes = filteredChildren != null && filteredChildren.Count > 0;
            bool hasEntries = filteredEntries != null && filteredEntries.Any();

            if (hasChildNodes || hasEntries)
            {
                FlurryAssetPath result = Copy(keepVisualState);

                if (hasChildNodes)
                {
                    foreach (var kvp in filteredChildren)
                    {
                        result.Children[kvp.Key] = kvp.Value;
                    }

                    if (result.IsExpanded)
                    {
                        result.LazyLoadChildren();
                    }

                    if (result.VisibleChildren.Count == 0)
                    {
                        result.VisibleChildren.Add(new FlurryAssetPath("[Loading...]", "", null));
                    }
                }

                if (hasEntries)
                    foreach (var kvp in filteredEntries)
                        result.Entries.Add(kvp);

                return result;
            }
            else
            {
                return null;
            }
        }

        public void LazyLoadChildren()
        {
            VisibleChildren.Clear();
            foreach (var child in Children.Values.OrderBy(c => c.DisplayName))
            {
                VisibleChildren.Add(child);
            }
            IsLazyLoaded = true;
        }
    }
}
