using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio
{
    /// <summary>
    /// Aligns user messages to the right and every other role to the left.
    /// </summary>
    public class RoleToAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (value?.ToString() == "user") ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Returns a distinct background brush for user versus assistant messages.
    /// </summary>
    public class RoleToBackgroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value?.ToString() == "user")
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30")); // Darker neutral for user
            
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333E3E42")); // Semi-transparent for assistant
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Collapses the element when the bound string is null or whitespace.
    /// </summary>
    public class StringVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return string.IsNullOrWhiteSpace(value?.ToString()) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts a boolean value to <see cref="Visibility.Visible"/> or <see cref="Visibility.Collapsed"/>.
    /// </summary>
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b) return Visibility.Visible;
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Shows the element only when the bound role matches the converter parameter.
    /// </summary>
    public class RoleToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value?.ToString() == parameter?.ToString()) return Visibility.Visible;
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Maps a <see cref="ThoughtKind"/> to its matching themed brush resource.
    /// </summary>
    public class ThoughtKindToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ThoughtKind kind)
            {
                return kind switch
                {
                    ThoughtKind.Analysis => Application.Current.TryFindResource("ThoughtAnalysisBrush"),
                    ThoughtKind.Creative => Application.Current.TryFindResource("ThoughtCreativeBrush"),
                    ThoughtKind.Warning => Application.Current.TryFindResource("ThoughtWarningBrush"),
                    ThoughtKind.Error => Application.Current.TryFindResource("ThoughtErrorBrush"),
                    ThoughtKind.General => Application.Current.TryFindResource("ThoughtGeneralBrush"),
                    _ => Application.Current.TryFindResource("ThoughtGeneralBrush")
                };
            }
            return Application.Current.TryFindResource("ThoughtGeneralBrush");
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts a lightweight Markdown string into a <see cref="FlowDocument"/> of inline elements.
    /// </summary>
    public class MarkdownToInlineConverter : IValueConverter
    {
        /// <summary>
        /// Resolves the thought-text brush for the current theme. A FlowDocument produced by a
        /// converter is detached from the visual tree, so DynamicResource references cannot be
        /// inherited. The resource must be resolved explicitly here, otherwise it falls back to
        /// the default black and becomes unreadable against the dark thought background.
        /// </summary>
        private static Brush ResolveThoughtForeground()
        {
            try
            {
                var res = System.Windows.Application.Current?.TryFindResource("AgentTextThoughtBrush");
                if (res is Brush b) return b;
            }
            catch { /* Fall back to the default below when the resource is unavailable. */ }
            // Fallback: neutral gray, readable on both light and dark backgrounds.
            return new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // The binding target is FlowDocumentScrollViewer.Document, so a FlowDocument must be
            // returned. Returning a Paragraph (Block) caused a type mismatch that failed the
            // binding and left the body blank.
            var fgBrush = ResolveThoughtForeground();

            if (value is not string text || string.IsNullOrWhiteSpace(text))
                return new FlowDocument(new Paragraph(new Run(""))) { Foreground = fgBrush };

            var paragraph = new Paragraph();
            int i = 0;
            
            while (i < text.Length)
            {
                // Bold **text**
                if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
                {
                    int end = text.IndexOf("**", i + 2);
                    if (end > i)
                    {
                        var boldText = text.Substring(i + 2, end - i - 2);
                        paragraph.Inlines.Add(new Bold(new Run(boldText)));
                        i = end + 2;
                        continue;
                    }
                }
                
                // Italic *text*
                if (text[i] == '*' && (i == 0 || text[i - 1] != '*'))
                {
                    int end = text.IndexOf('*', i + 1);
                    if (end > i && (end + 1 >= text.Length || text[end + 1] != '*'))
                    {
                        var italicText = text.Substring(i + 1, end - i - 1);
                        paragraph.Inlines.Add(new Italic(new Run(italicText)));
                        i = end + 1;
                        continue;
                    }
                }
                
                // Code `text`
                if (text[i] == '`')
                {
                    int end = text.IndexOf('`', i + 1);
                    if (end > i)
                    {
                        var codeText = text.Substring(i + 1, end - i - 1);
                        var run = new Run(codeText)
                        {
                            Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
                            Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220))
                        };
                        paragraph.Inlines.Add(run);
                        i = end + 1;
                        continue;
                    }
                }
                
                // List item - text
                if (i + 1 < text.Length && text[i] == '-' && text[i + 1] == ' ')
                {
                    int lineEnd = text.IndexOf('\n', i);
                    if (lineEnd < 0) lineEnd = text.Length;
                    var itemText = text.Substring(i + 2, lineEnd - i - 2);
                    paragraph.Inlines.Add(new Run("• " + itemText));
                    paragraph.Inlines.Add(new LineBreak());
                    i = lineEnd + 1;
                    continue;
                }
                
                // Numbered list 1. text
                if (char.IsDigit(text[i]) && i + 1 < text.Length && text[i + 1] == '.')
                {
                    int lineEnd = text.IndexOf('\n', i);
                    if (lineEnd < 0) lineEnd = text.Length;
                    var itemText = text.Substring(i + 2, lineEnd - i - 2);
                    paragraph.Inlines.Add(new Run(text.Substring(i, 2) + " " + itemText));
                    paragraph.Inlines.Add(new LineBreak());
                    i = lineEnd + 1;
                    continue;
                }
                
                // Regular text
                int nextSpecial = text.Length;
                var markers = new[] { "**", "*", "`", "-", "\n" };
                foreach (var marker in markers)
                {
                    int pos = text.IndexOf(marker, i);
                    if (pos >= i && pos < nextSpecial)
                        nextSpecial = pos;
                }
                
                if (nextSpecial > i)
                {
                    paragraph.Inlines.Add(new Run(text.Substring(i, nextSpecial - i)));
                    i = nextSpecial;
                }
                else
                {
                    paragraph.Inlines.Add(new Run(text.Substring(i)));
                    break;
                }
            }
            
            // Setting FlowDocument.Foreground makes all Inlines without an explicit color (plain
            // text, bold, italic, lists) inherit it. Code runs carry their own light foreground and
            // are unaffected.
            return new FlowDocument(paragraph) { Foreground = fgBrush };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts a Base64 string into a BitmapImage for displaying images in chat messages.
    /// </summary>
    public class Base64ToImageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string base64 || string.IsNullOrWhiteSpace(base64))
                return null;

            try
            {
                var bytes = System.Convert.FromBase64String(base64);
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                using (var ms = new System.IO.MemoryStream(bytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();
                }
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
