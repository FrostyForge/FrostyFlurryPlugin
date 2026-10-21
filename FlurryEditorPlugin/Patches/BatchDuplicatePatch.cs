using Flurry.Editor.Windows;
using DuplicationPlugin;
using DuplicationPlugin.Windows;
using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Controls;
using Frosty.Core.Viewport;
using Frosty.Core.Windows;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Flurry.Editor.Patches
{
    /// <summary>
    /// Patches the Data Explorer's context menu to add a "Batch Duplicate" option
    /// that works when multiple assets are selected (requires pluginm for multi-select).
    /// </summary>
    [HarmonyPatch(typeof(FrostyDataExplorer))]
    [HarmonyPatchCategory("flurry.editor")]
    public class BatchDuplicatePatch
    {
        [HarmonyPatch("OnApplyTemplate")]
        [HarmonyPostfix]
        public static void AddBatchDuplicateMenuItem(FrostyDataExplorer __instance)
        {
            if (__instance != App.EditorWindow?.DataExplorer)
                return;

            ContextMenu cm = __instance.AssetContextMenu;
            if (cm == null)
                return;

            cm.Opened += (s, e) =>
            {
                IList<AssetEntry> selectedAssets = __instance.SelectedAssets;
                List<EbxAssetEntry> selectedEbxAssets = GetSelectedEbxAssets(__instance);
                int selectedEbxCount = selectedEbxAssets.Count;

                MenuItem batchDupeItem = GetOrAddMenuItem(cm, "Batch Duplicate", BatchDuplicate_Click, "pack://application:,,,/FrostyEditor;component/Images/Add.png");
                batchDupeItem.Visibility = (selectedAssets != null && selectedAssets.Count > 1)
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                MenuItem renameAssetItem = GetOrAddMenuItem(cm, "Rename Asset", RenameAsset_Click);
                MenuItem moveSelectedItem = GetOrAddMenuItem(cm, "Move Selected", MoveSelected_Click);
                renameAssetItem.Visibility = selectedEbxCount == 1 ? Visibility.Visible : Visibility.Collapsed;
                moveSelectedItem.Visibility = selectedEbxCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            };
        }

        private static MenuItem GetOrAddMenuItem(ContextMenu menu, string header, RoutedEventHandler click, string iconPath = null)
        {
            foreach (var item in menu.Items)
                if (item is MenuItem mi && mi.Header?.ToString() == header)
                    return mi;

            MenuItem menuItem = new MenuItem { Header = header };
            if (iconPath != null)
            {
                ImageSource source = new ImageSourceConverter().ConvertFromString(iconPath) as ImageSource;
                menuItem.Icon = new Image { Source = source, Opacity = 0.5 };
            }
            menuItem.Click += click;
            menu.Items.Add(menuItem);
            return menuItem;
        }

        private static void BatchDuplicate_Click(object sender, RoutedEventArgs e)
        {
            IList<AssetEntry> selectedAssets = App.EditorWindow.DataExplorer.SelectedAssets;
            if (selectedAssets == null || selectedAssets.Count <= 1)
                return;

            List<EbxAssetEntry> entries = selectedAssets.OfType<EbxAssetEntry>().ToList();
            if (entries.Count == 0)
                return;

            // Find common parent path of all selected assets
            string commonPath = GetCommonPath(entries);

            // Ask user for destination path in the asset tree
            string destPath = SimpleInputDialog.Show(
                "Batch Duplicate",
                $"Duplicating {entries.Count} assets.\n\nEnter the destination path in the asset tree.",
                commonPath,
                Application.Current.MainWindow);

            if (destPath == null)
                return;

            destPath = destPath.Replace('\\', '/').Trim('/');

            // Optional find/replace for renaming
            string findText = SimpleInputDialog.Show(
                "Batch Duplicate - Rename (Optional)",
                "Enter text to find in asset filenames.\nLeave empty to keep original names.",
                "",
                Application.Current.MainWindow);

            if (findText == null)
                return; // User cancelled

            string replaceText = "";
            if (!string.IsNullOrEmpty(findText))
            {
                replaceText = SimpleInputDialog.Show(
                    "Batch Duplicate - Rename",
                    $"Replace \"{findText}\" with:",
                    "",
                    Application.Current.MainWindow);

                if (replaceText == null)
                    return; // User cancelled
            }

            bool doRename = !string.IsNullOrEmpty(findText);
            Dictionary<string, DuplicationTool.DuplicateAssetExtension> extensions = BuildDuplicationExtensions();

            int duplicated = 0;
            int failed = 0;

            FrostyTaskWindow.Show("Batch Duplicate", "", (task) =>
            {
                if (!MeshVariationDb.IsLoaded)
                    MeshVariationDb.LoadVariations(task);

                for (int i = 0; i < entries.Count; i++)
                {
                    EbxAssetEntry entry = entries[i];
                    task.Update($"Duplicating {entry.Filename} ({i + 1}/{entries.Count})");

                    try
                    {
                        string filename = entry.Filename;

                        // Apply find/replace if user provided one
                        if (doRename)
                        {
                            // Case-insensitive find, case-aware replace
                            int idx = filename.IndexOf(findText, StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                            {
                                // Match the casing of the first character
                                string matched = filename.Substring(idx, findText.Length);
                                string casedReplace = replaceText;
                                if (casedReplace.Length > 0 && matched.Length > 0)
                                {
                                    char first = char.IsUpper(matched[0])
                                        ? char.ToUpper(casedReplace[0])
                                        : char.ToLower(casedReplace[0]);
                                    casedReplace = first + casedReplace.Substring(1);
                                }
                                filename = filename.Substring(0, idx) + casedReplace + filename.Substring(idx + findText.Length);
                            }
                        }

                        string newName = string.IsNullOrEmpty(destPath)
                            ? filename
                            : destPath + "/" + filename;

                        if (App.AssetManager.GetEbxEntry(newName) != null)
                        {
                            App.Logger.LogWarning($"Skipping {entry.Name}: {newName} already exists");
                            failed++;
                            continue;
                        }

                        DuplicateAsset(entry, newName, extensions);
                        duplicated++;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.LogWarning($"Failed to duplicate {entry.Name}: {ex.Message}");
                        failed++;
                    }
                }
            });

            App.Logger.Log($"Batch Duplicate complete: {duplicated} duplicated, {failed} failed/skipped.");
            App.EditorWindow.DataExplorer.RefreshAll();
        }

        private static void RenameAsset_Click(object sender, RoutedEventArgs e)
        {
            EbxAssetEntry entry = GetSelectedEbxAssets(App.EditorWindow?.DataExplorer).FirstOrDefault();
            if (entry == null)
                return;

            if (!entry.IsAdded)
            {
                FrostyMessageBox.Show(
                    "Rename currently supports duplicated/added assets only.\n\n" +
                    "This avoids breaking base-game asset indexing and references.",
                    "Rename Asset",
                    MessageBoxButton.OK);
                return;
            }

            string input = SimpleInputDialog.Show(
                "Rename Asset",
                "Enter new filename:",
                entry.Filename,
                Application.Current.MainWindow);

            if (input == null)
                return;

            string newFilename = (input ?? "").Trim();
            if (string.IsNullOrWhiteSpace(newFilename) || newFilename.Contains("/") || newFilename.Contains("\\"))
            {
                FrostyMessageBox.Show("Invalid filename.", "Rename Asset", MessageBoxButton.OK);
                return;
            }

            string newName = string.IsNullOrEmpty(entry.Path) ? newFilename : entry.Path + "/" + newFilename;

            if (string.Equals(newName, entry.Name, StringComparison.OrdinalIgnoreCase))
                return;

            CloseOpenEditorsForAssets(new[] { entry });

            string oldName = entry.Name;
            if (!TryRenameEbxEntry(entry, newName, out string error))
            {
                FrostyMessageBox.Show(error ?? "Rename failed.", "Rename Asset", MessageBoxButton.OK);
                return;
            }

            App.Logger.Log($"Asset renamed: {oldName} -> {newName}");
            App.EditorWindow.DataExplorer.RefreshAll();
            App.EditorWindow.DataExplorer.SelectAsset(entry);
            PromptToSaveProject("Rename Asset");
        }

        private static void MoveSelected_Click(object sender, RoutedEventArgs e)
        {
            List<EbxAssetEntry> entries = GetSelectedEbxAssets(App.EditorWindow?.DataExplorer);
            if (entries == null || entries.Count == 0)
                return;

            if (entries.Any(x => !x.IsAdded))
            {
                FrostyMessageBox.Show(
                    "Move currently supports duplicated/added assets only.\n\n" +
                    "Select only added assets and try again.",
                    "Move Selected",
                    MessageBoxButton.OK);
                return;
            }

            if (!TryPickDestinationFolder(entries[0], out string destinationFolder))
                return;

            CloseOpenEditorsForAssets(entries);

            int moved = 0;
            int failed = 0;

            FrostyTaskWindow.Show("Move Selected", "", (task) =>
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    EbxAssetEntry entry = entries[i];
                    task.Update($"Moving {entry.Filename} ({i + 1}/{entries.Count})");

                    string newName = string.IsNullOrEmpty(destinationFolder)
                        ? entry.Filename
                        : destinationFolder + "/" + entry.Filename;

                    if (string.Equals(newName, entry.Name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (TryRenameEbxEntry(entry, newName, out string error))
                    {
                        moved++;
                    }
                    else
                    {
                        failed++;
                        App.Logger.LogWarning($"Create Folder: Failed to move {entry.Name}: {error}");
                    }
                }
            });

            App.Logger.Log($"Move Selected complete: {moved} moved, {failed} failed.");
            App.EditorWindow.DataExplorer.RefreshAll();
            if (moved > 0)
                PromptToSaveProject("Move Selected");
        }

        private static void CloseOpenEditorsForAssets(IEnumerable<EbxAssetEntry> entries)
        {
            try
            {
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (EbxAssetEntry entry in entries ?? Enumerable.Empty<EbxAssetEntry>())
                {
                    if (!string.IsNullOrEmpty(entry?.Name))
                        names.Add(entry.Name);
                }

                if (names.Count == 0)
                    return;

                Window mainWindow = Application.Current?.MainWindow;
                if (mainWindow == null)
                    return;

                Action closeAction = () =>
                {
                    FieldInfo tabControlField = mainWindow.GetType().GetField("tabControl", BindingFlags.Instance | BindingFlags.NonPublic);
                    FrostyTabControl tabControl = tabControlField?.GetValue(mainWindow) as FrostyTabControl;
                    if (tabControl == null)
                        return;

                    MethodInfo shutdownMethod = mainWindow.GetType().GetMethod("ShutdownEditorAndRemoveTab", BindingFlags.Instance | BindingFlags.Public);
                    MethodInfo removeMethod = mainWindow.GetType().GetMethod("RemoveTab", BindingFlags.Instance | BindingFlags.Public);

                    for (int i = tabControl.Items.Count - 1; i >= 1; i--)
                    {
                        if (!(tabControl.Items[i] is FrostyTabItem tabItem))
                            continue;

                        if (string.IsNullOrEmpty(tabItem.TabId) || !names.Contains(tabItem.TabId))
                            continue;

                        if (tabItem.Content is FrostyAssetEditor editor && shutdownMethod != null)
                            shutdownMethod.Invoke(mainWindow, new object[] { editor, tabItem });
                        else
                            removeMethod?.Invoke(mainWindow, new object[] { tabItem });
                    }
                };

                if (mainWindow.Dispatcher.CheckAccess())
                    closeAction();
                else
                    mainWindow.Dispatcher.Invoke(closeAction);
            }
            catch (Exception ex)
            {
                App.Logger?.LogWarning($"Rename/Move: failed to close open editors before operation: {ex.Message}");
            }
        }

        private static string GetCommonPath(List<EbxAssetEntry> entries)
        {
            if (entries.Count == 0)
                return "";

            string[] first = entries[0].Path.Split('/');
            int commonLength = first.Length;

            for (int i = 1; i < entries.Count; i++)
            {
                string[] parts = entries[i].Path.Split('/');
                commonLength = Math.Min(commonLength, parts.Length);
                for (int j = 0; j < commonLength; j++)
                {
                    if (!string.Equals(first[j], parts[j], StringComparison.OrdinalIgnoreCase))
                    {
                        commonLength = j;
                        break;
                    }
                }
            }

            return string.Join("/", first.Take(commonLength));
        }

        private static string NormalizeFolderPath(string input)
        {
            return (input ?? "").Replace('\\', '/').Trim().Trim('/');
        }

        private static bool TryPickDestinationFolder(EbxAssetEntry anchorEntry, out string destinationFolder)
        {
            destinationFolder = null;
            if (anchorEntry == null)
                return false;

            DuplicateAssetWindow picker = new DuplicateAssetWindow(anchorEntry)
            {
                Title = "Move Selected - Destination"
            };

            if (picker.FindName("typeButton") is Button typeButton)
                typeButton.Visibility = Visibility.Collapsed;

            if (picker.FindName("assetTypeTextBox") is TextBox typeTextBox)
                typeTextBox.Visibility = Visibility.Collapsed;

            if (picker.ShowDialog() != true)
                return false;

            string selectedPath = NormalizeFolderPath(picker.SelectedPath);
            string selectedName = (picker.SelectedName ?? "").Replace('\\', '/').Trim('/');
            if (string.IsNullOrWhiteSpace(selectedName))
                return false;

            // Duplicate window stores both folder and final filename in SelectedName.
            // For move, we only want the destination folder.
            int slashIndex = selectedName.LastIndexOf('/');
            string subFolder = slashIndex >= 0 ? selectedName.Substring(0, slashIndex) : "";
            string fullFolder = string.IsNullOrEmpty(selectedPath)
                ? subFolder
                : string.IsNullOrEmpty(subFolder) ? selectedPath : selectedPath + "/" + subFolder;

            destinationFolder = NormalizeFolderPath(fullFolder);
            return true;
        }

        private static List<EbxAssetEntry> GetSelectedEbxAssets(FrostyDataExplorer explorer)
        {
            var results = new List<EbxAssetEntry>();
            if (explorer == null)
                return results;

            if (explorer.SelectedAssets != null)
            {
                results.AddRange(explorer.SelectedAssets.OfType<EbxAssetEntry>());
            }

            if (results.Count == 0 && explorer.SelectedAsset is EbxAssetEntry selectedEntry)
                results.Add(selectedEntry);

            return results;
        }

        private static bool TryRenameEbxEntry(EbxAssetEntry entry, string newName, out string error)
        {
            error = null;
            if (entry == null)
            {
                error = "No asset selected.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(newName))
            {
                error = "Invalid name.";
                return false;
            }

            if (!entry.IsAdded)
            {
                error = "Only added assets can be renamed safely.";
                return false;
            }

            string oldName = entry.Name;
            string oldKey = oldName.ToLower();
            string newKey = newName.ToLower();

            EbxAssetEntry existing = App.AssetManager.GetEbxEntry(newName);
            if (existing != null && !ReferenceEquals(existing, entry))
            {
                error = $"An asset named '{newName}' already exists.";
                return false;
            }

            var ebxListField = typeof(AssetManager).GetField("ebxList", BindingFlags.Instance | BindingFlags.NonPublic);
            var ebxList = ebxListField?.GetValue(App.AssetManager) as Dictionary<string, EbxAssetEntry>;
            if (ebxList == null)
            {
                error = "Could not access asset index for rename.";
                return false;
            }

            if (!ebxList.ContainsKey(oldKey))
            {
                string foundKey = ebxList.FirstOrDefault(kvp => ReferenceEquals(kvp.Value, entry)).Key;
                if (string.IsNullOrEmpty(foundKey))
                {
                    error = "Could not locate selected asset in index.";
                    return false;
                }
                oldKey = foundKey;
            }

            if (!string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase))
            {
                if (ebxList.ContainsKey(newKey))
                {
                    error = $"An asset named '{newName}' already exists.";
                    return false;
                }

                ebxList.Remove(oldKey);
                ebxList[newKey] = entry;
            }

            entry.Name = newName;
            entry.IsDirty = true;
            if (entry.ModifiedEntry != null)
                entry.ModifiedEntry.IsDirty = true;

            TryRenameBlueprintBundle(entry, newName);
            TryUpdateRootObjectName(entry, newName);

            return true;
        }

        private static void TryRenameBlueprintBundle(EbxAssetEntry entry, string newName)
        {
            try
            {
                var bundlesField = typeof(AssetManager).GetField("bundles", BindingFlags.Instance | BindingFlags.NonPublic);
                var bundles = bundlesField?.GetValue(App.AssetManager) as List<BundleEntry>;
                if (bundles == null)
                    return;

                string newBundleName = "win32/" + newName.ToLower();
                foreach (BundleEntry bundle in bundles)
                {
                    if (!bundle.Added)
                        continue;
                    if (!ReferenceEquals(bundle.Blueprint, entry))
                        continue;
                    if (bundle.Type != BundleType.BlueprintBundle && bundle.Type != BundleType.SubLevel)
                        continue;

                    bundle.Name = newBundleName;
                }
            }
            catch (Exception ex)
            {
                App.Logger.LogWarning($"Rename Asset: bundle rename warning: {ex.Message}");
            }
        }

        private static void TryUpdateRootObjectName(EbxAssetEntry entry, string newName)
        {
            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                if (asset?.RootObject == null)
                    return;

                PropertyInfo nameProperty = asset.RootObject.GetType().GetProperty(
                    "Name",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (nameProperty == null || !nameProperty.CanWrite || nameProperty.PropertyType != typeof(string))
                    return;

                string current = nameProperty.GetValue(asset.RootObject) as string;
                if (string.Equals(current, newName, StringComparison.Ordinal))
                    return;

                nameProperty.SetValue(asset.RootObject, newName);
                App.AssetManager.ModifyEbx(newName, asset);
            }
            catch (Exception ex)
            {
                App.Logger.LogWarning($"Rename Asset: root-name update warning: {ex.Message}");
            }
        }

        private static void PromptToSaveProject(string featureName)
        {
            try
            {
                Window mainWindow = Application.Current?.MainWindow;
                if (mainWindow == null)
                    return;

                MethodInfo promptMethod = mainWindow.GetType().GetMethod(
                    "AskIfShouldSaveProject",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(bool) },
                    null);

                if (promptMethod == null)
                    return;

                MessageBoxResult result = FrostyMessageBox.Show(
                    $"{featureName} finished.\n\nSave project now?",
                    featureName,
                    MessageBoxButton.YesNo);

                if (result == MessageBoxResult.Yes)
                    promptMethod.Invoke(mainWindow, new object[] { false });
            }
            catch
            {
                // Ignore save-prompt issues.
            }
        }

        private static Dictionary<string, DuplicationTool.DuplicateAssetExtension> BuildDuplicationExtensions()
        {
            var extensions = new Dictionary<string, DuplicationTool.DuplicateAssetExtension>(StringComparer.OrdinalIgnoreCase);
            Type extensionBaseType = typeof(DuplicationTool.DuplicateAssetExtension);
            var assembly = extensionBaseType.Assembly;

            foreach (Type type in assembly.GetTypes())
            {
                if (!type.IsSubclassOf(extensionBaseType) || type.IsAbstract)
                    continue;

                try
                {
                    var extension = (DuplicationTool.DuplicateAssetExtension)Activator.CreateInstance(type);
                    if (extension?.AssetType != null && !extensions.ContainsKey(extension.AssetType))
                        extensions.Add(extension.AssetType, extension);
                }
                catch
                {
                    // Ignore extensions that fail to instantiate; we'll fall back to EBX-only duplication.
                }
            }

            return extensions;
        }

        private static EbxAssetEntry DuplicateAsset(EbxAssetEntry entry, string newName,
            Dictionary<string, DuplicationTool.DuplicateAssetExtension> extensions)
        {
            foreach (var kvp in extensions)
            {
                if (TypeLibrary.IsSubClassOf(entry.Type, kvp.Key))
                {
                    return kvp.Value.DuplicateAsset(entry, newName, false, null);
                }
            }

            // Fallback for asset types without a dedicated duplication extension.
            return DuplicateEbxOnly(entry, newName);
        }

        private static EbxAssetEntry DuplicateEbxOnly(EbxAssetEntry entry, string newName)
        {
            EbxAsset asset = App.AssetManager.GetEbx(entry);

            EbxAsset newAsset;
            using (EbxBaseWriter writer = EbxBaseWriter.CreateWriter(new MemoryStream(), EbxWriteFlags.DoNotSort))
            {
                writer.WriteAsset(asset);
                byte[] buf = writer.ToByteArray();
                using (EbxReader reader = EbxReader.CreateReader(new MemoryStream(buf)))
                    newAsset = reader.ReadAsset<EbxAsset>();
            }

            newAsset.SetFileGuid(Guid.NewGuid());

            dynamic obj = newAsset.RootObject;
            obj.Name = newName;

            AssetClassGuid guid = new AssetClassGuid(
                Utils.GenerateDeterministicGuid(newAsset.Objects, (Type)obj.GetType(), newAsset.FileGuid), -1);
            obj.SetInstanceGuid(guid);

            EbxAssetEntry newEntry = App.AssetManager.AddEbx(newName, newAsset);
            newEntry.AddedBundles.AddRange(entry.EnumerateBundles());
            newEntry.ModifiedEntry.DependentAssets.AddRange(newAsset.Dependencies);

            return newEntry;
        }
    }
}
