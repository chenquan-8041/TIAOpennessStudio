using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TIAOpennessStudio.Models
{
    /// <summary>
    /// Wrapper for ChatSession to support selection in manage mode.
    /// </summary>
    public class ChatSessionItemViewModel : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _isManageMode;

        public ChatSession Session { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsManageMode
        {
            get => _isManageMode;
            set
            {
                if (_isManageMode != value)
                {
                    _isManageMode = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
