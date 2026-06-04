using App = Frosty.Core.App;
using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Attributes;
using Frosty.Core.Bookmarks;
using Frosty.Core.Controls;
using Frosty.Core.Mod;
using Frosty.Core.Windows;
using FrostyEditor;
using FrostyEditor.Windows;
using FrostySdk;
using FrostySdk.Managers;
using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using Formatting = Newtonsoft.Json.Formatting;
using Flurry.Editor.Windows;

namespace Flurry.Editor.Patches
{
    [HarmonyPatch(typeof(MainWindow))]
    [HarmonyPatchCategory("flurry.editor")]
    public class MainWindow_EditorUIPatches
    {
        private static AccessTools.FieldRef<MainWindow, Grid> mainGridRef = AccessTools.FieldRefAccess<MainWindow, Grid>("mainGrid");
        private static AccessTools.FieldRef<MainWindow, TreeView> BookmarkTreeViewRef = AccessTools.FieldRefAccess<MainWindow, TreeView>("BookmarkTreeView");
        private static AccessTools.FieldRef<MainWindow, Button> launchButtonRef = AccessTools.FieldRefAccess<MainWindow, Button>("launchButton");
        private static AccessTools.FieldRef<MainWindow, Menu> menuRef = AccessTools.FieldRefAccess<MainWindow, Menu>("menu");

        // Patches: Extra export button

        [HarmonyPatch("InitializeComponent")]
        [HarmonyPostfix]
        public static void StartupUIChanges(MainWindow __instance)
        {
            FlurryEditorConfig config = new FlurryEditorConfig();
            config.Load();

            #region Extra export button
            ICommand secondExportCommand = new ExportModMenuItemCommand_AlwaysCanExecute();
            Grid mainGrid = mainGridRef(__instance);
            Grid gridRowOne = (Grid)mainGrid.Children[1];
            Border outerBorder = (Border)gridRowOne.Children[0];
            DockPanel upperDockPanel = (DockPanel)outerBorder.Child;
            Border buttonsBorder = (Border)upperDockPanel.Children[0];
            StackPanel buttonsStackPanel = (StackPanel)buttonsBorder.Child;
            //Button newProjectButton = (Button)buttonsStackPanel.Children[0];

            Image exportImage = new Image()
            {
                Source = new ImageSourceConverter().ConvertFromString("pack://application:,,,/FrostyEditor;component/Images/Export.png") as ImageSource,
                Width = 16
            };
            Button extraExportButton = new Button()
            {
                ToolTip = "Export to Mod",
                Margin = new System.Windows.Thickness(4, 0, 0, 0),
                Height = 20,
                Width = 20,
                Command = secondExportCommand,
                CommandParameter = __instance,
                Content = exportImage
            };

            buttonsStackPanel.Children.Add(extraExportButton);

            #endregion

            #region Kyber Launch button
            if (config.KyberIntegration && ProfilesLibrary.DataVersion == (int)ProfileVersion.StarWarsBattlefrontII)
            {
                Button launchButton = launchButtonRef(__instance);
                Border kyberStuff = GetKyberElements(__instance);
                StackPanel launchButtonParent = (StackPanel)launchButton.Parent;
                Border launchButtonBorderContainer = (Border)launchButtonParent.Parent;
                DockPanel dockPanel = (DockPanel)launchButtonBorderContainer.Parent;
                dockPanel.Children.Insert(dockPanel.Children.IndexOf(launchButtonBorderContainer) + 1, kyberStuff);
            }
            #endregion
        }

