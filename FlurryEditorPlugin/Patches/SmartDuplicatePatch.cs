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
using System.Collections;
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
    /// Holds state between the DuplicateAssetWindow checkboxes and the post-duplication hook.
    /// </summary>
    internal static class SmartDuplicateState
    {
        public static bool Pending { get; set; }
        public static bool DuplicateDependencies { get; set; }
        public static EbxAssetEntry OriginalEntry { get; set; }
        public static string NewAssetFullName { get; set; }
        public static string DestinationPath { get; set; }
        public static string OldPrefix { get; set; }
        public static string NewPrefix { get; set; }

        public static void Clear()
        {
            Pending = false;
            DuplicateDependencies = false;
            OriginalEntry = null;
            NewAssetFullName = null;
            DestinationPath = null;
            OldPrefix = null;
            NewPrefix = null;
        }
    }

    // -----------------------------------------------------------------------
    // Patch 1: Inject checkboxes into the DuplicateAssetWindow for mesh assets
    // -----------------------------------------------------------------------
    [HarmonyPatch(typeof(DuplicateAssetWindow))]
    [HarmonyPatchCategory("flurry.editor")]
    internal class DuplicateWindowPatch
    {
        private static CheckBox depCheckBox;
        private static EbxAssetEntry currentEntry;

        /// <summary>
        /// After the DuplicateAssetWindow constructor runs, inject our checkbox
        /// if the asset being duplicated is a mesh.
        /// </summary>
        [HarmonyPatch(MethodType.Constructor, typeof(EbxAssetEntry))]
        [HarmonyPostfix]
        public static void ConstructorPostfix(DuplicateAssetWindow __instance, EbxAssetEntry currentEntry)
        {
            SmartDuplicateState.Clear();
            DuplicateWindowPatch.currentEntry = currentEntry;
            depCheckBox = null;

            // Only add checkbox for mesh types (SkinnedMeshAsset, CompositeMeshAsset, RigidMeshAsset, etc.)
            if (!TypeLibrary.IsSubClassOf(currentEntry.Type, "MeshAsset"))
                return;

            // Find the main Grid (root content of the window)
            Grid rootGrid = __instance.Content as Grid;
            if (rootGrid == null)
                return;

            // The XAML has: Row 0 = page content (*), Row 1 = button bar (38)
            // We insert a new row between them for our checkbox
            rootGrid.RowDefinitions.Insert(1, new RowDefinition { Height = GridLength.Auto });

            // Shift the button bar from row 1 to row 2
            foreach (UIElement child in rootGrid.Children)
            {
                int row = Grid.GetRow(child);
                if (row >= 1)
                    Grid.SetRow(child, row + 1);
            }

            // Theme colors
            Brush fgBrush = Application.Current?.Resources.Contains("FontColor") == true
                ? Application.Current.Resources["FontColor"] as Brush
                : new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF8));
            Brush bgBrush = Application.Current?.Resources.Contains("WindowBackground") == true
                ? Application.Current.Resources["WindowBackground"] as Brush
                : new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));

            // Create the checkbox panel
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 4, 8, 4),
                Background = bgBrush
            };
            Grid.SetRow(panel, 1);

            depCheckBox = new CheckBox
            {
                Content = "Duplicate Dependencies",
                Foreground = fgBrush,
                VerticalContentAlignment = VerticalAlignment.Center,
                IsChecked = false,
                ToolTip = "Also duplicate ObjectBlueprints/ClothObjectBlueprints that reference this mesh,\nincluding ClothAssets and ClothWrappingAssets.\nAll duplicated assets will have their names and references updated."
            };

            panel.Children.Add(depCheckBox);
            rootGrid.Children.Add(panel);

            // Grow the window height a bit to accommodate the new row
            __instance.Height += 30;
        }

        /// <summary>
        /// After the Save button closes the dialog, capture our checkbox state.
        /// </summary>
        [HarmonyPatch("SaveButton_Click")]
        [HarmonyPostfix]
        public static void SaveClickPostfix(DuplicateAssetWindow __instance)
        {
            // Only act if dialog was accepted (DialogResult == true)
            if (__instance.DialogResult != true)
                return;

            if (currentEntry == null || !TypeLibrary.IsSubClassOf(currentEntry.Type, "MeshAsset"))
                return;

            bool wantDeps = depCheckBox?.IsChecked == true;

            if (!wantDeps)
                return;

            // Capture state for the post-duplication hook
            string selectedPath = __instance.SelectedPath ?? "";
            string selectedName = __instance.SelectedName ?? "";

            // Handle subfolders in the name field (e.g. "Mace/mace_01_mesh")
            // selectedPath = tree-selected folder, selectedName = text field (may include subfolders)
            string nameFilename = selectedName;
            string nameSubPath = "";
            if (selectedName.Contains("/"))
            {
                int lastSlash = selectedName.LastIndexOf('/');
                nameSubPath = selectedName.Substring(0, lastSlash);
                nameFilename = selectedName.Substring(lastSlash + 1);
            }

            // Full destination path, preserving the user's folder casing
            string fullDestPath = selectedPath;
            if (!string.IsNullOrEmpty(nameSubPath))
                fullDestPath = string.IsNullOrEmpty(fullDestPath) ? nameSubPath : fullDestPath + "/" + nameSubPath;

            // Full asset name for mesh lookup (lowercased, because MeshExtension does ToLower)
            string fullName = (selectedPath + "/" + selectedName).Trim('/');

            SmartDuplicateState.Pending = true;
            SmartDuplicateState.DuplicateDependencies = true;
            SmartDuplicateState.OriginalEntry = currentEntry;
            SmartDuplicateState.NewAssetFullName = fullName.ToLower();
            SmartDuplicateState.DestinationPath = fullDestPath;

            // Parse name prefixes for smart renaming
            // Original mesh: "anakin_01_mesh" -> prefix "anakin_01"
            // New mesh: "mace_01_mesh" -> prefix "mace_01"
            string oldFilename = currentEntry.Filename;
            SmartDuplicateState.OldPrefix = StripMeshSuffix(oldFilename);
            SmartDuplicateState.NewPrefix = StripMeshSuffix(nameFilename);

            App.Logger.Log($"Smart Duplicate: queued deps duplication. Old prefix='{SmartDuplicateState.OldPrefix}', New prefix='{SmartDuplicateState.NewPrefix}', Dest='{fullDestPath}'");
        }

        /// <summary>
        /// Strip common mesh suffixes to get the character/skin prefix.
        /// e.g. "anakin_01_mesh" -> "anakin_01", "anakin_01_flaps_mesh" -> "anakin_01_flaps"
        /// We only strip "_mesh" from the end since that's the primary asset being duplicated.
        /// The prefix is the part BEFORE _mesh.
        /// </summary>
        private static string StripMeshSuffix(string name)
        {
            if (name.EndsWith("_mesh", StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - 5);
            return name;
        }
    }

    // -----------------------------------------------------------------------
    // Patch 2: After RefreshAll, perform the extra duplication work
    // -----------------------------------------------------------------------
    [HarmonyPatch(typeof(FrostyDataExplorer), "RefreshAll")]
    [HarmonyPatchCategory("flurry.editor")]
    internal class SmartDuplicateRefreshPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (!SmartDuplicateState.Pending)
                return;

            // Capture and clear state immediately to prevent re-entry
            bool wantDeps = SmartDuplicateState.DuplicateDependencies;
            EbxAssetEntry originalMesh = SmartDuplicateState.OriginalEntry;
            string newMeshName = SmartDuplicateState.NewAssetFullName;
            string destPath = SmartDuplicateState.DestinationPath;
            string oldPrefix = SmartDuplicateState.OldPrefix;
            string newPrefix = SmartDuplicateState.NewPrefix;
            SmartDuplicateState.Clear();

            if (!wantDeps || originalMesh == null || string.IsNullOrEmpty(newMeshName))
                return;

            // Verify the new mesh was actually created
            EbxAssetEntry newMeshEntry = App.AssetManager.GetEbxEntry(newMeshName);
            if (newMeshEntry == null)
            {
                App.Logger.LogWarning($"Smart Duplicate: Could not find duplicated mesh at '{newMeshName}'. Skipping dependency duplication.");
                return;
            }

            App.Logger.Log($"Smart Duplicate: Starting dependency duplication for '{originalMesh.Name}' -> '{newMeshName}'");

            int duplicated = 0;
            int failed = 0;

            FrostyTaskWindow.Show("Smart Duplicate - Dependencies", "", (task) =>
            {
                try
                {
                    if (!MeshVariationDb.IsLoaded)
                        MeshVariationDb.LoadVariations(task);

                    // Track what we duplicate so we can rewrite references
                    // Maps original FileGuid -> new FileGuid
                    Dictionary<Guid, Guid> guidMap = new Dictionary<Guid, Guid>();
                    // Maps original FileGuid -> new asset name
                    Dictionary<Guid, string> nameMap = new Dictionary<Guid, string>();
                    // Maps original root ClassGuid -> duplicated root ClassGuid
                    Dictionary<Guid, Guid> classGuidMap = new Dictionary<Guid, Guid>();

                    // The mesh itself is already duplicated; record it in our maps
                    guidMap[originalMesh.Guid] = newMeshEntry.Guid;
                    nameMap[originalMesh.Guid] = newMeshEntry.Name;
                    RegisterRootClassGuidMap(originalMesh, newMeshEntry, classGuidMap);

                    // ----------------------------------------------------------
                    // Find and duplicate parent Blueprints + Cloth assets
                    // ----------------------------------------------------------
                    task.Update("Finding blueprints that reference this mesh...");
                    var parentBlueprints = FindParentBlueprints(originalMesh);
                    App.Logger.Log($"Smart Duplicate: Found {parentBlueprints.Count} parent blueprint(s).");

                    for (int i = 0; i < parentBlueprints.Count; i++)
                    {
                        EbxAssetEntry bp = parentBlueprints[i];
                        task.Update($"Duplicating {bp.Filename} ({i + 1}/{parentBlueprints.Count})...");

                        try
                        {
                            string newBpName = BuildNewAssetName(bp, destPath, oldPrefix, newPrefix);
                            newBpName = EnsureUniqueName(newBpName);

                            App.Logger.Log($"Smart Duplicate: {bp.Name} -> {newBpName}");
                            EbxAssetEntry newBp = DuplicateEbxAsset(bp, newBpName);
                            guidMap[bp.Guid] = newBp.Guid;
                            nameMap[bp.Guid] = newBp.Name;
                            RegisterRootClassGuidMap(bp, newBp, classGuidMap);
                            duplicated++;

                            // For ClothObjectBlueprints, also duplicate cloth assets
                            if (bp.Type == "ClothObjectBlueprint")
                            {
                                DuplicateClothDependencies(bp, destPath, oldPrefix, newPrefix,
                                    guidMap, nameMap, classGuidMap, task, ref duplicated, ref failed);
                            }
                        }
                        catch (Exception ex)
                        {
                            App.Logger.LogWarning($"Smart Duplicate: Failed to duplicate {bp.Name}: {ex.Message}");
                            failed++;
                        }
                    }

                    // ----------------------------------------------------------
                    // Rewrite references in all duplicated assets
                    // ----------------------------------------------------------
                    if (guidMap.Count > 1)
                    {
                        task.Update("Updating internal references...");
                        foreach (var kvp in nameMap)
                        {
                            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(kvp.Value);
                            if (entry == null)
                                continue;

                            EbxAsset asset = App.AssetManager.GetEbx(entry);
                            bool modified = false;
                            RewriteContext rewriteContext = new RewriteContext();

                            foreach (object obj in asset.Objects)
                            {
                                if (RewriteReferences(obj, guidMap, classGuidMap, rewriteContext) > 0)
                                    modified = true;
                            }

                            if (modified)
                            {
                                // Ensure remapped external refs are represented in dependencies, even for metadata
                                // that Frosty's automatic dependency scanner may not classify as references.
                                foreach (Guid depGuid in rewriteContext.AddedDependencies)
                                    asset.AddDependency(depGuid);

                                App.AssetManager.ModifyEbx(entry.Name, asset);
                                ApplyDependencyRemapToEntry(entry, rewriteContext.FileDependencyRemap);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.LogWarning($"Smart Duplicate error: {ex.Message}\n{ex.StackTrace}");
                }
            });

            App.Logger.Log($"Smart Duplicate complete: {duplicated} extra assets duplicated, {failed} failed.");
            App.EditorWindow.DataExplorer.RefreshAll();
            PromptToSaveProjectAfterDuplication(duplicated, failed);
        }

        private static void PromptToSaveProjectAfterDuplication(int duplicated, int failed)
        {
            if (duplicated <= 0)
                return;

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
                {
                    App.Logger.LogWarning("Smart Duplicate: Could not find AskIfShouldSaveProject on main window.");
                    return;
                }

                string details = failed > 0
                    ? $" ({failed} failed)"
                    : string.Empty;

                MessageBoxResult result = FrostyMessageBox.Show(
                    $"Smart Duplicate finished.\n\nDuplicated {duplicated} asset(s){details}.\n\nSave project now?",
                    "Smart Duplicate",
                    MessageBoxButton.YesNo);

                if (result == MessageBoxResult.Yes)
                    promptMethod.Invoke(mainWindow, new object[] { false });
            }
            catch (Exception ex)
            {
                App.Logger.LogWarning($"Smart Duplicate: Save prompt failed: {ex.Message}");
            }
        }

        // ===================================================================
        // Helper: Find ObjectBlueprints/ClothObjectBlueprints referencing mesh
        // ===================================================================
        private static List<EbxAssetEntry> FindParentBlueprints(EbxAssetEntry meshEntry)
        {
            var results = new List<EbxAssetEntry>();
            Guid meshGuid = meshEntry.Guid;

            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
            {
                if (entry.Type != "ObjectBlueprint" && entry.Type != "ClothObjectBlueprint")
                    continue;

                if (entry.ContainsDependency(meshGuid))
                    results.Add(entry);
            }

            return results;
        }

        // ===================================================================
        // Helper: Duplicate ClothAsset + ClothWrappingAsset for a ClothObjectBlueprint
        // ===================================================================
        private static void DuplicateClothDependencies(EbxAssetEntry clothBp, string destPath,
            string oldPrefix, string newPrefix,
            Dictionary<Guid, Guid> guidMap, Dictionary<Guid, string> nameMap, Dictionary<Guid, Guid> classGuidMap,
            FrostyTaskWindow task, ref int duplicated, ref int failed)
        {
            try
            {
                EbxAsset bpAsset = App.AssetManager.GetEbx(clothBp);
                dynamic root = bpAsset.RootObject;
                dynamic entityData = ((PointerRef)root.Object).Internal;
                if (entityData == null) return;

                // ClothEntityData has Cloth (ClothAsset) and ClothWrapping (ClothWrappingAsset)
                // EACloth assets go into a Cloth subfolder, matching vanilla structure
                string clothFolder = string.IsNullOrEmpty(destPath) ? "Cloth" : destPath + "/Cloth";

                PointerRef clothRef = (PointerRef)entityData.Cloth;
                if (clothRef.Type == PointerRefType.External)
                {
                    EbxAssetEntry clothEntry = App.AssetManager.GetEbxEntry(clothRef.External.FileGuid);
                    if (clothEntry != null && !guidMap.ContainsKey(clothEntry.Guid))
                    {
                        task.Update($"Duplicating {clothEntry.Filename}...");
                        string newClothFilename = SmartReplace(clothEntry.Filename, oldPrefix, newPrefix);
                        string newName = clothFolder + "/" + newClothFilename;
                        newName = EnsureUniqueName(newName);

                        EbxAssetEntry newCloth = DuplicateWithExtension(clothEntry, newName, task);
                        if (newCloth != null)
                        {
                            guidMap[clothEntry.Guid] = newCloth.Guid;
                            nameMap[clothEntry.Guid] = newCloth.Name;
                            RegisterRootClassGuidMap(clothEntry, newCloth, classGuidMap);
                            duplicated++;
                        }
                    }
                }

                PointerRef wrapRef = (PointerRef)entityData.ClothWrapping;
                if (wrapRef.Type == PointerRefType.External)
                {
                    EbxAssetEntry wrapEntry = App.AssetManager.GetEbxEntry(wrapRef.External.FileGuid);
                    if (wrapEntry != null && !guidMap.ContainsKey(wrapEntry.Guid))
                    {
                        task.Update($"Duplicating {wrapEntry.Filename}...");
                        string newName = BuildNewAssetName(wrapEntry, destPath, oldPrefix, newPrefix);
                        newName = EnsureUniqueName(newName);

                        EbxAssetEntry newWrap = DuplicateWithExtension(wrapEntry, newName, task);
                        if (newWrap != null)
                        {
                            guidMap[wrapEntry.Guid] = newWrap.Guid;
                            nameMap[wrapEntry.Guid] = newWrap.Name;
                            RegisterRootClassGuidMap(wrapEntry, newWrap, classGuidMap);
                            duplicated++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.LogWarning($"Smart Duplicate: Failed to process cloth deps for {clothBp.Name}: {ex.Message}");
                failed++;
            }
        }

        private static void RegisterRootClassGuidMap(EbxAssetEntry originalEntry, EbxAssetEntry duplicatedEntry, Dictionary<Guid, Guid> classGuidMap)
        {
            if (originalEntry == null || duplicatedEntry == null || classGuidMap == null)
                return;

            try
            {
                EbxAsset originalAsset = App.AssetManager.GetEbx(originalEntry);
                EbxAsset duplicatedAsset = App.AssetManager.GetEbx(duplicatedEntry);
                if (originalAsset == null || duplicatedAsset == null)
                    return;

                Guid originalRootGuid = originalAsset.RootInstanceGuid;
                Guid duplicatedRootGuid = duplicatedAsset.RootInstanceGuid;

                if (originalRootGuid == Guid.Empty || duplicatedRootGuid == Guid.Empty || originalRootGuid == duplicatedRootGuid)
                    return;

                classGuidMap[originalRootGuid] = duplicatedRootGuid;
            }
            catch
            {
                // If root guid lookup fails for a type, continue without class-guid remapping for that asset.
            }
        }

        private static void ApplyDependencyRemapToEntry(EbxAssetEntry entry, Dictionary<Guid, Guid> fileDependencyRemap)
        {
            if (entry?.ModifiedEntry == null || fileDependencyRemap == null || fileDependencyRemap.Count == 0)
                return;

            List<Guid> deps = entry.ModifiedEntry.DependentAssets;
            for (int i = 0; i < deps.Count; i++)
            {
                if (fileDependencyRemap.TryGetValue(deps[i], out Guid remappedGuid))
                    deps[i] = remappedGuid;
            }

            foreach (Guid remappedGuid in fileDependencyRemap.Values)
            {
                if (!deps.Contains(remappedGuid))
                    deps.Add(remappedGuid);
            }

            var seen = new HashSet<Guid>();
            for (int i = deps.Count - 1; i >= 0; i--)
            {
                if (!seen.Add(deps[i]))
                    deps.RemoveAt(i);
            }
        }

        // ===================================================================
        // Name replacement helpers
        // ===================================================================

        /// <summary>
        /// Build a new name for a dependency asset using smart prefix replacement.
        /// e.g. old prefix "anakin_01", new prefix "mace_01":
        ///   "Anakin_01" -> "Mace_01"
        ///   "anakin_01_flaps_mesh" -> "mace_01_flaps_mesh"
        /// </summary>
        private static string BuildNewAssetName(EbxAssetEntry entry, string destPath, string oldPrefix, string newPrefix)
        {
            string filename = entry.Filename;
            string newFilename = SmartReplace(filename, oldPrefix, newPrefix);

            return string.IsNullOrEmpty(destPath) ? newFilename : destPath + "/" + newFilename;
        }

        /// <summary>
        /// Case-aware prefix replacement.
        /// Finds oldPrefix in the text (case-insensitive) and replaces with newPrefix,
        /// matching the case pattern of the original occurrence.
        /// </summary>
        private static string SmartReplace(string text, string oldPrefix, string newPrefix)
        {
            if (string.IsNullOrEmpty(oldPrefix) || string.IsNullOrEmpty(newPrefix) ||
                string.IsNullOrEmpty(text))
                return text;

            int idx = text.IndexOf(oldPrefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return text;

            // Get the original casing of the matched segment
            string matched = text.Substring(idx, oldPrefix.Length);

            // Apply matching casing to the new prefix
            string casedNew = ApplyCasing(matched, newPrefix);

            return text.Substring(0, idx) + casedNew + text.Substring(idx + oldPrefix.Length);
        }

        /// <summary>
        /// Applies the casing pattern from 'template' to 'target'.
        /// If template[0] is uppercase, capitalize target[0]. Otherwise keep lowercase.
        /// Rest of target keeps its original casing (as typed by user).
        /// </summary>
        private static string ApplyCasing(string template, string target)
        {
            if (string.IsNullOrEmpty(template) || string.IsNullOrEmpty(target))
                return target;

            char first = target[0];
            if (char.IsUpper(template[0]))
                first = char.ToUpper(first);
            else
                first = char.ToLower(first);

            return first + target.Substring(1);
        }

        private static string EnsureUniqueName(string name)
        {
            string baseName = name;
            int suffix = 1;
            while (App.AssetManager.GetEbxEntry(name) != null)
            {
                name = baseName + "_" + suffix;
                suffix++;
            }
            return name;
        }

        // ===================================================================
        // Asset duplication methods
        // ===================================================================

        /// <summary>
        /// Duplicate an EBX asset using the DuplicationPlugin's extension system
        /// so that Res/Chunk resources are properly handled.
        /// Falls back to basic EBX duplication if no extension matches.
        /// </summary>
        private static EbxAssetEntry DuplicateWithExtension(EbxAssetEntry entry, string newName, FrostyTaskWindow task)
        {
            // Use reflection to instantiate the correct DuplicateAssetExtension
            var assembly = typeof(DuplicateAssetWindow).Assembly;
            var extensions = new Dictionary<string, DuplicationTool.DuplicateAssetExtension>();

            foreach (var type in assembly.GetTypes())
            {
                if (type.IsSubclassOf(typeof(DuplicationTool.DuplicateAssetExtension)) && !type.IsAbstract)
                {
                    try
                    {
                        var ext = (DuplicationTool.DuplicateAssetExtension)Activator.CreateInstance(type);
                        if (ext.AssetType != null)
                            extensions[ext.AssetType] = ext;
                    }
                    catch { }
                }
            }

            string key = "null";
            foreach (string typekey in extensions.Keys)
            {
                if (TypeLibrary.IsSubClassOf(entry.Type, typekey))
                {
                    key = typekey;
                    break;
                }
            }

            if (key == "null")
            {
                // Fall back to basic EBX-only duplication
                return DuplicateEbxAsset(entry, newName);
            }

            return extensions[key].DuplicateAsset(entry, newName, false, null);
        }

        /// <summary>
        /// Basic EBX-only duplication via serialize round-trip.
        /// </summary>
        private static EbxAssetEntry DuplicateEbxAsset(EbxAssetEntry entry, string newName)
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

        // ===================================================================
        // Reference rewriting (carried over from DeepDuplicateMenuExt)
        // ===================================================================

        /// <summary>
        /// Walks all PointerRef fields in an object and replaces any external references
        /// whose FileGuid and/or root ClassGuid are in the remap dictionaries.
        /// Returns the count of replaced references.
        /// </summary>
        internal static int RewriteReferences(object obj, Dictionary<Guid, Guid> guidMap)
        {
            return RewriteReferences(obj, guidMap, null);
        }

        internal static int RewriteReferences(object obj, Dictionary<Guid, Guid> guidMap, Dictionary<Guid, Guid> classGuidMap)
        {
            return RewriteReferences(obj, guidMap, classGuidMap, null);
        }

        private static int RewriteReferences(object obj, Dictionary<Guid, Guid> guidMap, Dictionary<Guid, Guid> classGuidMap, RewriteContext rewriteContext)
        {
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            return RewriteReferencesInternal(obj, guidMap, classGuidMap, rewriteContext, visited);
        }

        private static int RewriteReferencesInternal(object obj, Dictionary<Guid, Guid> guidMap, Dictionary<Guid, Guid> classGuidMap, RewriteContext rewriteContext, HashSet<object> visited)
        {
            if (obj == null)
                return 0;

            Type type = obj.GetType();
            if (type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(Guid))
                return 0;

            if (!type.IsValueType && !visited.Add(obj))
                return 0;

            int count = 0;

            if (obj is IList listObject)
            {
                return RewriteListReferences(listObject, guidMap, classGuidMap, rewriteContext, visited);
            }

            foreach (PropertyInfo pi in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                try
                {
                    if (!pi.CanRead || pi.GetIndexParameters().Length > 0)
                        continue;

                    if (pi.PropertyType == typeof(PointerRef))
                    {
                        PointerRef pr = (PointerRef)pi.GetValue(obj);
                        if (TryRewritePointerRef(pr, guidMap, classGuidMap, rewriteContext, out PointerRef newPointer))
                        {
                            if (TrySetPropertyOrBackingField(obj, pi, newPointer))
                                count++;
                        }
                    }
                    else
                    {
                        object value = pi.GetValue(obj);
                        if (value == null)
                            continue;

                        int before = count;

                        if (value is IList list)
                        {
                            count += RewriteListReferences(list, guidMap, classGuidMap, rewriteContext, visited);
                        }
                        else if (value is IDictionary dictionary)
                        {
                            count += RewriteDictionaryReferences(dictionary, guidMap, classGuidMap, rewriteContext, visited);
                        }
                        else
                        {
                            count += RewriteReferencesInternal(value, guidMap, classGuidMap, rewriteContext, visited);
                        }

                        if (pi.PropertyType.IsValueType && count > before)
                        {
                            // Value-types are copied when read through reflection; write them back when changed.
                            TrySetPropertyOrBackingField(obj, pi, value);
                        }
                    }
                }
                catch
                {
                    // Skip inaccessible properties
                }
            }

            foreach (FieldInfo fi in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                try
                {
                    if (fi.IsInitOnly || fi.IsStatic)
                        continue;

                    if (fi.FieldType == typeof(PointerRef))
                    {
                        PointerRef pr = (PointerRef)fi.GetValue(obj);
                        if (TryRewritePointerRef(pr, guidMap, classGuidMap, rewriteContext, out PointerRef newPointer))
                        {
                            fi.SetValue(obj, newPointer);
                            count++;
                        }
                    }
                    else
                    {
                        object value = fi.GetValue(obj);
                        if (value == null)
                            continue;

                        int before = count;

                        if (value is IList list)
                        {
                            count += RewriteListReferences(list, guidMap, classGuidMap, rewriteContext, visited);
                        }
                        else if (value is IDictionary dictionary)
                        {
                            count += RewriteDictionaryReferences(dictionary, guidMap, classGuidMap, rewriteContext, visited);
                        }
                        else
                        {
                            count += RewriteReferencesInternal(value, guidMap, classGuidMap, rewriteContext, visited);
                        }

                        if (fi.FieldType.IsValueType && count > before)
                        {
                            // Value-types are copied when read through reflection; write them back when changed.
                            fi.SetValue(obj, value);
                        }
                    }
                }
                catch
                {
                    // Skip inaccessible fields
                }
            }

            return count;
        }

        private static int RewriteListReferences(IList list, Dictionary<Guid, Guid> guidMap, Dictionary<Guid, Guid> classGuidMap, RewriteContext rewriteContext, HashSet<object> visited)
        {
            int count = 0;

            for (int i = 0; i < list.Count; i++)
            {
                try
                {
                    object item = list[i];
                    if (item == null)
                        continue;

                    if (item is PointerRef pointer)
                    {
                        if (TryRewritePointerRef(pointer, guidMap, classGuidMap, rewriteContext, out PointerRef newPointer))
                        {
                            list[i] = newPointer;
                            count++;
                        }
                        continue;
                    }

                    int before = count;
                    count += RewriteReferencesInternal(item, guidMap, classGuidMap, rewriteContext, visited);

                    if (item.GetType().IsValueType && count > before)
                        list[i] = item;
                }
                catch
                {
                    // Skip inaccessible list values
                }
            }

            return count;
        }

        private static int RewriteDictionaryReferences(IDictionary dictionary, Dictionary<Guid, Guid> guidMap, Dictionary<Guid, Guid> classGuidMap, RewriteContext rewriteContext, HashSet<object> visited)
        {
            int count = 0;
            object[] keys = new object[dictionary.Keys.Count];
            dictionary.Keys.CopyTo(keys, 0);

            foreach (object key in keys)
            {
                try
                {
                    object value = dictionary[key];
                    if (value == null)
                        continue;

                    if (value is PointerRef pointer)
                    {
                        if (TryRewritePointerRef(pointer, guidMap, classGuidMap, rewriteContext, out PointerRef newPointer))
                        {
                            dictionary[key] = newPointer;
                            count++;
                        }
                        continue;
                    }

                    int before = count;
                    count += RewriteReferencesInternal(value, guidMap, classGuidMap, rewriteContext, visited);

                    if (value.GetType().IsValueType && count > before)
                        dictionary[key] = value;
                }
                catch
                {
                    // Skip inaccessible dictionary values
                }
            }

            return count;
        }

        private static bool TryRewritePointerRef(PointerRef pointer, Dictionary<Guid, Guid> guidMap, Dictionary<Guid, Guid> classGuidMap, RewriteContext rewriteContext, out PointerRef rewrittenPointer)
        {
            rewrittenPointer = pointer;
            if (pointer.Type != PointerRefType.External)
                return false;

            Guid oldFileGuid = pointer.External.FileGuid;
            Guid newFileGuid = pointer.External.FileGuid;
            Guid newClassGuid = pointer.External.ClassGuid;
            bool changed = false;

            if (guidMap != null && guidMap.TryGetValue(pointer.External.FileGuid, out Guid remappedFileGuid)
                && remappedFileGuid != newFileGuid)
            {
                newFileGuid = remappedFileGuid;
                changed = true;
            }

            if (classGuidMap != null && classGuidMap.TryGetValue(pointer.External.ClassGuid, out Guid remappedClassGuid)
                && remappedClassGuid != newClassGuid)
            {
                newClassGuid = remappedClassGuid;
                changed = true;
            }

            if (!changed)
                return false;

            rewriteContext?.RegisterFileGuidChange(oldFileGuid, newFileGuid);

            EbxImportReference newRef = new EbxImportReference
            {
                FileGuid = newFileGuid,
                ClassGuid = newClassGuid
            };

            rewrittenPointer = new PointerRef(newRef);
            return true;
        }

        private static bool TrySetPropertyOrBackingField(object target, PropertyInfo property, object value)
        {
            try
            {
                if (property.CanWrite)
                {
                    property.SetValue(target, value);
                    return true;
                }
            }
            catch
            {
                // Fall through to backing-field attempt.
            }

            try
            {
                string backingFieldName = $"<{property.Name}>k__BackingField";
                FieldInfo backingField = target.GetType().GetField(
                    backingFieldName,
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (backingField != null && !backingField.IsInitOnly && !backingField.IsStatic)
                {
                    backingField.SetValue(target, value);
                    return true;
                }
            }
            catch
            {
                // Ignore and report failure via return value.
            }

            return false;
        }

        private sealed class RewriteContext
        {
            public Dictionary<Guid, Guid> FileDependencyRemap { get; } = new Dictionary<Guid, Guid>();
            public HashSet<Guid> AddedDependencies { get; } = new HashSet<Guid>();

            public void RegisterFileGuidChange(Guid oldFileGuid, Guid newFileGuid)
            {
                if (oldFileGuid == Guid.Empty || newFileGuid == Guid.Empty)
                    return;

                if (oldFileGuid == newFileGuid)
                {
                    AddedDependencies.Add(newFileGuid);
                    return;
                }

                FileDependencyRemap[oldFileGuid] = newFileGuid;
                AddedDependencies.Add(newFileGuid);
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
