using Frosty.Core;
using Frosty.Core.Controls;
using FrostyEditor;
using FrostySdk;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Flurry.Editor.Patches
{
    public static class ExceptionHelper
    {
        public static string BuildFullExceptionText(Exception e)
        {
            if (e == null) return "";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(e.Message);
            sb.AppendLine();
            sb.AppendLine(e.StackTrace);

            Exception inner = e.InnerException;
            int depth = 1;
            while (inner != null)
            {
                sb.AppendLine();
                sb.AppendLine($"--- Inner Exception {depth} ---");
                sb.AppendLine(inner.Message);
                sb.AppendLine();
                sb.AppendLine(inner.StackTrace);

                inner = inner.InnerException;
                depth++;
            }

            return sb.ToString();
        }
    }

    [HarmonyPatch(typeof(FrostyExceptionBox))]
    [HarmonyPatchCategory("flurry.editor")]
    public class ExceptionBoxPatch
    {
        [HarmonyPatch(nameof(FrostyExceptionBox.OnApplyTemplate))]
        [HarmonyPostfix]
        public static void OnApplyTemplate_Postfix(FrostyExceptionBox __instance)
        {
            __instance.Loaded += (s, e) =>
            {
                __instance.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { AddButtons(__instance); }
                    catch { }
                }), DispatcherPriority.Loaded);
            };
        }

        private static void AddButtons(FrostyExceptionBox window)
        {
            StackPanel buttonPanel = FindChild<StackPanel>(window, sp => sp.FlowDirection == FlowDirection.RightToLeft);
            if (buttonPanel == null) return;

            Button reportButton = new Button
            {
                Content = "Copy Crash Report",
                Width = 130,
                Margin = new Thickness(5, 0, 0, 0)
            };
            reportButton.Click += (s, e) =>
            {
                try
                {
                    StringBuilder sb = new StringBuilder();

                    sb.AppendLine("=== Exception Details ===");
                    sb.AppendLine(window.ExceptionText ?? "(no exception text)");
                    sb.AppendLine();

                    sb.AppendLine("=== Project State ===");
                    try
                    {
                        if (Application.Current.MainWindow is MainWindow win)
                        {
                            FrostyProject project = win.Project;
                            sb.AppendLine($"Project: {project.DisplayName}");
                            sb.AppendLine($"Profile: {ProfilesLibrary.ProfileName}");
                            sb.AppendLine($"Is Dirty: {project.IsDirty}");
                            sb.AppendLine($"Filename: {project.Filename ?? "Unsaved"}");

                            int modifiedEbx = 0, addedEbx = 0, modifiedRes = 0, addedRes = 0, modifiedChunks = 0;
                            var modifiedAssets = new List<string>();

                            foreach (var entry in Frosty.Core.App.AssetManager.EnumerateEbx(modifiedOnly: true))
                            {
                                if (entry.IsDirectlyModified)
                                {
                                    modifiedEbx++;
                                    modifiedAssets.Add($"  EBX: {entry.Name} ({entry.Type}) [Modified]");
                                }
                                else if (entry.IsAdded)
                                {
                                    addedEbx++;
                                    modifiedAssets.Add($"  EBX: {entry.Name} ({entry.Type}) [Added]");
                                }
                            }

                            foreach (var entry in Frosty.Core.App.AssetManager.EnumerateRes(modifiedOnly: true))
                            {
                                if (entry.IsDirectlyModified)
                                {
                                    modifiedRes++;
                                    modifiedAssets.Add($"  RES: {entry.Name} (Type: {entry.ResType}) [Modified]");
                                }
                                else if (entry.IsAdded)
                                {
                                    addedRes++;
                                    modifiedAssets.Add($"  RES: {entry.Name} (Type: {entry.ResType}) [Added]");
                                }
                            }

                            foreach (var entry in Frosty.Core.App.AssetManager.EnumerateChunks(modifiedOnly: true))
                            {
                                if (entry.IsDirectlyModified)
                                {
                                    modifiedChunks++;
                                    modifiedAssets.Add($"  Chunk: {entry.Name} [Modified]");
                                }
                            }

                            sb.AppendLine($"Modified EBX: {modifiedEbx}");
                            sb.AppendLine($"Added EBX: {addedEbx}");
                            sb.AppendLine($"Modified RES: {modifiedRes}");
                            sb.AppendLine($"Added RES: {addedRes}");
                            sb.AppendLine($"Modified Chunks: {modifiedChunks}");
                            sb.AppendLine();

                            if (modifiedAssets.Count > 0 && modifiedAssets.Count <= 200)
                            {
                                sb.AppendLine("=== Modified Assets ===");
                                foreach (var asset in modifiedAssets)
                                    sb.AppendLine(asset);
                                sb.AppendLine();
                            }
                            else if (modifiedAssets.Count > 200)
                            {
                                sb.AppendLine("=== Modified Assets (first 100) ===");
                                for (int i = 0; i < 100; i++)
                                    sb.AppendLine(modifiedAssets[i]);
                                sb.AppendLine($"  ... and {modifiedAssets.Count - 100} more");
                                sb.AppendLine();
                            }
                        }
                        else
                        {
                            sb.AppendLine("(unable to access main window)");
                            sb.AppendLine();
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"(failed to get project state: {ex.Message})");
                        sb.AppendLine();
                    }

                    sb.AppendLine("=== Crash Context ===");
                    try
                    {
                        var exceptionText = window.ExceptionText ?? "";
                        var lines = exceptionText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

                        var operationKeywords = new Dictionary<string, string>
                        {
                            { "WriteProject", "Exporting mod" },
                            { "SaveToMod", "Saving asset to mod" },
                            { "Import", "Importing asset" },
                            { "Export", "Exporting asset" },
                            { "Load", "Loading asset" },
                            { "OnApplyTemplate", "Applying UI template" },
                            { "Save", "Saving project" },
                            { "OpenAsset", "Opening asset" },
                            { "GetEbx", "Loading EBX asset" },
                            { "GetRes", "Loading RES asset" },
                            { "GetResAs", "Loading resource as type" },
                            { "ShaderBlockDepot", "Processing shader block depot" },
                            { "MeshSet", "Processing mesh set" },
                            { "Fbx", "Processing FBX file" },
                            { "Texture", "Processing texture" },
                            { "Bundle", "Processing bundle" },
                        };

                        var detectedOperations = new List<string>();
                        foreach (var line in lines)
                        {
                            foreach (var kvp in operationKeywords)
                            {
                                if (line.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    if (!detectedOperations.Contains(kvp.Value))
                                        detectedOperations.Add(kvp.Value);
                                }
                            }
                        }

                        if (detectedOperations.Count > 0)
                        {
                            sb.AppendLine("  Detected operations:");
                            foreach (var op in detectedOperations)
                                sb.AppendLine($"    - {op}");
                        }
                        else
                        {
                            sb.AppendLine("  (no specific operation detected)");
                        }

                        var assetPaths = new List<string>();
                        foreach (var line in lines)
                        {
                            var pathMatch = System.Text.RegularExpressions.Regex.Match(line, @"[a-zA-Z0-9_/\\]+\.(ebx|res|chunk|fbx|mesh|dds|ttf|bundle)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (pathMatch.Success && !assetPaths.Contains(pathMatch.Value))
                                assetPaths.Add(pathMatch.Value);
                        }

                        if (assetPaths.Count > 0)
                        {
                            sb.AppendLine("  Referenced assets in stack trace:");
                            foreach (var path in assetPaths)
                                sb.AppendLine($"    - {path}");
                        }

                        var pluginNames = new List<string>();
                        foreach (var line in lines)
                        {
                            var pluginMatch = System.Text.RegularExpressions.Regex.Match(line, @"(\w+Plugin)\.\w+");
                            if (pluginMatch.Success && !pluginNames.Contains(pluginMatch.Groups[1].Value))
                                pluginNames.Add(pluginMatch.Groups[1].Value);
                        }

                        if (pluginNames.Count > 0)
                        {
                            sb.AppendLine("  Involved plugins:");
                            foreach (var plugin in pluginNames)
                                sb.AppendLine($"    - {plugin}");
                        }

                        var handlerTypes = new List<string>();
                        foreach (var line in lines)
                        {
                            var handlerMatch = System.Text.RegularExpressions.Regex.Match(line, @"(\w+CustomActionHandler)\.\w+");
                            if (handlerMatch.Success && !handlerTypes.Contains(handlerMatch.Groups[1].Value))
                                handlerTypes.Add(handlerMatch.Groups[1].Value);
                        }

                        if (handlerTypes.Count > 0)
                        {
                            sb.AppendLine("  Custom handlers involved:");
                            foreach (var handler in handlerTypes)
                                sb.AppendLine($"    - {handler}");
                        }

                        if (detectedOperations.Count == 0 && assetPaths.Count == 0 && pluginNames.Count == 0 && handlerTypes.Count == 0)
                        {
                            sb.AppendLine("  (no additional context could be extracted)");
                        }

                        sb.AppendLine();
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"  (failed to analyze crash context: {ex.Message})");
                        sb.AppendLine();
                    }

                    sb.AppendLine("=== Loaded Plugins ===");
                    try
                    {
                        var pluginManager = Frosty.Core.App.PluginManager;
                        var pluginsField = pluginManager.GetType().GetField("plugins", BindingFlags.NonPublic | BindingFlags.Instance)
                                        ?? pluginManager.GetType().GetField("mPlugins", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (pluginsField != null)
                        {
                            var plugins = pluginsField.GetValue(pluginManager) as System.Collections.IEnumerable;
                            if (plugins != null)
                            {
                                foreach (var plugin in plugins)
                                {
                                    var nameProp = plugin.GetType().GetProperty("Name") ?? plugin.GetType().GetProperty("DisplayName");
                                    var versionProp = plugin.GetType().GetProperty("Version");
                                    string name = nameProp?.GetValue(plugin)?.ToString() ?? plugin.GetType().Name;
                                    string version = versionProp?.GetValue(plugin)?.ToString() ?? "unknown";
                                    sb.AppendLine($"  {name} v{version}");
                                }
                            }
                        }
                        else
                        {
                            sb.AppendLine("  (unable to enumerate plugins)");
                        }
                        sb.AppendLine();
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"  (failed to get plugins: {ex.Message})");
                        sb.AppendLine();
                    }

                    sb.AppendLine("=== Open Editors ===");
                    try
                    {
                        if (Application.Current.MainWindow is MainWindow win2)
                        {
                            var editorProp = win2.GetType().GetProperty("OpenEditors", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                          ?? win2.GetType().GetProperty("Editors", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (editorProp != null)
                            {
                                var editors = editorProp.GetValue(win2) as System.Collections.IEnumerable;
                                if (editors != null)
                                {
                                    int count = 0;
                                    foreach (var editor in editors)
                                    {
                                        var nameProp = editor.GetType().GetProperty("DisplayName") ?? editor.GetType().GetProperty("Title");
                                        string name = nameProp?.GetValue(editor)?.ToString() ?? editor.GetType().Name;
                                        sb.AppendLine($"  {name}");
                                        count++;
                                    }
                                    if (count == 0)
                                        sb.AppendLine("  (none)");
                                }
                                else
                                {
                                    sb.AppendLine("  (unable to enumerate editors)");
                                }
                            }
                            else
                            {
                                sb.AppendLine("  (unable to find editors property)");
                            }
                        }
                        else
                        {
                            sb.AppendLine("  (unable to access main window)");
                        }
                        sb.AppendLine();
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"  (failed to get open editors: {ex.Message})");
                        sb.AppendLine();
                    }

                    try
                    {
                        var logTextProp = Frosty.Core.App.Logger.GetType().GetProperty("LogText");
                        if (logTextProp != null)
                        {
                            string internalLog = logTextProp.GetValue(Frosty.Core.App.Logger) as string;
                            if (!string.IsNullOrEmpty(internalLog))
                            {
                                sb.AppendLine("=== Editor Log (internal) ===");
                                sb.AppendLine(internalLog);
                                sb.AppendLine();

                                var lines = internalLog.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                                var errors = new List<string>();
                                foreach (var line in lines)
                                {
                                    if (line.Contains("(WARNING)") || line.Contains("(ERROR)"))
                                        errors.Add(line);
                                }
                                if (errors.Count > 0)
                                {
                                    sb.AppendLine("=== Pre-Exception Errors ===");
                                    foreach (var err in errors)
                                        sb.AppendLine(err);
                                    sb.AppendLine();
                                }
                            }
                        }
                    }
                    catch { }

                    sb.AppendLine("=== Binary File Hashes ===");
                    sb.AppendLine(FlurryEditorUtils.GetBinaryFileHashes());

                    Clipboard.SetText(sb.ToString());
                    Frosty.Core.App.Logger.Log("Copied full crash report to clipboard.");
                }
                catch (Exception ex)
                {
                    Frosty.Core.App.Logger.LogWarning("Failed to copy crash report: " + ex.Message);
                }
            };

            Button logButton = new Button
            {
                Content = "Go to Log",
                Width = 80,
                Margin = new Thickness(5, 0, 0, 0)
            };
            logButton.Click += (s, e) =>
            {
                string crashLogPath = GetCrashLogPath();
                if (File.Exists(crashLogPath))
                    Process.Start("explorer.exe", $"/select,\"{crashLogPath}\"");
                else
                {
                    string dir = Path.GetDirectoryName(crashLogPath);
                    if (Directory.Exists(dir))
                        Process.Start("explorer.exe", dir);
                }
            };

            buttonPanel.Children.Add(reportButton);
            buttonPanel.Children.Add(logButton);
        }

        private static T FindChild<T>(DependencyObject parent, Func<T, bool> predicate = null) where T : DependencyObject
        {
            if (parent == null) return null;
            int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T found && (predicate == null || predicate(found)))
                    return found;
                T result = FindChild(child, predicate);
                if (result != null) return result;
            }
            return null;
        }

        private static string GetCrashLogPath()
        {
            string editorDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location)
                               ?? AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(editorDir, "crashlog.txt");
        }
    }

    [HarmonyPatch(typeof(FrostyExceptionBox))]
    [HarmonyPatchCategory("flurry.editor")]
    public class ExceptionBoxShowPatch
    {
        [HarmonyPatch(nameof(FrostyExceptionBox.Show))]
        [HarmonyPrefix]
        public static bool Show_Prefix(Exception e, string title, ref MessageBoxResult __result)
        {
            FrostyExceptionBox window = new FrostyExceptionBox();
            window.Title = title;
            window.ExceptionText = ExceptionHelper.BuildFullExceptionText(e);

            __result = (window.ShowDialog() == true) ? MessageBoxResult.OK : MessageBoxResult.Cancel;
            return false;
        }
    }

    [HarmonyPatch(typeof(FrostyEditor.App))]
    [HarmonyPatchCategory("flurry.editor")]
    public class CrashLogPatch
    {
        [HarmonyPatch("App_DispatcherUnhandledException")]
        [HarmonyPrefix]
        public static bool CrashHandler_Prefix(FrostyEditor.App __instance, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                if (Application.Current.MainWindow is MainWindow win)
                {
                    FrostyProject project = win.Project;
                    if (project.IsDirty)
                    {
                        string name = project.DisplayName.Replace(".fbproject", "");
                        DateTime timeStamp = DateTime.Now;
                        project.Filename = "Autosave/" + name + "_"
                            + timeStamp.Day.ToString("D2") + timeStamp.Month.ToString("D2") + timeStamp.Year.ToString("D4") + "_"
                            + timeStamp.Hour.ToString("D2") + timeStamp.Minute.ToString("D2") + timeStamp.Second.ToString("D2")
                            + ".fbproject";
                        project.Save();
                    }
                }
            }
            catch { }

            try
            {
                string fullText = ExceptionHelper.BuildFullExceptionText(e.Exception);
                File.WriteAllText("crashlog.txt", fullText);
            }
            catch { }

            FrostyExceptionBox.Show(e.Exception, "Frosty Editor");
            Environment.Exit(0);

            return false;
        }
    }
}
