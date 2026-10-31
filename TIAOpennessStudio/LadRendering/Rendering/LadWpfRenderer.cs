using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.LadRendering.Parser;
using TIAOpennessStudio.LadRendering.Controls;

namespace TIAOpennessStudio.LadRendering.Rendering
{
    public static class LadWpfRenderer
    {
        private const double NodeWidth = 120; // Reduced node width
        private const double NodeHeaderHeight = 20; // Reduced node header height
        private const double NodeBodyMinHeight = 30; // Reduced node body height
        private const double NodeStartX = 72;
        private const double NodeStartY = 50;
        private const double NodeColumnSpacing = 190;
        private const double NodeRowSpacing = 105;

        /// <summary>
        /// Sets the cross-reference service (used by the hover popup); set to null to disable hover interaction.
        /// </summary>
        public static ITiaOpennessService CrossReferenceService { get; set; }

        /// <summary>
        /// Worker child-process host service: supplementary TIA API cross-reference queries go through this
        /// service (HTTP to the Worker's STA thread) so the UI thread is not blocked. When null, falls back to
        /// the in-process <see cref="CrossReferenceService"/>.
        /// </summary>
        public static IProjectKnowledgeWorkerHostService WorkerHostService { get; set; }

        /// <summary>
        /// Offline index query service (used for phase-2 fast project-wide reference lookup).
        /// Set before rendering by MainWindow or LadEditorWrapper.
        /// </summary>
        public static ProjectIndexService OfflineIndexStore { get; set; }

        /// <summary>Cache key of the current project (used for offline index queries)</summary>
        public static string CurrentProjectKey { get; set; }

        /// <summary>
        /// Name of the block currently being rendered (used for cross-reference queries)
        /// </summary>
        public static string CurrentBlockName { get; set; }

        /// <summary>Index of the Network currently being rendered (1-based)</summary>
        public static int CurrentNetworkIndex { get; set; }

        /// <summary>Title of the Network currently being rendered</summary>
        public static string CurrentNetworkTitle { get; set; }

        /// <summary>
        /// Navigation event raised when the user clicks a cross-reference entry.
        /// Parameters: (blockName, networkIndex)
        /// </summary>
        public static event Action<string, int> NavigateToReference;

        private static CrossReferencePopup _sharedPopup;
        private static bool _hoverHandlerSet;
        private static string _lastBlockName;

        /// <summary>
        /// Phase 2: queries the offline index for all project-wide reference locations of a variable.
        /// Returns in milliseconds and does not depend on a TIA connection.
        /// </summary>
        private static CrossReferenceResult TryQueryFromOfflineIndex(string variableName)
        {
            if (OfflineIndexStore == null || string.IsNullOrWhiteSpace(CurrentProjectKey))
                return null;

            try
            {
                var locations = OfflineIndexStore.QueryVariableReferenceLocations(CurrentProjectKey, variableName);
                if (locations == null || locations.Count == 0)
                    return null;

                var result = new CrossReferenceResult
                {
                    VariableName = variableName,
                    Scope = "GlobalVariable"
                };

                foreach (var loc in locations)
                {
                    result.Entries.Add(new CrossReferenceEntry
                    {
                        VariableName = variableName,
                        SourceName = loc.BlockName,
                        BlockName = loc.BlockName,
                        BlockType = loc.BlockType,
                        NetworkIndex = loc.NetworkIndex,
                        NetworkTitle = loc.NetworkTitle,
                        NetworkComment = loc.NetworkComment,
                        ReferenceType = MapDirection(loc.Direction),
                        ReferenceLocation = $"NW{loc.NetworkIndex}" + (string.IsNullOrWhiteSpace(loc.NetworkTitle) ? "" : $": {loc.NetworkTitle}"),
                        LocationName = loc.Kind
                    });
                }

                return result;
            }
            catch
            {
                return null;
            }
        }

        private static string MapDirection(string direction)
        {
            if (string.IsNullOrWhiteSpace(direction)) return "?";
            var d = direction.ToLowerInvariant();
            if (d == "read") return "Read";
            if (d == "write") return "Write";
            if (d == "readwrite") return "ReadWrite";
            return direction;
        }

        private sealed class Edge
        {
            public string SrcUid { get; set; }
            public string SrcPin { get; set; }
            public string DstUid { get; set; }
            public string DstPin { get; set; }
        }

        private sealed class PinRef
        {
            public string Uid { get; set; }
            public string Pin { get; set; }
        }

        private sealed class NodeVisual
        {
            public FrameworkElement Node { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
        }

        private sealed class ConnectionVisualModel
        {
            public Point Source { get; set; }
            public Point Target { get; set; }
            public Brush Stroke { get; set; }
            public double StrokeThickness { get; set; }
            /// <summary>
            /// Precomputed path geometry used to render non-standard connections (e.g. ground symbols, power rails).
            /// When this property is not null, the template renders with Path instead of LineConnection.
            /// </summary>
            public Geometry Geometry { get; set; }
        }

        public static UIElement Render(string xmlContent)
        {
            var snapshot = new GraphSnapshot
            {
                XmlContent = xmlContent ?? string.Empty
            };
            try
            {
                snapshot.Document = XDocument.Parse(snapshot.XmlContent);
                snapshot.ParseSucceeded = true;
            }
            catch
            {
                snapshot.ParseSucceeded = false;
            }
            return Render(snapshot);
        }

        public static UIElement Render(GraphSnapshot snapshot)
        {
            AppLogger.Log("Starting LAD Nodify rendering...");

            var scrollViewer = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = GetThemeBrush("LadCanvasBackgroundBrush", new SolidColorBrush(Color.FromRgb(30, 30, 30))),
                CanContentScroll = false,
                PanningMode = PanningMode.Both
            };
            // No local ScrollBar/Thumb style override: the app-wide slim themed style (Controls.Input.xaml)
            // already covers this ScrollViewer, so the canvas keeps the same non-intrusive bars as the rest of the UI.

            var mainStack = new StackPanel { Margin = new Thickness(20) };
            scrollViewer.Content = mainStack;