        private static Border GetKyberElements(MainWindow instance)
        {
            // 1. Create the main Border container
            Border container = new Border();

            // Set Border properties
            // Background="{StaticResource ControlBackground}"
            // Note: You must ensure 'ControlBackground' is accessible in your application's resources.
            // If it's a SolidColorBrush, you might load it like this:
            container.SetResourceReference(Border.BackgroundProperty, "ControlBackground");

            // Margin="1 0 0 0"
            container.Margin = new Thickness(1, 0, 0, 0);

            // DockPanel.Dock="Left" (Attached Property)
            DockPanel.SetDock(container, Dock.Left);

            // RenderOptions.EdgeMode="Aliased" (Attached Property)
            RenderOptions.SetEdgeMode(container, EdgeMode.Aliased);

            // 2. Create the inner StackPanel (Child of the Border)
            StackPanel innerStackPanel = new StackPanel();

            // Set StackPanel properties
            innerStackPanel.Orientation = Orientation.Horizontal;
            innerStackPanel.Margin = new Thickness(6, 2, 6, 2);

            // 3. Create the first Button: kyberLaunchButton
            Button kyberLaunchButton = new Button();
            kyberLaunchButton.Name = "kyberLaunchButton"; // Optional in code-behind but good for consistency
            kyberLaunchButton.Margin = new Thickness(0);
            kyberLaunchButton.ToolTip = "Launch game with Kyber";
            kyberLaunchButton.IsEnabled = true;

            // Attach Click event handler
            // Note: The method 'kyberLaunchButton_Click' must be defined in the same class.
            // Assuming the event handler signature is: void kyberLaunchButton_Click(object sender, RoutedEventArgs e)
            kyberLaunchButton.Click += (s, e) =>
            {
                kyberLaunchButton_Click(s, e, instance);
            };

            // Create Content for kyberLaunchButton
            StackPanel kyberLaunchButtonContent = new StackPanel();
            kyberLaunchButtonContent.Orientation = Orientation.Horizontal;
            kyberLaunchButtonContent.Margin = new Thickness(4, 0, 4, 0);

            // Image for kyberLaunchButton
            Image launchImage = new Image();
            // Grid.Column="0" is only relevant if the parent was a Grid, can be ignored here.
            launchImage.Source = new BitmapImage(new Uri("../Images/Play.png", UriKind.Relative));
            launchImage.Width = 16;

            // TextBlock for kyberLaunchButton
            TextBlock kyberTextBlock = new TextBlock();
            // Grid.Column="1" is only relevant if the parent was a Grid, can be ignored here.
            kyberTextBlock.Text = "Kyber";
            kyberTextBlock.VerticalAlignment = VerticalAlignment.Center;
            kyberTextBlock.Margin = new Thickness(4, 0, 0, 0);

            // Add Image and TextBlock to the Button's StackPanel
            kyberLaunchButtonContent.Children.Add(launchImage);
            kyberLaunchButtonContent.Children.Add(kyberTextBlock);

            // Set the StackPanel as the Button's content
            kyberLaunchButton.Content = kyberLaunchButtonContent;

            // 4. Create the second Button: kyberSettingsButton
            Button kyberSettingsButton = new Button();
            kyberSettingsButton.Name = "kyberSettingsButton"; // Optional in code-behind
            kyberSettingsButton.Margin = new Thickness(0);
            kyberSettingsButton.ToolTip = "Modify Kyber launch settings.";
            kyberSettingsButton.IsEnabled = true;

            // Attach Click event handler
            // Note: The method 'kyberSettingsButton_Click' must be defined in the same class.
            // Assuming the event handler signature is: void kyberSettingsButton_Click(object sender, RoutedEventArgs e)
            kyberSettingsButton.Click += (s, e) =>
            {
                Windows.KyberSettingsWindow win = new Windows.KyberSettingsWindow(KyberIntegration.GetKyberJsonSettings());
                win.ShowDialog();
            };

            // Create Content for kyberSettingsButton
            StackPanel kyberSettingsButtonContent = new StackPanel();
            kyberSettingsButtonContent.Orientation = Orientation.Horizontal;
            kyberSettingsButtonContent.Margin = new Thickness(4, 0, 4, 0);

            // Image for kyberSettingsButton
            Image settingsImage = new Image();
            // Grid.Column="0" is only relevant if the parent was a Grid, can be ignored here.
            settingsImage.Source = new ImageSourceConverter().ConvertFromString("pack://application:,,,/FlurryEditorPlugin;component/Images/KyberCog.png") as ImageSource;
            settingsImage.Width = 16;
            RenderOptions.SetBitmapScalingMode(settingsImage, BitmapScalingMode.Fant);


            // Add Image to the Settings Button's StackPanel
            kyberSettingsButtonContent.Children.Add(settingsImage);

            // Set the StackPanel as the Button's content
            kyberSettingsButton.Content = kyberSettingsButtonContent;

            // 5. Add Buttons to the inner StackPanel
            innerStackPanel.Children.Add(kyberLaunchButton);
            innerStackPanel.Children.Add(kyberSettingsButton);

            // 6. Set the inner StackPanel as the Border's child
            container.Child = innerStackPanel;

            // Return the final Border element
            return container;
        }

