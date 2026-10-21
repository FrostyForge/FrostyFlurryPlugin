using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Flurry.Editor.Windows
{
    public class SimpleInputDialog : Window
    {
        public string InputText { get; private set; }

        private TextBox textBox;

        public SimpleInputDialog(string title, string prompt, string defaultValue = "")
        {
            Title = title;
            Width = 420;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.ToolWindow;

            // Use Frosty theme colors
            Brush bgBrush = TryFindBrush("WindowBackground") ?? new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
            Brush fgBrush = TryFindBrush("FontColor") ?? new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF8));
            Brush ctrlBg = TryFindBrush("ControlBackground") ?? new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x45));

            Background = bgBrush;

            var panel = new StackPanel { Margin = new Thickness(14) };

            panel.Children.Add(new TextBlock
            {
                Text = prompt,
                TextWrapping = TextWrapping.Wrap,
                Foreground = fgBrush,
                Margin = new Thickness(0, 0, 0, 10)
            });

            textBox = new TextBox
            {
                Text = defaultValue,
                Background = ctrlBg,
                Foreground = fgBrush,
                CaretBrush = fgBrush,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70)),
                Padding = new Thickness(4, 3, 4, 3),
                Margin = new Thickness(0, 0, 0, 14)
            };
            textBox.SelectAll();
            panel.Children.Add(textBox);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var okButton = new Button
            {
                Content = "OK",
                Width = 80,
                Padding = new Thickness(0, 4, 0, 4),
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            okButton.Click += (s, e) =>
            {
                InputText = textBox.Text;
                DialogResult = true;
            };
            buttonPanel.Children.Add(okButton);

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 80,
                Padding = new Thickness(0, 4, 0, 4),
                IsCancel = true
            };
            buttonPanel.Children.Add(cancelButton);

            panel.Children.Add(buttonPanel);
            Content = panel;

            Loaded += (s, e) => textBox.Focus();
        }

        private static Brush TryFindBrush(string key)
        {
            if (Application.Current != null && Application.Current.Resources.Contains(key))
                return Application.Current.Resources[key] as Brush;
            return null;
        }

        public static string Show(string title, string prompt, string defaultValue = "", Window owner = null)
        {
            var dialog = new SimpleInputDialog(title, prompt, defaultValue);
            if (owner != null)
                dialog.Owner = owner;
            return dialog.ShowDialog() == true ? dialog.InputText : null;
        }
    }
}