            try
            {
                if (snapshot == null)
                {
                    throw new ArgumentNullException(nameof(snapshot));
                }

                var doc = snapshot.Document ?? XDocument.Parse(snapshot.XmlContent ?? string.Empty);
                var compileUnits = doc.Descendants()
                    .Where(e => e.Name.LocalName.EndsWith("CompileUnit", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                AppLogger.Log($"Found {compileUnits.Count} networks to render. cache={snapshot.IsFromCache}, parseMs={snapshot.ParseElapsedMs}");

                int netIndex = 1;
                foreach (var cu in compileUnits)
                {
                    if (netIndex > 1)
                    {
                        mainStack.Children.Add(new Separator
                        {
                            Background = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                            Height = 1,
                            Margin = new Thickness(0, 5, 0, 15)
                        });
                    }

                    mainStack.Children.Add(RenderNetworkNodify(cu, netIndex, snapshot));
                    netIndex++;
                }

                if (compileUnits.Count == 0)
                {
                    mainStack.Children.Add(new TextBlock
                    {
                        Text = "No LAD/FBD compile units found.",
                        Foreground = Brushes.Gray
                    });
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError("LAD Nodify rendering failed", ex);
                mainStack.Children.Add(new TextBlock
                {
                    Text = "WPF Render Error: " + ex.Message,
                    Foreground = Brushes.Red
                });
            }

            return scrollViewer;
        }

        private static FrameworkElement RenderNetworkNodify(XElement cu, int index, GraphSnapshot snapshot)
        {
            var title = cu.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "MultilingualText" &&
                                     e.Attribute("CompositionName")?.Value == "Title")
                ?.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Text")
                ?.Value;

            // Track current network for cross-reference popup
            CurrentNetworkIndex = index;
            CurrentNetworkTitle = title ?? "";

            var host = new Expander
            {
                Header = $"Network {index}: {title ?? "(No Title)"}",
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 14),
                IsExpanded = true
            };

            var logicRoot = cu.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "FlgNet" ||
                                     e.Name.LocalName == "FbdNet" ||
                                     e.Descendants().Any(d => d.Name.LocalName == "Parts"));

            if (logicRoot == null)
            {
                // SCL network embedded in a LAD/FBD block: no FlgNet graph, the code lives in a
                // <StructuredText> token tree. Rebuild the readable SCL and show it inline (mixed
                // with LAD networks) so the SCL network is no longer silently dropped.
                var structuredText = cu.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "StructuredText");
                if (structuredText != null)
                {
                    var scl = TiaXmlParseHelper.RebuildStructuredText(structuredText);
                    host.Header = $"Network {index}: {title ?? "(No Title)"}  [SCL]";
                    host.Content = BuildSclCodeBlock(scl);
                    return host;
                }

                // STL network embedded in a LAD/FBD block: no FlgNet graph, the code lives in a
                // <StatementList> statement tree. Rebuild readable STL and show it inline so STL
                // networks (e.g. the CALL scheduling networks in OB1) are no longer dropped.
                var statementList = cu.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "StatementList");
                if (statementList != null)
                {
                    var stl = TiaXmlParseHelper.RebuildStatementList(statementList);
                    host.Header = $"Network {index}: {title ?? "(No Title)"}  [STL]";
                    host.Content = BuildSclCodeBlock(stl);
                    return host;
                }

                host.Content = new TextBlock
                {
                    Text = "No logic container found.",
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(8)
                };
                return host;
            }

            var placeholder = new Border
            {
                Background = GetThemeBrush("LadCanvasBackgroundBrush", new SolidColorBrush(Color.FromRgb(30, 30, 30))),
                BorderBrush = GetThemeBrush("LadCanvasBorderBrush", new SolidColorBrush(Color.FromRgb(63, 63, 70))),
                BorderThickness = new Thickness(1),
                Height = 64,
                Margin = new Thickness(0, 2, 0, 2),
                Child = new TextBlock
                {
                    Text = "Expand to load Nodify rendering...",
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(12, 8, 12, 8),
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            host.Content = placeholder;

            bool realized = false;
            void EnsureRealized()
            {
                if (realized) return;
                host.Content = RenderNetworkReadonly(logicRoot);
                realized = true;
            }

            host.Expanded += (s, e) => EnsureRealized();
            host.Loaded += (s, e) =>
            {
                if (host.IsExpanded) EnsureRealized();
            };

            return host;
        }

        /// <summary>
        /// Render an inline SCL code block for an SCL network embedded in a LAD/FBD block.
        /// Monospaced, selectable, dark canvas to match the LAD networks it is mixed with.
        /// </summary>
        private static FrameworkElement BuildSclCodeBlock(string sclText)
        {
            var codeBrush = GetThemeBrush("LadCanvasBackgroundBrush", new SolidColorBrush(Color.FromRgb(30, 30, 30)));
            var borderBrush = GetThemeBrush("LadCanvasBorderBrush", new SolidColorBrush(Color.FromRgb(63, 63, 70)));

            var textBox = new TextBox
            {
                Text = string.IsNullOrEmpty(sclText) ? "(empty SCL network)" : sclText,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = GetThemeBrush("LadTextBrush", Brushes.Gainsboro),
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
                FontSize = 13,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(10, 6, 10, 6)
            };

            return new Border
            {
                Background = codeBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 2, 0, 2),
                Child = textBox
            };
        }

