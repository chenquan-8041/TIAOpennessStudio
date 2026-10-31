using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TIAOpennessStudio.Models;
using TIAOpennessStudio.Services;

namespace TIAOpennessStudio.Controls
{
    public partial class AgentInputBox : UserControl, INotifyPropertyChanged
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(AgentInputBox),
                new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public static readonly DependencyProperty SelectedModelProperty =
            DependencyProperty.Register(nameof(SelectedModel), typeof(string), typeof(AgentInputBox),
                new PropertyMetadata("qwen2.5:3b"));

        public string SelectedModel
        {
            get => (string)GetValue(SelectedModelProperty);
            set => SetValue(SelectedModelProperty, value);
        }

        public static readonly DependencyProperty IsGeneratingProperty =
            DependencyProperty.Register(nameof(IsGenerating), typeof(bool), typeof(AgentInputBox),
                new PropertyMetadata(false, OnIsGeneratingChanged));

        public bool IsGenerating
        {
            get => (bool)GetValue(IsGeneratingProperty);
            set => SetValue(IsGeneratingProperty, value);
        }

        public ObservableCollection<MentionItem> MentionItems { get; } = new ObservableCollection<MentionItem>();

        /// <summary>
        /// External search delegate: takes a search keyword and returns matching MentionItem list.
        /// Injected by the host (MainWindow) to connect to ProjectIndexService for live search.
        /// </summary>
        public Func<string, Task<List<MentionItem>>> SearchMentionItemsAsync { get; set; }

        public event EventHandler<string> SendRequested;
        public event EventHandler<(string text, List<byte[]> images)> SendWithImagesRequested;
        public event EventHandler StopRequested;
        public event EventHandler<string> ModelChanged;
        public event EventHandler<string> MentionInserted; // Kept for compatibility: name text
        public event EventHandler<MentionItem> MentionSelected; // New: full item, for host to build reference
        public event EventHandler<string> QuickCommandInvoked;
        public event EventHandler CompressRequested;

        /// <summary>Raised when the user clicks the read-only / allow-writes switch. The host flips
        /// <see cref="TIAOpennessStudio.Services.Abstractions.IAgentAccessMode.Current"/>; the control itself
        /// only reflects state via <see cref="SetAccessMode"/> and never owns it.</summary>
        public event EventHandler AccessModeToggleRequested;

        // Image attachments
        private readonly List<byte[]> _attachedImages = new List<byte[]>();
        public IReadOnlyList<byte[]> AttachedImages => _attachedImages;
        public event EventHandler ImagesChanged;

        /// <summary>
        /// Whether the current model supports image input. When false, the UI hides the image button.
        /// </summary>
        private bool _visionSupported = true;
        public bool VisionSupported
        {
            get => _visionSupported;
            set
            {
                if (_visionSupported == value) return;
                _visionSupported = value;
                // Update image button visibility
                if (ImageButton != null)
                    ImageButton.Visibility = value ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                if (!value)
                {
                    _attachedImages.Clear();
                    RefreshImagePreview();
                }
            }
        }

        private bool _isAtMentionOpen;
        private int _atMentionStartIndex;
        private bool _allowWrites;

        public AgentInputBox()
        {
            InitializeComponent();
            DataContext = this;
            AtMentionList.ItemsSource = MentionItems;
            InputTextBox.PreviewKeyDown += InputTextBox_PreviewKeyDown;
            // Everything uses DynamicResource so theme switching follows automatically; no code-behind injection needed.

            // Three languages: apply initially + refresh on language change, unbind on unload.
            LanguageManager.Instance.LanguageChanged += OnLanguageChanged;
            ApplyLanguage();
            Unloaded += (_, __) => LanguageManager.Instance.LanguageChanged -= OnLanguageChanged;
        }

        public TextBox PromptTextBox => InputTextBox;

        /// <summary>
        /// Inserts text at the current caret position of the prompt box, moves the caret to
        /// the end of the inserted text and gives the box focus. Used by chat code blocks.
        /// </summary>
        public void InsertTextAtCaret(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            var current = InputTextBox.Text ?? string.Empty;
            int caret = Math.Max(0, Math.Min(InputTextBox.CaretIndex, current.Length));
            InputTextBox.Text = current.Insert(caret, text);
            InputTextBox.CaretIndex = caret + text.Length;
            InputTextBox.Focus();
        }

