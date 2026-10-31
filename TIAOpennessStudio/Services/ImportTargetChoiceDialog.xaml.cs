using System.Windows;

namespace TIAOpennessStudio.Services
{
    /// <summary>
    /// Shown after AI code generation when the user has not yet picked a target
    /// BlockGroup via the StatusBar chip. Lets the user choose one of three options:
    /// write to the TIA root, pick a specific chip group, or save to a local file.
    /// </summary>
    public partial class ImportTargetChoiceDialog : Window
    {
        private readonly string _blockName;
        private readonly string _blockType;

        public ImportTargetChoice Choice { get; private set; } = ImportTargetChoice.Cancel;

        public ImportTargetChoiceDialog(string blockName, string blockType)
        {
            InitializeComponent();
            _blockName = blockName;
            _blockType = string.IsNullOrEmpty(blockType) ? "FB" : blockType;

            // Three languages: apply initially + refresh while the dialog is open, unbind on close.
            LanguageManager.Instance.LanguageChanged += OnLanguageChanged;
            ApplyLanguage();
            Closed += (_, __) => LanguageManager.Instance.LanguageChanged -= OnLanguageChanged;
        }

        private void OnLanguageChanged(AppLanguage lang)
        {
            ApplyLanguage();
        }

        /// <summary>Applies the current language strings to every visible element.</summary>
        private void ApplyLanguage()
        {
            var lm = LanguageManager.Instance;
            Title = lm.GetString("Import.Dlg.Title");
            if (txtHeading != null) txtHeading.Text = lm.GetString("Import.Dlg.Heading");
            if (txtSubtitle != null)
            {
                txtSubtitle.Text = string.IsNullOrEmpty(_blockName)
                    ? lm.GetString("Import.Dlg.Subtitle")
                    : lm.Format("Import.Dlg.SubtitleWithBlock",
                        $"Generated {_blockType} \"{_blockName}\". No target group is selected in the StatusBar yet. Choose how to proceed:",
                        _blockType, _blockName);
            }
            if (txtTiaRootTitle != null) txtTiaRootTitle.Text = lm.GetString("Import.Dlg.TiaRoot");
            if (txtTiaRootHint != null) txtTiaRootHint.Text = lm.GetString("Import.Dlg.TiaRootHint");
            if (txtPickGroupTitle != null) txtPickGroupTitle.Text = lm.GetString("Import.Dlg.PickGroup");
            if (txtPickGroupHint != null) txtPickGroupHint.Text = lm.GetString("Import.Dlg.PickGroupHint");
            if (txtLocalFileTitle != null) txtLocalFileTitle.Text = lm.GetString("Import.Dlg.LocalFile");
            if (txtLocalFileHint != null) txtLocalFileHint.Text = lm.GetString("Import.Dlg.LocalFileHint");
            if (btnCancel != null) btnCancel.Content = lm.GetString("Kb.Common.Cancel");
        }

        private void BtnTiaRoot_Click(object sender, RoutedEventArgs e)
        {
            Choice = ImportTargetChoice.TiaRoot;
            DialogResult = true;
            Close();
        }

        private void BtnPickGroup_Click(object sender, RoutedEventArgs e)
        {
            Choice = ImportTargetChoice.PickGroup;
            DialogResult = true;
            Close();
        }

        private void BtnLocalFile_Click(object sender, RoutedEventArgs e)
        {
            Choice = ImportTargetChoice.LocalFile;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Choice = ImportTargetChoice.Cancel;
            DialogResult = false;
            Close();
        }
    }

    public enum ImportTargetChoice
    {
        Cancel,
        TiaRoot,
        PickGroup,
        LocalFile,
    }
}
