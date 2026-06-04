using Frosty.Core;
using Frosty.Core.Controls;
using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Flurry.Editor.Patches
{
    public static class FacePoserMappings
    {
        private static Dictionary<string, string> _guidToName;
        private static string _mappingFilePath;

        public static void EnsureLoaded()
        {
            if (_guidToName != null)
                return;

            _guidToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _mappingFilePath = GetMappingFilePath();

            if (File.Exists(_mappingFilePath))
            {
                try
                {
                    string json = File.ReadAllText(_mappingFilePath);
                    var mappings = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                    if (mappings != null)
                    {
                        foreach (var kvp in mappings)
                            _guidToName[kvp.Key] = kvp.Value;
                    }
                    App.Logger?.Log($"[Flurry] Loaded {_guidToName.Count} FacePoser mappings");
                }
                catch (Exception ex)
                {
                    App.Logger?.Log($"[Flurry] Failed to load FacePoser mappings: {ex.Message}");
                }
            }
            else
            {
                CreateTemplate();
            }
        }

        public static string Resolve(string guid)
        {
            EnsureLoaded();
            return _guidToName.TryGetValue(guid, out string name) ? name : null;
        }

        public static IReadOnlyDictionary<string, string> GetAll()
        {
            EnsureLoaded();
            return _guidToName;
        }

        public static void Reload()
        {
            _guidToName = null;
            EnsureLoaded();
        }

        public static string GetFilePath()
        {
            return _mappingFilePath ?? GetMappingFilePath();
        }

        private static void CreateTemplate()
        {
            try
            {
                string json = null;
                var assembly = Assembly.GetExecutingAssembly();
                string resourceName = "Flurry.Editor.Data.FacePoserMappings.json";
                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        using (StreamReader reader = new StreamReader(stream))
                            json = reader.ReadToEnd();
                    }
                }

                if (string.IsNullOrEmpty(json))
                {
                    var fallback = new Dictionary<string, string>
                    {
                        ["00000000-0000-0000-0000-000000000000"] = "None / Empty"
                    };
                    json = JsonConvert.SerializeObject(fallback, Formatting.Indented);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_mappingFilePath));
                File.WriteAllText(_mappingFilePath, json);
                App.Logger?.Log($"[Flurry] Created FacePoser mappings at {_mappingFilePath}");
            }
            catch (Exception ex)
            {
                App.Logger?.Log($"[Flurry] Failed to create mappings file: {ex.Message}");
            }
        }

        private static string GetMappingFilePath()
        {
            string editorDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location)
                               ?? AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(editorDir, "Plugins", "FlurryData", "FacePoserMappings.json");
        }
    }

    [HarmonyPatch(typeof(FrostyPropertyGridItem))]
    [HarmonyPatchCategory("flurry.editor")]
    public class FacePoserPropertyGridItemPatch
    {
        [HarmonyPatch(nameof(FrostyPropertyGridItem.OnApplyTemplate))]
        [HarmonyPostfix]
        public static void OnApplyTemplate_Postfix(FrostyPropertyGridItem __instance)
        {
            try
            {
                FrostyPropertyGridItemData item = __instance.DataContext as FrostyPropertyGridItemData;
                if (item == null)
                    return;

                if (!IsFacePoserAssetGuid(item))
                    return;

                FacePoserMappings.EnsureLoaded();

                TextBlock friendlyLabel = AddFriendlyNameOverlay(__instance, item);

                AddFacePoserContextMenuItems(__instance, item, friendlyLabel);
            }
            catch
            {
            }
        }

        private static bool IsFacePoserAssetGuid(FrostyPropertyGridItemData item)
        {
            var parent = item.Parent;
            return item.Name == "AssetGuid"
                && (parent?.Name == "FacePoserLibrary" || parent?.Parent?.Name == "FacePoserLibrary");
        }

        private static TextBlock AddFriendlyNameOverlay(FrostyPropertyGridItem gridItem, FrostyPropertyGridItemData item)
        {
            ContentControl valueControl = gridItem.Template?.FindName("PART_Value", gridItem) as ContentControl;
            if (valueControl == null)
                return null;

            string guidStr = item.Value?.ToString();
            if (string.IsNullOrEmpty(guidStr))
                return null;

            string friendlyName = FacePoserMappings.Resolve(guidStr);

            UIElement existingContent = valueControl.Content as UIElement;
            if (existingContent == null)
                return null;

            StackPanel wrapper = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };
            valueControl.Content = null;
            wrapper.Children.Add(existingContent);

            TextBlock label = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                FontStyle = FontStyles.Italic,
                FontSize = 11
            };
            UpdateFriendlyLabel(label, friendlyName);
            wrapper.Children.Add(label);

            valueControl.Content = wrapper;
            return label;
        }

        private static void UpdateFriendlyLabel(TextBlock label, string friendlyName)
        {
            if (friendlyName != null)
            {
                label.Text = $"  [{friendlyName}]";
                label.Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
            }
            else
            {
                label.Text = "  [Unknown - edit FacePoserMappings.json]";
                label.Foreground = new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78));
            }
        }

        private static void AddFacePoserContextMenuItems(FrostyPropertyGridItem gridItem, FrostyPropertyGridItemData item, TextBlock friendlyLabel)
        {
            ContextMenu cm = gridItem.ContextMenu;
            if (cm == null)
                return;

            cm.Items.Add(new Separator());

            MenuItem selectMenu = new MenuItem { Header = "Select FacePoser" };
            var mappings = FacePoserMappings.GetAll();
            foreach (var kvp in mappings)
            {
                string guid = kvp.Key;
                string name = kvp.Value;

                MenuItem mi = new MenuItem
                {
                    Header = $"{name}  ({guid})"
                };
                mi.Click += (s, e) => SetFacePoserValue(item, guid, friendlyLabel);
                selectMenu.Items.Add(mi);
            }

            if (selectMenu.Items.Count == 0)
            {
                selectMenu.Items.Add(new MenuItem
                {
                    Header = "(No mappings defined)",
                    IsEnabled = false
                });
            }
            cm.Items.Add(selectMenu);

            MenuItem reloadItem = new MenuItem { Header = "Reload FacePoser Mappings" };
            reloadItem.Click += (s, e) =>
            {
                FacePoserMappings.Reload();
                App.Logger?.Log("[Flurry] FacePoser mappings reloaded");
            };
            cm.Items.Add(reloadItem);

            MenuItem openFileItem = new MenuItem { Header = "Open FacePoser Mappings File" };
            openFileItem.Click += (s, e) =>
            {
                string path = FacePoserMappings.GetFilePath();
                if (File.Exists(path))
                {
                    System.Diagnostics.Process.Start(path);
                }
                else
                {
                    App.Logger?.Log($"[Flurry] Mappings file not found at: {path}");
                }
            };
            cm.Items.Add(openFileItem);
        }

        private static void SetFacePoserValue(FrostyPropertyGridItemData item, string guid, TextBlock friendlyLabel)
        {
            try
            {
                if (!Guid.TryParse(guid, out Guid parsedGuid))
                    return;

                item.Value = parsedGuid;
                if (friendlyLabel != null)
                    UpdateFriendlyLabel(friendlyLabel, FacePoserMappings.Resolve(parsedGuid.ToString()));
            }
            catch (Exception ex)
            {
                App.Logger?.Log($"[Flurry] Failed to set FacePoser GUID: {ex.Message}");
            }
        }
    }
}