        private static void kyberLaunchButton_Click(object sender, RoutedEventArgs e, MainWindow instance)
        {
            //List<ExportActionOverride> actions =  App.PluginManager.GetExportActionOverrides().Where(lst => !new List<ExportType> { ExportType.All, ExportType.LaunchOnly, ExportType.KyberLaunchOnly}.Contains(lst.Item2)).Select(lst => lst.Item3).ToList();

            KyberJsonSettings jsonSettings = KyberIntegration.GetKyberJsonSettings();
            if (!KyberIntegration.DoesCliExist())
                return;
            CancellationTokenSource cancelToken = new CancellationTokenSource();
            string editorModName = "KyberMod.fbmod";

            //
            // Export Mod Order Json
            //

            KyberModsJson exportJson = new KyberModsJson();
            string path = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string basePath = $@"{(path.Substring(0, path.Length - 8)).Replace("\\", @"/")}/Mods/Kyber";
            exportJson.basePath = basePath;

            List<string> fbmodNames = KyberIntegration.GetLoadOrder(basePath);
            exportJson.modPaths = new List<string>(fbmodNames);

            File.WriteAllText("Mods/Kyber/Kyber-Launch.json", JsonConvert.SerializeObject(exportJson, new JsonSerializerSettings
            {
                Formatting = Formatting.Indented
            }));

            string editorModPath = $"Mods/Kyber/{editorModName}";
            List<string> loadOrderModPaths = fbmodNames.Select(modName => $"Mods/Kyber/{modName}").ToList();
            FrostyTaskWindow.Show("Preparing", "", (task) =>
            {
                //foreach (ExportActionOverride exportAction in actions)
                //exportAction.PreExport(task, ExportType.KyberLaunchOnly, editorModPath, loadOrderModPaths);
                // SKIP UNIMPLEMENTED PREEXPORTS
            });



            // create temporary editor mod
            ModSettings editorSettings = new ModSettings { Title = editorModName, Author = "Frosty Editor", Version = "-1", Category = "Editor" };

            //
            // Export Kyber mod
            //
            Random random = new Random();
            bool cancelled = false;
            try
            {
                // run mod applying process
                FrostyTaskWindow.Show("Launching", "", (task) =>
                {
                    try
                    {
                        foreach (ExecutionAction executionAction in App.PluginManager.ExecutionActions)
                        {
                            executionAction.PreLaunchAction(task.TaskLogger, PluginManagerType.Editor, cancelToken.Token);
                        }

                        task.Update("Exporting Mod");
                        instance.ExportMod(editorSettings, editorModPath, true, cancelToken.Token);
                        App.Logger.Log($"Editor Mod Saved As {editorModName}");
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                        // swollow
                        foreach (ExecutionAction executionAction in App.PluginManager.ExecutionActions)
                        {
                            executionAction.PostLaunchAction(task.TaskLogger, PluginManagerType.ModManager, cancelToken.Token);
                        }
                    }

                }, showCancelButton: true, cancelCallback: (task) => cancelToken.Cancel());
            }
            catch (OperationCanceledException)
            {
                // process was cancelled
                App.Logger.Log("Launch Cancelled");
                cancelled = true;
            }
            if (!cancelled)
            {
                //
                // Export Kyber commands
                //
                List<string> commands = new List<string>();
                if (!KyberSettings.FrontendLaunch)
                {
                    if (KyberSettings.AutoplayerType == "Dummy Bots")
                    {
                        commands.Add($"Whiteshark.AutoBalanceTeamsOnNeutral 1");
                        commands.Add($"AutoPlayers.PlayerCount {KyberSettings.Team1Bots + KyberSettings.Team2Bots}");
                    }
                    else if (KyberSettings.AutoplayerType == "Gamemode Tied")
                    {
                        commands.Add($"AutoPlayers.ForceFillGameplayBotsTeam1 {KyberSettings.Team1Bots}");
                        commands.Add($"AutoPlayers.ForceFillGameplayBotsTeam2 {KyberSettings.Team2Bots}");
                    }

                    commands.Add($"Kyber.SetTeamByIndex 0 {KyberSettings.TeamId}");
                    if (KyberSettings.Autostart)
                        commands.Add($"Kyber.startgame");  //commands.Add($"Kyber.Delay 5 startgame");
                }

                foreach (string command in KyberSettings.LaunchCommands)
                    commands.Add($"{command}");

                using (StreamWriter writer = new StreamWriter("Mods/Kyber/Kyber-Commands.txt"))
                {
                    foreach (string str in commands)
                        writer.WriteLine(str);
                }

                //
                //  Execute kyber_cli.exe
                //
                int randomNumber = random.Next();
                string cliCommand = (KyberSettings.FrontendLaunch ? "start_game" : "start_server") + $" --module-branch={KyberSettings.ModuleBranch} --raw-mods \"{$@"{basePath}/Kyber-Launch.json"}\"" + (KyberSettings.DebugMode ? " --verbose --debug" : "");
                if (!KyberSettings.FrontendLaunch)
                    cliCommand += $" --server-password \"FlurryPlugin{randomNumber}\" --no-dedicated --server-name \"Test\" --map \"{KyberSettings.Level}\" --mode \"{KyberSettings.GameMode}\" --startup-commands \"{$@"{basePath}/Kyber-Commands.txt"}\"";
                App.Logger.Log(cliCommand);

                ProcessStartInfo psi = new ProcessStartInfo(KyberSettings.CliDirectory);
                psi.EnvironmentVariables["KYBER_ONLINE_MODE"] = "0";
                psi.EnvironmentVariables["KYBER_ALLOW_DEDICATED"] = "1";
                psi.EnvironmentVariables["KYBER_BYPASS_DOCKER_I_REALLY_KNOW_WHAT_I_AM_DOING"] = "1";
                if (KyberSettings.DebugMode)
                {
                    psi.EnvironmentVariables["KYBER_PROPERTY_DEBUG"] = "1";
                    psi.EnvironmentVariables["KYBER_LOG_LEVEL"] = "debug";
                    psi.EnvironmentVariables["MAXIMA_LAUNCH_ARGS"] = "-Kyber.RenderPropertyDebug true";
                }

                psi.Arguments = cliCommand;
                //psi.RedirectStandardInput = true;
                //psi.RedirectStandardError = true;
                //psi.RedirectStandardOutput = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = false; // Show cmd window
                psi.WorkingDirectory = Path.GetDirectoryName(KyberSettings.CliDirectory); // Set the working directory here

                // Start the process and read the output
                Process process = Process.Start(psi);
                //if (process != null)
                //{
                //    process.StandardInput.WriteLine("exit");

                //    // Read the output
                //    string result = process.StandardOutput.ReadToEnd();
                //    App.Logger.Log(result);

                //    string error = process.StandardError.ReadToEnd();
                //    if (!string.IsNullOrEmpty(error))
                //    {
                //        App.Logger.LogWarning(error);
                //    }

                //    //process.WaitForExit();
                //    //process.Close();
                //}
            }


            FrostyTaskWindow.Show("Completing", "", (task) =>
            {
                //foreach (ExportActionOverride exportAction in actions)
                //exportAction.PostExport(task, ExportType.KyberLaunchOnly, editorModPath, loadOrderModPaths);
            });

            GC.Collect();
        }


