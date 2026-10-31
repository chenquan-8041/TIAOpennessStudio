using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// WPF implementation of IDialogService using MessageBox.
    /// </summary>
    public class DialogService : IDialogService
    {
        public void ShowWarning(string message, string title)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        public void ShowInformation(string message, string title)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public void ShowError(string message, string title)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        public bool ShowConfirmation(string message, string title)
        {
            return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        public bool ShowDiffConfirmDialog(string targetName, string diffText)
        {
            var dialog = new Window
            {
                Title = $"Apply Fix Preview - {targetName}",
                Width = 900,
                Height = 650,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E1E"))
            };

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = $"Target: {targetName}  |  Diff Preview",
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(title, 0);
            grid.Children.Add(title);

            var txt = new TextBox
            {
                Text = diffText,
                IsReadOnly = true,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252526")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3E3E42"))
            };
            Grid.SetRow(txt, 1);
            grid.Children.Add(txt);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 8, 0) };
            var apply = new Button { Content = "Apply", Padding = new Thickness(18, 6, 18, 6), Background = Brushes.DodgerBlue };
            cancel.Click += (s, e) => { dialog.DialogResult = false; dialog.Close(); };
            apply.Click += (s, e) => { dialog.DialogResult = true; dialog.Close(); };
            buttons.Children.Add(cancel);
            buttons.Children.Add(apply);
            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);

            dialog.Content = grid;
            return dialog.ShowDialog() == true;
        }
    }
}