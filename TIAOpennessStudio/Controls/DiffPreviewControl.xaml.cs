using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using TIAOpennessStudio.Services;

namespace TIAOpennessStudio.Controls
{
    public partial class DiffPreviewControl : UserControl
    {
        private string _oldText = string.Empty;
        private string _newText = string.Empty;
        private SideBySideDiffModel _sideBySideModel;
        private List<DiffPiece> _leftLines;
        private List<DiffPiece> _rightLines;
        private bool _syncScrolling;
        private bool _viewModeBeforeEdit = true;
        private string _titleOverride;
        private string _statusOverride;

        public event EventHandler Applied;
        public event EventHandler Rejected;

        public string TitleText
        {
            get => lblTitle.Text;
            set
            {
                _titleOverride = string.IsNullOrEmpty(value) ? null : value;
                lblTitle.Text = ResolveTitle();
            }
        }

        public string StatusText
        {
            get => lblStatus.Text;
            set
            {
                _statusOverride = string.IsNullOrEmpty(value) ? null : value;
                lblStatus.Text = ResolveStatus();
            }
        }

        public DiffPreviewControl()
        {
            InitializeComponent();

            // Three languages: apply initially + refresh on language change, unbind on unload.
            LanguageManager.Instance.LanguageChanged += OnLanguageChanged;
            ApplyLanguage();
            Unloaded += (_, __) => LanguageManager.Instance.LanguageChanged -= OnLanguageChanged;

            Loaded += OnLoaded;
        }

        private void OnLanguageChanged(AppLanguage lang)
        {
            ApplyLanguage();
        }

        /// <summary>Applies the current language strings to the named controls.</summary>
        private void ApplyLanguage()
        {
            var lm = LanguageManager.Instance;
            if (lblTitle != null) lblTitle.Text = ResolveTitle();
            if (btnViewMode != null)
            {
                // The toggle label shows the mode currently in effect.
                btnViewMode.Content = lm.GetString(btnViewMode.IsChecked == true ? "Diff.View.SideBySide" : "Diff.View.Unified");
                btnViewMode.ToolTip = lm.GetString("Diff.View.ToggleTip");
            }
            if (btnEditMode != null) { btnEditMode.Content = lm.GetString("Diff.Edit"); btnEditMode.ToolTip = lm.GetString("Diff.Edit.Tip"); }
            if (btnApply != null) btnApply.Content = lm.GetString("Diff.Apply");
            if (btnReject != null) btnReject.Content = lm.GetString("Diff.Reject");
            if (lblOriginal != null) lblOriginal.Text = lm.GetString("Diff.Original");
            if (lblModified != null) lblModified.Text = lm.GetString("Diff.Modified");
            if (lblStatus != null) lblStatus.Text = ResolveStatus();
        }

        /// <summary>The default title is localized; an explicitly supplied title is kept as-is.</summary>
        private string ResolveTitle()
        {
            if (_titleOverride == null || string.Equals(_titleOverride, "Code Diff", StringComparison.Ordinal))
                return LanguageManager.Instance.GetString("Diff.Title");
            return _titleOverride;
        }

        /// <summary>The default status hint is localized; an explicitly supplied status is kept as-is.</summary>
        private string ResolveStatus()
        {
            return string.IsNullOrEmpty(_statusOverride)
                ? LanguageManager.Instance.GetString("Diff.Status.Default")
                : _statusOverride;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            HookSynchronizedScrolling();
        }

        public void SetDiff(string oldText, string newText)
        {
            _oldText = oldText ?? string.Empty;
            _newText = newText ?? string.Empty;

            BuildAndRenderDiff();
        }

        private void BuildAndRenderDiff()
        {
            var builder = new SideBySideDiffBuilder(new Differ());
            _sideBySideModel = builder.BuildDiffModel(_oldText, _newText, ignoreWhitespace: false);

            _leftLines = _sideBySideModel.OldText.Lines.ToList();
            _rightLines = _sideBySideModel.NewText.Lines.ToList();

            RenderSideBySide();
            RenderUnified();

            int adds = _rightLines.Count(p => p.Type == ChangeType.Inserted || p.Type == ChangeType.Modified);
            int dels = _leftLines.Count(p => p.Type == ChangeType.Deleted || p.Type == ChangeType.Modified);
            lblStats.Text = $"+{adds}  -{dels}";
        }