        [HarmonyPatch("ExportMod")]
        [HarmonyPostfix]
        public static void AutosaveOnExport(MainWindow __instance)
        {
            FlurryEditorConfig config = new FlurryEditorConfig();
            config.Load();
            if (config.AutosaveOnExport)
            {
                if (__instance is MainWindow win)
                {
                    FrostyProject project = win.Project;
                    if (project.IsDirty)
                    {
                        string name = project.DisplayName.Replace(".fbproject", "");
                        DateTime timeStamp = DateTime.Now;

                        string targetName = "Autosave/Export/" + name + "_" + timeStamp.Day.ToString("D2") + timeStamp.Month.ToString("D2") + timeStamp.Year.ToString("D4") + "_" + timeStamp.Hour.ToString("D2") + timeStamp.Minute.ToString("D2") + timeStamp.Second.ToString("D2") + ".fbproject";
                        App.Logger.Log($"Autosaving project to Autosave/Export/{name}_{targetName}");
                        project.Save(overrideFilename: targetName, updateDirtyState: false);
                    }
                }
            }
        }

        [HarmonyPatch("LoadTabExtensions")]
        [HarmonyPostfix]
        public static void BookmarksMenuChanges(MainWindow __instance)
        {

            FileLog.Debug("BookmarksMenuChanges Patch Applied");
            FlurryEditorConfig config = new FlurryEditorConfig();
            config.Load();

            FileLog.Debug("Post config load");
            ImageSourceConverter imageSourceConverter = new ImageSourceConverter();

            #region Bookmarks modifications
            if (config.BookmarksTabTweaks)
            {
                FileLog.Debug("Applying bookmarks tweaks");
                ContextMenu bookmarksContextMenu = __instance.FindResource("bookmarksContextMenu") as ContextMenu;
                FileLog.Debug("Context menu found");

                // Open asset (modify)
                MenuItem openAssetOption = bookmarksContextMenu.Items.GetItemAt(0) as MenuItem;
                (openAssetOption.Icon as Image).Opacity = 0.5;

                FileLog.Debug("Open asset modified");

                // Find in explorer (modify)
                MenuItem findInExplorerOption = bookmarksContextMenu.Items.GetItemAt(1) as MenuItem;
                Image findInExplorerImage = new Image()
                {
                    Source = imageSourceConverter.ConvertFromString("pack://application:,,,/FrostyEditor;component/Images/Open.png") as ImageSource,
                    Opacity = 0.5
                };
                RenderOptions.SetBitmapScalingMode(findInExplorerImage, BitmapScalingMode.Fant);
                findInExplorerOption.Icon = findInExplorerImage;

                FileLog.Debug("Find in explorer modified");


                TreeView BookmarkTreeView = BookmarkTreeViewRef(__instance);

                // Open in Blueprint Editor (add)
                if (config.BlueprintEditorTweaks)
                {
                    MenuItem openInBlueprintEditorOption = new MenuItem()
                    {
                        Header = "Open in Blueprint Editor",
                        Icon = new Image()
                        {
                            Source = new ImageSourceConverter().ConvertFromString("pack://application:,,,/FrostyCore;component/Images/Assets/BlueprintFileType.png") as ImageSource,
                            Opacity = 0.5
                        }
                    };
                    RenderOptions.SetBitmapScalingMode(openInBlueprintEditorOption.Icon as Image, BitmapScalingMode.Fant);
                    openInBlueprintEditorOption.Click += (sender, e) =>
                    {
                        if (BookmarkTreeView.SelectedItem == null)
                            return;
                        BookmarkItem target = BookmarkTreeView.SelectedItem as BookmarkItem;
                        if (target.Target is AssetBookmarkTarget assetTarget)
                        {
                            if (assetTarget.Asset is EbxAssetEntry entry)
                            {
                                if (entry != null)
                                {
                                    FlurryEditorUtils.OpenInBlueprintEditor(entry);
                                }
                            }
                        }
                    };

                    bookmarksContextMenu.Items.Add(openInBlueprintEditorOption);
                }

                // Copy file path (add)
                MenuItem copyFilePathOption = new MenuItem()
                {
                    Icon = new Image()
                    {
                        Source = imageSourceConverter.ConvertFromString("pack://application:,,,/FrostyCore;component/Images/Copy.png") as ImageSource,
                        Opacity = 0.5
                    },
                    Header = "Copy file path"
                };
                copyFilePathOption.Click += (sender, e) =>
                {
                    if (BookmarkTreeView.SelectedItem == null)
                        return;
                    BookmarkItem target = BookmarkTreeView.SelectedItem as BookmarkItem;
                    if (target.Target is AssetBookmarkTarget assetTarget)
                    {
                        if (assetTarget.Asset is EbxAssetEntry entry)
                        {
                            if (entry != null)
                            {
                                Clipboard.SetText(entry.Name);
                            }
                        }
                    }
                };
                bookmarksContextMenu.Items.Add(copyFilePathOption);
            }
            #endregion
        }

