using System.Windows;
using System.Windows.Controls;

namespace TIAOpennessStudio.Controls
{
    public partial class StatusBarControl : UserControl
    {
        public static readonly DependencyProperty StatusTextProperty =
            DependencyProperty.Register(nameof(StatusText), typeof(string), typeof(StatusBarControl),
                new PropertyMetadata("Ready", OnStatusTextChanged));

        public static readonly DependencyProperty ProgressTextProperty =
            DependencyProperty.Register(nameof(ProgressText), typeof(string), typeof(StatusBarControl),
                new PropertyMetadata("", OnProgressTextChanged));

        public static readonly DependencyProperty InfoTextProperty =
            DependencyProperty.Register(nameof(InfoText), typeof(string), typeof(StatusBarControl),
                new PropertyMetadata("", OnInfoTextChanged));

        public static readonly DependencyProperty IsProgressVisibleProperty =
            DependencyProperty.Register(nameof(IsProgressVisible), typeof(bool), typeof(StatusBarControl),
                new PropertyMetadata(false, OnIsProgressVisibleChanged));

        public string StatusText
        {
            get => (string)GetValue(StatusTextProperty);
            set => SetValue(StatusTextProperty, value);
        }

        public string ProgressText
        {
            get => (string)GetValue(ProgressTextProperty);
            set => SetValue(ProgressTextProperty, value);
        }

        public string InfoText
        {
            get => (string)GetValue(InfoTextProperty);
            set => SetValue(InfoTextProperty, value);
        }

        public bool IsProgressVisible
        {
            get => (bool)GetValue(IsProgressVisibleProperty);
            set => SetValue(IsProgressVisibleProperty, value);
        }

        public StatusBarControl()
        {
            InitializeComponent();
        }

        private static void OnStatusTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StatusBarControl control)
            {
                control.lblStatus.Text = e.NewValue as string ?? "Ready";
            }
        }

        private static void OnProgressTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StatusBarControl control)
            {
                control.lblProgress.Text = e.NewValue as string ?? "";
            }
        }

        private static void OnInfoTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StatusBarControl control)
            {
                control.lblInfo.Text = e.NewValue as string ?? "";
            }
        }

        private static void OnIsProgressVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StatusBarControl control)
            {
                control.stackProgress.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }
}
