using ChunkResEditorPlugin;
using Frosty.Core;
using Frosty.Core.Windows;
using FrostySdk.IO;
using FrostySdk.Managers;
using HarmonyLib;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Flurry.Editor.Patches
{
    [HarmonyPatch(typeof(FrostyChunkResEditor))]
    [HarmonyPatchCategory("flurry.editor")]
    public class ChunkResEditorPatch
    {
        private static AccessTools.FieldRef<FrostyChunkResEditor, ListBox> chunksListBox_ref =
            AccessTools.FieldRefAccess<FrostyChunkResEditor, ListBox>("chunksListBox");

        /// <summary>
        /// Fix: RefreshChunksListBox only calls Items.Refresh() which doesn't pick up
        /// added/removed chunks. Re-bind the ItemsSource to get a fresh enumeration.
        /// </summary>
        [HarmonyPatch("RefreshChunksListBox")]
        [HarmonyPrefix]
        public static bool RefreshChunksListBoxPrefix(FrostyChunkResEditor __instance, ChunkAssetEntry selectedAsset)
        {
            ListBox chunksListBox = chunksListBox_ref(__instance);
            chunksListBox.ItemsSource = App.AssetManager.EnumerateChunks();
            chunksListBox.Items.SortDescriptions.Add(
                new System.ComponentModel.SortDescription("DisplayName", System.ComponentModel.ListSortDirection.Ascending));
            if (selectedAsset != null && !selectedAsset.IsAdded)
                chunksListBox.SelectedItem = selectedAsset;
            return false; // Skip original
        }

        [HarmonyPatch("OnApplyTemplate")]
        [HarmonyPostfix]
        public static void AddDuplicateChunkMenuItem(FrostyChunkResEditor __instance)
        {
            ListBox chunksListBox = chunksListBox_ref(__instance);
            if (chunksListBox == null)
                return;

            // Hook ContextMenuOpening on the ListBox — this bubbles up from each ListBoxItem
            // and lets us inject into any item's context menu right before it shows.
            chunksListBox.AddHandler(FrameworkElement.ContextMenuOpeningEvent, new ContextMenuEventHandler((sender, e) =>
            {
                // Find which ListBoxItem triggered this
                if (!(e.OriginalSource is FrameworkElement fe))
                    return;

                ListBoxItem item = fe as ListBoxItem;
                if (item == null)
                    item = FindParent<ListBoxItem>(fe);
                if (item == null)
                    return;

                ContextMenu cm = item.ContextMenu;
                if (cm == null)
                    return;

                // Check if we already injected by looking for our menu items
                bool alreadyInjected = false;
                foreach (var existing in cm.Items)
                {
                    if (existing is MenuItem mi && mi.Header?.ToString() == "Duplicate")
                    {
                        alreadyInjected = true;
                        break;
                    }
                }

                if (!alreadyInjected)
                {
                    ImageSourceConverter converter = new ImageSourceConverter();

                    cm.Items.Add(new Separator());

                    MenuItem duplicateItem = new MenuItem()
                    {
                        Header = "Duplicate",
                        Icon = new Image()
                        {
                            Source = converter.ConvertFromString("pack://application:,,,/FrostyEditor;component/Images/Add.png") as ImageSource,
                            Opacity = 0.5
                        }
                    };
                    duplicateItem.Click += (s, args) =>
                    {
                        ChunkAssetEntry selectedChunk = chunksListBox.SelectedItem as ChunkAssetEntry;
                        if (selectedChunk == null)
                            return;

                        ChunkAssetEntry newChunk = null;
                        FrostyTaskWindow.Show("Duplicating Chunk", "", (task) =>
                        {
                            newChunk = DuplicateChunk(selectedChunk);
                        });

                        if (newChunk != null)
                        {
                            // Re-bind to pick up the newly added chunk
                            chunksListBox.ItemsSource = App.AssetManager.EnumerateChunks();
                            chunksListBox.Items.SortDescriptions.Add(
                                new System.ComponentModel.SortDescription("DisplayName", System.ComponentModel.ListSortDirection.Ascending));
                            chunksListBox.SelectedItem = newChunk;
                            chunksListBox.ScrollIntoView(newChunk);
                        }
                    };
                    cm.Items.Add(duplicateItem);

                    MenuItem copyIdItem = new MenuItem()
                    {
                        Header = "Copy Chunk ID",
                        Icon = new Image()
                        {
                            Source = converter.ConvertFromString("pack://application:,,,/FrostyCore;component/Images/Copy.png") as ImageSource,
                            Opacity = 0.5
                        }
                    };
                    copyIdItem.Click += (s, args) =>
                    {
                        ChunkAssetEntry selectedChunk = chunksListBox.SelectedItem as ChunkAssetEntry;
                        if (selectedChunk == null)
                            return;
                        Clipboard.SetText(selectedChunk.Id.ToString());
                    };
                    cm.Items.Add(copyIdItem);
                }
            }));
        }

        private static T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parent = VisualTreeHelper.GetParent(child);
            while (parent != null)
            {
                if (parent is T result)
                    return result;
                parent = VisualTreeHelper.GetParent(parent);
            }
            return null;
        }

        public static ChunkAssetEntry DuplicateChunk(ChunkAssetEntry entry)
        {
            byte[] random = new byte[16];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                while (true)
                {
                    rng.GetBytes(random);
                    random[15] |= 1;

                    if (App.AssetManager.GetChunkEntry(new Guid(random)) == null)
                        break;
                }
            }

            Guid newGuid;
            using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(entry)))
            {
                newGuid = App.AssetManager.AddChunk(reader.ReadToEnd(), new Guid(random), null, entry.EnumerateBundles().ToArray());
            }

            ChunkAssetEntry newEntry = App.AssetManager.GetChunkEntry(newGuid);
            App.Logger.Log($"Duplicated chunk {entry.Id} to {newGuid}");
            return newEntry;
        }
    }
}
