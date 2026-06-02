using Flurry.Editor.Windows;
using Frosty.Controls;
using Frosty.Core;
using Frosty.Hash;
using Frosty.Core.Windows;
using FrostySdk;
using FrostySdk.Attributes;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace Flurry.Editor
{
    public class MassBinExportMenuExt : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Flurry";
        public override string MenuItemName => "Mass Bin Export";
        public override ImageSource Icon => new ImageSourceConverter().ConvertFromString("pack://application:,,,/FrostyEditor;component/Images/Export.png") as ImageSource;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            List<EbxAssetEntry> modifiedAssets = App.AssetManager.EnumerateEbx("", modifiedOnly: true).ToList();
            List<ResAssetEntry> modifiedResAssets = App.AssetManager.EnumerateRes(modifiedOnly: true).ToList();
            List<ChunkAssetEntry> modifiedChunkAssets = App.AssetManager.EnumerateChunks(modifiedOnly: true).ToList();

            if (modifiedAssets.Count == 0 && modifiedResAssets.Count == 0 && modifiedChunkAssets.Count == 0)
            {
                FrostyMessageBox.Show("No modified assets to export.", "Mass Bin Export", MessageBoxButton.OK);
                return;
            }

            var fbd = new VistaFolderBrowserDialog("Select Export Folder");
            if (!fbd.ShowDialog())
                return;

            string basePath = fbd.SelectedPath;
            int exported = 0;
            int skipped = 0;
            int exportedRes = 0;
            int skippedRes = 0;
            int exportedChunks = 0;
            int skippedChunks = 0;

            FrostyTaskWindow.Show("Mass Bin Export", "", (task) =>
            {
                for (int i = 0; i < modifiedAssets.Count; i++)
                {
                    EbxAssetEntry entry = modifiedAssets[i];
                    task.Update($"Exporting {entry.Filename} ({i + 1}/{modifiedAssets.Count})");

                    try
                    {
                        string relativePath = entry.Name.Replace("/", "\\");
                        string fullPath = Path.Combine(basePath, relativePath + ".bin");
                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

                        // Reuse Frosty's built-in per-asset binary export behavior.
                        AssetDefinition assetDefinition = App.PluginManager.GetAssetDefinition(entry.Type) ?? new AssetDefinition();
                        if (assetDefinition.Export(entry, fullPath, "bin"))
                            exported++;
                        else
                            skipped++;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.LogWarning($"Failed to export {entry.Name}: {ex.Message}");
                        skipped++;
                    }
                }

                for (int i = 0; i < modifiedResAssets.Count; i++)
                {
                    ResAssetEntry entry = modifiedResAssets[i];
                    task.Update($"Exporting RES {entry.Filename} ({i + 1}/{modifiedResAssets.Count})");

                    try
                    {
                        string relativePath = entry.Name.Replace("/", "\\");
                        string fullPath = Path.Combine(basePath, "res", relativePath + ".res.bin");
                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

                        using (NativeWriter writer = new NativeWriter(new FileStream(fullPath, FileMode.Create, FileAccess.Write)))
                            writer.Write(NativeReader.ReadInStream(App.AssetManager.GetRes(entry)));

                        exportedRes++;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.LogWarning($"Failed to export RES {entry.Name}: {ex.Message}");
                        skippedRes++;
                    }
                }

                for (int i = 0; i < modifiedChunkAssets.Count; i++)
                {
                    ChunkAssetEntry entry = modifiedChunkAssets[i];
                    string chunkLabel = string.IsNullOrWhiteSpace(entry.Name) ? entry.Id.ToString() : entry.Name;
                    task.Update($"Exporting Chunk {chunkLabel} ({i + 1}/{modifiedChunkAssets.Count})");

                    try
                    {
                        string fullPath = Path.Combine(basePath, "chunks", entry.Id.ToString("D") + ".chunk.bin");
                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

                        using (NativeWriter writer = new NativeWriter(new FileStream(fullPath, FileMode.Create, FileAccess.Write)))
                            writer.Write(NativeReader.ReadInStream(App.AssetManager.GetChunk(entry)));

                        exportedChunks++;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.LogWarning($"Failed to export Chunk {chunkLabel}: {ex.Message}");
                        skippedChunks++;
                    }
                }
            });

            App.Logger.Log(
                $"Mass Bin Export complete: EBX {exported} exported, RES {exportedRes} exported, Chunks {exportedChunks} exported, skipped {skipped + skippedRes + skippedChunks}.");
        });
    }

    public class MassBinImportMenuExt : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Flurry";
        public override string MenuItemName => "Mass Bin Import";
        public override ImageSource Icon => new ImageSourceConverter().ConvertFromString("pack://application:,,,/FrostyEditor;component/Images/Import.png") as ImageSource;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            var fbd = new VistaFolderBrowserDialog("Select Folder Containing .bin Files");
            if (!fbd.ShowDialog())
                return;

            string basePath = fbd.SelectedPath;
            string[] allBinFiles = Directory.GetFiles(basePath, "*.bin", SearchOption.AllDirectories);
            if (allBinFiles.Length == 0)
            {
                FrostyMessageBox.Show("No .bin files found in selected folder.", "Mass Bin Import", MessageBoxButton.OK);
                return;
            }

            List<(string binPath, string assetName, EbxAssetEntry entry)> matchedEbx = new List<(string, string, EbxAssetEntry)>();
            List<(string binPath, string resName, ResAssetEntry entry)> matchedRes = new List<(string, string, ResAssetEntry)>();
            List<(string binPath, Guid chunkId, ChunkAssetEntry entry)> matchedChunks = new List<(string, Guid, ChunkAssetEntry)>();
            List<string> unmatched = new List<string>();
            int unknownTypeCount = 0;

            foreach (string binFile in allBinFiles)
            {
                string relativePath = binFile.Substring(basePath.Length).TrimStart('\\', '/');
                string normalizedRelative = relativePath.Replace("\\", "/");
                string lowerRelative = normalizedRelative.ToLowerInvariant();

                if (lowerRelative.StartsWith("res/") && lowerRelative.EndsWith(".res.bin"))
                {
                    string resName = normalizedRelative.Substring("res/".Length);
                    resName = resName.Substring(0, resName.Length - ".res.bin".Length).Replace("\\", "/");
                    ResAssetEntry resEntry = App.AssetManager.GetResEntry(resName) ?? App.AssetManager.GetResEntry(resName.ToLowerInvariant());
                    if (resEntry != null)
                        matchedRes.Add((binFile, resName, resEntry));
                    else
                        unmatched.Add("RES:" + resName);
                    continue;
                }

                if (lowerRelative.StartsWith("chunks/") && lowerRelative.EndsWith(".chunk.bin"))
                {
                    string chunkFileName = Path.GetFileName(normalizedRelative);
                    string chunkIdString = chunkFileName.Substring(0, chunkFileName.Length - ".chunk.bin".Length);
                    if (Guid.TryParse(chunkIdString, out Guid chunkId))
                    {
                        ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(chunkId);
                        if (chunkEntry != null)
                            matchedChunks.Add((binFile, chunkId, chunkEntry));
                        else
                            unmatched.Add("CHUNK:" + chunkIdString);
                    }
                    else
                    {
                        unmatched.Add("CHUNK:" + chunkIdString);
                    }
                    continue;
                }

                if (lowerRelative.EndsWith(".res.bin") || lowerRelative.EndsWith(".chunk.bin"))
                {
                    unknownTypeCount++;
                    continue;
                }

                string assetName = Path.ChangeExtension(normalizedRelative, null).Replace("\\", "/");
                EbxAssetEntry ebxEntry = App.AssetManager.GetEbxEntry(assetName);
                if (ebxEntry != null)
                    matchedEbx.Add((binFile, assetName, ebxEntry));
                else
                    unmatched.Add("EBX:" + assetName);
            }

            int totalMatched = matchedEbx.Count + matchedRes.Count + matchedChunks.Count;
            if (totalMatched == 0)
            {
                FrostyMessageBox.Show("No .bin files matched any assets in the project.\n\nMake sure the folder structure matches the asset paths.", "Mass Bin Import", MessageBoxButton.OK);
                return;
            }

            List<string> identityMismatches = new List<string>();
            foreach (var (binPath, assetName, entry) in matchedEbx)
            {
                if (TryDetectIdentityMismatch(binPath, entry, out string mismatchDetails))
                    identityMismatches.Add($"{assetName} -> {mismatchDetails}");
            }

            string message = $"Found {totalMatched} matching asset(s) to import.";
            message += $"\n - EBX: {matchedEbx.Count}";
            message += $"\n - RES: {matchedRes.Count}";
            message += $"\n - Chunks: {matchedChunks.Count}";
            if (unmatched.Count > 0)
                message += $"\n\n{unmatched.Count} file(s) did not match any asset and will be skipped.";
            if (unknownTypeCount > 0)
                message += $"\n\n{unknownTypeCount} file(s) had unknown bulk-bin format and were ignored.";
            if (identityMismatches.Count > 0)
            {
                StringBuilder mismatchPreview = new StringBuilder();
                int previewCount = Math.Min(12, identityMismatches.Count);
                for (int i = 0; i < previewCount; i++)
                    mismatchPreview.AppendLine(" - " + identityMismatches[i]);
                if (identityMismatches.Count > previewCount)
                    mismatchPreview.AppendLine($" - ... and {identityMismatches.Count - previewCount} more");

                message += "\n\nPotential identity mismatches detected (bin GUID differs from target asset):\n";
                message += mismatchPreview.ToString();
            }

            if (FrostyMessageBox.Show(message + "\n\nProceed with import?", "Mass Bin Import", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;

            int importedEbx = 0;
            int importedRes = 0;
            int importedChunks = 0;
            int skippedEbx = 0;
            int skippedRes = 0;
            int skippedChunks = 0;

            FrostyTaskWindow.Show("Mass Bin Import", "", (task) =>
            {
                List<EbxAssetEntry> allEbxEntries = App.AssetManager.EnumerateEbx("", modifiedOnly: false).ToList();
                List<ResAssetEntry> allResEntries = App.AssetManager.EnumerateRes(modifiedOnly: false).ToList();
                Dictionary<string, EbxAssetEntry> ebxByName = new Dictionary<string, EbxAssetEntry>(StringComparer.OrdinalIgnoreCase);
                Dictionary<int, ResAssetEntry> resByNameHash = new Dictionary<int, ResAssetEntry>();
                Dictionary<AssetEntry, List<EbxAssetEntry>> ebxOwnersByLinkedAsset = BuildEbxOwnerIndex(allEbxEntries);
                foreach (EbxAssetEntry ebx in allEbxEntries)
                {
                    if (ebx != null && !string.IsNullOrWhiteSpace(ebx.Name) && !ebxByName.ContainsKey(ebx.Name))
                        ebxByName.Add(ebx.Name, ebx);
                }
                foreach (ResAssetEntry res in allResEntries)
                {
                    if (res == null || string.IsNullOrWhiteSpace(res.Name))
                        continue;

                    int hash = Fnv1.HashString(res.Name.ToLowerInvariant());
                    if (!resByNameHash.ContainsKey(hash))
                        resByNameHash.Add(hash, res);
                }

                for (int i = 0; i < matchedEbx.Count; i++)
                {
                    var (binPath, assetName, entry) = matchedEbx[i];
                    task.Update($"Importing EBX {entry.Filename} ({i + 1}/{matchedEbx.Count})");

                    try
                    {
                        AssetDefinition assetDefinition = App.PluginManager.GetAssetDefinition(entry.Type) ?? new AssetDefinition();
                        List<AssetImportType> importTypes = new List<AssetImportType>();
                        assetDefinition.GetSupportedImportTypes(importTypes);

                        AssetImportType binImportType = importTypes.FirstOrDefault(t =>
                            string.Equals(t.Extension, "bin", StringComparison.OrdinalIgnoreCase)
                            && t.Description != null
                            && t.Description.IndexOf("Data Only", StringComparison.OrdinalIgnoreCase) >= 0)
                            ;
                        if (string.IsNullOrWhiteSpace(binImportType.Extension))
                        {
                            binImportType = importTypes.FirstOrDefault(t => string.Equals(t.Extension, "bin", StringComparison.OrdinalIgnoreCase));
                        }

                        if (string.IsNullOrWhiteSpace(binImportType.Extension))
                        {
                            App.Logger.LogWarning($"Skipping {assetName}: no bin import type available.");
                            skippedEbx++;
                            continue;
                        }

                        if (assetDefinition.Import(entry, binPath, binImportType))
                            importedEbx++;
                        else
                            skippedEbx++;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.LogWarning($"Failed to import {assetName}: {ex.Message}");
                        skippedEbx++;
                    }
                }

                for (int i = 0; i < matchedRes.Count; i++)
                {
                    var (binPath, resName, entry) = matchedRes[i];
                    task.Update($"Importing RES {entry.Filename} ({i + 1}/{matchedRes.Count})");

                    try
                    {
                        byte[] data = NativeReader.ReadInStream(new FileStream(binPath, FileMode.Open, FileAccess.Read));
                        App.AssetManager.ModifyRes(entry.Name, data);
                        LinkIndexedEbxOwners(entry, ebxOwnersByLinkedAsset);
                        LinkEbxOwnerToRes(entry, ebxByName);
                        MarkLinkedAssetsDirty(entry);
                        importedRes++;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.LogWarning($"Failed to import RES {resName}: {ex.Message}");
                        skippedRes++;
                    }
                }

                for (int i = 0; i < matchedChunks.Count; i++)
                {
                    var (binPath, chunkId, entry) = matchedChunks[i];
                    string chunkLabel = string.IsNullOrWhiteSpace(entry.Name) ? chunkId.ToString("D") : entry.Name;
                    task.Update($"Importing Chunk {chunkLabel} ({i + 1}/{matchedChunks.Count})");

                    try
                    {
                        byte[] data = NativeReader.ReadInStream(new FileStream(binPath, FileMode.Open, FileAccess.Read));
                        if (App.AssetManager.ModifyChunk(chunkId, data))
                        {
                            LinkIndexedEbxOwners(entry, ebxOwnersByLinkedAsset);
                            LinkResOwnerToChunk(entry, resByNameHash);
                            LinkEbxOwnerToChunkViaRes(entry, ebxByName, resByNameHash);
                            MarkLinkedAssetsDirty(entry);
                            importedChunks++;
                        }
                        else
                            skippedChunks++;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.LogWarning($"Failed to import Chunk {chunkLabel}: {ex.Message}");
                        skippedChunks++;
                    }
                }
            });

            App.Logger.Log(
                $"Mass Bin Import complete: EBX {importedEbx} imported, RES {importedRes} imported, Chunks {importedChunks} imported, skipped {skippedEbx + skippedRes + skippedChunks}.");
            App.EditorWindow.DataExplorer.RefreshAll();
        });

        private static bool TryDetectIdentityMismatch(string binPath, EbxAssetEntry targetEntry, out string details)
        {
            details = string.Empty;
            if (string.IsNullOrWhiteSpace(binPath) || targetEntry == null)
                return false;

            try
            {
                EbxAsset targetAsset = App.AssetManager.GetEbx(targetEntry);
                if (targetAsset == null || targetAsset.FileGuid == Guid.Empty)
                    return false;

                using (EbxReader reader = EbxReader.CreateReader(new FileStream(binPath, FileMode.Open, FileAccess.Read), App.FileSystem, true))
                {
                    EbxAsset sourceAsset = reader.ReadAsset<EbxAsset>();
                    if (sourceAsset == null || sourceAsset.FileGuid == Guid.Empty)
                        return false;

                    if (sourceAsset.FileGuid != targetAsset.FileGuid)
                    {
                        details = $"bin GUID {sourceAsset.FileGuid} vs target GUID {targetAsset.FileGuid}";
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                details = "unable to validate bin identity (" + ex.Message + ")";
                return true;
            }

            return false;
        }

        private static void MarkLinkedAssetsDirty(AssetEntry entry)
        {
            if (entry == null || entry.LinkedAssets == null || entry.LinkedAssets.Count == 0)
                return;

            foreach (AssetEntry linked in entry.LinkedAssets)
            {
                if (linked != null)
                    linked.IsDirty = true;
            }
        }

        private static void LinkEbxOwnerToRes(ResAssetEntry resEntry, IDictionary<string, EbxAssetEntry> ebxByName)
        {
            if (resEntry == null || string.IsNullOrWhiteSpace(resEntry.Name) || ebxByName == null)
                return;

            if (!ebxByName.TryGetValue(resEntry.Name, out EbxAssetEntry ownerEbx) || ownerEbx == null)
                return;

            ownerEbx.LinkAsset(resEntry);
            ownerEbx.IsDirty = true;
        }

        private static void LinkResOwnerToChunk(ChunkAssetEntry chunkEntry, IDictionary<int, ResAssetEntry> resByNameHash)
        {
            if (chunkEntry == null || resByNameHash == null)
                return;

            int hash = GetChunkOwnerHash(chunkEntry);
            if (hash == 0)
                return;

            if (!resByNameHash.TryGetValue(hash, out ResAssetEntry ownerRes) || ownerRes == null)
                return;

            ownerRes.LinkAsset(chunkEntry);
            ownerRes.IsDirty = true;
        }

        private static void LinkEbxOwnerToChunkViaRes(ChunkAssetEntry chunkEntry, IDictionary<string, EbxAssetEntry> ebxByName, IDictionary<int, ResAssetEntry> resByNameHash)
        {
            if (chunkEntry == null || ebxByName == null || resByNameHash == null)
                return;

            int hash = GetChunkOwnerHash(chunkEntry);
            if (hash == 0)
                return;

            if (!resByNameHash.TryGetValue(hash, out ResAssetEntry ownerRes) || ownerRes == null || string.IsNullOrWhiteSpace(ownerRes.Name))
                return;

            if (!ebxByName.TryGetValue(ownerRes.Name, out EbxAssetEntry ownerEbx) || ownerEbx == null)
                return;

            ownerEbx.LinkAsset(ownerRes);
            ownerEbx.IsDirty = true;
        }

        private static int GetChunkOwnerHash(ChunkAssetEntry chunkEntry)
        {
            if (chunkEntry == null)
                return 0;

            if (chunkEntry.ModifiedEntry != null && chunkEntry.ModifiedEntry.H32 != 0)
                return chunkEntry.ModifiedEntry.H32;

            return chunkEntry.H32;
        }

        private static Dictionary<AssetEntry, List<EbxAssetEntry>> BuildEbxOwnerIndex(IEnumerable<EbxAssetEntry> ebxEntries)
        {
            Dictionary<AssetEntry, List<EbxAssetEntry>> ownersByLinked = new Dictionary<AssetEntry, List<EbxAssetEntry>>();
            if (ebxEntries == null)
                return ownersByLinked;

            foreach (EbxAssetEntry owner in ebxEntries)
            {
                if (owner?.LinkedAssets == null || owner.LinkedAssets.Count == 0)
                    continue;

                foreach (AssetEntry linked in owner.LinkedAssets)
                {
                    IndexLinkedOwner(owner, linked, ownersByLinked, maxDepth: 3);
                }
            }

            return ownersByLinked;
        }

        private static void IndexLinkedOwner(EbxAssetEntry owner, AssetEntry linked, IDictionary<AssetEntry, List<EbxAssetEntry>> ownersByLinked, int maxDepth)
        {
            if (owner == null || linked == null || ownersByLinked == null || maxDepth < 1)
                return;

            HashSet<AssetEntry> visited = new HashSet<AssetEntry>();
            Queue<(AssetEntry entry, int depth)> queue = new Queue<(AssetEntry entry, int depth)>();
            queue.Enqueue((linked, 1));

            while (queue.Count > 0)
            {
                (AssetEntry current, int depth) = queue.Dequeue();
                if (current == null || !visited.Add(current))
                    continue;

                if (!ownersByLinked.TryGetValue(current, out List<EbxAssetEntry> owners))
                {
                    owners = new List<EbxAssetEntry>();
                    ownersByLinked[current] = owners;
                }
                if (!owners.Contains(owner))
                    owners.Add(owner);

                if (depth >= maxDepth || current.LinkedAssets == null || current.LinkedAssets.Count == 0)
                    continue;

                foreach (AssetEntry nested in current.LinkedAssets)
                {
                    if (nested != null)
                        queue.Enqueue((nested, depth + 1));
                }
            }
        }

        private static void LinkIndexedEbxOwners(AssetEntry importedAsset, IDictionary<AssetEntry, List<EbxAssetEntry>> ownersByLinked)
        {
            if (importedAsset == null || ownersByLinked == null)
                return;

            if (!ownersByLinked.TryGetValue(importedAsset, out List<EbxAssetEntry> owners) || owners == null || owners.Count == 0)
                return;

            foreach (EbxAssetEntry owner in owners)
            {
                if (owner == null)
                    continue;
                owner.LinkAsset(importedAsset);
                owner.IsDirty = true;
            }
        }
    }
}
