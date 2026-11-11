using Frosty.Controls;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.Interfaces;
using FrostySdk.Managers;
using Grpc.Core;
using Grpc.Net.Client;
using KyberApi;
using KyberCommon;
using KyberInterface;
using ReferencesPlugin;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Tomlyn;
using Tomlyn.Model;

namespace Flurry.Editor.KyberAPI
{

    public class KyberServerManagementTabItem : FrostyTabItem
    {
        private static KyberServerManagementTabItem instance;
        private Metadata headers = new Metadata();
        private GrpcChannel channel;
        private Empty empty = new Empty();
        private string ServerUrl = "https://api-rpc.prod.kyber.gg:443";
        private string EaToken = null;
        private Timer RefreshIntervalTimer;
        private static string TargetServerApiId = "";
        private static Task<string> KyberToken = GetKyberTokenFromCli();
        private static string TargetServerGuidName = "";
        public static void SetTargetServerGuidName(string guidName)
        {
            TargetServerGuidName = guidName;

            if (instance is null)
                return;

            // Make sure the comboboxes correctly reflect what was selected elsewhere (in case of update after open)
            instance.gamemodeComboBox.SelectedIndex = instance.gameModesData.Select(data => data.Item1).Contains(KyberSettings.GameMode) ? instance.gameModesData.Select(data => data.Item1).ToList().IndexOf(KyberSettings.GameMode) : 0;
            instance.RepopulateLevelComboBox();
            instance.levelComboBox.SelectedIndex = instance.gameModesData[instance.gamemodeComboBox.SelectedIndex].Item3.Select(data => data.Item1).Contains(KyberSettings.Level) ? instance.gameModesData[instance.gamemodeComboBox.SelectedIndex].Item3.Select(data => data.Item1).ToList().IndexOf(KyberSettings.Level) : 0;
        }