        // Read-only LAD rendering (deterministic geometry layout, replacing the old Nodify hand-computed coordinate path).
        // Uses LadNetworkRenderer to produce an adaptive Canvas wrapped in a container that supports Ctrl+wheel zoom.
        private static FrameworkElement RenderNetworkReadonly(XElement logicRoot)
        {
            LadNetworkRenderer.SetThemeResolver((key, fallback) => GetThemeBrush(key, fallback));
            try
            {
                var net = TiaXmlParseHelper.ParseFlgNet(logicRoot);

                // Set up hover interaction (cross-reference popup) — uses a shared popup + local references
                var crossRefSvc = CrossReferenceService;
                var blockName = CurrentBlockName;
                if (crossRefSvc != null)
                {
                    // Lazy-initialize the shared popup
                    if (_sharedPopup == null)
                    {
                        _sharedPopup = new CrossReferencePopup();
                        _sharedPopup.LoadFullCrossRefs = async (varName, blkName) =>
                        {
                            // Hybrid approach: phase 2 offline index (instant) + phase 3 TIA API (parallel supplement, e.g. HMI)
                            CrossReferenceResult mergedResult = null;

                            // Phase 2: offline index query. The SQLite query does not touch the TIA STA thread, so it runs on a
                            // background thread, avoiding UI freezes from full-table LIKE scans in large projects.
                            var offlineResult = await Task.Run(() => TryQueryFromOfflineIndex(varName)).ConfigureAwait(true);
                            if (offlineResult != null && offlineResult.Count > 0)
                            {
                                mergedResult = offlineResult;
                            }

                            // Phase 3: TIA API supplement (includes references from HMI and other devices).
                            // Prefer the Worker child process: the project-wide TIA scan runs on the Worker's dedicated STA
                            // thread and the main process just waits over HTTP, which is truly async and never blocks the UI.
                            // If the Worker is unavailable, fall back to the in-process OpennessService (pseudo-async, briefly uses the UI thread).
                            var workerHost = WorkerHostService;
                            if (workerHost != null || crossRefSvc != null)
                            {
                                try
                                {
                                    CrossReferenceResult apiResult;
                                    if (workerHost != null)
                                    {
                                        // Worker path: ConfigureAwait(false) keeps the HTTP wait off the UI thread.
                                        apiResult = await workerHost.GetCrossReferencesAsync(varName, blkName)
                                            .ConfigureAwait(true);
                                    }
                                    else
                                    {
                                        // Fallback path: let the popup and offline results render first, then run the slower UI-thread scan on an idle frame.
                                        await Dispatcher.Yield(DispatcherPriority.Background);
                                        apiResult = await crossRefSvc.GetCrossReferencesAsync(varName, blkName)
                                            .ConfigureAwait(true);
                                    }

                                    if (apiResult != null && apiResult.Count > 0)
                                    {
                                        if (mergedResult == null)
                                        {
                                            mergedResult = apiResult;
                                        }
                                        else
                                        {
                                            // Append API entries not present in the offline index (HMI, other devices)
                                            var existingKeys = new System.Collections.Generic.HashSet<string>(
                                                mergedResult.Entries.Select(e => $"{e.BlockName}|{e.NetworkIndex}|{e.ReferenceType}"),
                                                StringComparer.OrdinalIgnoreCase);
                                            foreach (var entry in apiResult.Entries)
                                            {
                                                var key = $"{entry.BlockName ?? entry.SourceName}|{entry.NetworkIndex}|{entry.ReferenceType}";
                                                if (!existingKeys.Contains(key))
                                                {
                                                    mergedResult.Entries.Add(entry);
                                                    existingKeys.Add(key);
                                                }
                                            }

                                            // Fill in data type and scope
                                            if (string.IsNullOrWhiteSpace(mergedResult.DataType) && !string.IsNullOrWhiteSpace(apiResult.DataType))
                                                mergedResult.DataType = apiResult.DataType;
                                            if (string.IsNullOrWhiteSpace(mergedResult.Scope) && !string.IsNullOrWhiteSpace(apiResult.Scope))
                                                mergedResult.Scope = apiResult.Scope;
                                        }
                                    }
                                }
                                catch { /* A TIA API failure does not affect offline results */ }
                            }

                            return mergedResult;
                        };
                        _sharedPopup.OnEntryClicked += entry =>
                        {
                            if (entry != null && entry.CanNavigate)
                            {
                                NavigateToReference?.Invoke(entry.BlockName, entry.NetworkIndex);
                            }
                        };
                    }

                    // Clear old references when switching blocks, then merge the current page's references
                    if (!string.Equals(_lastBlockName, CurrentBlockName, StringComparison.OrdinalIgnoreCase))
                    {
                        _lastBlockName = CurrentBlockName;
                        _sharedPopup.ClearLocalReferences();
                    }
                    var localRefs = LadNetworkRenderer.GetLocalReferences(
                        net, CurrentBlockName, CurrentNetworkIndex, CurrentNetworkTitle);
                    _sharedPopup.MergeLocalReferences(localRefs);

                    // Set the right-click handler only once (static scope)
                    // Cross-references are triggered by right-click to avoid repeated queries freezing the UI when hovering over many elements
                    if (!_hoverHandlerSet)
                    {
                        _hoverHandlerSet = true;
                        LadNetworkRenderer.SetContextHandler((elem, target) =>
                        {
                            var variable = elem.Operand ?? elem.Title ?? "";
                            if (!string.IsNullOrWhiteSpace(variable))
                                _sharedPopup.ShowForElement(target, variable, CurrentBlockName);
                        });
                    }
                }
                else
                {
                    LadNetworkRenderer.SetContextHandler(null);
                }

                var content = LadNetworkRenderer.RenderNetwork(net);

                var scale = new ScaleTransform(1.0, 1.0);
                content.LayoutTransform = scale;

                var host = new Border
                {
                    Background = GetThemeBrush("LadCanvasBackgroundBrush", new SolidColorBrush(Color.FromRgb(30, 30, 30))),
                    Child = content,
                    Padding = new Thickness(4)
                };

                // Ctrl+wheel zoom; plain wheel is passed to the outer ScrollViewer
                host.PreviewMouseWheel += (s, e) =>
                {
                    if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
                    double factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
                    double next = Math.Max(0.3, Math.Min(3.0, scale.ScaleX * factor));
                    scale.ScaleX = scale.ScaleY = next;
                    e.Handled = true;
                };
                return host;
            }
            catch (Exception ex)
            {
                AppLogger.LogError("LAD readonly render failed", ex);
                return new TextBlock { Text = "LAD render error: " + ex.Message, Foreground = Brushes.Red, Margin = new Thickness(8) };
            }
        }


        private static string BuildPartDisplayName(FlgPart part)
        {
            if (part != null && part.IsCall)
            {
                var callName = part.DisplayName ?? part.CallName ?? "";
                var instance = part.Instance ?? "";
                if (!string.IsNullOrWhiteSpace(callName) && !string.IsNullOrWhiteSpace(instance))
                    return callName + "\n" + instance;
                if (!string.IsNullOrWhiteSpace(callName))
                    return callName;
                if (!string.IsNullOrWhiteSpace(instance))
                    return instance;
            }
            return TranslatePartKind(part?.Name ?? "Part");
        }

        private static string ResolveOperandForPart(string uid, Dictionary<string, string> partPinSymbol)
        {
            string operand;
            if (partPinSymbol.TryGetValue(BuildPinKey(uid, "operand"), out operand)) return operand;
            if (partPinSymbol.TryGetValue(BuildPinKey(uid, "in"), out operand)) return operand;
            if (partPinSymbol.TryGetValue(BuildPinKey(uid, "out"), out operand)) return operand;

            var any = partPinSymbol.FirstOrDefault(kv => kv.Key.StartsWith(uid + "|", StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(any.Value) ? "" : any.Value;
        }

        private static double EstimateNodeHeight(int pinCount)
        {
            int rows = Math.Max(2, pinCount);
            return NodeHeaderHeight + NodeBodyMinHeight + rows * 10;
        }

        /// <summary>
        /// Estimates the displayed text width (used to compute node width dynamically).
        /// </summary>
        private static double EstimateTextWidth(string text, double fontSize = 10)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            
            var formattedText = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                fontSize,
                Brushes.Black,
                VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip);
            
            return formattedText.Width;
        }