        private void RenderSideBySide()
        {
            var leftSb = new StringBuilder();
            foreach (var piece in _leftLines)
            {
                leftSb.AppendLine(piece.Text ?? string.Empty);
            }
            var rightSb = new StringBuilder();
            foreach (var piece in _rightLines)
            {
                rightSb.AppendLine(piece.Text ?? string.Empty);
            }

            editorLeft.Text = leftSb.ToString();
            editorRight.Text = rightSb.ToString();

            ResetLineTransformers(editorLeft);
            ResetLineTransformers(editorRight);
            editorLeft.TextArea.TextView.LineTransformers.Add(new SideBySideLineColorizer(_leftLines, isLeftSide: true, this));
            editorRight.TextArea.TextView.LineTransformers.Add(new SideBySideLineColorizer(_rightLines, isLeftSide: false, this));

            editorLeft.TextArea.TextView.Redraw();
            editorRight.TextArea.TextView.Redraw();
        }

        private void RenderUnified()
        {
            var inlineBuilder = new InlineDiffBuilder(new Differ());
            var inlineModel = inlineBuilder.BuildDiffModel(_oldText, _newText);

            var sb = new StringBuilder();
            foreach (var line in inlineModel.Lines)
            {
                switch (line.Type)
                {
                    case ChangeType.Inserted:
                        sb.Append("+ ");
                        break;
                    case ChangeType.Deleted:
                        sb.Append("- ");
                        break;
                    case ChangeType.Modified:
                        sb.Append("~ ");
                        break;
                    case ChangeType.Imaginary:
                        sb.Append("  ");
                        break;
                    case ChangeType.Unchanged:
                    default:
                        sb.Append("  ");
                        break;
                }
                sb.AppendLine(line.Text ?? string.Empty);
            }

            editorUnified.Text = sb.ToString();
            ResetLineTransformers(editorUnified);
            editorUnified.TextArea.TextView.LineTransformers.Add(new UnifiedDiffLineColorizer(this));
            editorUnified.TextArea.TextView.Redraw();
        }

        private static void ResetLineTransformers(TextEditor editor)
        {
            var transformers = editor.TextArea.TextView.LineTransformers;
            for (int i = transformers.Count - 1; i >= 0; i--)
            {
                if (transformers[i] is SideBySideLineColorizer ||
                    transformers[i] is UnifiedDiffLineColorizer)
                {
                    transformers.RemoveAt(i);
                }
            }
        }

        private void HookSynchronizedScrolling()
        {
            var leftSv = FindScrollViewer(editorLeft);
            var rightSv = FindScrollViewer(editorRight);
            if (leftSv != null) leftSv.ScrollChanged += LeftSv_ScrollChanged;
            if (rightSv != null) rightSv.ScrollChanged += RightSv_ScrollChanged;
        }

