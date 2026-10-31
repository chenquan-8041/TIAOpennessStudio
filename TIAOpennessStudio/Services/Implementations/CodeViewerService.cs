using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Document;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.ExternalSources;
using TIAOpennessStudio.Services.Abstractions;

namespace TIAOpennessStudio.Services.Implementations
{
    /// <summary>
    /// WPF implementation of ICodeViewerService.
    /// </summary>
    public class CodeViewerService : ICodeViewerService
    {
        private readonly IDialogService _dialogService;

        /// <summary>
        /// Display-only JSON options. The IR builder keeps its output compact on purpose
        /// (single line, to stay inside the agent context budget), so the viewer expands
        /// it again — one huge line is both unreadable and slow to lay out/colorize.
        /// </summary>
        private static readonly JsonSerializerOptions DisplayJsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public CodeViewerService(IDialogService dialogService)
        {
            _dialogService = dialogService;
        }

        public void ShowCodeViewer(string name, string type, object content)
        {
            // This method will be implemented when we migrate from MainWindow
            // For now, it's a placeholder that will be called from MainWindow
            // The actual implementation requires access to the tabsCode control
            // which will be passed through the ViewModel events
            throw new NotImplementedException("ShowCodeViewer requires UI context");
        }

        public TextEditor CreateCodeEditor(string content)
        {
            var editor = new TextEditor
            {
                Text = content,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14,
                IsReadOnly = false,
                ShowLineNumbers = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10)
            };

            // Ensure the editor grabs keyboard focus on click, so Ctrl+C works inside a TabControl
            editor.PreviewMouseDown += (s, e) =>
            {
                if (!editor.TextArea.IsFocused)
                {
                    editor.TextArea.Focus();
                }
            };

            // Bind colors via DynamicResource so they follow theme changes automatically
            editor.SetResourceReference(TextEditor.BackgroundProperty, "EditorBackgroundBrush");
            editor.SetResourceReference(TextEditor.ForegroundProperty, "EditorForegroundBrush");
            editor.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "EditorLineNumberBrush");

            // Indentation & Formatting
            editor.Options.ConvertTabsToSpaces = true;
            editor.Options.IndentationSize = 4;
            editor.Options.HighlightCurrentLine = true;

            // Selection & current line — set via resources on the TextArea
            editor.TextArea.SetResourceReference(ICSharpCode.AvalonEdit.Editing.TextArea.SelectionBrushProperty, "EditorSelectionBrush");
            editor.TextArea.TextView.SetResourceReference(ICSharpCode.AvalonEdit.Rendering.TextView.CurrentLineBackgroundProperty, "EditorCurrentLineBrush");

            // Folding Manager
            var foldingManager = FoldingManager.Install(editor.TextArea);
            object foldingStrategy;

            // Syntax Highlighting & Strategy Selection
            if (content.TrimStart().StartsWith("<"))
            {
                editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("XML");
                foldingStrategy = new XmlFoldingStrategy();
            }
            else if (IsJsonContent(content))
            {
                // JSON: AvalonEdit has no built-in JSON definition, and its C#
                // definition paints almost everything one hardcoded (non-theme)
                // color, so use a theme-aware colorizer instead. Arrays ("[]")
                // fold in addition to objects ("{}").
                editor.SyntaxHighlighting = null;
                editor.TextArea.TextView.LineTransformers.Add(new JsonSyntaxColorizer());
                foldingStrategy = new BraceFoldingStrategy(foldSquareBrackets: true);
            }
            else if (IsSclContent(content))
            {
                editor.SyntaxHighlighting = null;
                editor.TextArea.TextView.LineTransformers.Add(new SclSyntaxColorizer());
                foldingStrategy = new BraceFoldingStrategy();
            }
            else
            {
                // Default to C# if not XML
                editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
                foldingStrategy = new BraceFoldingStrategy();
            }

            // Initial Folding Update
            UpdateFoldings(foldingManager, foldingStrategy, editor.Document);

            // Timer for dynamic folding updates
            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += (s, e) => {
                timer.Stop();
                UpdateFoldings(foldingManager, foldingStrategy, editor.Document);
            };

            editor.TextChanged += (s, e) => {
                timer.Stop();
                timer.Start();
            };