        /// <summary>
        /// Computes the Contact/Coil node width dynamically from the label length.
        /// </summary>
        private static double CalculateContactCoilWidth(string label, string operand, string kind = "")
        {
            // Fixed width of the symbol area (the contact/coil symbol itself)
            bool isNot = IsNotContactKind(kind);
            double symbolAreaWidth = isNot ? 60 : 40; // NOT needs to be wider to fit 3 letters
            
            string displayText = !string.IsNullOrEmpty(operand) ? operand : label ?? "";
            double textWidth = EstimateTextWidth(displayText, 10);
            
            // Minimum width keeps the symbol fully visible; maximum width avoids it becoming too wide
            double minWidth = 80;
            double maxWidth = 200;
            
            // Dynamic width = symbol area + text width + left/right margins
            double dynamicWidth = symbolAreaWidth + textWidth + 16;
            
            return Math.Max(minWidth, Math.Min(maxWidth, dynamicWidth));
        }

        private static bool IsNotContactKind(string kind)
        {
            var k = (kind ?? "").Trim().ToLowerInvariant();
            return k == "not_contact" || k == "not";
        }

        private static string BuildPinKey(string uid, string pin)
        {
            return (uid ?? "") + "|" + NormalizePinName(pin ?? "");
        }

        private static string NormalizePinName(string pin)
        {
            return (pin ?? "").Trim().ToLowerInvariant();
        }


        /// <summary>
        /// Creates a standard IEC 61131-3 symbol
        /// </summary>
        private static FrameworkElement CreateStandardSymbol(string kind, string title, string operand, bool isNegated, double width, double height)
        {
            var canvas = new Canvas
            {
                Width = width,
                Height = height,
                Background = Brushes.Transparent
            };

            // Draw a different symbol depending on the node type
            switch (kind)
            {
                case "contact":
                case "pcontact":
                case "ncontact":
                case "not_contact":
                case "not":
                    DrawContactSymbol(canvas, width, height, isNegated, title, kind);
                    break;
                case "coil":
                case "scoil":
                case "rcoil":
                case "pcoil":
                case "ncoil":
                    DrawCoilSymbol(canvas, width, height, isNegated, title, kind);
                    break;
                case "timer":
                    DrawTimerSymbol(canvas, width, height, title, operand);
                    break;
                case "counter":
                    DrawCounterSymbol(canvas, width, height, title, operand);
                    break;
                default:
                    DrawGenericSymbol(canvas, width, height, title);
                    break;
            }

            return canvas;
        }

        /// <summary>
        /// Draws a Contact symbol
        /// NO contact: -|  |-
        /// NC contact: -|/|-
        /// P contact: -| P |-
        /// N contact: -| N |-
        /// NOT contact: -| NOT |-
        /// </summary>
        private static void DrawContactSymbol(Canvas canvas, double width, double height, bool isNegated, string title, string kind = "contact")
        {
            var brush = GetThemeBrush("LadSymbolBrush", Brushes.White);
            double centerY = height / 2;
            double leftX = 0; // connects directly to the power rail, no margin
            double rightX = width;
            double symbolWidth = 14;

            var leftLine = new Line
            {
                X1 = leftX, Y1 = centerY,
                X2 = leftX + symbolWidth, Y2 = centerY,
                Stroke = brush, StrokeThickness = 2
            };
            canvas.Children.Add(leftLine);

            var rightLine = new Line
            {
                X1 = rightX - symbolWidth, Y1 = centerY,
                X2 = rightX, Y2 = centerY,
                Stroke = brush, StrokeThickness = 2
            };
            canvas.Children.Add(rightLine);

            double symbolLeft = leftX + symbolWidth;
            double symbolRight = rightX - symbolWidth;
            double symbolHeight = 16;

            var leftVertical = new Line
            {
                X1 = symbolLeft, Y1 = centerY - symbolHeight / 2,
                X2 = symbolLeft, Y2 = centerY + symbolHeight / 2,
                Stroke = brush, StrokeThickness = 2
            };
            canvas.Children.Add(leftVertical);

            var rightVertical = new Line
            {
                X1 = symbolRight, Y1 = centerY - symbolHeight / 2,
                X2 = symbolRight, Y2 = centerY + symbolHeight / 2,
                Stroke = brush, StrokeThickness = 2
            };
            canvas.Children.Add(rightVertical);

            // Diagonal line for an NC contact
            if (isNegated)
            {
                var diagonal = new Line
                {
                    X1 = symbolLeft + 2, Y1 = centerY - symbolHeight / 2 + 2,
                    X2 = symbolRight - 2, Y2 = centerY + symbolHeight / 2 - 2,
                    Stroke = brush, StrokeThickness = 2
                };
                canvas.Children.Add(diagonal);
            }

            // Special contact type letter: P / N / NOT
            var contactLetter = GetContactLetter(kind);
            if (!string.IsNullOrEmpty(contactLetter))
            {
                var letterText = new TextBlock
                {
                    Text = contactLetter,
                    Foreground = brush,
                    FontSize = contactLetter == "NOT" ? 7 : 9,
                    FontWeight = FontWeights.Bold,
                    TextAlignment = TextAlignment.Center,
                    Width = symbolRight - symbolLeft - 4
                };
                Canvas.SetLeft(letterText, symbolLeft + 2);
                Canvas.SetTop(letterText, centerY - 7);
                canvas.Children.Add(letterText);
            }

            var label = new TextBlock
            {
                Text = title,
                Foreground = brush,
                FontSize = 10,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, // allow automatic wrapping
                Width = width - 2,
                MaxHeight = 40 // increased height to fit multiple lines
            };
            Canvas.SetLeft(label, 1);
            Canvas.SetTop(label, centerY + symbolHeight / 2 + 6); // below the contact symbol, 6px gap
            canvas.Children.Add(label);
        }

        private static string GetContactLetter(string kind)
        {
            var k = (kind ?? "").Trim().ToLowerInvariant();
            if (k == "pcontact") return "P";
            if (k == "ncontact") return "N";
            if (k == "not_contact" || k == "not") return "NOT";
            return "";
        }

        private static string GetCoilTypeLabel(string coilType)
        {
            var k = (coilType ?? "").Trim().ToLowerInvariant();
            if (k == "scoil") return "S";
            if (k == "rcoil") return "R";
            if (k == "pcoil") return "P";
            if (k == "ncoil") return "N";
            return k.ToUpperInvariant();
        }

