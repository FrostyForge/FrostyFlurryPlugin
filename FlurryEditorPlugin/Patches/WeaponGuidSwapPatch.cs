using Frosty.Core;
using Frosty.Core.Controls;
using HarmonyLib;
using Newtonsoft.Json;
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
    public static class WeaponMappings
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
                    App.Logger?.Log($"[Flurry] Loaded {_guidToName.Count} weapon mappings");
                }
                catch (Exception ex)
                {
                    App.Logger?.Log($"[Flurry] Failed to load weapon mappings: {ex.Message}");
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
                string resourceName = "Flurry.Editor.Data.WeaponMappings.json";
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
                App.Logger?.Log($"[Flurry] Created weapon mappings at {_mappingFilePath}");
            }
            catch (Exception ex)
            {
                App.Logger?.Log($"[Flurry] Failed to create weapon mappings file: {ex.Message}");
            }
        }

        private static string GetMappingFilePath()
        {
            string editorDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location)
                               ?? AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(editorDir, "Plugins", "FlurryData", "WeaponMappings.json");
        }
    }

    [HarmonyPatch(typeof(FrostyPropertyGridItem))]
    [HarmonyPatchCategory("flurry.editor")]
    public class WeaponPropertyGridItemPatch
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

                if (!IsWeaponMeshField(item))
                    return;

                WeaponMappings.EnsureLoaded();

                TextBlock friendlyLabel = AddFriendlyNameOverlay(__instance, item);

                AddWeaponContextMenuItems(__instance, item, friendlyLabel);
            }
            catch
            {
            }
        }

        private static bool IsWeaponMeshField(FrostyPropertyGridItemData item)
        {
            var current = item.Parent;
            bool hasWeaponStates = false;
            string immediateParentName = null;

            while (current != null)
            {
                if (string.Equals(current.Name, "WeaponStates", StringComparison.OrdinalIgnoreCase))
                {
                    hasWeaponStates = true;
                    break;
                }
                if (immediateParentName == null)
                    immediateParentName = current.Name;
                current = current.Parent;
            }

            if (!hasWeaponStates)
                return false;

            if (string.Equals(item.Name, "AssetGuid", StringComparison.OrdinalIgnoreCase))
                return true;

            if (item.IsPointerRef)
            {
                string lower = item.Name?.ToLowerInvariant();
                if (lower == "weapon" || lower == "weapon3p" || lower == "weapon1p")
                    return true;
            }

            return false;
        }

        private static TextBlock AddFriendlyNameOverlay(FrostyPropertyGridItem gridItem, FrostyPropertyGridItemData item)
        {
            ContentControl valueControl = gridItem.Template?.FindName("PART_Value", gridItem) as ContentControl;
            if (valueControl == null)
                return null;

            string guidStr = item.Value?.ToString();
            if (string.IsNullOrEmpty(guidStr))
                return null;

            string friendlyName = WeaponMappings.Resolve(guidStr);

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
                label.Text = "  [Unknown - edit WeaponMappings.json]";
                label.Foreground = new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78));
            }
        }

        private static readonly (string Prefix, string Category)[] WeaponCategories =
        {
            ("w_pistol_", "Pistols"),
            ("w_blasterrifle_", "Assault Rifles"),
            ("w_heavyblaster_", "Heavy Weapons"),
            ("w_longrange_", "Sniper Rifles"),
            ("w_shortrange_", "Shotguns"),
            ("w_gadget_", "Gadgets"),
            ("w_melee_", "Melee"),
            ("w_special_", "Special"),
            ("w_grenadelauncher_", "Grenade Launchers"),
            ("w_iondisruptor_", "Ion Disruptors"),
            ("w_arc_", "ARC Weapons"),
            ("w_droideka_", "Droideka"),
            ("w_ewokbow-", "Ewok Bow"),
            ("w_bermudacop_", "Bermuda Cop"),
            ("w_isbagent_", "ISB Agent"),
            ("w_mode3_", "Mode 3"),
            ("w_rangeweapon_", "Range Weapons"),
            ("w_sentry_", "Sentry"),
            ("w_nest_", "Nest"),
            ("w_ability_", "Abilities"),
            ("w_ewok_sling", "Ewok Sling"),
            ("w_iden_", "Iden"),
            ("w_blasterrifle_e5", "E5 Blaster"),
        };

        private static string GetCategory(string name)
        {
            var parts = name.Split(',');
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                foreach (var (prefix, category) in WeaponCategories)
                {
                    if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return category;
                }
            }
            return "Other";
        }

        private static void AddWeaponContextMenuItems(FrostyPropertyGridItem gridItem, FrostyPropertyGridItemData item, TextBlock friendlyLabel)
        {
            ContextMenu cm = gridItem.ContextMenu;
            if (cm == null)
                return;

            cm.Items.Add(new Separator());

            var mappings = WeaponMappings.GetAll();
            var categorized = new Dictionary<string, List<(string guid, string name)>>();

            foreach (var kvp in mappings)
            {
                string guid = kvp.Key;
                string name = kvp.Value;

                var parts = name.Split(new[] { ", " }, StringSplitOptions.None);
                foreach (var part in parts)
                {
                    string cleanName = part.Trim();
                    if (string.IsNullOrEmpty(cleanName))
                        continue;
                    string cat = GetCategory(cleanName);
                    if (!categorized.ContainsKey(cat))
                        categorized[cat] = new List<(string, string)>();
                    categorized[cat].Add((guid, cleanName));
                }
            }

            foreach (var cat in categorized.OrderBy(c => c.Key))
            {
                var catMenu = new MenuItem { Header = cat.Key };
                var sortedItems = cat.Value.OrderBy(w => w.name).ToList();
                for (int i = 0; i < sortedItems.Count; i++)
                {
                    var (guid, name) = sortedItems[i];
                    string capturedGuid = guid;
                    var mi = new MenuItem { Header = name };
                    mi.ToolTip = guid;
                    mi.Click += (s, e) => SetWeaponValue(item, capturedGuid, friendlyLabel);
                    catMenu.Items.Add(mi);
                }
                if (sortedItems.Count > 20)
                {
                    catMenu.SubmenuOpened += (s, e) =>
                    {
                        catMenu.ApplyTemplate();
                        var popup = catMenu.Template?.FindName("PART_Popup", catMenu) as System.Windows.Controls.Primitives.Popup;
                        if (popup?.Child is FrameworkElement fe)
                        {
                            fe.MaxHeight = 400;
                        }
                    };
                }
                cm.Items.Add(catMenu);
            }

            cm.Items.Add(new Separator());

            MenuItem reloadItem = new MenuItem { Header = "Reload Weapon Mappings" };
            reloadItem.Click += (s, e) =>
            {
                WeaponMappings.Reload();
                App.Logger?.Log("[Flurry] Weapon mappings reloaded");
            };
            cm.Items.Add(reloadItem);

            MenuItem openFileItem = new MenuItem { Header = "Open Weapon Mappings File" };
            openFileItem.Click += (s, e) =>
            {
                string path = WeaponMappings.GetFilePath();
                if (File.Exists(path))
                {
                    System.Diagnostics.Process.Start(path);
                }
                else
                {
                    App.Logger?.Log($"[Flurry] Weapon mappings file not found at: {path}");
                }
            };
            cm.Items.Add(openFileItem);
        }

        private static void SetWeaponValue(FrostyPropertyGridItemData item, string guid, TextBlock friendlyLabel)
        {
            try
            {
                if (!Guid.TryParse(guid, out Guid parsedGuid))
                    return;

                item.Value = parsedGuid;
                if (friendlyLabel != null)
                    UpdateFriendlyLabel(friendlyLabel, WeaponMappings.Resolve(parsedGuid.ToString()));
            }
            catch (Exception ex)
            {
                App.Logger?.Log($"[Flurry] Failed to set weapon GUID: {ex.Message}");
            }
        }

    }
}
