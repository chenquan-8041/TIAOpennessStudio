using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Helpers
{
    /// <summary>
    /// Maps an <see cref="AgentExecutionState"/> to a themed brush, with a fallback for
    /// unresolved resources.
    /// </summary>
    public class ExecutionStateToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is AgentExecutionState state)
            {
                switch (state)
                {
                    case AgentExecutionState.Running:
                        return Application.Current.Resources["AccentBrush"] as Brush ?? Brushes.DodgerBlue;
                    case AgentExecutionState.Done:
                        return Brushes.ForestGreen;
                    case AgentExecutionState.Failed:
                        return Brushes.Crimson;
                    case AgentExecutionState.WaitingConfirmation:
                        return Brushes.Orange;
                    case AgentExecutionState.Cancelled:
                        return Brushes.Gray;
                    default:
                        return Application.Current.Resources["MutedTextBrush"] as Brush ?? Brushes.Gray;
                }
            }
            return Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Maps an <see cref="AgentExecutionState"/> to its corresponding icon glyph.
    /// </summary>
    public class ExecutionStateToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is AgentExecutionState state)
            {
                switch (state)
                {
                    case AgentExecutionState.Running:
                        return "\uE945"; // Play/Sync
                    case AgentExecutionState.Done:
                        return "\uE73E"; // Check
                    case AgentExecutionState.Failed:
                        return "\uE711"; // Error
                    case AgentExecutionState.WaitingConfirmation:
                        return "\uE946"; // Info
                    case AgentExecutionState.Cancelled:
                        return "\uE71A"; // Cancel
                    default:
                        return "\uE712"; // More/Pending
                }
            }
            return "\uE712";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
