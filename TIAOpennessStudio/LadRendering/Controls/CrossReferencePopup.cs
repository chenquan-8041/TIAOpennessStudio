using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TIAOpennessStudio.Services;

namespace TIAOpennessStudio.LadRendering.Controls
{
    /// <summary>
    /// Cross-reference popup: shows a variable's reference locations when hovering over a contact/coil.
    /// Two-phase loading:
    ///   Phase 1 — immediately show references on the current page (extracted from FlgNetModel, zero delay)
    ///   Phase 2 — asynchronously load full TIA Portal cross-references (background query, popup updated on completion)
    /// </summary>
    public class CrossReferencePopup
    {
        private readonly Popup _popup;
        private readonly DispatcherTimer _hoverTimer;
        private readonly TextBlock _headerText;
        private readonly TextBlock _typeInfo;
        private readonly TextBlock _sectionLabel;
        private readonly ItemsControl _localRefList;
        private readonly ItemsControl _fullRefList;
        private readonly TextBlock _loadingText;
        private readonly TextBlock _emptyText;
        private readonly Border _localSeparator;
        private readonly Border _fullSeparator;

        private string _currentVariable;
        private UIElement _currentTarget;
        private bool _isShowing;

        /// <summary>References on the current page (variable → reference list)</summary>
        private Dictionary<string, List<CrossReferenceEntry>> _localRefs
            = new Dictionary<string, List<CrossReferenceEntry>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Callback that asynchronously loads full cross-references</summary>
        public Func<string, string, Task<CrossReferenceResult>> LoadFullCrossRefs { get; set; }

        public event Action<CrossReferenceEntry> OnEntryClicked;

        private const double PopupWidth = 340;
        private const double MaxPopupHeight = 400;
        private const int HoverDelayMs = 400;

        private static readonly SolidColorBrush BgBrush = new SolidColorBrush(Color.FromRgb(45, 45, 50));
        private static readonly SolidColorBrush BorderCol = new SolidColorBrush(Color.FromRgb(80, 80, 88));
        private static readonly SolidColorBrush HeaderFg = new SolidColorBrush(Color.FromRgb(220, 220, 230));
        private static readonly SolidColorBrush MutedFg = new SolidColorBrush(Color.FromRgb(150, 150, 160));
        private static readonly SolidColorBrush AccentFg = new SolidColorBrush(Color.FromRgb(100, 180, 255));
        private static readonly SolidColorBrush HoverBg = new SolidColorBrush(Color.FromRgb(60, 60, 68));

        public CrossReferencePopup()
        {
            _hoverTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(HoverDelayMs),
                DispatcherPriority.Normal,
                OnHoverTimerTick,
                Dispatcher.CurrentDispatcher)
            { IsEnabled = false };

            _popup = new Popup
            {
                AllowsTransparency = true,
                Placement = PlacementMode.Right,
                StaysOpen = false,
                HorizontalOffset = 8,
                VerticalOffset = -4
            };

            var mainPanel = new StackPanel();

            // Header
            _headerText = new TextBlock
            {
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = HeaderFg, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(14, 10, 14, 0)
            };
            mainPanel.Children.Add(_headerText);

            _typeInfo = new TextBlock
            {
                FontSize = 11, Foreground = MutedFg,
                Margin = new Thickness(14, 2, 14, 6)
            };
            mainPanel.Children.Add(_typeInfo);

            var sep = new Border { Height = 1, Background = BorderCol, Margin = new Thickness(0, 0, 0, 4) };
            mainPanel.Children.Add(sep);

            // Local references header
            _sectionLabel = new TextBlock
            {
                Text = "This page:",
                FontSize = 10.5, Foreground = MutedFg,
                Margin = new Thickness(14, 4, 14, 2),
                Visibility = Visibility.Collapsed
            };
            mainPanel.Children.Add(_sectionLabel);

            _localRefList = new ItemsControl
            {
                Visibility = Visibility.Collapsed
            };
            _localRefList.ItemTemplate = CreateItemTemplate();
            mainPanel.Children.Add(_localRefList);

            _localSeparator = new Border
            {
                Height = 1, Background = BorderCol,
                Margin = new Thickness(8, 4, 8, 2),
                Visibility = Visibility.Collapsed
            };
            mainPanel.Children.Add(_localSeparator);

            // Full cross-reference area
            _loadingText = new TextBlock
            {
                Text = "Loading full cross-references...",
                FontSize = 11, Foreground = MutedFg,
                Margin = new Thickness(14, 4, 14, 4),
                Visibility = Visibility.Collapsed
            };
            mainPanel.Children.Add(_loadingText);

