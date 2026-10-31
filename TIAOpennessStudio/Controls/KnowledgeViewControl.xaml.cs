using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Controls;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Standards;

namespace TIAOpennessStudio.Controls
{
    /// <summary>
    /// Central tab page: browses all rule entries of a knowledge base in a table + detail form.
    /// Consistent with the TagTableViewControl pattern — created and loaded dynamically by MainWindow when the user clicks a knowledge base.
    /// </summary>
    public partial class KnowledgeViewControl : UserControl
    {
        public string KbName { get; private set; }

        // All rules (data source for filtering).
        private List<StandardRule> _allRules = new List<StandardRule>();

        public KnowledgeViewControl()
        {
            InitializeComponent();

            // Three-language localization: apply initially + refresh on language change, unbind on unload.
            LanguageManager.Instance.LanguageChanged += OnLanguageChanged;
            ApplyLanguage();
            Unloaded += (_, __) => LanguageManager.Instance.LanguageChanged -= OnLanguageChanged;
        }

        private void OnLanguageChanged(AppLanguage lang)
        {
            ApplyLanguage();
            // Status/count lines embed localized text, so rebuild them for the new language.
            if (!string.IsNullOrEmpty(KbName))
                txtStatus.Text = LanguageManager.Instance.Format("Kb.View.Loaded", "Loaded {0} rules from {1}", _allRules.Count, KbName);
            ApplyFilter(txtFilter?.Text);
        }

        /// <summary>Applies the current language strings to column headers and section captions.</summary>
        private void ApplyLanguage()
        {
            var lm = LanguageManager.Instance;
            if (colCategory != null) colCategory.Header = lm.GetString("Kb.View.Col.Category");
            if (colScope != null) colScope.Header = lm.GetString("Kb.View.Col.Scope");
            if (colTitle != null) colTitle.Header = lm.GetString("Kb.View.Col.Title");
            if (lblContent != null) lblContent.Text = lm.GetString("Kb.View.Content");
            if (lblExamples != null) lblExamples.Text = lm.GetString("Kb.View.Examples");
        }

        /// <summary>Loads and displays a set of rules.</summary>
        public void Load(string kbName, IReadOnlyList<StandardRule> rules)
        {
            KbName = kbName ?? LanguageManager.Instance.GetString("Kb.Title");
            txtKbName.Text = KbName;

            _allRules = (rules ?? new List<StandardRule>()).ToList();
            ApplyFilter(txtFilter?.Text);

            txtStatus.Text = LanguageManager.Instance.Format("Kb.View.Loaded", "Loaded {0} rules from {1}", _allRules.Count, KbName);
        }

        private void ApplyFilter(string keyword)
        {
            IEnumerable<StandardRule> view = _allRules;
            keyword = (keyword ?? string.Empty).Trim();
            if (keyword.Length > 0)
            {
                view = _allRules.Where(r =>
                    Contains(r.Title, keyword) ||
                    Contains(r.Content, keyword) ||
                    Contains(r.Category, keyword) ||
                    Contains(r.Scope, keyword) ||
                    Contains(r.RuleId, keyword));
            }

            var list = view.ToList();
            dgRules.ItemsSource = new ObservableCollection<StandardRule>(list);
            txtRuleCount.Text = keyword.Length > 0
                ? $"({list.Count}/{_allRules.Count})"
                : $"({LanguageManager.Instance.Format("Kb.View.RuleCount", "{0} rules", _allRules.Count)})";

            if (list.Count > 0)
                dgRules.SelectedIndex = 0;
            else
                ShowDetail(null);
        }

        private static bool Contains(string source, string keyword)
            => !string.IsNullOrEmpty(source) &&
               source.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;

        private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
            => ApplyFilter(txtFilter.Text);

        private void DgRules_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => ShowDetail(dgRules.SelectedItem as StandardRule);

        private void ShowDetail(StandardRule rule)
        {
            if (rule == null)
            {
                dTitle.Text = "";
                dMeta.Text = "";
                dContent.Text = "";
                dExamples.Text = "";
                lblExamples.Visibility = System.Windows.Visibility.Collapsed;
                dExamplesBox.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }

            dTitle.Text = rule.Title ?? "";
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(rule.RuleId)) meta.Add(rule.RuleId);
            if (!string.IsNullOrWhiteSpace(rule.Category)) meta.Add(rule.Category);
            if (!string.IsNullOrWhiteSpace(rule.Scope)) meta.Add(rule.Scope);
            dMeta.Text = string.Join("  ·  ", meta);
            dContent.Text = rule.Content ?? "";

            bool hasExamples = !string.IsNullOrWhiteSpace(rule.Examples);
            lblExamples.Visibility = hasExamples ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            dExamplesBox.Visibility = hasExamples ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            dExamples.Text = rule.Examples ?? "";
        }
    }
}