        private void LeftSv_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_syncScrolling) return;
            var target = FindScrollViewer(editorRight);
            if (target == null) return;
            try
            {
                _syncScrolling = true;
                if (Math.Abs(target.VerticalOffset - e.VerticalOffset) > 0.5)
                    target.ScrollToVerticalOffset(e.VerticalOffset);
                if (Math.Abs(target.HorizontalOffset - e.HorizontalOffset) > 0.5)
                    target.ScrollToHorizontalOffset(e.HorizontalOffset);
            }
            finally { _syncScrolling = false; }
        }

        private void RightSv_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_syncScrolling) return;
            var target = FindScrollViewer(editorLeft);
            if (target == null) return;
            try
            {
                _syncScrolling = true;
                if (Math.Abs(target.VerticalOffset - e.VerticalOffset) > 0.5)
                    target.ScrollToVerticalOffset(e.VerticalOffset);
                if (Math.Abs(target.HorizontalOffset - e.HorizontalOffset) > 0.5)
                    target.ScrollToHorizontalOffset(e.HorizontalOffset);
            }
            finally { _syncScrolling = false; }
        }

        private static ScrollViewer FindScrollViewer(DependencyObject d)
        {
            if (d == null) return null;
            if (d is ScrollViewer sv) return sv;
            int count = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(d, i);
                var found = FindScrollViewer(child);
                if (found != null) return found;
            }
            return null;
        }

        public string GetEditedCode()
        {
            if (btnEditMode.IsChecked == true)
            {
                return editorManualEdit.Text;
            }
            return _newText;
        }

        private void btnViewMode_Click(object sender, RoutedEventArgs e)
        {
            if (btnEditMode.IsChecked == true)
            {
                btnEditMode.IsChecked = false;
                _newText = editorManualEdit.Text;
                BuildAndRenderDiff();
            }

            bool sideBySide = btnViewMode.IsChecked == true;
            btnViewMode.Content = LanguageManager.Instance.GetString(sideBySide ? "Diff.View.SideBySide" : "Diff.View.Unified");
            gridSideBySide.Visibility = sideBySide ? Visibility.Visible : Visibility.Collapsed;
            borderUnified.Visibility = sideBySide ? Visibility.Collapsed : Visibility.Visible;
            borderEdit.Visibility = Visibility.Collapsed;
        }

        private void btnEditMode_Click(object sender, RoutedEventArgs e)
        {
            if (btnEditMode.IsChecked == true)
            {
                _viewModeBeforeEdit = btnViewMode.IsChecked == true;
                editorManualEdit.Text = _newText;
                // Keep only one mode toggle active: two accent-filled toggles read as two active modes.
                btnViewMode.IsChecked = false;
                gridSideBySide.Visibility = Visibility.Collapsed;
                borderUnified.Visibility = Visibility.Collapsed;
                borderEdit.Visibility = Visibility.Visible;
            }
            else
            {
                _newText = editorManualEdit.Text;
                btnViewMode.IsChecked = _viewModeBeforeEdit;
                BuildAndRenderDiff();
                btnViewMode_Click(null, null);
            }
        }

        private void btnApply_Click(object sender, RoutedEventArgs e)
        {
            Applied?.Invoke(this, EventArgs.Empty);
        }

        private void btnReject_Click(object sender, RoutedEventArgs e)
        {
            Rejected?.Invoke(this, EventArgs.Empty);
        }

        internal Brush GetThemeBrush(string key, Color fallback)
        {
            if (TryFindResource(key) is Brush brush) return brush;
            var b = new SolidColorBrush(fallback);
            b.Freeze();
            return b;
        }
    }

    internal sealed class SideBySideLineColorizer : DocumentColorizingTransformer
    {
        private readonly IList<DiffPiece> _pieces;
        private readonly bool _isLeftSide;
        private readonly DiffPreviewControl _host;

        public SideBySideLineColorizer(IList<DiffPiece> pieces, bool isLeftSide, DiffPreviewControl host)
        {
            _pieces = pieces;
            _isLeftSide = isLeftSide;
            _host = host;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            int idx = line.LineNumber - 1;
            if (idx < 0 || idx >= _pieces.Count) return;

            var piece = _pieces[idx];
            Brush bg = null;

            switch (piece.Type)
            {
                case ChangeType.Inserted:
                    if (!_isLeftSide) bg = _host.GetThemeBrush("DiffAddBackgroundBrush", Color.FromArgb(40, 46, 160, 67));
                    break;
                case ChangeType.Deleted:
                    if (_isLeftSide) bg = _host.GetThemeBrush("DiffRemoveBackgroundBrush", Color.FromArgb(40, 248, 81, 73));
                    break;
                case ChangeType.Modified:
                    bg = _isLeftSide
                        ? _host.GetThemeBrush("DiffRemoveBackgroundBrush", Color.FromArgb(40, 248, 81, 73))
                        : _host.GetThemeBrush("DiffAddBackgroundBrush", Color.FromArgb(40, 46, 160, 67));
                    break;
                case ChangeType.Imaginary:
                    bg = _host.GetThemeBrush("DiffImaginaryLineBrush", Color.FromArgb(26, 255, 255, 255));
                    break;
            }

            if (bg == null) return;

            int start = line.Offset;
            int end = line.EndOffset;
            if (end <= start) return;

            ChangeLinePart(start, end, element =>
            {
                element.TextRunProperties.SetBackgroundBrush(bg);
            });
        }
    }

    internal sealed class UnifiedDiffLineColorizer : DocumentColorizingTransformer
    {
        private readonly DiffPreviewControl _host;

        public UnifiedDiffLineColorizer(DiffPreviewControl host)
        {
            _host = host;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length < 1) return;
            string head = CurrentContext.Document.GetText(line.Offset, Math.Min(2, line.Length));

            Brush bg = null;
            if (head.StartsWith("+"))
                bg = _host.GetThemeBrush("DiffAddBackgroundBrush", Color.FromArgb(40, 46, 160, 67));
            else if (head.StartsWith("-"))
                bg = _host.GetThemeBrush("DiffRemoveBackgroundBrush", Color.FromArgb(40, 248, 81, 73));
            else if (head.StartsWith("~"))
                bg = _host.GetThemeBrush("DiffHunkBackgroundBrush", Color.FromArgb(40, 56, 139, 253));

            if (bg == null) return;

            ChangeLinePart(line.Offset, line.EndOffset, element =>
            {
                element.TextRunProperties.SetBackgroundBrush(bg);
            });
        }
    }
}
