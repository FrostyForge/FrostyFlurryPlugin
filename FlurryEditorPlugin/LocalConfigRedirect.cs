using Frosty.Core;
using FrostySdk.Managers;
using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.IO;

namespace Flurry.Editor
{
    internal sealed class LocalConfigRedirectState
    {
        public bool Enabled { get; set; }
        public string DirectoryPath { get; set; } = string.Empty;
    }

    internal static class FlurryLocalConfigRedirect
    {
        private const string RedirectFileName = "flurry.config.redirect.editor.json";
        private const string EditorConfigFileName = "editor_config.json";

        private static string RedirectStatePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RedirectFileName);

        public static LocalConfigRedirectState LoadState()
        {
            try
            {
                if (!File.Exists(RedirectStatePath))
                    return new LocalConfigRedirectState();

                string json = File.ReadAllText(RedirectStatePath);
                LocalConfigRedirectState state = JsonConvert.DeserializeObject<LocalConfigRedirectState>(json);
                return state ?? new LocalConfigRedirectState();
            }
            catch
            {
                return new LocalConfigRedirectState();
            }
        }

        public static void SaveState(bool enabled, string directoryPath)
        {
            try
            {
                LocalConfigRedirectState state = new LocalConfigRedirectState
                {
                    Enabled = enabled,
                    DirectoryPath = directoryPath ?? string.Empty
                };

                File.WriteAllText(RedirectStatePath, JsonConvert.SerializeObject(state, Formatting.Indented));
            }
            catch
            {
                // Keep options saving resilient if file IO fails.
            }
        }

        public static string ResolvePath(string originalPath, PluginManagerType managerType, bool forLoad)
        {
            string defaultPath = string.IsNullOrWhiteSpace(originalPath)
                ? Path.Combine(App.GlobalSettingsPath, EditorConfigFileName)
                : originalPath;

            LocalConfigRedirectState state = LoadState();
            if (!state.Enabled || string.IsNullOrWhiteSpace(state.DirectoryPath))
                return defaultPath;

            string resolvedDirectory = state.DirectoryPath.Trim().Trim('"');
            if (!Path.IsPathRooted(resolvedDirectory))
                resolvedDirectory = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, resolvedDirectory));

            string fileName = Path.GetFileName(defaultPath);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = managerType == PluginManagerType.ModManager ? "manager_config.json" : EditorConfigFileName;

            string redirectedPath = Path.Combine(resolvedDirectory, fileName);
            if (forLoad && !File.Exists(redirectedPath))
                return defaultPath;

            if (!forLoad)
                Directory.CreateDirectory(Path.GetDirectoryName(redirectedPath));

            return redirectedPath;
        }

        public static void ApplyRuntimeRedirect(PluginManagerType managerType)
        {
            if (managerType != PluginManagerType.Editor)
                return;

            string defaultPath = Path.Combine(App.GlobalSettingsPath, EditorConfigFileName);
            string resolvedPath = ResolvePath(defaultPath, managerType, forLoad: false);

            if (string.Equals(defaultPath, resolvedPath, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                if (!File.Exists(resolvedPath) && File.Exists(defaultPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(resolvedPath));
                    File.Copy(defaultPath, resolvedPath, overwrite: true);
                }

                if (File.Exists(resolvedPath))
                {
                    Config.Load(resolvedPath);
                }
            }
            catch
            {
                // Runtime redirect should not block editor startup.
            }
        }
    }
}

namespace Flurry.Editor.Patches
{
    [HarmonyPatch(typeof(Config))]
    [HarmonyPatchCategory("flurry.editor")]
    internal static class ConfigLoadSaveRedirectPatch
    {
        [HarmonyPatch("Load")]
        [HarmonyPrefix]
        private static void Load_Prefix(ref string path)
        {
            path = FlurryLocalConfigRedirect.ResolvePath(path, PluginManagerType.Editor, forLoad: true);
        }

        [HarmonyPatch("Save")]
        [HarmonyPrefix]
        private static void Save_Prefix(ref string path)
        {
            path = FlurryLocalConfigRedirect.ResolvePath(path, PluginManagerType.Editor, forLoad: false);
        }
    }
}