            _fullRefList = new ItemsControl
            {
                Visibility = Visibility.Collapsed
            };
            _fullRefList.ItemTemplate = CreateItemTemplate();
            mainPanel.Children.Add(_fullRefList);

            _emptyText = new TextBlock
            {
                Text = "No other references found",
                FontSize = 11, Foreground = MutedFg,
                Margin = new Thickness(14, 4, 14, 8),
                Visibility = Visibility.Collapsed
            };
            mainPanel.Children.Add(_emptyText);

            _fullSeparator = new Border
            {
                Height = 1, Background = BorderCol,
                Margin = new Thickness(0, 0, 0, 0),
                Visibility = Visibility.Collapsed
            };
            mainPanel.Children.Add(_fullSeparator);

            var contentBorder = new Border
            {
                Width = PopupWidth, MaxHeight = MaxPopupHeight,
                Background = BgBrush, BorderBrush = BorderCol,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Color.FromArgb(120, 0, 0, 0),
                    BlurRadius = 12, ShadowDepth = 3, Opacity = 0.6
                },
                Child = mainPanel
            };
            _popup.Child = contentBorder;
        }

        /// <summary>Merges current-page reference data (appends entries for the same variable)</summary>
        public void MergeLocalReferences(Dictionary<string, List<CrossReferenceEntry>> localRefs)
        {
            if (localRefs == null) return;
            foreach (var kvp in localRefs)
            {
                if (!_localRefs.ContainsKey(kvp.Key))
                    _localRefs[kvp.Key] = new List<CrossReferenceEntry>();
                _localRefs[kvp.Key].AddRange(kvp.Value);
            }
        }

        /// <summary>Clears local references (called when switching blocks)</summary>
        public void ClearLocalReferences()
        {
            _localRefs.Clear();
        }

        private DataTemplate CreateItemTemplate()
        {
            var template = new DataTemplate();
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetValue(Border.PaddingProperty, new Thickness(14, 5, 14, 5));
            factory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            factory.SetValue(Border.CursorProperty, Cursors.Hand);

            factory.AddHandler(Border.MouseEnterEvent, new MouseEventHandler((s, e) =>
            {
                if (s is Border b) b.Background = HoverBg;
            }));
            factory.AddHandler(Border.MouseLeaveEvent, new MouseEventHandler((s, e) =>
            {
                if (s is Border b) b.Background = Brushes.Transparent;
            }));
            factory.AddHandler(Border.MouseLeftButtonDownEvent, new MouseButtonEventHandler((s, e) =>
            {
                if (s is FrameworkElement fe && fe.DataContext is CrossReferenceEntry entry)
                {
                    OnEntryClicked?.Invoke(entry);
                    _popup.IsOpen = false;
                }
            }));

            var stack = new FrameworkElementFactory(typeof(StackPanel));

            var nameText = new FrameworkElementFactory(typeof(TextBlock));
            nameText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DisplayText"));
            nameText.SetValue(TextBlock.FontSizeProperty, 12.0);
            nameText.SetValue(TextBlock.ForegroundProperty, HeaderFg);
            nameText.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            stack.AppendChild(nameText);

            var detailText = new FrameworkElementFactory(typeof(TextBlock));
            detailText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DetailText"));
            detailText.SetValue(TextBlock.FontSizeProperty, 10.5);
            detailText.SetValue(TextBlock.ForegroundProperty, MutedFg);
            detailText.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            detailText.SetValue(TextBlock.MarginProperty, new Thickness(0, 1, 0, 0));
            stack.AppendChild(detailText);

            factory.AppendChild(stack);
            template.VisualTree = factory;
            return template;
        }

        public void OnElementEnter(UIElement target, string variableName, string blockName)
        {
            if (target == null || string.IsNullOrWhiteSpace(variableName)) return;

            _currentTarget = target;
            _currentVariable = variableName;

            _hoverTimer.Stop();
            _hoverTimer.Start();
        }

        /// <summary>
        /// Mouse leaves the element: only cancels the timer, does not close the popup.
        /// The popup closes by clicking outside (StaysOpen=false) or by hovering a new element.
        /// </summary>
        public void OnElementLeave()
        {
            _hoverTimer.Stop();
        }

        /// <summary>
        /// Shows the cross-reference popup immediately on right-click (without the hover delay).
        /// Compared with hover triggering, it only queries on an explicit user request, avoiding repeated
        /// queries freezing the UI when the mouse passes over many elements.
        /// </summary>
        public void ShowForElement(UIElement target, string variableName, string blockName)
        {
            if (target == null || string.IsNullOrWhiteSpace(variableName)) return;

            _hoverTimer.Stop();

            // If a popup is already open, close it first so it can be repositioned for the new element
            if (_popup.IsOpen)
            {
                try { _popup.IsOpen = false; } catch { }
                _isShowing = false;
            }

            _currentTarget = target;
            _currentVariable = variableName;

            ShowPopupCore(target, variableName);
        }

        private void OnHoverTimerTick(object sender, EventArgs e)
        {
            _hoverTimer.Stop();

            if (_currentTarget == null || string.IsNullOrWhiteSpace(_currentVariable))
                return;

            ShowPopupCore(_currentTarget, _currentVariable);
        }

        private async void ShowPopupCore(UIElement target, string variableName)
        {
            if (target == null || string.IsNullOrWhiteSpace(variableName)) return;
            if (_isShowing) return;

            _isShowing = true;

            try
            {
                // ── Phase 1: immediately show references on the current page ──
                _headerText.Text = $"Cross Reference: {variableName}";

                List<CrossReferenceEntry> localEntries = null;
                if (_localRefs.TryGetValue(variableName, out var entries) && entries.Count > 0)
                {
                    localEntries = entries;
                    _typeInfo.Text = "";
                    if (!string.IsNullOrWhiteSpace(entries[0].Scope))
                        _typeInfo.Text = $"Scope: {entries[0].Scope}";
                    _typeInfo.Text += string.IsNullOrEmpty(_typeInfo.Text)
                        ? $"This page: {entries.Count} ref(s)"
                        : $"  |  This page: {entries.Count} ref(s)";

                    _sectionLabel.Visibility = Visibility.Visible;
                    _localRefList.ItemsSource = entries;
                    _localRefList.Visibility = Visibility.Visible;
                    _localSeparator.Visibility = Visibility.Visible;
                }
                else
                {
                    _typeInfo.Text = "No references on this page";
                    _sectionLabel.Visibility = Visibility.Collapsed;
                    _localRefList.Visibility = Visibility.Collapsed;
                    _localSeparator.Visibility = Visibility.Collapsed;
                }

                _emptyText.Visibility = Visibility.Collapsed;
                _fullRefList.Visibility = Visibility.Collapsed;
                _fullSeparator.Visibility = Visibility.Collapsed;
                _loadingText.Visibility = Visibility.Collapsed;

                _popup.PlacementTarget = target;
                try { _popup.IsOpen = true; } catch { }

                // ── Phase 2: asynchronously load full cross-references ──
                var fullLoader = LoadFullCrossRefs;
                if (fullLoader != null)
                {
                    _loadingText.Visibility = Visibility.Visible;

                    try
                    {
                        await Task.Yield();
                        var result = await fullLoader(variableName, null);

                        if (_currentVariable != variableName || !_popup.IsOpen) return;

                        _loadingText.Visibility = Visibility.Collapsed;

                        if (result != null && result.Entries.Count > 0)
                        {
                            _typeInfo.Text = "";
                            if (!string.IsNullOrWhiteSpace(result.DataType))
                                _typeInfo.Text = $"Type: {result.DataType}";
                            if (!string.IsNullOrWhiteSpace(result.Scope))
                                _typeInfo.Text += string.IsNullOrEmpty(_typeInfo.Text)
                                    ? $"Scope: {result.Scope}"
                                    : $"  |  Scope: {result.Scope}";
                            if (result.Entries.Count > 0)
                                _typeInfo.Text += $"  |  Total: {result.Entries.Count} ref(s)";

                            _fullRefList.ItemsSource = result.Entries;
                            _fullRefList.Visibility = Visibility.Visible;
                            _fullSeparator.Visibility = localEntries != null
                                ? Visibility.Visible : Visibility.Collapsed;
                            _emptyText.Visibility = Visibility.Collapsed;
                        }
                        else
                        {
                            if (localEntries == null || localEntries.Count == 0)
                            {
                                _emptyText.Text = "No cross-references found";
                                _emptyText.Visibility = Visibility.Visible;
                            }
                        }
                    }
                    catch
                    {
                        _loadingText.Visibility = Visibility.Collapsed;
                        if (localEntries == null || localEntries.Count == 0)
                        {
                            _emptyText.Text = "Failed to load cross-references";
                            _emptyText.Visibility = Visibility.Visible;
                        }
                    }
                }
            }
            finally
            {
                _isShowing = false;
            }
        }
    }
}