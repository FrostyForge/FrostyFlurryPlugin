using Frosty.Core;
using Frosty.Core.Controls;
using Frosty.Core.Mod;
using FrostyModManager;
using FrostySdk;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Flurry.Manager.Patches
{
    public static class ManagerExceptionHelper
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
    [HarmonyPatchCategory("flurry.manager")]
    public class ManagerExceptionBoxPatch
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

                    sb.AppendLine("=== Manager State ===");
                    try
                    {
                        sb.AppendLine($"Profile: {ProfilesLibrary.ProfileName}");

                        if (Application.Current.MainWindow is FrostyModManager.MainWindow managerWin)
                        {
                            var selectedPackField = AccessTools.Field(typeof(FrostyModManager.MainWindow), "selectedPack");
                            var selectedPack = selectedPackField?.GetValue(managerWin) as FrostyPack;
                            if (selectedPack != null)
                            {
                                sb.AppendLine($"Selected Pack: {selectedPack.Name}");
                                sb.AppendLine($"Applied Mods ({selectedPack.AppliedMods.Count}):");
                                foreach (FrostyAppliedMod mod in selectedPack.AppliedMods)
                                {
                                    string status = mod.IsFound
                                        ? (mod.IsEnabled ? "Enabled" : "Disabled")
                                        : "Missing";
                                    sb.AppendLine($"  - {mod.ModName} [{status}]");
                                }
                            }
                            else
                            {
                                sb.AppendLine("Selected Pack: (none)");
                            }
                        }
                        else
                        {
                            sb.AppendLine("(unable to access main window)");
                        }
                        sb.AppendLine();
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"(failed to get manager state: {ex.Message})");
                        sb.AppendLine();
                    }

                    sb.AppendLine("=== Crash Context ===");
                    try
                    {
                        var exceptionText = window.ExceptionText ?? "";
                        var lines = exceptionText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

                        var operationKeywords = new Dictionary<string, string>
                        {
                            { "Launch", "Launching game" },
                            { "FrostyModExecutor", "Applying mods" },
                            { "ModExecutor", "Executing mod application" },
                            { "Install", "Installing mods" },
                            { "Load", "Loading data" },
                            { "OnApplyTemplate", "Applying UI template" },
                            { "FileSystem", "Accessing file system" },
                            { "Bundle", "Processing bundle" },
                            { "Cas", "Processing CAS file" },
                            { "Chunk", "Processing chunk" },
                            { "Texture", "Processing texture" },
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

                        var pluginNames = new List<string>();
                        foreach (var line in lines)
                        {
                            var pluginMatch = Regex.Match(line, @"(\w+Plugin)\.\w+");
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
                            var handlerMatch = Regex.Match(line, @"(\w+CustomActionHandler)\.\w+");
                            if (handlerMatch.Success && !handlerTypes.Contains(handlerMatch.Groups[1].Value))
                                handlerTypes.Add(handlerMatch.Groups[1].Value);
                        }

                        if (handlerTypes.Count > 0)
                        {
                            sb.AppendLine("  Custom handlers involved:");
                            foreach (var handler in handlerTypes)
                                sb.AppendLine($"    - {handler}");
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

                    try
                    {
                        var logTextProp = Frosty.Core.App.Logger.GetType().GetProperty("LogText");
                        if (logTextProp != null)
                        {
                            string internalLog = logTextProp.GetValue(Frosty.Core.App.Logger) as string;
                            if (!string.IsNullOrEmpty(internalLog))
                            {
                                sb.AppendLine("=== Manager Log (internal) ===");
                                sb.AppendLine(internalLog);
                                sb.AppendLine();

                                var logLines = internalLog.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                                var errors = new List<string>();
                                foreach (var line in logLines)
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

                    Clipboard.SetText(sb.ToString());
                    Frosty.Core.App.Logger.Log("Copied full crash report to clipboard.");
                }
                catch (Exception ex)
                {
                    // Fallback: just copy raw exception text
                    try { Clipboard.SetText(window.ExceptionText ?? ""); }
                    catch { }
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
            string managerDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location)
                                ?? AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(managerDir, "crashlog.txt");
        }
    }

    [HarmonyPatch(typeof(FrostyExceptionBox))]
    [HarmonyPatchCategory("flurry.manager")]
    public class ManagerExceptionBoxShowPatch
    {
        [HarmonyPatch(nameof(FrostyExceptionBox.Show))]
        [HarmonyPrefix]
        public static bool Show_Prefix(Exception e, string title, ref MessageBoxResult __result)
        {
            FrostyExceptionBox window = new FrostyExceptionBox();
            window.Title = title;
            window.ExceptionText = ManagerExceptionHelper.BuildFullExceptionText(e);

            __result = (window.ShowDialog() == true) ? MessageBoxResult.OK : MessageBoxResult.Cancel;
            return false;
        }
    }

    [HarmonyPatch(typeof(FrostyModManager.App))]
    [HarmonyPatchCategory("flurry.manager")]
    public class ManagerCrashLogPatch
    {
        [HarmonyPatch("App_DispatcherUnhandledException")]
        [HarmonyPrefix]
        public static bool CrashHandler_Prefix(FrostyModManager.App __instance, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                string fullText = ManagerExceptionHelper.BuildFullExceptionText(e.Exception);
                File.WriteAllText("crashlog.txt", fullText);
            }
            catch { }

            FrostyExceptionBox.Show(e.Exception, "Frosty Mod Manager");
            Environment.Exit(0);

            return false;
        }
    }
}