        class ExportModMenuItemCommand_AlwaysCanExecute : ICommand
        {
            public event EventHandler CanExecuteChanged
            {
                add => CommandManager.RequerySuggested += value;
                remove => CommandManager.RequerySuggested -= value;
            }

            public bool CanExecute(object parameter)
            {
                return true;
            }

            public void Execute(object parameter)
            {
                if (!(App.AssetManager != null && App.AssetManager.GetModifiedCount() != 0))
                {
                    FrostyMessageBox.Show("Cannot export mod when no changes have been made.", "Frosty Editor");
                    return;
                }

                MainWindow mainWin = parameter as MainWindow;
                ModSettingsWindow win = new ModSettingsWindow(mainWin.Project);
                win.ShowDialog();

                if (win.DialogResult == true)
                {
                    FrostySaveFileDialog sfd = new FrostySaveFileDialog("Save Mod", "*.fbmod (Frosty Mod)|*.fbmod", "Mod");
                    if (sfd.ShowDialog())
                    {
                        string filename = sfd.FileName;

                        // setup ability to cancel the process
                        CancellationTokenSource cancelToken = new CancellationTokenSource();

                        FrostyTaskWindow.Show("Saving Mod", "", (task) =>
                        {
                            try
                            {
                                mainWin.ExportMod(mainWin.Project.GetModSettings(), filename, false, cancelToken.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                // process was cancelled
                                App.Logger.Log("Export Cancelled");

                                if (File.Exists(filename))
                                {
                                    File.Delete(filename);
                                }
                            }
                        }, showCancelButton: true, cancelCallback: (task) => cancelToken.Cancel());
                    }
                }
            }
        }

        [HarmonyPatch("LoadMenuExtensions")]
        [HarmonyPostfix]
        public static void MenuExtensionsChanges(MainWindow __instance)
        {
            foreach (var _menuExtension in App.PluginManager.MenuExtensions)
            {
                dynamic menuExtension = _menuExtension;
                if (!string.IsNullOrEmpty(menuExtension.SubLevelMenuName))
                {
                    if (FlurryEditorUtils.HasProperty(menuExtension, "ParentIcon"))
                    {
                        Menu menu = menuRef(__instance);
                        foreach (var topLevelitem in menu.Items)
                        {
                            if (topLevelitem is MenuItem _mi)
                            {
                                foreach (var subMenuItem in _mi.Items)
                                {
                                    if (subMenuItem is MenuItem mi)
                                    {
                                        if (menuExtension.SubLevelMenuName == mi.Header.ToString())
                                        {
                                            if (menuExtension.ParentIcon is ImageSource)
                                            {
                                                mi.Icon = new Image() { Source = menuExtension.ParentIcon as ImageSource };
                                            } else
                                            {
                                                App.Logger.LogWarning($"Menu extension '{menuExtension.MenuItemName}' does not properly implement ParentIcon.");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