        /// <summary>
        /// Draws a Coil symbol
        /// Output coil: -( )-
        /// Set coil: -(S)-
        /// Reset coil: -(R)-
        /// </summary>
        private static void DrawCoilSymbol(Canvas canvas, double width, double height, bool isNegated, string title, string coilType)
        {
            var brush = GetThemeBrush("LadSymbolBrush", Brushes.White);
            double centerY = height / 2;
            double leftX = 0; // connects directly to the power rail, no margin
            double rightX = width;
            double symbolWidth = 14;

            var leftLine = new Line
            {
                X1 = leftX, Y1 = centerY,
                X2 = leftX + symbolWidth, Y2 = centerY,
                Stroke = brush, StrokeThickness = 2
            };
            canvas.Children.Add(leftLine);

            var rightLine = new Line
            {
                X1 = rightX - symbolWidth, Y1 = centerY,
                X2 = rightX, Y2 = centerY,
                Stroke = brush, StrokeThickness = 2
            };
            canvas.Children.Add(rightLine);

            // Coil symbol (left arc + right arc)
            double symbolLeft = leftX + symbolWidth;
            double symbolRight = rightX - symbolWidth;
            double radius = 10;
            double centerX = (symbolLeft + symbolRight) / 2;

            var leftArc = new Path
            {
                Stroke = brush,
                StrokeThickness = 2,
                Data = Geometry.Parse($"M {symbolLeft + radius} {centerY - radius} A {radius} {radius} 0 0 0 {symbolLeft + radius} {centerY + radius}")
            };
            canvas.Children.Add(leftArc);

            var rightArc = new Path
            {
                Stroke = brush,
                StrokeThickness = 2,
                Data = Geometry.Parse($"M {symbolRight - radius} {centerY - radius} A {radius} {radius} 0 0 1 {symbolRight - radius} {centerY + radius}")
            };
            canvas.Children.Add(rightArc);

            // Coil type marker (centered between the left and right arcs)
            if (!string.Equals(coilType, "coil", StringComparison.OrdinalIgnoreCase))
            {
                var typeLabel = new TextBlock
                {
                    Text = GetCoilTypeLabel(coilType),
                    Foreground = brush,
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    TextAlignment = TextAlignment.Center,
                    Width = radius * 2 - 4
                };
                Canvas.SetLeft(typeLabel, centerX - radius + 2);
                Canvas.SetTop(typeLabel, centerY - 6);
                canvas.Children.Add(typeLabel);
            }

            // Diagonal line for an NC coil
            if (isNegated)
            {
                var diagonal = new Line
                {
                    X1 = symbolLeft + 2, Y1 = centerY - radius + 2,
                    X2 = symbolRight - 2, Y2 = centerY + radius - 2,
                    Stroke = brush, StrokeThickness = 2
                };
                canvas.Children.Add(diagonal);
            }

            var label = new TextBlock
            {
                Text = title,
                Foreground = brush,
                FontSize = 10,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, // allow automatic wrapping
                Width = width - 2,
                MaxHeight = 40 // increased height to fit multiple lines
            };
            Canvas.SetLeft(label, 1);
            Canvas.SetTop(label, centerY + radius + 6); // below the coil symbol, 6px gap
            canvas.Children.Add(label);
        }

        /// <summary>
        /// Draws a Timer symbol
        /// </summary>
        private static void DrawTimerSymbol(Canvas canvas, double width, double height, string title, string timerType)
        {
            var brush = GetThemeBrush("LadSymbolBrush", Brushes.White);
            double centerY = height / 2;
            double leftX = 10;
            double rightX = width - 10;
            double symbolWidth = 20;

            var leftLine = new Line
            {
                X1 = leftX, Y1 = centerY,
                X2 = leftX + symbolWidth, Y2 = centerY,
                Stroke = brush, StrokeThickness = 2
            };
            canvas.Children.Add(leftLine);

            var rightLine = new Line
            {
                X1 = rightX - symbolWidth, Y1 = centerY,
                X2 = rightX, Y2 = centerY,
                Stroke = brush, StrokeThickness = 2
            };
            canvas.Children.Add(rightLine);

            // Timer box
            double boxLeft = leftX + symbolWidth;
            double boxRight = rightX - symbolWidth;
            double boxTop = centerY - 20;
            double boxBottom = centerY + 20;

            var rect = new Rectangle
            {
                Width = boxRight - boxLeft,
                Height = boxBottom - boxTop,
                Stroke = brush,
                StrokeThickness = 2,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(rect, boxLeft);
            Canvas.SetTop(rect, boxTop);
            canvas.Children.Add(rect);

            // Timer type
            var typeLabel = new TextBlock
            {
                Text = timerType?.ToUpperInvariant() ?? "TON",
                Foreground = brush,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                Width = boxRight - boxLeft
            };
            Canvas.SetLeft(typeLabel, boxLeft);
            Canvas.SetTop(typeLabel, centerY - 14);
            canvas.Children.Add(typeLabel);

            var label = new TextBlock
            {
                Text = title,
                Foreground = brush,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Width = width - 4,
                MaxHeight = 32
            };
            Canvas.SetLeft(label, 2);
            Canvas.SetTop(label, height - 34);
            canvas.Children.Add(label);
        }

        /// <summary>
        /// Draws a Counter symbol
        /// </summary>
        private static void DrawCounterSymbol(Canvas canvas, double width, double height, string title, string counterType)
        {
            var brush = GetThemeBrush("LadSymbolBrush", Brushes.White);
            double centerY = height / 2;
            double leftX = 10;
            double rightX = width - 10;
            double symbolWidth = 20;

            var leftLine = new Line
            {
                X1 = leftX, Y1 = centerY,
                X2 = leftX + symbolWidth, Y2 = centerY,
                Stroke = brush, StrokeThickness = 2
            };
            canvas.Children.Add(leftLine);

            var rightLine = new Line
            {
                X1 = rightX - symbolWidth, Y1 = centerY,
                X2 = rightX, Y2 = centerY,
                Stroke = brush, StrokeThickness = 2
            };
            canvas.Children.Add(rightLine);

            // Counter box
            double boxLeft = leftX + symbolWidth;
            double boxRight = rightX - symbolWidth;
            double boxTop = centerY - 20;
            double boxBottom = centerY + 20;

            var rect = new Rectangle
            {
                Width = boxRight - boxLeft,
                Height = boxBottom - boxTop,
                Stroke = brush,
                StrokeThickness = 2,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(rect, boxLeft);
            Canvas.SetTop(rect, boxTop);
            canvas.Children.Add(rect);

            // Counter type
            var typeLabel = new TextBlock
            {
                Text = counterType?.ToUpperInvariant() ?? "CTU",
                Foreground = brush,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                Width = boxRight - boxLeft
            };
            Canvas.SetLeft(typeLabel, boxLeft);
            Canvas.SetTop(typeLabel, centerY - 14);
            canvas.Children.Add(typeLabel);

            var label = new TextBlock
            {
                Text = title,
                Foreground = brush,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Width = width - 4,
                MaxHeight = 32
            };
            Canvas.SetLeft(label, 2);
            Canvas.SetTop(label, height - 34);
            canvas.Children.Add(label);
        }

        /// <summary>
        /// Draws a generic symbol
        /// </summary>
        private static void DrawGenericSymbol(Canvas canvas, double width, double height, string title)
        {
            var brush = GetThemeBrush("LadSymbolBrush", Brushes.White);
            double centerY = height / 2;
            double leftX = 10;
            double rightX = width - 10;

            var leftLine = new Line
            {
                X1 = leftX, Y1 = centerY,
                X2 = leftX + 20, Y2 = centerY,
                Stroke = brush, StrokeThickness = 2
            };
            canvas.Children.Add(leftLine);

            var rightLine = new Line
            {
                X1 = rightX - 20, Y1 = centerY,
                X2 = rightX, Y2 = centerY,
                Stroke = brush, StrokeThickness = 2
            };
            canvas.Children.Add(rightLine);

            // Generic box
            var rect = new Rectangle
            {
                Width = width - 60,
                Height = 40,
                Stroke = brush,
                StrokeThickness = 2,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(rect, leftX + 20);
            Canvas.SetTop(rect, centerY - 20);
            canvas.Children.Add(rect);

            var label = new TextBlock
            {
                Text = title,
                Foreground = brush,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Width = width - 60,
                MaxHeight = 32
            };
            Canvas.SetLeft(label, leftX + 20);
            Canvas.SetTop(label, centerY - 8);
            canvas.Children.Add(label);
        }

        /// <summary>
        /// Creates a Call block symbol
        /// </summary>
        private static FrameworkElement CreateCallBlockSymbol(string title, List<string> inputs, List<string> outputs, double width, string kind = "")
        {
            var callGrid = new Grid();
            callGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            callGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var callName = new TextBox
            {
                Text = title,
                TextWrapping = TextWrapping.Wrap,
                Foreground = GetThemeBrush("TextBrush", Brushes.White),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 1, 0, 2),
                MaxWidth = width - 20,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0)
            };
            Grid.SetRow(callName, 0);
            callGrid.Children.Add(callName);

            // Build pin labels: left column = inputs, right column = outputs
            var pinGrid = new Grid { Margin = new Thickness(0, 1, 0, 0) };
            pinGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pinGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var leftStack = new StackPanel();
            var rightStack = new StackPanel();

            foreach (var pin in inputs)
            {
                leftStack.Children.Add(new TextBox
                {
                    Text = pin,
                    Foreground = Brushes.Silver,
                    FontSize = 10,
                    Margin = new Thickness(0, 0, 0, 0),
                    IsReadOnly = true,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Padding = new Thickness(0)
                });
            }

            foreach (var pin in outputs)
            {
                rightStack.Children.Add(new TextBox
                {
                    Text = pin,
                    Foreground = Brushes.Silver,
                    FontSize = 10,
                    Margin = new Thickness(0, 0, 0, 0),
                    IsReadOnly = true,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Padding = new Thickness(0)
                });
            }

            // EN/ENO only for Call blocks, not for box blocks (MOVE, ADD, Timer, etc.)
            bool isBoxBlock = IsBoxBlock(kind);
            if (!isBoxBlock)
            {
                if (leftStack.Children.Count == 0)
                    leftStack.Children.Add(new TextBox { Text = "EN", Foreground = Brushes.Silver, FontSize = 10, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0) });
                if (rightStack.Children.Count == 0)
                    rightStack.Children.Add(new TextBox { Text = "ENO", Foreground = Brushes.Silver, FontSize = 10, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0) });
            }