        #region Boilerplate / helpers
        static KyberServerManagementTabItem()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(KyberServerManagementTabItem), new FrameworkPropertyMetadata(typeof(KyberServerManagementTabItem)));
        }

        public static void HelloWorld()
        {
            // Do nothing, this method exists so we can force the CLI to try to load immediately.
            //App.Logger.Log("Hello World from KyberServerManagementTabItem!");
        }
        #endregion

        #region Parts
        private TextBlock loggedInAs;
        private TextBox consoleInput;
        private TextBox consoleOutput;
        private ComboBox gamemodeComboBox;
        private ComboBox levelComboBox;
        private Button loadSelectedLevelButton;
        private List<(string, string, List<(string, string)>, int)> gameModesData = new List<(string, string, List<(string, string)>, int)>();
        #endregion

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            loggedInAs = GetTemplateChild("PART_LoggedInAs") as TextBlock;
            consoleOutput = GetTemplateChild("ConsoleOutputTextBox") as TextBox;
            consoleInput = GetTemplateChild("ConsoleInputTextBox") as TextBox;
            gamemodeComboBox = GetTemplateChild("gamemodeComboBox") as ComboBox;
            levelComboBox = GetTemplateChild("levelComboBox") as ComboBox;
            loadSelectedLevelButton = GetTemplateChild("loadSelectedLevelButton") as Button;

            #region Fill mode/level combo boxes
            //Fill Gamemodes/Levels
            EbxAssetEntry modesEntry = App.AssetManager.GetEbxEntry("UI/Data/GameModes/GameModes");
            dynamic modesRoot = App.AssetManager.GetEbx(modesEntry).RootObject;

            // KyberLevelDeclaratorPlugin
            /*
            [REDACTED]
            */

            KyberJsonSettings jsonSettings = KyberIntegration.GetKyberJsonSettings();

            foreach (PointerRef modePr in modesRoot.GameModes)
            {
                EbxAssetEntry modeEntry = App.AssetManager.GetEbxEntry(modePr.External.FileGuid);
                if (modeEntry != null)
                {
                    dynamic modeRoot = App.AssetManager.GetEbx(modeEntry).RootObject;
                    string modeName = $"{FlurryEditorUtils.ConvertToTitleCase(modeRoot.AurebeshGameModeName)} \t[{modeRoot.GameModeId}]";

                    List<(string, string)> levelPairs = new List<(string, string)>();
                    foreach (PointerRef levelPr in modeRoot.Levels)
                    {
                        EbxAssetEntry levEntry = App.AssetManager.GetEbxEntry(levelPr.External.FileGuid);
                        if (levEntry != null)
                        {
                            dynamic levRoot = App.AssetManager.GetEbx(levEntry).RootObject;
                            string aurabesh = FlurryEditorUtils.ConvertToTitleCase(levRoot.LevelAurebesh.Internal.String);
                            if (aurabesh.StartsWith("Yavin") && aurabesh.EndsWith("4"))
                                aurabesh = "Yavin IV";
                            else if (aurabesh == "Death Star Ii")
                                aurabesh = "Death Star II";

                            string levName = $"{aurabesh} - {FlurryEditorUtils.ConvertToTitleCase(LocalizedStringDatabase.Current.GetString((uint)levRoot.LevelName.Internal.StringHash))}";

                            jsonSettings.LevelOverrides.Where(levelOverride => levelOverride.LevelId == levRoot.LevelId).ToList().ForEach(levelOverride => levName = levelOverride.Name);
                            levelPairs.Add((levRoot.LevelId, levName));
                        }
                    }
                    foreach (KyberLevelJsonSettings levelJsonSettings in jsonSettings.LevelOverrides.Where(levelOverride => levelOverride.ModeIds.Contains(modeRoot.GameModeId)).ToList())
                    {
                        if (levelPairs.Select(pair => pair.Item1).Contains(levelJsonSettings.LevelId))
                            continue;
                        levelPairs.Add((levelJsonSettings.LevelId, levelJsonSettings.Name));
                    }

                    levelPairs = levelPairs.OrderBy(item => item.Item2).ToList();
                    List<string> duplicates = levelPairs.Select(item => item.Item2).GroupBy(x => x).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
                    for (int i = 0; i < levelPairs.Count; i++)
                    {
                        (string, string) pair = levelPairs[i];
                        if (duplicates.Contains(pair.Item2))
                            levelPairs[i] = (pair.Item1, $"{pair.Item2} [{pair.Item1}]");
                    }
                    int playerCount = modeRoot.NumberOfPlayers;
                    foreach (KyberGamemodeJsonSettings jsonGamemode in jsonSettings.GamemodeOverrides.Where(gamemodeInfo => gamemodeInfo.ModeId == modeRoot.GameModeId))
                    {
                        modeName = $"{jsonGamemode.Name} \t[{modeRoot.GameModeId}]";
                        playerCount = jsonGamemode.PlayerCount;
                    }
                    if (modeName.StartsWith("DO NOT USE"))
                        continue;
                    gameModesData.Add((modeRoot.GameModeId, modeName, levelPairs, playerCount));
                }
            }
            foreach (KyberGamemodeJsonSettings jsonGamemode in jsonSettings.GamemodeOverrides.Where(gamemodeInfo => !gameModesData.Select(modeData => modeData.Item1).Contains(gamemodeInfo.ModeId)))
            {
                List<(string, string)> levelPairs = new List<(string, string)>();
                foreach (KyberLevelJsonSettings levelJsonSettings in jsonSettings.LevelOverrides.Where(levelOverride => levelOverride.ModeIds.Contains(jsonGamemode.ModeId)).ToList())
                {
                    if (levelPairs.Select(pair => pair.Item1).Contains(levelJsonSettings.LevelId))
                        continue;
                    levelPairs.Add((levelJsonSettings.LevelId, levelJsonSettings.Name));
                }
                if (jsonGamemode.Name.StartsWith("DO NOT USE"))
                    continue;
                gameModesData.Add((jsonGamemode.ModeId, jsonGamemode.Name, levelPairs, jsonGamemode.PlayerCount));
            }


            gameModesData = gameModesData.OrderBy(data => data.Item2).ToList();
            //gameModesData.Insert(0, ("NOGAMEMODE", "Main Menu \t[FRONTEND]", new List<(string, string)>() { ("win32/Levels/Frontend/Frontend", "Frontend")}, 0));
            foreach (var item in gameModesData)
            {
                gamemodeComboBox.Items.Add(item.Item2);
            }

            gamemodeComboBox.SelectionChanged += (sender, e) =>
            {
                if (gamemodeComboBox.SelectedIndex >= 0 && gamemodeComboBox.SelectedIndex < gameModesData.Count)
                    RepopulateLevelComboBox();
            };
            #endregion

            consoleInput.KeyDown += ConsoleInput_KeyDown;
            loadSelectedLevelButton.Click += (sender, e) =>
            {
                int modeIndex = gamemodeComboBox.SelectedIndex;
                int levelIndex = levelComboBox.SelectedIndex;
                if (modeIndex < 0 || modeIndex >= gameModesData.Count || levelIndex < 0 || levelIndex >= gameModesData[modeIndex].Item3.Count)
                    return;

                string selectedLevelId = gameModesData[modeIndex].Item3[levelIndex].Item1;
                string selectedGameModeId = gameModesData[modeIndex].Item1;
                ExecuteCommand2($"Kyber.LoadLevel {selectedLevelId} {selectedGameModeId}");
            };

            gamemodeComboBox.SelectedIndex = gameModesData.Select(data => data.Item1).Contains(KyberSettings.GameMode) ? gameModesData.Select(data => data.Item1).ToList().IndexOf(KyberSettings.GameMode) : 0;
            RepopulateLevelComboBox();
            levelComboBox.SelectedIndex = gameModesData[gamemodeComboBox.SelectedIndex].Item3.Select(data => data.Item1).Contains(KyberSettings.Level) ? gameModesData[gamemodeComboBox.SelectedIndex].Item3.Select(data => data.Item1).ToList().IndexOf(KyberSettings.Level) : 0;

            instance = this;

            channel = GrpcChannel.ForAddress(ServerUrl);

            headers.Add("user-agent", "AdamRaichu_FlurryEditorPlugin_KyberServerManager_InDev");
            headers.Add("x-kyber-version", "not-kyber");
            KyberToken.Wait();
            headers.Add("authorization", KyberToken.Result);
            
            var authclient = new Authentication.AuthenticationClient(channel);
            EaToken = GetEaTokenFromAuthToml();
            LoginResponse loginResp = authclient.Login(new LoginRequest { Token = EaToken }, headers);
            loggedInAs.Text = "Logged in as: " + loginResp.Name;

            RefreshIntervalTimer = new Timer(RefreshIntervalTimerCallback, null, 0, 8000);
        }

        private void RepopulateLevelComboBox()
        {
            string curLevel = levelComboBox.Text;
            levelComboBox.Items.Clear();

            if (gamemodeComboBox.SelectedIndex < 0 || gamemodeComboBox.SelectedIndex >= gameModesData.Count)
                return;

            List<(string, string)> levels = gameModesData[gamemodeComboBox.SelectedIndex].Item3;
            foreach ((string, string) pair in levels)
            {
                levelComboBox.Items.Add(pair.Item2);
            }

            int preservedLevelIndex = levels.FindIndex(pair => pair.Item2 == curLevel);
            levelComboBox.SelectedIndex = preservedLevelIndex >= 0 ? preservedLevelIndex : levelComboBox.Items.Count > 0 ? 0 : -1;
        }

        public void AppendToConsole(string message)
        {
            Dispatcher.Invoke(() =>
            {
                consoleOutput.AppendText(message + Environment.NewLine);
                consoleOutput.ScrollToEnd();
            });
        }

        private void ConsoleInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                string command = consoleInput.Text;
                if (!ExecuteCommand2(command))
                {
                    AppendToConsole("Failed to execute command: " + command);
                    AppendToConsole("(Make sure you have a server running and/or wait 8 seconds.)");
                }
                consoleInput.Clear();
            }
        }

        public static bool ExecuteCommand(string command)
        {
            return instance.ExecuteCommand2(command);
        }

        public bool ExecuteCommand2(string command)
        {
            if (TargetServerApiId == "")
            {
                App.Logger.Log("No target server found, cannot execute command.");
                return false;
            }

            try
            {
                var mgmtclient = new ServerManagement.ServerManagementClient(channel);
                mgmtclient.RunCommand(new ServerRunCommandRequest() { Command = command, Id = TargetServerApiId }, headers);
                AppendToConsole("Executed command: " + command);
                return true;
            }
            catch (Exception ex)
            {
                AppendToConsole("Error executing command: " + ex.Message);
                return false;
            }
        }

        private void RefreshIntervalTimerCallback(object state)
        {
            var mgmtclient = new ServerManagement.ServerManagementClient(channel);
            ServerList resp = mgmtclient.ModeratedServers(empty, headers);
            foreach (var server in resp.Servers)
            {
                if (server.Name == TargetServerGuidName)
                {
                    //App.Logger.Log("Found target server: " + server.Name + " | ID: " + server.Id);
                    TargetServerApiId = server.Id;
                    return;
                }
                TargetServerApiId = "";
            }
        }

        #region Get auth tokens

        private static async Task<string> GetKyberTokenFromCli()
        {
            return await Task.Run(() =>
            {
                App.Logger.Log("Getting Kyber auth token from CLI...");
                Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = KyberSettings.CliDirectory,
                    Arguments = "get_token",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                process.WaitForExit();
                string output = process.StandardOutput.ReadToEnd();
                Match match = Regex.Match(output, "Kyber auth token: (.+)");
                App.Logger.Log("Found kyber auth token.");
                return match.Groups[1].Value.Trim();
            });
        }

        private string GetEaTokenFromAuthToml()
        {
            TomlTable authToml = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArmchairDevelopers", "Maxima", "data", "auth.toml")));
            string currentAccount = (string)authToml["selected"];
            return (string)((dynamic)authToml["accounts"])[currentAccount]["access_token"];
        }
        #endregion
    }

    public class KyberServerManagementTabExtension : TabExtension
    {
        public override string TabItemName => "Kyber Server";
        public override ImageSource TabItemIcon => new ImageSourceConverter().ConvertFromString("pack://application:,,,/FlurryEditorPlugin;component/Images/KyberCog.png") as ImageSource;
        public override FrostyTabItem TabContent => new KyberServerManagementTabItem();
    }

    public class AuthInitStartupAction : StartupAction
    {
        public override Action<ILogger> Action => (logger) =>
        {
            KyberServerManagementTabItem.HelloWorld();
        };
    }
}
