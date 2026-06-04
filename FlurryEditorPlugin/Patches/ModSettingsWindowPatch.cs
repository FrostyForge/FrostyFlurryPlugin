using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Mod;
using FrostyEditor.Windows;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Flurry.Editor.Patches
{
    [HarmonyPatch(typeof(ModSettingsWindow))]
    [HarmonyPatchCategory("flurry.editor")]
    public static class ModSettingsWindowPatch
    {
        private sealed class WindowState
        {
            public string CustomCategory { get; set; } = string.Empty;
        }

        private sealed class WindowContext
        {
            public ModSettingsWindow Window { get; set; }
            public FrameworkElement Root { get; set; }
            public ModSettings ModSettings { get; set; }
            public IList<string> Categories { get; set; }
            public TextBox TitleTextBox { get; set; }
            public TextBox AuthorTextBox { get; set; }
            public TextBox VersionTextBox { get; set; }
            public ComboBox CategoryComboBox { get; set; }
            public TextBox CategoryTextBox { get; set; }
            public TextBox LinkTextBox { get; set; }
            public TextBox DescriptionTextBox { get; set; }
            public object IconButton { get; set; }
            public object[] ScreenshotButtons { get; set; }
        }

        private static readonly ConditionalWeakTable<ModSettingsWindow, WindowState> s_windowStates
            = new ConditionalWeakTable<ModSettingsWindow, WindowState>();

        [ThreadStatic]
        private static bool s_syncingSelection;

        [HarmonyPatch("ModSettingsWindow_Loaded")]
        [HarmonyPrefix]
        public static bool LoadedPrefix(ModSettingsWindow __instance)
        {
            try
            {
                return !TryInitializeWindow(__instance);
            }
            catch
            {
                return true;
            }
        }

        [HarmonyPatch("modCategoryComboBox_SelectionChanged")]
        [HarmonyPrefix]
        public static bool CategorySelectionPrefix(ModSettingsWindow __instance)
        {
            if (s_syncingSelection)
                return false;

            try
            {
                return !TryApplyCategorySelection(__instance, true);
            }
            catch
            {
                return true;
            }
        }

        [HarmonyPatch("saveButton_Click")]
        [HarmonyPrefix]
        public static bool SaveButtonPrefix(ModSettingsWindow __instance)
        {
            try
            {
                return !TrySave(__instance);
            }
            catch
            {
                return true;
            }
        }

        private static bool TryInitializeWindow(ModSettingsWindow window)
        {
            if (!TryCreateContext(window, out WindowContext context))
                return false;

            WindowState state = s_windowStates.GetOrCreateValue(window);
            state.CustomCategory = ExtractCustomCategory(context.ModSettings.Category, context.Categories);

            context.CategoryComboBox.ItemsSource = null;
            context.CategoryComboBox.ItemsSource = context.Categories;

            context.TitleTextBox.Text = context.ModSettings.Title ?? string.Empty;
            context.AuthorTextBox.Text = context.ModSettings.Author ?? string.Empty;
            context.VersionTextBox.Text = context.ModSettings.Version ?? string.Empty;
            context.DescriptionTextBox.Text = context.ModSettings.Description ?? string.Empty;
            context.LinkTextBox.Text = context.ModSettings.Link ?? string.Empty;

            SetImage(context.IconButton, context.ModSettings.Icon);
            for (int i = 0; i < context.ScreenshotButtons.Length; i++)
                SetImage(context.ScreenshotButtons[i], context.ModSettings.GetScreenshot(i));

            int selectedIndex = ResolveSelectedCategoryIndex(context.ModSettings, context.Categories);
            SetSelectedIndex(context.CategoryComboBox, selectedIndex);

            ApplyWindowPolish(context);
            ApplyCategorySelection(context, state, false);
            return true;
        }

        private static bool TryApplyCategorySelection(ModSettingsWindow window, bool preserveTypedText)
        {
            if (!TryCreateContext(window, out WindowContext context))
                return false;

            WindowState state = s_windowStates.GetOrCreateValue(window);
            if (string.IsNullOrWhiteSpace(state.CustomCategory))
                state.CustomCategory = ExtractCustomCategory(context.ModSettings.Category, context.Categories);

            ApplyCategorySelection(context, state, preserveTypedText);
            return true;
        }

        private static bool TrySave(ModSettingsWindow window)
        {
            if (!TryCreateContext(window, out WindowContext context))
                return false;

            WindowState state = s_windowStates.GetOrCreateValue(window);

            string title = (context.TitleTextBox.Text ?? string.Empty).Trim();
            string author = (context.AuthorTextBox.Text ?? string.Empty).Trim();
            string version = (context.VersionTextBox.Text ?? string.Empty).Trim();

            string selectedCategory = context.CategoryComboBox.SelectedItem as string;
            bool isCustomCategory = string.IsNullOrWhiteSpace(selectedCategory)
                || string.Equals(selectedCategory, "Custom", StringComparison.OrdinalIgnoreCase);

            string category = isCustomCategory
                ? (context.CategoryTextBox.Text ?? string.Empty).Trim()
                : selectedCategory;

            if (string.IsNullOrWhiteSpace(title)
                || string.IsNullOrWhiteSpace(author)
                || string.IsNullOrWhiteSpace(category)
                || string.IsNullOrWhiteSpace(version))
            {
                FrostyMessageBox.Show("Title, Author, Category and Version are mandatory fields", "Frosty Editor");
                return true;
            }

            string normalizedLink = string.Empty;
            string rawLink = (context.LinkTextBox.Text ?? string.Empty).Trim();
            if (!TryNormalizeApprovedLink(rawLink, out normalizedLink))
            {
                FrostyMessageBox.Show("Link needs to be a full Nexus Mods or Mod DB URL", "Frosty Editor");
                return true;
            }

            if (isCustomCategory)
                state.CustomCategory = category;

            context.ModSettings.Title = title;
            context.ModSettings.Author = author;
            context.ModSettings.Category = category;
            context.ModSettings.SelectedCategory = isCustomCategory
                ? 0
                : Math.Max(context.CategoryComboBox.SelectedIndex, 0);
            context.ModSettings.Version = version;
            context.ModSettings.Description = context.DescriptionTextBox.Text ?? string.Empty;
            context.ModSettings.Link = normalizedLink;
            context.ModSettings.Icon = GetImage(context.IconButton);

            for (int i = 0; i < context.ScreenshotButtons.Length; i++)
                context.ModSettings.SetScreenshot(i, GetImage(context.ScreenshotButtons[i]));

            context.Window.DialogResult = true;
            context.Window.Close();
            return true;
        }

        private static void ApplyCategorySelection(WindowContext context, WindowState state, bool preserveTypedText)
        {
            string currentText = (context.CategoryTextBox.Text ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(currentText) && !IsKnownCategory(currentText, context.Categories))
                state.CustomCategory = currentText;

            string selectedCategory = context.CategoryComboBox.SelectedItem as string;
            bool isCustomCategory = string.IsNullOrWhiteSpace(selectedCategory)
                || string.Equals(selectedCategory, "Custom", StringComparison.OrdinalIgnoreCase);

            if (isCustomCategory)
            {
                context.CategoryTextBox.IsEnabled = true;

                string customCategory = state.CustomCategory;
                if (string.IsNullOrWhiteSpace(customCategory))
                    customCategory = ExtractCustomCategory(context.ModSettings.Category, context.Categories);

                if (!preserveTypedText
                    || string.IsNullOrWhiteSpace(currentText)
                    || IsKnownCategory(currentText, context.Categories))
                {
                    context.CategoryTextBox.Text = customCategory ?? string.Empty;
                }
            }
            else
            {
                context.CategoryTextBox.Text = selectedCategory;
                context.CategoryTextBox.IsEnabled = false;
            }
        }

        private static void ApplyWindowPolish(WindowContext context)
        {
            context.Window.Title = "Mod Info";
            context.Window.ResizeMode = ResizeMode.CanResizeWithGrip;
            context.Window.SizeToContent = SizeToContent.Manual;
            context.Window.MinWidth = 640;
            context.Window.MinHeight = 560;

            if (context.Window.Width < 760)
                context.Window.Width = 760;
            if (context.Window.Height < 660)
                context.Window.Height = 660;

            context.DescriptionTextBox.MinHeight = 180;
            context.DescriptionTextBox.VerticalContentAlignment = VerticalAlignment.Top;
            context.LinkTextBox.ToolTip = "Use a full Nexus Mods or Mod DB URL.";
            context.CategoryTextBox.ToolTip = "Select a preset category or enter your own custom category.";

            foreach (Label label in FindVisualChildren<Label>(context.Root))
            {
                if (label.Content is string content
                    && content.IndexOf("Only the first section is mandatory", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    label.Content = "Title, Author, Category, and Version are required. Links support Nexus Mods and Mod DB.";
                    label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
                    label.HorizontalContentAlignment = HorizontalAlignment.Center;
                    break;
                }
            }
        }

        private static bool TryCreateContext(ModSettingsWindow window, out WindowContext context)
        {
            context = null;

            FrameworkElement root = window;
            ModSettings modSettings = AccessTools.Property(window.GetType(), "ModSettings")?.GetValue(window) as ModSettings;
            IList<string> categories = AccessTools.Field(window.GetType(), "categories")?.GetValue(window) as IList<string>;

            TextBox titleTextBox = root.FindName("modTitleTextBox") as TextBox;
            TextBox authorTextBox = root.FindName("modAuthorTextBox") as TextBox;
            TextBox versionTextBox = root.FindName("modVersionTextBox") as TextBox;
            ComboBox categoryComboBox = root.FindName("modCategoryComboBox") as ComboBox;
            TextBox categoryTextBox = root.FindName("modCategoryTextBox") as TextBox;
            TextBox linkTextBox = root.FindName("modPageLinkTextBox") as TextBox;
            TextBox descriptionTextBox = root.FindName("modDescriptionTextBox") as TextBox;

            if (modSettings == null
                || categories == null
                || titleTextBox == null
                || authorTextBox == null
                || versionTextBox == null
                || categoryComboBox == null
                || categoryTextBox == null
                || linkTextBox == null
                || descriptionTextBox == null)
            {
                return false;
            }

            context = new WindowContext
            {
                Window = window,
                Root = root,
                ModSettings = modSettings,
                Categories = categories,
                TitleTextBox = titleTextBox,
                AuthorTextBox = authorTextBox,
                VersionTextBox = versionTextBox,
                CategoryComboBox = categoryComboBox,
                CategoryTextBox = categoryTextBox,
                LinkTextBox = linkTextBox,
                DescriptionTextBox = descriptionTextBox,
                IconButton = root.FindName("iconImageButton"),
                ScreenshotButtons = new[]
                {
                    root.FindName("ssImageButton1"),
                    root.FindName("ssImageButton2"),
                    root.FindName("ssImageButton3"),
                    root.FindName("ssImageButton4")
                }
            };

            return true;
        }

        private static int ResolveSelectedCategoryIndex(ModSettings modSettings, IList<string> categories)
        {
            if (modSettings.SelectedCategory >= 0 && modSettings.SelectedCategory < categories.Count)
                return modSettings.SelectedCategory;

            int categoryIndex = GetCategoryIndex(modSettings.Category, categories);
            return categoryIndex >= 0 ? categoryIndex : 0;
        }

        private static int GetCategoryIndex(string category, IList<string> categories)
        {
            if (string.IsNullOrWhiteSpace(category))
                return -1;

            for (int i = 0; i < categories.Count; i++)
            {
                if (string.Equals(categories[i], category, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        private static string ExtractCustomCategory(string category, IList<string> categories)
        {
            if (string.IsNullOrWhiteSpace(category) || IsKnownCategory(category, categories))
                return string.Empty;

            return category.Trim();
        }

        private static bool IsKnownCategory(string category, IList<string> categories)
        {
            if (string.IsNullOrWhiteSpace(category))
                return false;

            return categories.Any(item => string.Equals(item, category, StringComparison.OrdinalIgnoreCase));
        }

        private static void SetSelectedIndex(ComboBox comboBox, int selectedIndex)
        {
            s_syncingSelection = true;
            try
            {
                comboBox.SelectedIndex = selectedIndex;
            }
            finally
            {
                s_syncingSelection = false;
            }
        }

        private static bool TryNormalizeApprovedLink(string rawLink, out string normalizedLink)
        {
            normalizedLink = string.Empty;
            if (string.IsNullOrWhiteSpace(rawLink))
                return true;

            if (!Uri.TryCreate(rawLink.Trim(), UriKind.Absolute, out Uri uri))
                return false;

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string host = uri.Host ?? string.Empty;
            string[] approvedDomains = { "nexusmods.com", "moddb.com" };
            bool approved = approvedDomains.Any(domain =>
                string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

            if (!approved)
                return false;

            normalizedLink = uri.AbsoluteUri;
            return true;
        }

        private static void SetImage(object imageButton, byte[] data)
        {
            if (imageButton == null)
                return;

            AccessTools.Method(imageButton.GetType(), "SetImage")?.Invoke(imageButton, new object[] { data });
        }

        private static byte[] GetImage(object imageButton)
        {
            if (imageButton == null)
                return null;

            return AccessTools.Method(imageButton.GetType(), "GetImage")?.Invoke(imageButton, null) as byte[];
        }

        private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
                yield break;

            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                    yield return typedChild;

                foreach (T descendant in FindVisualChildren<T>(child))
                    yield return descendant;
            }
        }
    }
}