            Grid.SetColumn(leftStack, 0);
            Grid.SetColumn(rightStack, 1);
            pinGrid.Children.Add(leftStack);
            pinGrid.Children.Add(rightStack);
            Grid.SetRow(pinGrid, 1);
            callGrid.Children.Add(pinGrid);

            return callGrid;
        }

        private static string BuildNodeTitle(string normalizedKind, string displayName, string operand, bool isNegated, string srcType, string compareTitle)
        {
            if (TryGetCompareOperator(normalizedKind, out var op))
            {
                if (!string.IsNullOrWhiteSpace(compareTitle)) return compareTitle;
                return string.IsNullOrWhiteSpace(operand) ? string.Empty : operand;
            }

            string baseTitle;
            if (string.Equals(normalizedKind, "call", StringComparison.OrdinalIgnoreCase))
            {
                baseTitle = displayName;
            }
            else
            {
                // Show the operand (variable name); also show displayName if it differs
                bool hasOperand = !string.IsNullOrWhiteSpace(operand);
                bool hasDisplayName = !string.IsNullOrWhiteSpace(displayName);
                bool areDifferent = hasOperand && hasDisplayName &&
                    !string.Equals(operand, displayName, StringComparison.OrdinalIgnoreCase);

                if (hasOperand && areDifferent)
                {
                    baseTitle = operand + "\n" + displayName;
                }
                else if (hasOperand)
                {
                    baseTitle = operand;
                }
                else if (hasDisplayName)
                {
                    baseTitle = displayName;
                }
                else
                {
                    baseTitle = string.IsNullOrWhiteSpace(normalizedKind) ? "PART" : normalizedKind.ToUpperInvariant();
                }
            }

            if (isNegated && baseTitle.IndexOf("NEGATED", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return baseTitle + " [NEGATED]";
            }

            return baseTitle;
        }

        private static string BuildCompareTitle(string uid, string partKind, string srcType, Dictionary<string, string> partPinSymbol)
        {
            var normalizedKind = (partKind ?? string.Empty).Trim().ToLowerInvariant();
            if (!TryGetCompareOperator(normalizedKind, out var op)) return string.Empty;

            var left = ResolvePinSymbol(uid, "in1", partPinSymbol);
            var right = ResolvePinSymbol(uid, "in2", partPinSymbol);
            if (string.IsNullOrWhiteSpace(left))
            {
                left = ResolvePinSymbol(uid, "operand", partPinSymbol);
            }

            if (!string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right))
            {
                return left + "\n" + op + "\n" + right;
            }
            if (!string.IsNullOrWhiteSpace(left))
            {
                return left;
            }
            if (!string.IsNullOrWhiteSpace(right))
            {
                return right;
            }
            return string.Empty;
        }

        private static string ResolvePinSymbol(string uid, string pin, Dictionary<string, string> partPinSymbol)
        {
            if (partPinSymbol == null) return string.Empty;
            if (partPinSymbol.TryGetValue(BuildPinKey(uid, pin), out var symbol))
            {
                return symbol ?? string.Empty;
            }
            return string.Empty;
        }

        private static string BuildSemanticTag(string normalizedKind, bool isNegated, string srcType, string callType)
        {
            if (normalizedKind == "contact")
            {
                return isNegated ? "NC" : "NO";
            }
            if (normalizedKind == "pcontact")
            {
                return "P";
            }
            if (normalizedKind == "ncontact")
            {
                return "N";
            }
            if (normalizedKind == "not_contact" || normalizedKind == "not")
            {
                return "NOT";
            }
            if (normalizedKind == "coil" || normalizedKind == "scoil" || normalizedKind == "rcoil"
                || normalizedKind == "pcoil" || normalizedKind == "ncoil")
            {
                if (normalizedKind == "scoil") return "S COIL";
                if (normalizedKind == "rcoil") return "R COIL";
                if (normalizedKind == "pcoil") return "P COIL";
                if (normalizedKind == "ncoil") return "N COIL";
                return "COIL";
            }
            if (normalizedKind == "call")
            {
                if (string.IsNullOrWhiteSpace(callType)) return "CALL";
                var ct = callType.Trim().ToUpperInvariant();
                if (ct == "FC" || ct == "FB" || ct == "OB" || ct == "SFC" || ct == "SFB" || ct == "UDT")
                    return ct;
                return ct;
            }
            if (TryGetCompareOperator(normalizedKind, out var op))
            {
                var t = string.IsNullOrWhiteSpace(srcType) ? "Int" : srcType;
                var logic = isNegated ? "NC" : "NO";
                return $"{logic} {op}:[{t}]";
            }
            // Box block type recognition: MOVE, AND, OR, XOR, NOT, ADD, SUB, MUL, DIV, Timer, Counter, etc.
            if (IsBoxBlock(normalizedKind))
            {
                return normalizedKind.ToUpperInvariant();
            }
            return "PART";
        }

        /// <summary>
        /// Determines whether this is a box block (requires rectangle rendering + pin display)
        /// </summary>
        private static bool IsBoxBlock(string normalizedKind)
        {
            if (string.IsNullOrWhiteSpace(normalizedKind)) return false;
            switch (normalizedKind)
            {
                // Logical operations
                case "and": case "a":
                case "or": case "o":
                case "xor": case "x":
                // Data operations
                case "move":
                case "conv": case "convert":
                case "swap":
                // Math operations
                case "add":
                case "sub":
                case "mul":
                case "div":
                case "mod":
                case "abs":
                case "neg":
                case "inc":
                case "dec":
                // Shifts
                case "shl": case "shr":
                case "rol": case "ror":
                // Strings
                case "len": case "concat":
                case "left": case "right": case "mid":
                // Conversions
                case "bcd_to_int": case "int_to_bcd":
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryGetCompareOperator(string normalizedKind, out string op)
        {
            op = string.Empty;
            switch (normalizedKind)
            {
                case "ne":
                    op = "<>";
                    return true;
                case "eq":
                    op = "=";
                    return true;
                case "gt":
                    op = ">";
                    return true;
                case "ge":
                    op = ">=";
                    return true;
                case "lt":
                    op = "<";
                    return true;
                case "le":
                    op = "<=";
                    return true;
                default:
                    return false;
            }
        }

        private static string T(string key)
        {
            try
            {
                return LanguageManager.Instance.GetString(key);
            }
            catch
            {
                return key;
            }
        }

        private static bool IsExecutionOutputPin(string pin)
        {
            var n = NormalizePinName(pin);
            if (string.IsNullOrWhiteSpace(n)) return false;
            return n.StartsWith("out", StringComparison.Ordinal) ||
                   n == "eno" ||
                   n == "q" ||
                   n == "cv" ||
                   n == "et" ||
                   n == "dn" ||
                   n == "error";
        }

        private static bool IsExecutionInputPin(string pin)
        {
            var n = NormalizePinName(pin);
            if (string.IsNullOrWhiteSpace(n)) return false;
            return n == "in" ||
                   n == "en" ||
                   n == "pre" ||
                   n.StartsWith("in", StringComparison.Ordinal);
        }

        private static void AddEdge(
            PinRef src,
            PinRef dst,
            List<Edge> edges,
            HashSet<string> edgeDedup,
            Dictionary<string, HashSet<string>> successors,
            Dictionary<string, HashSet<string>> predecessors)
        {
            if (src == null || dst == null) return;
            if (string.IsNullOrWhiteSpace(src.Uid) || string.IsNullOrWhiteSpace(dst.Uid)) return;
            if (string.Equals(src.Uid, dst.Uid, StringComparison.OrdinalIgnoreCase)) return;

            string key = src.Uid + "|" + src.Pin + "->" + dst.Uid + "|" + dst.Pin;
            if (!edgeDedup.Add(key)) return;

            edges.Add(new Edge
            {
                SrcUid = src.Uid,
                SrcPin = src.Pin,
                DstUid = dst.Uid,
                DstPin = dst.Pin
            });

            if (!successors.ContainsKey(src.Uid))
                successors[src.Uid] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            successors[src.Uid].Add(dst.Uid);

            if (!predecessors.ContainsKey(dst.Uid))
                predecessors[dst.Uid] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            predecessors[dst.Uid].Add(src.Uid);
        }

        private static SolidColorBrush GetThemeBrush(string key, Brush fallback)
        {
            try
            {
                var brush = Application.Current?.TryFindResource(key) as SolidColorBrush;
                if (brush != null)
                {
                    var clone = brush.CloneCurrentValue();
                    if (clone.CanFreeze) clone.Freeze();
                    return clone;
                }
            }
            catch
            {
            }

            if (fallback is SolidColorBrush solid)
            {
                var clone = solid.CloneCurrentValue();
                if (clone.CanFreeze) clone.Freeze();
                return clone;
            }

            return new SolidColorBrush(Colors.Transparent);
        }

        private static string TranslatePartKind(string partKind)
        {
            var p = (partKind ?? "").Trim();
            if (string.IsNullOrWhiteSpace(p)) return "PART";
            if (p.Equals("Contact", StringComparison.OrdinalIgnoreCase)) return "CONTACT";
            if (p.Equals("PContact", StringComparison.OrdinalIgnoreCase)) return "PCONTACT";
            if (p.Equals("NContact", StringComparison.OrdinalIgnoreCase)) return "NCONTACT";
            if (p.Equals("NotContact", StringComparison.OrdinalIgnoreCase)) return "NOT_CONTACT";
            if (p.Equals("Coil", StringComparison.OrdinalIgnoreCase)) return "COIL";
            if (p.Equals("SCoil", StringComparison.OrdinalIgnoreCase)) return "SCOIL";
            if (p.Equals("RCoil", StringComparison.OrdinalIgnoreCase)) return "RCOIL";
            if (p.Equals("PCoil", StringComparison.OrdinalIgnoreCase)) return "PCOIL";
            if (p.Equals("NCoil", StringComparison.OrdinalIgnoreCase)) return "NCOIL";
            if (p.Equals("Call", StringComparison.OrdinalIgnoreCase)) return "CALL";
            if (p.Equals("Ne", StringComparison.OrdinalIgnoreCase)) return "NE";
            if (p.Equals("Eq", StringComparison.OrdinalIgnoreCase)) return "EQ";
            if (p.Equals("Gt", StringComparison.OrdinalIgnoreCase)) return "GT";
            if (p.Equals("Ge", StringComparison.OrdinalIgnoreCase)) return "GE";
            if (p.Equals("Lt", StringComparison.OrdinalIgnoreCase)) return "LT";
            if (p.Equals("Le", StringComparison.OrdinalIgnoreCase)) return "LE";
            if (p.Equals("And", StringComparison.OrdinalIgnoreCase)) return "AND";
            if (p.Equals("A", StringComparison.OrdinalIgnoreCase)) return "AND";
            if (p.Equals("Or", StringComparison.OrdinalIgnoreCase)) return "OR";
            if (p.Equals("O", StringComparison.OrdinalIgnoreCase)) return "OR";
            if (p.Equals("Xor", StringComparison.OrdinalIgnoreCase)) return "XOR";
            if (p.Equals("Not", StringComparison.OrdinalIgnoreCase)) return "NOT";
            if (p.Equals("Move", StringComparison.OrdinalIgnoreCase)) return "MOVE";
            return p.ToUpperInvariant();
        }

        private static ConnectionVisualModel CreateConnectionModel(Point source, Point target, Brush stroke, double thickness)
        {
            return new ConnectionVisualModel
            {
                Source = source,
                Target = target,
                Stroke = stroke,
                StrokeThickness = thickness,
                Geometry = CreateLineGeometry(source, target)
            };
        }

        /// <summary>
        /// Creates a simple straight-line geometry
        /// </summary>
        private static Geometry CreateLineGeometry(Point start, Point end)
        {
            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new LineSegment(end, true));
            geometry.Figures.Add(figure);
            return geometry;
        }

        /// <summary>
        /// Creates a vertical-line geometry (for power rails)
        /// </summary>
        private static Geometry CreateVerticalLineGeometry(Point top, Point bottom)
        {
            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = top, IsClosed = false };
            figure.Segments.Add(new LineSegment(bottom, true));
            geometry.Figures.Add(figure);
            return geometry;
        }

        /// <summary>
        /// Generates right-angle polyline path points
        /// </summary>
        private static List<Point> GenerateOrthogonalRoute(Point start, Point end)
        {
            var points = new List<Point> { start };

            double midX = (start.X + end.X) / 2;

            // Add intermediate points
            points.Add(new Point(midX, start.Y));
            points.Add(new Point(midX, end.Y));

            points.Add(end);
            return points;
        }

        /// <summary>
        /// Creates a ground-symbol connection model
        /// Ground symbol: ─┬─ (three horizontal lines, shortening from top to bottom)
        /// </summary>
        private static ConnectionVisualModel CreateGroundConnectionModel(Point source, Brush stroke, double thickness)
        {
            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = source, IsClosed = false };
            
            // Horizontal connecting line to the ground symbol
            var symbolX = source.X + 12;
            figure.Segments.Add(new LineSegment(new Point(symbolX, source.Y), true));
            
            // Vertical line
            figure.Segments.Add(new LineSegment(new Point(symbolX, source.Y - 8), true));
            figure.Segments.Add(new LineSegment(new Point(symbolX, source.Y + 8), true));
            
            geometry.Figures.Add(figure);
            
            // Three horizontal lines (shortening from top to bottom)
            var h1 = new PathFigure { StartPoint = new Point(source.X + 6, source.Y - 5), IsClosed = false };
            h1.Segments.Add(new LineSegment(new Point(source.X + 18, source.Y - 5), true));
            geometry.Figures.Add(h1);
            
            var h2 = new PathFigure { StartPoint = new Point(source.X + 8, source.Y), IsClosed = false };
            h2.Segments.Add(new LineSegment(new Point(source.X + 16, source.Y), true));
            geometry.Figures.Add(h2);
            
            var h3 = new PathFigure { StartPoint = new Point(source.X + 10, source.Y + 5), IsClosed = false };
            h3.Segments.Add(new LineSegment(new Point(source.X + 14, source.Y + 5), true));
            geometry.Figures.Add(h3);
            
            return new ConnectionVisualModel
            {
                Source = source,
                Target = new Point(source.X + 20, source.Y),
                Stroke = stroke,
                StrokeThickness = thickness,
                Geometry = geometry
            };
        }

        /// <summary>
        /// Creates a right-angle polyline connection model
        /// </summary>
        private static ConnectionVisualModel CreateOrthogonalConnectionModel(List<Point> points, Brush stroke, double thickness)
        {
            if (points == null || points.Count < 2)
                return null;

            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = points[0], IsClosed = false };
            for (int i = 1; i < points.Count; i++)
            {
                figure.Segments.Add(new LineSegment(points[i], true));
            }
            geometry.Figures.Add(figure);

            return new ConnectionVisualModel
            {
                Source = points.First(),
                Target = points.Last(),
                Stroke = stroke,
                StrokeThickness = thickness,
                Geometry = geometry
            };
        }

        private static DataTemplate BuildConnectionTemplate()
        {
            var template = new DataTemplate(typeof(ConnectionVisualModel));
            
            // Use a Path element bound to the precomputed Geometry property
            var path = new FrameworkElementFactory(typeof(Path));
            path.SetBinding(Shape.StrokeProperty, new Binding(nameof(ConnectionVisualModel.Stroke)));
            path.SetBinding(Shape.StrokeThicknessProperty, new Binding(nameof(ConnectionVisualModel.StrokeThickness)));
            path.SetBinding(Path.DataProperty, new Binding(nameof(ConnectionVisualModel.Geometry)));
            path.SetValue(UIElement.IsHitTestVisibleProperty, false);
            path.SetValue(Shape.StrokeStartLineCapProperty, PenLineCap.Flat);
            path.SetValue(Shape.StrokeEndLineCapProperty, PenLineCap.Flat);
            path.SetValue(Shape.StrokeLineJoinProperty, PenLineJoin.Miter);
            
            template.VisualTree = path;
            return template;
        }
    }
}
