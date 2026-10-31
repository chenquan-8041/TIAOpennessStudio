using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TIAOpennessStudio.Helpers
{
    /// <summary>
    /// Converts a boolean to <see cref="Visibility"/>, with an optional "Inverse" parameter to
    /// invert the result.
    /// </summary>
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool flag = false;
            if (value is bool)
            {
                flag = (bool)value;
            }
            else if (value is bool?)
            {
                bool? b = (bool?)value;
                flag = b.HasValue && b.Value;
            }

            if (parameter?.ToString() == "Inverse")
            {
                flag = !flag;
            }

            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is Visibility && (Visibility)value == Visibility.Visible;
        }
    }

    /// <summary>
    /// Converts null to <see cref="Visibility.Collapsed"/> and non-null to
    /// <see cref="Visibility.Visible"/>, with an optional "Inverse" parameter.
    /// </summary>
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isNull = value == null;
            if (parameter?.ToString() == "Inverse")
            {
                return isNull ? Visibility.Visible : Visibility.Collapsed;
            }
            return isNull ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}