            var editorMenu = new ContextMenu();
            var addSelectionItem = new MenuItem { Header = "Add Selection To AI" };
            addSelectionItem.Click += (s, e) =>
            {
                var selected = editor.SelectedText;
                if (string.IsNullOrWhiteSpace(selected))
                {
                    _dialogService.ShowWarning("Please select code first.", "Selection Required");
                    return;
                }
                // This will be handled by the caller through an event or callback
                // For now, we'll leave this as a placeholder
            };
            var addAllItem = new MenuItem { Header = "Add Full Code To AI" };
            addAllItem.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(editor.Text))
                {
                    _dialogService.ShowWarning("No code content.", "Empty Content");
                    return;
                }
                // This will be handled by the caller through an event or callback
            };
            editorMenu.Items.Add(addSelectionItem);
            editorMenu.Items.Add(addAllItem);
            editorMenu.Items.Add(new Separator());
            editorMenu.Items.Add(new MenuItem { Header = "Copy", Command = System.Windows.Input.ApplicationCommands.Copy });
            editorMenu.Items.Add(new MenuItem { Header = "Cut", Command = System.Windows.Input.ApplicationCommands.Cut });
            editorMenu.Items.Add(new MenuItem { Header = "Paste", Command = System.Windows.Input.ApplicationCommands.Paste });
            editorMenu.Items.Add(new Separator());
            editorMenu.Items.Add(new MenuItem { Header = "Select All", Command = System.Windows.Input.ApplicationCommands.SelectAll });
            editor.ContextMenu = editorMenu;
            
            // Search Panel (Ctrl+F)
            ICSharpCode.AvalonEdit.Search.SearchPanel.Install(editor);

            return editor;
        }

        public void RenderContentToGrid(Grid grid, object content)
        {
            if (content is UIElement element)
            {
                grid.Children.Add(element);
            }
            else if (content is string strContent)
            {
                if (strContent.StartsWith("PDF:"))
                {
                    string pdfPath = strContent.Substring(4);
                    WebBrowser wb = new WebBrowser();
                    wb.Navigate(new Uri(pdfPath));
                    grid.Children.Add(wb);
                }
                else if (strContent.TrimStart().StartsWith("<!DOCTYPE html") || strContent.TrimStart().StartsWith("<html>"))
                {
                    WebBrowser wb = new WebBrowser();
                    string tempHtmlFile = Path.Combine(Path.GetTempPath(), $"lad_view_{Guid.NewGuid():N}.html");
                    File.WriteAllText(tempHtmlFile, strContent, Encoding.UTF8);
                    wb.Navigate(new Uri(tempHtmlFile));
                    grid.Children.Add(wb);
                }
                else
                {
                    TextEditor editor = CreateCodeEditor(strContent);
                    grid.Children.Add(editor);
                }
            }
        }

        public UIElement CreateMultiTabView(string xmlContent, string mainHeader, object mainContent, string sclContent = null, string jsonIrContent = null, bool isSafety = false)
        {
            var tab = new TabControl
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };

            // Main Tab
            var mainItem = new TabItem { Header = mainHeader };
            UIElement mainElement = null;
            if (mainContent is UIElement element)
            {
                mainElement = element;
            }
            else if (mainContent is string str)
            {
                if (str.TrimStart().StartsWith("<!DOCTYPE html") || str.TrimStart().StartsWith("<html>"))
                {
                    WebBrowser wb = new WebBrowser();
                    string tempHtmlFile = Path.Combine(Path.GetTempPath(), $"view_{Guid.NewGuid():N}.html");
                    File.WriteAllText(tempHtmlFile, str, Encoding.UTF8);
                    wb.Navigate(new Uri(tempHtmlFile));
                    mainElement = wb;
                }
                else
                {
                    mainElement = CreateCodeEditor(str);
                }
            }

            // Safety-program (F-Program) block: overlay a safety badge at the top of the main tab.
            mainItem.Content = isSafety ? WrapWithSafetyBadge(mainElement) : mainElement;
            tab.Items.Add(mainItem);

            // SCL Tab
            if (!string.IsNullOrEmpty(sclContent))
            {
                var sclItem = new TabItem { Header = "SCL" };
                sclItem.Content = CreateCodeEditor(sclContent);
                tab.Items.Add(sclItem);
            }

            // JSON Tab (TIA Agent IR)
            if (!string.IsNullOrWhiteSpace(jsonIrContent))
            {
                var jsonItem = new TabItem { Header = "JSON" };
                jsonItem.Content = CreateCodeEditor(PrettyPrintJson(jsonIrContent));
                tab.Items.Add(jsonItem);
            }

            // XML Tab
            var xmlItem = new TabItem { Header = "XML" };
            xmlItem.Content = CreateCodeEditor(xmlContent);
            tab.Items.Add(xmlItem);

            tab.SelectedIndex = 0;
            return tab;
        }

        /// <summary>
        /// Overlays a top safety-badge bar on the graphical view of a safety
        /// (F-Program) block so users can tell at a glance it is an F-LAD / F-FBD
        /// safety block. Leaves the content layout untouched; only adds a banner.
        /// </summary>
        private static UIElement WrapWithSafetyBadge(UIElement inner)
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Safety-yellow palette, matching TIA safety-program visual conventions.
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xC7, 0x44)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(6, 6, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = "🛡 " + LanguageManager.Instance.GetString("Lad.SafetyProgramBadge"),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20))
                }
            };
            Grid.SetRow(badge, 0);
            grid.Children.Add(badge);

            if (inner != null)
            {
                Grid.SetRow(inner, 1);
                grid.Children.Add(inner);
            }
            return grid;
        }

        public string TryGenerateSource(object item, PlcSoftware software)
        {
            try
            {
                if (software == null) return null;
                
                string extension = "";
                string name = "";
                IEngineeringObject engineeringObject = item as IEngineeringObject;
                
                if (item is PlcBlock block)
                {
                    name = block.Name;
                    
                    if (block is GlobalDB) 
                    {
                        extension = ".db";
                    }
                    else 
                    {
                        // Check language using dynamic to avoid compilation issues if property is missing on base
                        try 
                        {
                            dynamic dBlock = block;
                            var lang = dBlock.ProgrammingLanguage.ToString();
                            if (lang == "STL") extension = ".awl";
                            else if (lang == "SCL") extension = ".scl";
                            else return null; // Skip LAD/FBD/Graph for native source generation
                        }
                        catch
                        {
                            return null;
                        }
                    }
                }
                else if (item is PlcType type)
                {
                    name = type.Name;
                    extension = ".udt";
                }
                else
                {
                    return null;
                }
                
                string tempFile = Path.Combine(Path.GetTempPath(), name + extension);
                if (File.Exists(tempFile)) File.Delete(tempFile);
                
                // Note: GenerateSource requires an enumerable of IGenerateSource
                if (engineeringObject is IGenerateSource sourceItem)
                {
                    var list = new List<IGenerateSource> { sourceItem };
                    software.ExternalSourceGroup.GenerateSource(list, new FileInfo(tempFile), GenerateOptions.None);
                    
                    if (File.Exists(tempFile))
                    {
                        return File.ReadAllText(tempFile);
                    }
                }
            }
            catch
            {
                // Ignore errors and return null to trigger fallback
            }
            return null;
        }

        /// <summary>
        /// Re-serializes a compact JSON payload with indentation for display. Returns the
        /// original text unchanged when it is not valid JSON.
        /// </summary>
        private static string PrettyPrintJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                return JsonSerializer.Serialize(doc.RootElement, DisplayJsonOptions);
            }
            catch
            {
                return raw;
            }
        }

        private static bool IsJsonContent(string content)
        {
            var text = content?.TrimStart();
            if (string.IsNullOrEmpty(text)) return false;
            return text[0] == '{' || text[0] == '[';
        }

        public bool IsSclContent(string content)
        {
            var text = content ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.StartsWith("<")) return false;
            var sample = text.Length > 1000 ? text.Substring(0, 1000) : text;
            var patterns = new[]
            {
                "FUNCTION_BLOCK","END_FUNCTION_BLOCK","FUNCTION","END_FUNCTION",
                "VAR","VAR_INPUT","VAR_OUTPUT","VAR_IN_OUT","VAR_TEMP","END_VAR",
                "IF","THEN","ELSE","END_IF","WHILE","END_WHILE","FOR","END_FOR",
                "CASE","END_CASE","STRUCT","END_STRUCT","TYPE","END_TYPE"
            };
            foreach (var p in patterns)
            {
                if (sample.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private void UpdateFoldings(FoldingManager manager, object strategy, TextDocument document)
        {
            if (strategy == null) return;
            if (strategy is XmlFoldingStrategy xmlStrategy)
            {
                xmlStrategy.UpdateFoldings(manager, document);
            }
            else if (strategy is BraceFoldingStrategy braceStrategy)
            {
                braceStrategy.UpdateFoldings(manager, document);
            }
        }
    }
}