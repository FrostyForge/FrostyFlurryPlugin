using Flurry.Editor.Editors;
using Frosty.Controls;
using Frosty.Core;
using FrostySdk.Ebx;
using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Flurry.Editor.Windows
{
    /// <summary>
    /// Interaction logic for KyberSettingsWindow.xaml
    /// </summary>
    public partial class KyberSettingsWindow : FrostyDockableWindow
    {
        List<(string, string, List<(string, string)>, int)> gameModesData = new List<(string, string, List<(string, string)>, int)>();
        KyberJsonSettings jsonSettings = new KyberJsonSettings();

        public KyberSettingsWindow(KyberJsonSettings jsonSettings)
        {
            this.jsonSettings = jsonSettings;
            InitializeComponent();

            Loaded += ModSettingsWindow_Loaded;
        }

        private string ConvertToTitleCase(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            // Convert the string to lowercase
            input = input.ToLower();

            // Capitalize the first letter of each word
            string[] words = input.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                if (!string.IsNullOrEmpty(words[i]))
                {
                    char[] letters = words[i].ToCharArray();
                    letters[0] = char.ToUpper(letters[0]);
                    words[i] = new string(letters);
                }
            }

            return string.Join(" ", words);
        }

        private void RepopulateLevelComboBox(int oldPlayerCount)
        {
            string curLevel = levelComboBox.Text;
            levelComboBox.Items.Clear();
            foreach ((string, string) pair in gameModesData[gamemodeComboBox.SelectedIndex].Item3)
            {
                levelComboBox.Items.Add(pair.Item2);
                if (pair.Item2 == curLevel)
                    levelComboBox.SelectedIndex = levelComboBox.Items.Count - 1;
                else if (levelComboBox.Items.Count == 1)
                    levelComboBox.SelectedIndex = 0;
            }

            if ((Convert.ToInt32(Math.Floor((float)oldPlayerCount / 2) * 2) == team1AutoplayerCountComboBox.SelectedIndex + team2AutoplayerCountComboBox.SelectedIndex))
            {
                int newPlayerCount = gameModesData[gamemodeComboBox.SelectedIndex].Item4;
                int perTeamCount = Convert.ToInt32(Math.Floor((float)newPlayerCount / 2));
                team1AutoplayerCountComboBox.SelectedIndex = perTeamCount;
                team2AutoplayerCountComboBox.SelectedIndex = perTeamCount;
            }
            //else
            //{
            //    App.Logger.Log(oldPlayerCount.ToString());
            //    App.Logger.Log((team1AutoplayerCountComboBox.SelectedIndex + team2AutoplayerCountComboBox.SelectedIndex).ToString());
            //}
        }

        private void ModSettingsWindow_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            teamIdComboBox.Items.Add("TeamNeutral");
            for (int i = 0; i < 16; i++)
                teamIdComboBox.Items.Add($"Team{i + 1}");
            for (int i = 0; i < 33; i++)
            {
                team1AutoplayerCountComboBox.Items.Add((int)i);
                team2AutoplayerCountComboBox.Items.Add((int)i);
            }
            loadOrderComboBox.Items.Add("No Order");
            jsonSettings.LoadOrders.ForEach(loadOrder => loadOrderComboBox.Items.Add(loadOrder.Name));

            autoplayerTypeComboBox.Items.Add("No Bots");
            autoplayerTypeComboBox.Items.Add("Dummy Bots");
            autoplayerTypeComboBox.Items.Add("Gamemode Tied");

            autoStartComboBox.Items.Add("Disabled");
            autoStartComboBox.Items.Add("Enabled");

            frontendComboBox.Items.Add("Disabled");
            frontendComboBox.Items.Add("Enabled");

            debugModeComboBox.Items.Add("Disabled");
            debugModeComboBox.Items.Add("Enabled");

            //Fill Gamemodes/Levels
            EbxAssetEntry modesEntry = App.AssetManager.GetEbxEntry("UI/Data/GameModes/GameModes");
            dynamic modesRoot = App.AssetManager.GetEbx(modesEntry).RootObject;

            // KyberLevelDeclaratorPlugin
            /*
            [REDACTED]
            */

            foreach (PointerRef modePr in modesRoot.GameModes)
            {
                EbxAssetEntry modeEntry = App.AssetManager.GetEbxEntry(modePr.External.FileGuid);
                if (modeEntry != null)
                {
                    dynamic modeRoot = App.AssetManager.GetEbx(modeEntry).RootObject;
                    string modeName = $"{ConvertToTitleCase(modeRoot.AurebeshGameModeName)} \t[{modeRoot.GameModeId}]";

                    List<(string, string)> levelPairs = new List<(string, string)>();
                    foreach (PointerRef levelPr in modeRoot.Levels)
                    {
                        EbxAssetEntry levEntry = App.AssetManager.GetEbxEntry(levelPr.External.FileGuid);
                        if (levEntry != null)
                        {
                            dynamic levRoot = App.AssetManager.GetEbx(levEntry).RootObject;
                            string aurabesh = ConvertToTitleCase(levRoot.LevelAurebesh.Internal.String);
                            if (aurabesh.StartsWith("Yavin") && aurabesh.EndsWith("4"))
                                aurabesh = "Yavin IV";
                            else if (aurabesh == "Death Star Ii")
                                aurabesh = "Death Star II";

                            string levName = $"{aurabesh} - {ConvertToTitleCase(LocalizedStringDatabase.Current.GetString((uint)levRoot.LevelName.Internal.StringHash))}";

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
            //gamemodeComboBox.SelectedIndex = 0;

            //Set selected index
            kyberCliTextBox.Text = KyberSettings.CliDirectory;
            moduleBranchTextInput.Text = KyberSettings.ModuleBranch;
            loadOrderComboBox.SelectedIndex = loadOrderComboBox.Items.Contains(KyberSettings.SelectedLoadOrder) ? loadOrderComboBox.Items.IndexOf(KyberSettings.SelectedLoadOrder) : 0;
            gamemodeComboBox.SelectedIndex = gameModesData.Select(data => data.Item1).Contains(KyberSettings.GameMode) ? gameModesData.Select(data => data.Item1).ToList().IndexOf(KyberSettings.GameMode) : 0;
            RepopulateLevelComboBox(0);
            levelComboBox.SelectedIndex = gameModesData[gamemodeComboBox.SelectedIndex].Item3.Select(data => data.Item1).Contains(KyberSettings.Level) ? gameModesData[gamemodeComboBox.SelectedIndex].Item3.Select(data => data.Item1).ToList().IndexOf(KyberSettings.Level) : 0;
            frontendComboBox.SelectedIndex = KyberSettings.FrontendLaunch ? 1 : 0;
            autoStartComboBox.SelectedIndex = KyberSettings.Autostart ? 1 : 0;
            debugModeComboBox.SelectedIndex = KyberSettings.DebugMode ? 1 : 0;
            teamIdComboBox.SelectedIndex = KyberSettings.TeamId;
            team1AutoplayerCountComboBox.SelectedIndex = KyberSettings.Team1Bots;
            team2AutoplayerCountComboBox.SelectedIndex = KyberSettings.Team2Bots;
            autoplayerTypeComboBox.SelectedIndex = autoplayerTypeComboBox.Items.Contains(KyberSettings.AutoplayerType) ? autoplayerTypeComboBox.Items.IndexOf(KyberSettings.AutoplayerType) : 0;
            launchCommandTextBox.Text = string.Join("\n", KyberSettings.LaunchCommands);

            lastGamemodeIndex = gamemodeComboBox.SelectedIndex;
        }
        private int lastGamemodeIndex = -1;

        private void modCategoryComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (lastGamemodeIndex != -1)
            {
                RepopulateLevelComboBox(gameModesData[lastGamemodeIndex].Item4);
                lastGamemodeIndex = gamemodeComboBox.SelectedIndex;
            }
        }

        private void cancelButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void saveButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            KyberSettings.CliDirectory = kyberCliTextBox.Text;
            KyberSettings.SelectedLoadOrder = loadOrderComboBox.Text;
            KyberSettings.FrontendLaunch = (frontendComboBox.Text == "Enabled");
            KyberSettings.GameMode = gameModesData[gamemodeComboBox.SelectedIndex].Item1;
            KyberSettings.Level = gameModesData[gamemodeComboBox.SelectedIndex].Item3[levelComboBox.SelectedIndex].Item1;
            KyberSettings.Autostart = (autoStartComboBox.Text == "Enabled");
            KyberSettings.DebugMode = (debugModeComboBox.Text == "Enabled");
            KyberSettings.ModuleBranch = moduleBranchTextInput.Text;
            KyberSettings.TeamId = teamIdComboBox.SelectedIndex;
            KyberSettings.AutoplayerType = autoplayerTypeComboBox.Text;
            KyberSettings.Team1Bots = team1AutoplayerCountComboBox.SelectedIndex;
            KyberSettings.Team2Bots = team2AutoplayerCountComboBox.SelectedIndex;
            KyberSettings.LaunchCommands = launchCommandTextBox.Text.Split('\n').ToList();
            Config.Save();

            DialogResult = true;
            Close();
        }

        private void EditLoadOrder_click(object sender, System.Windows.RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
            App.EditorWindow.OpenEditor("[Flurry] Kyber Overrides Editor", new KyberLaunchOverridesEditor());
        }
    }
}