        private void OnLanguageChanged(AppLanguage lang)
        {
            ApplyLanguage();
        }

        /// <summary>Applies the current language strings to the static labels and tooltips.</summary>
        private void ApplyLanguage()
        {
            var lm = LanguageManager.Instance;
            if (ModeHintText != null) ModeHintText.Text = lm.GetString("Input.ModeHint");
            if (AtButtonBottom != null) AtButtonBottom.ToolTip = lm.GetString("Input.AtTip");
            if (HashButtonBottom != null) HashButtonBottom.ToolTip = lm.GetString("Input.HashTip");
            if (ImageButton != null) ImageButton.ToolTip = lm.GetString("Input.ImageTip");
            if (ClearImagesBtn != null) ClearImagesBtn.Content = lm.GetString("Input.ClearImages");
            if (CompressBtn != null) { CompressBtn.Content = lm.GetString("Input.Compress"); CompressBtn.ToolTip = lm.GetString("Input.CompressTip"); }
            if (SendStopButton != null)
                SendStopButton.ToolTip = lm.GetString(IsGenerating ? "Input.StopTip" : "Input.SendTip");

            // Re-apply the access-mode label/tooltip/colour with the new language.
            SetAccessMode(_allowWrites);
        }

        private static void OnIsGeneratingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AgentInputBox box)
            {
                bool isGenerating = (bool)e.NewValue;
                
                // Toggle icon display
                box.SendIcon.Visibility = isGenerating ? Visibility.Collapsed : Visibility.Visible;
                box.StopIcon.Visibility = isGenerating ? Visibility.Visible : Visibility.Collapsed;
                
                // Toggle background color
                box.SendStopBorder.Background = isGenerating 
                    ? new SolidColorBrush(Color.FromRgb(244, 67, 54)) // red
                    : (Brush)box.FindResource("AccentBrush"); // blue
                
                // Update tooltip text
                box.SendStopButton.ToolTip = Services.LanguageManager.Instance.GetString(isGenerating ? "Input.StopTip" : "Input.SendTip");
                
                box.InputTextBox.IsReadOnly = isGenerating;
            }
        }

        private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_isAtMentionOpen)
            {
                if (e.Key == Key.Escape)
                {
                    CloseAtMention();
                    e.Handled = true;
                }
                else if (e.Key == Key.Down && AtMentionList.Items.Count > 0)
                {
                    AtMentionList.SelectedIndex = Math.Min(AtMentionList.SelectedIndex + 1, AtMentionList.Items.Count - 1);
                    e.Handled = true;
                }
                else if (e.Key == Key.Up && AtMentionList.Items.Count > 0)
                {
                    AtMentionList.SelectedIndex = Math.Max(AtMentionList.SelectedIndex - 1, 0);
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter && AtMentionList.SelectedItem != null)
                {
                    InsertMention(AtMentionList.SelectedItem as MentionItem);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                // Ctrl+V: first check whether the clipboard contains an image
                if (TryPasteImageFromClipboard())
                {
                    e.Handled = true;
                }
                // Otherwise fall through to the default text paste
            }
            else if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                if (!IsGenerating)
                {
                    SendMessage();
                }
            }
        }

        private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
        }

        private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = InputTextBox.Text;
            var caretIndex = InputTextBox.CaretIndex;

            if (caretIndex > 0 && text[caretIndex - 1] == '@' && !_isAtMentionOpen)
            {
                _atMentionStartIndex = caretIndex - 1;
                OpenAtMention();
            }
            else if (_isAtMentionOpen)
            {
                if (caretIndex <= _atMentionStartIndex || (caretIndex > 0 && text[caretIndex - 1] == ' '))
                {
                    CloseAtMention();
                }
                else
                {
                    FilterAtMentions(text.Substring(_atMentionStartIndex + 1, caretIndex - _atMentionStartIndex - 1));
                }
            }
        }

        private async void OpenAtMention()
        {
            _isAtMentionOpen = true;
            await LoadMentionItemsAsync();
            AtSearchBox.Text = string.Empty;
            AtMentionPopup.IsOpen = true;
            AtSearchBox.Focus();
        }

        private void CloseAtMention()
        {
            _isAtMentionOpen = false;
            AtMentionPopup.IsOpen = false;
            InputTextBox.Focus();
        }

        private async Task LoadMentionItemsAsync()
        {
            MentionItems.Clear();

            // Prefer the external search delegate (ProjectIndexService)
            if (SearchMentionItemsAsync != null)
            {
                try
                {
                    var items = await SearchMentionItemsAsync(string.Empty);
                    foreach (var item in items)
                    {
                        MentionItems.Add(item);
                    }
                    return;
                }
                catch
                {
                    // Fall back to the default list when external search fails
                }
            }

            // Default fallback list
            MentionItems.Add(new MentionItem { Name = "MotorControl", RefType = "Function", Path = "Functions/MotorControl.cs" });
            MentionItems.Add(new MentionItem { Name = "PID_Controller", RefType = "Function", Path = "Functions/PID_Controller.cs" });
            MentionItems.Add(new MentionItem { Name = "MotorData", RefType = "Udt", Path = "UDTs/MotorData.cs" });
            MentionItems.Add(new MentionItem { Name = "SensorData", RefType = "Udt", Path = "UDTs/SensorData.cs" });
            MentionItems.Add(new MentionItem { Name = "iSpeed", RefType = "Variable", Path = "TagTable/Main" });
            MentionItems.Add(new MentionItem { Name = "rTemperature", RefType = "Variable", Path = "TagTable/Main" });
            MentionItems.Add(new MentionItem { Name = "MotorControl", RefType = "File", Path = "Services/MotorControl.cs" });
            MentionItems.Add(new MentionItem { Name = "AgentModels", RefType = "File", Path = "Models/AgentModels.cs" });
        }

        private async void FilterAtMentions(string filter)
        {
            if (string.IsNullOrEmpty(filter))
            {
                await LoadMentionItemsAsync();
                return;
            }

            MentionItems.Clear();

            // Prefer the external search delegate
            if (SearchMentionItemsAsync != null)
            {
                try
                {
                    var items = await SearchMentionItemsAsync(filter);
                    foreach (var item in items)
                    {
                        MentionItems.Add(item);
                    }
                    return;
                }
                catch
                {
                    // Fall back to default filtering when external search fails
                }
            }

            // Default fallback filtering
            var allItems = new List<MentionItem>
            {
                new MentionItem { Name = "MotorControl", RefType = "Function", Path = "Functions/MotorControl.cs" },
                new MentionItem { Name = "PID_Controller", RefType = "Function", Path = "Functions/PID_Controller.cs" },
                new MentionItem { Name = "MotorData", RefType = "Udt", Path = "UDTs/MotorData.cs" },
                new MentionItem { Name = "SensorData", RefType = "Udt", Path = "UDTs/SensorData.cs" },
                new MentionItem { Name = "iSpeed", RefType = "Variable", Path = "TagTable/Main" },
                new MentionItem { Name = "rTemperature", RefType = "Variable", Path = "TagTable/Main" },
            };

            var filtered = allItems.Where(x => x.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            foreach (var item in filtered)
            {
                MentionItems.Add(item);
            }
        }

        private void InsertMention(MentionItem item)
        {
            if (item == null) return;

            var text = InputTextBox.Text;
            var caretIndex = InputTextBox.CaretIndex;

            var before = text.Substring(0, _atMentionStartIndex);
            var after = text.Substring(caretIndex);
            var mention = $"@{item.Name} ";

            InputTextBox.Text = before + mention + after;
            InputTextBox.CaretIndex = before.Length + mention.Length;

            CloseAtMention();
            MentionInserted?.Invoke(this, item.Name);
            MentionSelected?.Invoke(this, item);
        }

        private void AtButton_Click(object sender, RoutedEventArgs e)
        {
            var text = InputTextBox.Text;
            var caretIndex = InputTextBox.CaretIndex;

            InputTextBox.Text = text.Insert(caretIndex, "@");
            InputTextBox.CaretIndex = caretIndex + 1;
            InputTextBox.Focus();

            _atMentionStartIndex = caretIndex;
            OpenAtMention();
        }

        private void HashButton_Click(object sender, RoutedEventArgs e)
        {
            var lm = LanguageManager.Instance;
            var quickCommands = new[]
            {
                ("#generate", lm.GetString("Input.Cmd.Generate"), "GenerateAndFix"),
                ("#modify", lm.GetString("Input.Cmd.Modify"), "ModifyCode"),
                ("#explain", lm.GetString("Input.Cmd.Explain"), "ExplainCode"),
                ("#diagnose", lm.GetString("Input.Cmd.Diagnose"), "Diagnose"),
                ("#compile", lm.GetString("Input.Cmd.Compile"), "Compile"),
                ("#trace", lm.GetString("Input.Cmd.Trace"), "TraceVariable")
            };

            var menu = new ContextMenu
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 48)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(62, 62, 66))
            };

            foreach (var (cmd, desc, intent) in quickCommands)
            {
                var item = new MenuItem
                {
                    Header = $"{cmd} - {desc}",
                    Tag = intent,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(224, 224, 224)),
                    Background = System.Windows.Media.Brushes.Transparent,
                    Padding = new Thickness(8, 4, 8, 4)
                };
                item.Click += (s, args) =>
                {
                    // Insert the command into the input box
                    var text = InputTextBox.Text;
                    var caretIndex = InputTextBox.CaretIndex;
                    InputTextBox.Text = text.Insert(caretIndex, cmd + " ");
                    InputTextBox.CaretIndex = caretIndex + cmd.Length + 1;
                    InputTextBox.Focus();

                    // Raise the quick command event to notify the host of the intent
                    QuickCommandInvoked?.Invoke(this, intent);
                };
                menu.Items.Add(item);
            }

            menu.IsOpen = true;
        }

        private void AtMentionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (AtMentionList.SelectedItem is MentionItem item)
            {
                InsertMention(item);
            }
        }

        private void AtMentionList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && AtMentionList.SelectedItem is MentionItem item)
            {
                InsertMention(item);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseAtMention();
                e.Handled = true;
            }
        }

        private void SendStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsGenerating)
            {
                StopRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                SendMessage();
            }
        }

        private void SendMessage()
        {
            if (string.IsNullOrWhiteSpace(Text) && _attachedImages.Count == 0) return;
            if (_attachedImages.Count > 0)
            {
                SendWithImagesRequested?.Invoke(this, (Text, new List<byte[]>(_attachedImages)));
            }
            else
            {
                SendRequested?.Invoke(this, Text);
            }
            Text = string.Empty;
            InputTextBox.Text = string.Empty;
            _attachedImages.Clear();
            RefreshImagePreview();
        }

        private bool _suppressSelectionChanged;

        private void ModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged) return;
            if (ModelSelector.SelectedItem is ComboBoxItem item)
            {
                SelectedModel = item.Content?.ToString() ?? "qwen2.5:3b";
                ModelChanged?.Invoke(this, SelectedModel);
            }
        }

        /// <summary>
        /// Sets the list of available models (injected by the host).
        /// Suppresses SelectionChanged during initialization to avoid writes triggered by passive selection.
        /// </summary>
        public void SetAvailableModels(IEnumerable<string> models, string currentModel = null)
        {
            _suppressSelectionChanged = true;
            try
            {
                ModelSelector.Items.Clear();
                foreach (var model in models)
                {
                    var item = new ComboBoxItem { Content = model };
                    ModelSelector.Items.Add(item);
                    if (model == currentModel)
                    {
                        ModelSelector.SelectedItem = item;
                    }
                }
                if (ModelSelector.SelectedIndex < 0 && ModelSelector.Items.Count > 0)
                {
                    ModelSelector.SelectedIndex = 0;
                }
                if (ModelSelector.SelectedItem is ComboBoxItem chosen)
                {
                    SelectedModel = chosen.Content?.ToString() ?? "";
                }
            }
            finally
            {
                _suppressSelectionChanged = false;
            }
        }

        /// <summary>
        /// Returns the list of model names available in the current dropdown (used by the host to decide
        /// whether a session model needs to be added back).
        /// </summary>
        public IEnumerable<string> GetAvailableModels()
        {
            var result = new List<string>();
            foreach (var obj in ModelSelector.Items)
            {
                if (obj is ComboBoxItem item && item.Content != null)
                {
                    result.Add(item.Content.ToString());
                }
            }
            return result;
        }

        public void Clear()
        {
            Text = string.Empty;
            InputTextBox.Focus();
        }

        /// <summary>
        /// Updates the Token usage display in the bottom toolbar.
        /// </summary>
        public void UpdateTokenUsage(TokenUsageData data)
        {
            if (data == null)
            {
                TokenUsagePanel.Visibility = Visibility.Collapsed;
                return;
            }

            TokenUsagePanel.Visibility = Visibility.Visible;

            string color;
            bool showCompress;
            if (data.Status == "Exceeded")
            {
                color = "#F44336"; // red
                showCompress = true;
            }
            else if (data.Status == "Warning" || data.UsagePercent > 80)
            {
                color = "#FF9800"; // orange
                showCompress = true;
            }
            else
            {
                color = "#888888"; // gray
                showCompress = data.UsagePercent > 20;
            }

            TokenUsageText.Text = $"Tokens: {data.TotalUsed:N0} / {data.TotalBudget:N0} ({data.UsagePercent:F0}%)";
            TokenUsageText.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(color));
            CompressBtn.Visibility = showCompress ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CompressBtn_Click(object sender, RoutedEventArgs e)
        {
            CompressRequested?.Invoke(this, EventArgs.Empty);
        }

        private void AccessModeButton_Click(object sender, RoutedEventArgs e)
        {
            AccessModeToggleRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Refresh the read-only / allow-writes switch visuals (icon, label, tooltip, colour).
        /// Called by the host when the shared access mode changes or the language is switched.
        /// </summary>
        public void SetAccessMode(bool allowWrites)
        {
            _allowWrites = allowWrites;
            var lm = Services.LanguageManager.Instance;
            AccessModeIcon.Text = allowWrites ? "🔓" : "🔒";
            AccessModeLabel.Text = lm.GetString(allowWrites ? "Access.AllowEdit" : "Access.ReadOnly");
            AccessModeButton.ToolTip = lm.GetString(allowWrites ? "Access.AllowEdit.Tip" : "Access.ReadOnly.Tip");
            // Writes-enabled is the state that deserves attention; read-only blends in with the other buttons.
            AccessModeButton.Foreground = allowWrites
                ? (FindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue)
                : (FindResource("InputHintBrush") as Brush ?? Brushes.Gray);
        }

        public void FocusInput()
        {
            InputTextBox.Focus();
            InputTextBox.CaretIndex = InputTextBox.Text.Length;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // ── Image handling ──

        private bool TryPasteImageFromClipboard()
        {
            if (!Clipboard.ContainsImage()) return false;
            var image = Clipboard.GetImage();
            if (image == null) return false;
            AddImageFromBitmapSource(image);
            return true;
        }

        private void ImageButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All Files|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog() == true)
            {
                foreach (var file in dlg.FileNames)
                {
                    try
                    {
                        AddImageFromFile(file);
                    }
                    catch { }
                }
            }
        }

        private void RemoveImage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is System.Windows.Media.Imaging.BitmapImage bmp)
            {
                // Find the index of the corresponding byte array
                int idx = _attachedImages.FindIndex(data =>
                {
                    var temp = new System.Windows.Media.Imaging.BitmapImage();
                    using (var ms = new System.IO.MemoryStream(data))
                    {
                        temp.BeginInit();
                        temp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        temp.StreamSource = ms;
                        temp.EndInit();
                    }
                    return temp.PixelWidth == bmp.PixelWidth && temp.PixelHeight == bmp.PixelHeight;
                });
                if (idx >= 0)
                {
                    _attachedImages.RemoveAt(idx);
                    RefreshImagePreview();
                }
            }
        }

        private void ClearImages_Click(object sender, RoutedEventArgs e)
        {
            _attachedImages.Clear();
            RefreshImagePreview();
        }

        private void AddImageFromBitmapSource(System.Windows.Media.Imaging.BitmapSource source)
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
            using (var ms = new System.IO.MemoryStream())
            {
                encoder.Save(ms);
                _attachedImages.Add(ms.ToArray());
            }
            RefreshImagePreview();
        }

        private void AddImageFromFile(string path)
        {
            byte[] data = System.IO.File.ReadAllBytes(path);
            _attachedImages.Add(data);
            RefreshImagePreview();
        }

        private void RefreshImagePreview()
        {
            var sources = new List<System.Windows.Media.Imaging.BitmapImage>();
            foreach (var data in _attachedImages)
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                using (var ms = new System.IO.MemoryStream(data))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                sources.Add(bmp);
            }
            ImagePreviewList.ItemsSource = sources;
            ImagePreviewGrid.Visibility = sources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ImagesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public class MentionItem
    {
        public string Name { get; set; }
        public string RefType { get; set; }
        public string Path { get; set; }
    }
}
