using Frosty.Core;
using Frosty.Core.Mod;
using MM = FrostyModManager;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Flurry.Manager
{
    internal static class ConflictAssetOverrideRules
    {
        private const string ConfigKeyPrefix = "Flurry.AssetOverrideRules.";

        private sealed class LaunchContext
        {
            public Dictionary<string, string> RulesByResourceKey;
            public HashSet<string> EnabledModNames;
        }

        private static readonly object launchContextLock = new object();
        private static LaunchContext activeLaunchContext;

        public static Dictionary<string, string> LoadPackRules(string packName)
        {
            Dictionary<string, string> rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string raw = Config.Get<string>(GetConfigKey(packName), string.Empty, ConfigScope.Game);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return rules;
            }

            try
            {
                Dictionary<string, string> parsed = JsonConvert.DeserializeObject<Dictionary<string, string>>(raw);
                if (parsed == null)
                {
                    return rules;
                }

                foreach (KeyValuePair<string, string> kvp in parsed)
                {
                    string key = NormalizeResourceKey(kvp.Key);
                    string preferredMod = kvp.Value?.Trim();
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(preferredMod))
                    {
                        continue;
                    }

                    rules[key] = preferredMod;
                }
            }
            catch
            {
                // Ignore invalid data and fall back to empty rules.
            }

            return rules;
        }

        public static void SavePackRules(string packName, IDictionary<string, string> rules)
        {
            if (rules == null || rules.Count == 0)
            {
                Config.Remove(GetConfigKey(packName), ConfigScope.Game);
                Config.Save();
                return;
            }

            Dictionary<string, string> sanitized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kvp in rules)
            {
                string key = NormalizeResourceKey(kvp.Key);
                string preferredMod = kvp.Value?.Trim();
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(preferredMod))
                {
                    continue;
                }

                sanitized[key] = preferredMod;
            }

            if (sanitized.Count == 0)
            {
                Config.Remove(GetConfigKey(packName), ConfigScope.Game);
            }
            else
            {
                Config.Add(GetConfigKey(packName), JsonConvert.SerializeObject(sanitized), ConfigScope.Game);
            }

            Config.Save();
        }

        public static bool SetRule(string packName, string resourceKey, string preferredMod)
        {
            string normalizedKey = NormalizeResourceKey(resourceKey);
            string normalizedPreferredMod = preferredMod?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedKey) || string.IsNullOrWhiteSpace(normalizedPreferredMod))
            {
                return false;
            }

            Dictionary<string, string> rules = LoadPackRules(packName);
            bool changed = !rules.TryGetValue(normalizedKey, out string existing)
                || !string.Equals(existing, normalizedPreferredMod, StringComparison.OrdinalIgnoreCase);
            if (!changed)
            {
                return false;
            }

            rules[normalizedKey] = normalizedPreferredMod;
            SavePackRules(packName, rules);
            return true;
        }

        public static bool ClearRule(string packName, string resourceKey)
        {
            string normalizedKey = NormalizeResourceKey(resourceKey);
            if (string.IsNullOrWhiteSpace(normalizedKey))
            {
                return false;
            }

            Dictionary<string, string> rules = LoadPackRules(packName);
            if (!rules.Remove(normalizedKey))
            {
                return false;
            }

            SavePackRules(packName, rules);
            return true;
        }

        public static bool TryGetPreferredMod(IDictionary<string, string> rulesByResourceKey, string resourceKey, out string preferredMod)
        {
            preferredMod = null;
            if (rulesByResourceKey == null)
            {
                return false;
            }

            string normalizedKey = NormalizeResourceKey(resourceKey);
            if (string.IsNullOrWhiteSpace(normalizedKey))
            {
                return false;
            }

            if (!rulesByResourceKey.TryGetValue(normalizedKey, out string resolved))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(resolved))
            {
                return false;
            }

            preferredMod = resolved.Trim();
            return preferredMod.Length != 0;
        }

        public static string BuildResourceKey(string resourceType, string resourceName)
        {
            if (string.IsNullOrWhiteSpace(resourceType) || string.IsNullOrWhiteSpace(resourceName))
            {
                return string.Empty;
            }

            return (resourceType.Trim().ToLowerInvariant() + "/" + resourceName.Trim().ToLowerInvariant());
        }

        public static string BuildResourceKey(BaseModResource resource)
        {
            if (resource == null)
            {
                return string.Empty;
            }

            string resourceType = resource.Type.ToString();
            string resourceName = resource.Name;

            if (!string.IsNullOrWhiteSpace(resource.UserData))
            {
                string[] parts = resource.UserData.Split(';');
                if (parts.Length >= 2)
                {
                    resourceType = parts[0];
                    resourceName = parts[1];
                }
            }

            return BuildResourceKey(resourceType, resourceName);
        }

        public static void ActivateLaunchContext(string packName, IEnumerable<MM.FrostyAppliedMod> appliedMods)
        {
            Dictionary<string, string> rules = LoadPackRules(packName);
            HashSet<string> enabledModNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (MM.FrostyAppliedMod appliedMod in appliedMods ?? Enumerable.Empty<MM.FrostyAppliedMod>())
            {
                if (appliedMod == null || !appliedMod.IsFound || !appliedMod.IsEnabled || appliedMod.Mod == null)
                {
                    continue;
                }

                string name = ResolveAppliedModDisplayName(appliedMod);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    enabledModNames.Add(name);
                }
            }

            lock (launchContextLock)
            {
                activeLaunchContext = new LaunchContext
                {
                    RulesByResourceKey = rules,
                    EnabledModNames = enabledModNames
                };
            }
        }

        public static void ClearLaunchContext()
        {
            lock (launchContextLock)
            {
                activeLaunchContext = null;
            }
        }

        public static bool ShouldKeepResourceForLaunch(FrostyMod mod, BaseModResource resource)
        {
            if (mod == null || resource == null)
            {
                return true;
            }

            LaunchContext context;
            lock (launchContextLock)
            {
                context = activeLaunchContext;
            }

            if (context == null || context.RulesByResourceKey == null || context.RulesByResourceKey.Count == 0)
            {
                return true;
            }

            string resourceKey = BuildResourceKey(resource);
            if (!TryGetPreferredMod(context.RulesByResourceKey, resourceKey, out string preferredMod))
            {
                return true;
            }

            // Ignore stale rules when preferred mod is not currently enabled in the pack.
            if (!context.EnabledModNames.Contains(preferredMod))
            {
                return true;
            }

            string currentModName = ResolveModDisplayName(mod);
            if (string.IsNullOrWhiteSpace(currentModName))
            {
                return true;
            }

            return string.Equals(currentModName, preferredMod, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetConfigKey(string packName)
        {
            string safePackName = string.IsNullOrWhiteSpace(packName) ? "Default" : packName.Trim();
            return ConfigKeyPrefix + safePackName;
        }

        private static string NormalizeResourceKey(string resourceKey)
        {
            if (string.IsNullOrWhiteSpace(resourceKey))
            {
                return string.Empty;
            }

            return resourceKey.Trim().ToLowerInvariant();
        }

        private static string ResolveAppliedModDisplayName(MM.FrostyAppliedMod appliedMod)
        {
            if (appliedMod == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(appliedMod.ModName))
            {
                return appliedMod.ModName;
            }

            if (appliedMod.Mod != null && appliedMod.Mod.ModDetails != null && !string.IsNullOrWhiteSpace(appliedMod.Mod.ModDetails.Title))
            {
                return appliedMod.Mod.ModDetails.Title;
            }

            return string.Empty;
        }

        private static string ResolveModDisplayName(FrostyMod mod)
        {
            if (mod == null)
            {
                return string.Empty;
            }

            if (mod.ModDetails != null && !string.IsNullOrWhiteSpace(mod.ModDetails.Title))
            {
                return mod.ModDetails.Title;
            }

            if (!string.IsNullOrWhiteSpace(mod.Filename))
            {
                return mod.Filename;
            }

            return string.Empty;
        }
    }
}
