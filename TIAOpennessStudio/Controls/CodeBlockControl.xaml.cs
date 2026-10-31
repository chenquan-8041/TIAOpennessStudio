using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Highlighting;
using Prism.Events;
using Prism.Ioc;
using TIAOpennessStudio.Events;
using TIAOpennessStudio.Services;

namespace TIAOpennessStudio.Controls
{
    /// <summary>
    /// Code block control that uses AvalonEdit for syntax highlighting
    /// </summary>
    public partial class CodeBlockControl : UserControl
    {
        #region Dependency Properties

        public static readonly DependencyProperty CodeProperty =
            DependencyProperty.Register(nameof(Code), typeof(string), typeof(CodeBlockControl),
                new PropertyMetadata(null, OnCodeChanged));

        public static new readonly DependencyProperty LanguageProperty =
            DependencyProperty.Register(nameof(Language), typeof(string), typeof(CodeBlockControl),
                new PropertyMetadata("scl", OnLanguageChanged));

        public static readonly DependencyProperty FileNameProperty =
            DependencyProperty.Register(nameof(FileName), typeof(string), typeof(CodeBlockControl),
                new PropertyMetadata(null, OnFileNameChanged));

        public string Code
        {
            get => (string)GetValue(CodeProperty);
            set => SetValue(CodeProperty, value);
        }

        public new string Language
        {
            get => (string)GetValue(LanguageProperty);
            set => SetValue(LanguageProperty, value);
        }

        public string FileName
        {
            get => (string)GetValue(FileNameProperty);
            set => SetValue(FileNameProperty, value);
        }

        #endregion

        public CodeBlockControl()
        {
            InitializeComponent();
            ConfigureEditor();

            // The Language DP already holds its default value, so OnLanguageChanged never fires
            // for a block that uses the default language — seed the label here.
            LanguageLabel.Text = (Language ?? "scl").ToUpperInvariant();

            _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
            _flashTimer.Tick += (s, e) =>
            {
                _flashTimer.Stop();
                if (_flashButton != null) _flashButton.Content = _flashOriginalLabel;
                _flashButton = null;
                _flashOriginalLabel = null;
            };

            // Three-language labels/tooltips for the header actions.
            LanguageManager.Instance.LanguageChanged += OnLanguageChanged;
            ApplyLanguage();
            Unloaded += (_, __) => LanguageManager.Instance.LanguageChanged -= OnLanguageChanged;
        }

        private DispatcherTimer _flashTimer;
        private Button _flashButton;
        private object _flashOriginalLabel;

        private void OnLanguageChanged(AppLanguage lang) => ApplyLanguage();

        private void ApplyLanguage()
        {
            var lm = LanguageManager.Instance;
            if (btnCopy != null) { btnCopy.Content = lm.GetString("Code.Copy"); btnCopy.ToolTip = lm.GetString("Code.CopyTip"); }
            if (btnInsert != null) { btnInsert.Content = lm.GetString("Code.Insert"); btnInsert.ToolTip = lm.GetString("Code.InsertTip"); }
            if (btnSave != null) { btnSave.Content = lm.GetString("Code.Save"); btnSave.ToolTip = lm.GetString("Code.SaveTip"); }
        }

        // ---- Header actions: copy / insert into the chat prompt / save to the Explorer folder ----

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(Code)) return;
            try
            {
                Clipboard.SetText(Code);
                Flash(btnCopy, "Code.Copied");
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Copy code block failed", ex);
            }
        }

        private void Insert_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(Code)) return;
            var aggregator = App.Services?.Resolve<IEventAggregator>();
            if (aggregator == null) return;
            aggregator.GetEvent<InsertCodeToInputEvent>().Publish(Code);
            Flash(btnInsert, "Code.Inserted");
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(Code)) return;
            var aggregator = App.Services?.Resolve<IEventAggregator>();
            if (aggregator == null) return;

            var payload = new SaveCodeBlockPayload
            {
                Text = Code,
                Language = Language,
                FileName = FileName
            };
            payload.Completed = (ok, message) => Flash(btnSave, ok ? "Code.Saved" : "Code.SaveFailed");
            aggregator.GetEvent<SaveCodeBlockEvent>().Publish(payload);
        }

        /// <summary>Temporarily replaces the button label with a localized result, then restores it.</summary>
        private void Flash(Button button, string resourceKey)
        {
            if (button == null) return;
            _flashTimer.Stop();
            if (_flashButton != null) _flashButton.Content = _flashOriginalLabel;

            _flashButton = button;
            _flashOriginalLabel = button.Content;
            button.Content = LanguageManager.Instance.GetString(resourceKey);
            _flashTimer.Start();
        }

        private void ConfigureEditor()
        {
            // Set theme colors
            CodeEditor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            CodeEditor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromRgb(38, 79, 120));
            CodeEditor.TextArea.TextView.LinkTextForegroundBrush = new SolidColorBrush(Color.FromRgb(58, 150, 221));
            // Set editor options
            CodeEditor.Options.ConvertTabsToSpaces = true;
            CodeEditor.Options.IndentationSize = 4;
        }

        private static void OnCodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CodeBlockControl control && e.NewValue is string code)
            {
                control.CodeEditor.Text = code;
                control.ApplySyntaxHighlighting();
            }
        }

        private static void OnLanguageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CodeBlockControl control)
            {
                control.LanguageLabel.Text = e.NewValue?.ToString()?.ToUpper() ?? "TEXT";
                control.ApplySyntaxHighlighting();
            }
        }

        private static void OnFileNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CodeBlockControl control)
            {
                control.FileNameLabel.Text = e.NewValue as string ?? "";
            }
        }

        private void ApplySyntaxHighlighting()
        {
            if (string.IsNullOrEmpty(Code))
                return;

            var language = (Language ?? "scl").ToLower();

            switch (language)
            {
                case "xml":
                case "aml":
                    CodeEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("XML");
                    break;

                case "scl":
                case "stl":
                case "awl":
                    // SCL uses a custom syntax colorizer
                    CodeEditor.SyntaxHighlighting = null;
                    CodeEditor.TextArea.TextView.LineTransformers.Clear();
                    CodeEditor.TextArea.TextView.LineTransformers.Add(new SclSyntaxColorizer());
                    break;

                case "csharp":
                case "cs":
                case "c#":
                    CodeEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
                    break;

                case "json":
                    CodeEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("JSON") 
                        ?? HighlightingManager.Instance.GetDefinition("JavaScript");
                    break;

                case "javascript":
                case "js":
                    CodeEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("JavaScript");
                    break;

                default:
                    // No syntax highlighting by default
                    CodeEditor.SyntaxHighlighting = null;
                    CodeEditor.TextArea.TextView.LineTransformers.Clear();
                    break;
            }
        }
    }
}