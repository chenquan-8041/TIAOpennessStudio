using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Controls;

namespace TIAOpennessStudio.Controls
{
    /// <summary>
    /// Displays PLC Tag Table content in a DataGrid.
    /// Loaded dynamically when user clicks a Tag Table node in the project tree.
    /// Tags arrive in batches (progressive disclosure) from the STA host thread;
    /// this control only binds plain data, never touching a COM object.
    /// </summary>
    public partial class TagTableViewControl : UserControl
    {
        public string TableName { get; private set; }

        private ObservableCollection<TagEntry> _tags;
        private int _count;

        public TagTableViewControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Reset the view and prepare an empty collection for streaming batches.
        /// <paramref name="hmiMode"/> shows the HMI-specific columns (connection / PLC name /
        /// PLC tag) that TIA's variable table displays; PLC tag tables keep them hidden.
        /// </summary>
        public void BeginLoad(string tableName, bool hmiMode = false)
        {
            TableName = tableName ?? "Tag Table";
            txtTableName.Text = TableName;
            txtStatus.Text = "Loading tags…";
            txtTagCount.Text = "";

            var hmiVisibility = hmiMode ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            colConnection.Visibility = hmiVisibility;
            colPlcName.Visibility = hmiVisibility;
            colPlcTag.Visibility = hmiVisibility;

            _tags = new ObservableCollection<TagEntry>();
            dgTags.ItemsSource = _tags;
            _count = 0;
        }

        /// <summary>Append one batch of tag entries. Called on the UI thread via Progress reporting.</summary>
        public void AppendBatch(IReadOnlyList<Services.Abstractions.TagEntryDto> batch)
        {
            if (batch == null) return;

            foreach (var dto in batch)
            {
                _tags.Add(new TagEntry
                {
                    Name = dto.Name,
                    DataType = dto.DataType,
                    Connection = dto.Connection,
                    PlcName = dto.PlcName,
                    PlcTag = dto.PlcTag,
                    LogicalAddress = dto.LogicalAddress,
                    Comment = dto.Comment,
                });
                _count++;
            }

            txtStatus.Text = $"Loading… {_count}";
            txtTagCount.Text = $"({_count})";
        }

        /// <summary>Finalize loading and show the total count.</summary>
        public void EndLoad()
        {
            txtTagCount.Text = $"({_count} tags)";
            txtStatus.Text = $"Loaded {_count} tags from {TableName}";
        }

        /// <summary>Show an error message in the status bar.</summary>
        public void ShowError(string message)
        {
            txtStatus.Text = $"Error: {message}";
            txtTagCount.Text = "";
        }
    }

    /// <summary>Display model for a single tag entry (PLC or HMI).</summary>
    public class TagEntry
    {
        public string Name { get; set; } = "";
        public string DataType { get; set; } = "";
        public string Connection { get; set; } = "";
        public string PlcName { get; set; } = "";
        public string PlcTag { get; set; } = "";
        public string LogicalAddress { get; set; } = "";
        public string Comment { get; set; } = "";
    }
}