using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Xml.Linq;
using TIAOpennessStudio.LadRendering.Parser;
using TIAOpennessStudio.Services;

namespace TIAOpennessStudio.LadRendering.Rendering
{
    /// <summary>
    /// Renders an S7-GRAPH sequencer (IEC 61131-3 SFC style) to a read-only Canvas.
    /// Steps = large nodes (with actions, supervision, and interlock info),
    /// Transitions = horizontal transition bars (with condition details),
    /// Branches = bars for parallel/alternative branches + multi-path layout.
    /// Uses ScrollViewer + Canvas for panning/scrolling.
    /// </summary>
    public static class GraphRenderer
    {
        // ═══ Layout constants (enlarged to make full use of the canvas space) ═══
        private const double StepWidth = 420;
        private const double StepMinHeight = 120;
        private const double StepActionRowHeight = 22;
        private const double TransitionBarWidth = 380;
        private const double TransitionBarHeight = 120;   // enlarged to fit LAD condition graphics
        private const double VerticalGap = 80;       // vertical spacing between step→transition or transition→step
        private const double BranchHGap = 100;       // horizontal spacing between parallel/alternative branches
        private const double InitialX = 200;
        private const double InitialY = 60;

        // ═══ Color theme (mimicking the TIA Portal GRAPH editor) ═══
        private static readonly Color BgColor = Color.FromRgb(24, 26, 30);
        private static readonly Color StepBg = Color.FromRgb(38, 42, 48);
        private static readonly Color StepBorder = Color.FromRgb(60, 140, 200);
        private static readonly Color InitStepBorder = Color.FromRgb(80, 200, 120);
        private static readonly Color TransitionBg = Color.FromRgb(50, 45, 35);
        private static readonly Color TransitionBorder = Color.FromRgb(200, 160, 60);
        private static readonly Color WireColor = Color.FromRgb(100, 180, 240);
        private static readonly Color ActionColor = Color.FromRgb(180, 220, 255);
        private static readonly Color SupervisionColor = Color.FromRgb(255, 200, 80);
        private static readonly Color InterlockColor = Color.FromRgb(255, 120, 100);
        private static readonly Color BranchBarColor = Color.FromRgb(140, 200, 255);

        // ═══ Layout node info ═══
        private sealed class LayoutNode
        {
            public int Id { get; set; }           // Step/Transition/Branch Number
            public string Type { get; set; }      // "Step", "Transition", "Branch"
            public double X { get; set; }
            public double Y { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public int Column { get; set; }       // used for branch layout
            public int Layer { get; set; }        // topological layer
        }

        // Public entry point
        public static UIElement Render(string xmlContent)
        {
            try
            {
                return RenderInternal(xmlContent);
            }
            catch (Exception ex)
            {
                return new TextBlock
                {
                    Text = $"Error rendering GRAPH: {ex.Message}",
                    Foreground = Brushes.OrangeRed,
                    FontSize = 14,
                    Margin = new Thickness(30),
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
        }

        private static UIElement RenderInternal(string xmlContent)
        {
            var seq = GraphXmlParser.Parse(xmlContent);
            if (seq.Steps.Count == 0)
            {
                return new TextBlock
                {
                    Text = "No GRAPH sequence found.",
                    Foreground = Brushes.Gray,
                    FontSize = 16,
                    Margin = new Thickness(30),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            // ── Layout computation ──
            var layout = ComputeLayout(seq);

            // ── Render to Canvas (read-only view; avoids Nodify to prevent NullReferenceException from drag interactions) ──
            var canvas = new Canvas
            {
                Background = new SolidColorBrush(BgColor),
                ClipToBounds = true
            };

            var stepMap = seq.Steps.ToDictionary(s => s.Number);
            var trMap = seq.Transitions.ToDictionary(t => t.Number);

            // Step node coordinate maps
            var stepCenterX = new Dictionary<int, double>();
            var stepTopY = new Dictionary<int, double>();
            var stepBottomY = new Dictionary<int, double>();

            foreach (var ln in layout.Where(n => n.Type == "Step"))
            {
                if (!stepMap.TryGetValue(ln.Id, out var step)) continue;
                var content = CreateStepNode(step);
                Canvas.SetLeft(content, ln.X);
                Canvas.SetTop(content, ln.Y);
                canvas.Children.Add(content);
                stepCenterX[step.Number] = ln.X + ln.Width / 2;
                stepTopY[step.Number] = ln.Y;
                stepBottomY[step.Number] = ln.Y + ln.Height;
            }

            // Transition node coordinate maps
            var trCenterX = new Dictionary<int, double>();
            var trTopY = new Dictionary<int, double>();
            var trBottomY = new Dictionary<int, double>();

            foreach (var ln in layout.Where(n => n.Type == "Transition"))
            {
                if (!trMap.TryGetValue(ln.Id, out var tr)) continue;
                var content = CreateTransitionNode(tr);
                Canvas.SetLeft(content, ln.X);
                Canvas.SetTop(content, ln.Y);
                canvas.Children.Add(content);
                trCenterX[tr.Number] = ln.X + ln.Width / 2;
                trTopY[tr.Number] = ln.Y;
                trBottomY[tr.Number] = ln.Y + ln.Height;
            }

            // Branch bar nodes
            var branchCenterX = new Dictionary<int, double>();
            var branchY = new Dictionary<int, double>();

            foreach (var ln in layout.Where(n => n.Type == "Branch"))
            {
                var branch = seq.Branches.FirstOrDefault(b => b.Number == ln.Id);
                if (branch == null) continue;
                var bar = CreateBranchBar(branch, ln.Width);
                Canvas.SetLeft(bar, ln.X);
                Canvas.SetTop(bar, ln.Y);
                canvas.Children.Add(bar);
                branchCenterX[branch.Number] = ln.X + ln.Width / 2;
                branchY[branch.Number] = ln.Y + 6; // bar center
            }

            // ── Render connections (SFC flow: Step bottom→Transition top→next Step top) ──
            double canvasMaxX = InitialX, canvasMaxY = InitialY;
            var mainWireBrush = new SolidColorBrush(WireColor);
            var jumpBrush = new SolidColorBrush(Color.FromRgb(200, 200, 100));

            // Iterate each Connection, correctly matching Step→Trans and Trans→Step
            foreach (var conn in seq.Connections)
            {
                Point? src = null, dst = null;
                bool isJump = conn.LinkType == "Jump";

                // Step → Transition connection
                if (conn.FromType == "StepRef" && conn.ToType == "TransitionRef")
                {
                    if (stepBottomY.ContainsKey(conn.FromNumber) && trTopY.ContainsKey(conn.ToNumber))
                    {
                        src = new Point(stepCenterX[conn.FromNumber], stepBottomY[conn.FromNumber]);
                        dst = new Point(trCenterX[conn.ToNumber], trTopY[conn.ToNumber]);
                    }
                }
                // Transition → Step connection
                else if (conn.FromType == "TransitionRef" && conn.ToType == "StepRef")
                {
                    if (isJump)
                    {
                        // Jump connection: drawn as source Step bottom → target Step top (yellow dashed line for a step-to-step jump)
                        var parentStepConn = seq.Connections.FirstOrDefault(c =>
                            c.FromType == "StepRef" && c.ToType == "TransitionRef" && c.ToNumber == conn.FromNumber);
                        int fromStepNum = parentStepConn != null ? parentStepConn.FromNumber : -1;

                        if (fromStepNum >= 0 && stepBottomY.ContainsKey(fromStepNum) && stepTopY.ContainsKey(conn.ToNumber))
                        {
                            src = new Point(stepCenterX[fromStepNum], stepBottomY[fromStepNum]);
                            dst = new Point(stepCenterX[conn.ToNumber], stepTopY[conn.ToNumber]);
                        }
                    }
                    else
                    {
                        // Normal flow: Trans bottom → next Step top
                        if (trBottomY.ContainsKey(conn.FromNumber) && stepTopY.ContainsKey(conn.ToNumber))
                        {
                            src = new Point(trCenterX[conn.FromNumber], trBottomY[conn.FromNumber]);
                            dst = new Point(stepCenterX[conn.ToNumber], stepTopY[conn.ToNumber]);
                        }
                    }
                }
                // Branch-related connections
                else if (conn.FromType == "StepRef" && conn.ToType == "BranchRef")
                {
                    if (stepBottomY.ContainsKey(conn.FromNumber) && branchY.ContainsKey(conn.ToNumber))
                    {
                        src = new Point(stepCenterX[conn.FromNumber], stepBottomY[conn.FromNumber]);
                        dst = new Point(branchCenterX[conn.ToNumber], branchY[conn.ToNumber]);
                    }
                }
                else if (conn.FromType == "BranchRef" && conn.ToType == "TransitionRef")
                {
                    if (branchY.ContainsKey(conn.FromNumber) && trTopY.ContainsKey(conn.ToNumber))
                    {
                        src = new Point(branchCenterX[conn.FromNumber], branchY[conn.FromNumber]);
                        dst = new Point(trCenterX[conn.ToNumber], trTopY[conn.ToNumber]);
                    }
                }
                else if (conn.FromType == "TransitionRef" && conn.ToType == "BranchRef")
                {
                    if (trBottomY.ContainsKey(conn.FromNumber) && branchY.ContainsKey(conn.ToNumber))
                    {
                        src = new Point(trCenterX[conn.FromNumber], trBottomY[conn.FromNumber]);
                        dst = new Point(branchCenterX[conn.ToNumber], branchY[conn.ToNumber]);
                    }
                }
                else if (conn.FromType == "BranchRef" && conn.ToType == "StepRef")
                {
                    if (branchY.ContainsKey(conn.FromNumber) && stepTopY.ContainsKey(conn.ToNumber))
                    {
                        src = new Point(branchCenterX[conn.FromNumber], branchY[conn.FromNumber]);
                        dst = new Point(stepCenterX[conn.ToNumber], stepTopY[conn.ToNumber]);
                    }
                }

                if (src.HasValue && dst.HasValue)
                {
                    var brush = isJump ? jumpBrush : mainWireBrush;
                    double thickness = isJump ? 2.0 : 2.5;
                    DrawConnection(canvas, src.Value, dst.Value, brush, thickness, isJump);
                    canvasMaxX = Math.Max(canvasMaxX, Math.Max(src.Value.X, dst.Value.X));
                    canvasMaxY = Math.Max(canvasMaxY, Math.Max(src.Value.Y, dst.Value.Y));

                    if (isJump)
                    {
                        string targetName = stepMap.TryGetValue(conn.ToNumber, out var ts) && !string.IsNullOrWhiteSpace(ts.Name)
                            ? ts.Name : $"S{conn.ToNumber}";
                        string sourceName = trMap.TryGetValue(conn.FromNumber, out var srcTr) && !string.IsNullOrWhiteSpace(srcTr.Name)
                            ? srcTr.Name : $"T{conn.FromNumber}";
                        AddJumpMarkers(canvas, src.Value, dst.Value, sourceName, targetName);
                    }
                }
            }

            // Compute the actual canvas size
            foreach (var ln in layout)
            {
                canvasMaxX = Math.Max(canvasMaxX, ln.X + ln.Width + 50);
                canvasMaxY = Math.Max(canvasMaxY, ln.Y + ln.Height + 50);
            }
            canvas.Width = canvasMaxX;
            canvas.Height = canvasMaxY;

            // ── Title info panel ──
            var titlePanel = CreateTitlePanel(seq);

            // ── Assemble the final UI: title + zoomable, scrollable Canvas ──
            var scaleTransform = new ScaleTransform(1.0, 1.0);
            canvas.RenderTransform = scaleTransform;
            canvas.RenderTransformOrigin = new Point(0, 0);

            var scrollViewer = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(BgColor),
                Content = canvas
            };

            // Ctrl+wheel zoom (low sensitivity 0.05); plain wheel scrolls vertically
            scrollViewer.PreviewMouseWheel += (s, e) =>
            {
                if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
                {
                    double delta = e.Delta > 0 ? 0.05 : -0.05;
                    double newScale = Math.Max(0.3, Math.Min(3.0, scaleTransform.ScaleX + delta));
                    scaleTransform.ScaleX = newScale;
                    scaleTransform.ScaleY = newScale;
                    e.Handled = true;
                }
            };

            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid.SetRow(titlePanel, 0);
            rootGrid.Children.Add(titlePanel);
            Grid.SetRow(scrollViewer, 1);
            rootGrid.Children.Add(scrollViewer);

            return rootGrid;
        }

        // Canvas connection drawing (orthogonal polyline + arrowhead)
        private static void DrawConnection(Canvas canvas, Point source, Point target, Brush stroke, double thickness, bool isDashed)
        {
            var path = new Path
            {
                Stroke = stroke,
                StrokeThickness = thickness,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };

            if (isDashed)
            {
                path.StrokeDashArray = new DoubleCollection { 4, 3 };
            }

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(source, false, false);

                // Orthogonal polyline: vertical → horizontal → vertical
                if (Math.Abs(source.X - target.X) < 1)
                {
                    ctx.LineTo(target, true, false);
                }
                else
                {
                    double midY = (source.Y + target.Y) / 2;
                    ctx.LineTo(new Point(source.X, midY), true, false);
                    ctx.LineTo(new Point(target.X, midY), true, false);
                    ctx.LineTo(target, true, false);
                }

                // Arrowhead
                if (!isDashed)
                {
                    double arrowSize = 6;
                    var arrowLeft = new Point(target.X - arrowSize, target.Y - arrowSize * 1.5);
                    var arrowRight = new Point(target.X + arrowSize, target.Y - arrowSize * 1.5);
                    ctx.BeginFigure(target, true, true);
                    ctx.LineTo(arrowLeft, true, false);
                    ctx.LineTo(arrowRight, true, false);
                }
            }
            geometry.Freeze();
            path.Data = geometry;
            canvas.Children.Insert(0, path); // wire sits below the nodes
        }

        // Jump markers: extend the yellow jump line out past the node into free space, then draw an arrowhead
        // plus a short leader line to a label. This keeps the annotation clear of the original graphics.
        private static void AddJumpMarkers(Canvas canvas, Point src, Point dst, string sourceName, string targetName)
        {
            var jumpBrush = new SolidColorBrush(Color.FromRgb(230, 210, 90));
            const double ext = 34;

            // Source = branch end (the last step). Extend the yellow line downward, down arrow + label.
            var srcTip = new Point(src.X, src.Y + ext);
            DrawJumpStub(canvas, src, srcTip, jumpBrush);
            DrawJumpArrowHead(canvas, srcTip, pointingDown: true, jumpBrush);
            DrawJumpLabel(canvas, srcTip, $"Jump to {targetName}", jumpBrush);

            // Target = the step being jumped into. Extend the yellow line upward, up arrow + label.
            var dstTip = new Point(dst.X, dst.Y - ext);
            DrawJumpStub(canvas, dst, dstTip, jumpBrush);
            DrawJumpArrowHead(canvas, dstTip, pointingDown: false, jumpBrush);
            DrawJumpLabel(canvas, dstTip, $"Jump from {sourceName}", jumpBrush);
        }

        private static void DrawJumpStub(Canvas canvas, Point from, Point to, Brush brush)
        {
            canvas.Children.Add(new Line
            {
                X1 = from.X, Y1 = from.Y, X2 = to.X, Y2 = to.Y,
                Stroke = brush,
                StrokeThickness = 2.0,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            });
        }

        private static void DrawJumpArrowHead(Canvas canvas, Point tip, bool pointingDown, Brush brush)
        {
            double baseY = pointingDown ? tip.Y - 12 : tip.Y + 12;
            canvas.Children.Add(new Polygon
            {
                Points = new PointCollection
                {
                    new Point(tip.X - 6, baseY),
                    new Point(tip.X + 6, baseY),
                    new Point(tip.X, tip.Y)
                },
                Fill = brush,
                IsHitTestVisible = false
            });
        }

        private static void DrawJumpLabel(Canvas canvas, Point tip, string text, Brush brush)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = brush,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush(BgColor),
                Padding = new Thickness(2, 0, 2, 0),
                IsHitTestVisible = false
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            double labelX = tip.X + 10;
            double labelY = tip.Y - label.DesiredSize.Height / 2;
            Canvas.SetLeft(label, labelX);
            Canvas.SetTop(label, labelY);

            // Short leader line from the arrow tip to the label.
            canvas.Children.Add(new Line
            {
                X1 = tip.X + 4, Y1 = tip.Y,
                X2 = labelX - 3, Y2 = tip.Y,
                Stroke = brush,
                StrokeThickness = 1.0,
                IsHitTestVisible = false
            });

            canvas.Children.Add(label);
        }

        // Step node (IEC 61131-3 SFC Step style — with action table)
        private static FrameworkElement CreateStepNode(GraphStep step)
        {
            bool isInit = step.Init;

            var outerBorder = new Border
            {
                Width = StepWidth,
                Background = new SolidColorBrush(StepBg),
                BorderBrush = new SolidColorBrush(isInit ? InitStepBorder : StepBorder),
                BorderThickness = isInit ? new Thickness(4) : new Thickness(2),
                CornerRadius = new CornerRadius(4),
                SnapsToDevicePixels = true,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 8,
                    ShadowDepth = 2,
                    Opacity = 0.4
                }
            };

            // Initial step double frame
            Border contentHost;
            if (isInit)
            {
                contentHost = new Border
                {
                    BorderBrush = new SolidColorBrush(InitStepBorder),
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(3)
                };
                outerBorder.Child = contentHost;
            }
            else
            {
                contentHost = outerBorder;
            }

            var mainStack = new StackPanel();

            // ── Header bar: S{Number} - {Name}: {MaxStepTime} ──
            var headerPanel = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 55, 70)),
                Padding = new Thickness(8, 5, 8, 5)
            };
            var headerStack = new StackPanel { Orientation = Orientation.Horizontal };

            headerStack.Children.Add(new TextBlock
            {
                Text = $"S{step.Number}",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(isInit ? InitStepBorder : Color.FromRgb(140, 200, 255)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            headerStack.Children.Add(new TextBlock
            {
                Text = "- " + (step.Name.Length > 0 ? step.Name : $"Step{step.Number}") + ":",
                FontSize = 12,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            if (step.MaxStepTime.Length > 0)
            {
                headerStack.Children.Add(new TextBlock
                {
                    Text = step.MaxStepTime,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 140)),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            headerPanel.Child = headerStack;
            mainStack.Children.Add(headerPanel);

            // ── Table header: interlock | event | qualifier | action | symbol | address ──
            var tableHeaderBrush = new SolidColorBrush(Color.FromRgb(55, 60, 72));
            var headerRow = CreateTableRow("互锁", "事件", "限定符", "动作", "", "",
                tableHeaderBrush, Brushes.White, FontWeights.SemiBold);
            mainStack.Children.Add(headerRow);

            mainStack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(70, 80, 95)) });

            // ── Action rows ──
            bool hasActions = step.Actions.Count > 0;
            if (hasActions)
            {
                for (int i = 0; i < step.Actions.Count; i++)
                {
                    var act = step.Actions[i];
                    var rowBg = i % 2 == 0
                        ? new SolidColorBrush(Color.FromRgb(35, 40, 48))
                        : new SolidColorBrush(Color.FromRgb(40, 44, 52));

                    string interlockMark = act.HasInterlock ? "🔒" : "";
                    string displayAction = act.Operand;
                    if (act.Parameter.Length > 0)
                        displayAction += "," + act.Parameter;

                    var row = CreateTableRow(interlockMark, act.Event, act.Qualifier, displayAction,
                        act.Operand, act.Address, rowBg, null, FontWeights.Normal);
                    mainStack.Children.Add(row);
                }
            }
            else if (step.ActionsTitle.Length > 0)
            {
                // Fallback: show only the action title
                var rowBg = new SolidColorBrush(Color.FromRgb(35, 40, 48));
                var row = CreateTableRow("", "", "N", step.ActionsTitle, "", "", rowBg, null, FontWeights.Normal);
                mainStack.Children.Add(row);
            }

            // ── Supervision conditions ──
            if (step.Supervisions.Count > 0)
            {
                mainStack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(70, 80, 95)) });
                var svHeader = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(30, 255, 200, 80)),
                    Padding = new Thickness(8, 3, 8, 3)
                };
                svHeader.Child = new TextBlock
                {
                    Text = $"🔍 Supervision × {step.Supervisions.Count}",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(SupervisionColor)
                };
                mainStack.Children.Add(svHeader);

                // Render the LAD condition graphic
                foreach (var sv in step.Supervisions)
                {
                    var ladView = TryRenderFlgNet(sv.FlgNetXml);
                    if (ladView != null)
                    {
                        var viewbox = new Viewbox
                        {
                            Stretch = Stretch.Uniform,
                            StretchDirection = StretchDirection.DownOnly,
                            MaxHeight = 80,
                            HorizontalAlignment = HorizontalAlignment.Left,
                            Margin = new Thickness(4, 2, 4, 2),
                            Child = ladView
                        };
                        mainStack.Children.Add(viewbox);
                    }
                }
            }

            // ── Interlock conditions ──
            if (step.Interlocks.Count > 0)
            {
                mainStack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(70, 80, 95)) });
                var ilHeader = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(30, 255, 120, 100)),
                    Padding = new Thickness(8, 3, 8, 3)
                };
                ilHeader.Child = new TextBlock
                {
                    Text = $"🔒 Interlock × {step.Interlocks.Count}",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(InterlockColor)
                };
                mainStack.Children.Add(ilHeader);

                // Render the LAD condition graphic
                foreach (var il in step.Interlocks)
                {
                    var ladView = TryRenderFlgNet(il.FlgNetXml);
                    if (ladView != null)
                    {
                        var viewbox = new Viewbox
                        {
                            Stretch = Stretch.Uniform,
                            StretchDirection = StretchDirection.DownOnly,
                            MaxHeight = 80,
                            HorizontalAlignment = HorizontalAlignment.Left,
                            Margin = new Thickness(4, 2, 4, 2),
                            Child = ladView
                        };
                        mainStack.Children.Add(viewbox);
                    }
                }
            }

            // Placeholder when there is no content
            if (!hasActions && step.ActionsTitle.Length == 0 && step.Supervisions.Count == 0 && step.Interlocks.Count == 0)
            {
                mainStack.Children.Add(new TextBlock
                {
                    Text = "( no actions )",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 110)),
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(8, 4, 8, 4)
                });
            }

            if (isInit)
                contentHost.Child = mainStack;
            else
                outerBorder.Child = mainStack;

            return outerBorder;
        }

        /// <summary>
        /// Creates a table row: interlock | event | qualifier | action | symbol | address
        /// </summary>
        private static FrameworkElement CreateTableRow(string interlock, string eventCol, string qualifier,
            string action, string symbol, string address, Brush background, Brush foreground, FontWeight weight)
        {
            var grid = new Grid
            {
                Background = background,
                MinHeight = 20
            };
            // Column definitions: interlock(30) | event(40) | qualifier(45) | action(stretch) | symbol(70) | address(65)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65) });

            var fg = foreground ?? new SolidColorBrush(Color.FromRgb(210, 215, 225));

            AddCell(grid, 0, interlock, fg, weight, HorizontalAlignment.Center);
            AddCell(grid, 1, eventCol, fg, weight, HorizontalAlignment.Center);
            AddCell(grid, 2, qualifier, new SolidColorBrush(Color.FromRgb(240, 200, 100)), weight, HorizontalAlignment.Center);
            AddCell(grid, 3, action, fg, weight, HorizontalAlignment.Left);
            AddCell(grid, 4, symbol, new SolidColorBrush(Color.FromRgb(150, 200, 255)), weight, HorizontalAlignment.Left);
            AddCell(grid, 5, address, new SolidColorBrush(Color.FromRgb(180, 180, 140)), weight, HorizontalAlignment.Right);

            return grid;
        }

        private static void AddCell(Grid grid, int col, string text, Brush fg, FontWeight weight, HorizontalAlignment hAlign)
        {
            var tb = new TextBlock
            {
                Text = text ?? "",
                FontSize = 10.5,
                FontWeight = weight,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = hAlign,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(3, 2, 3, 2)
            };
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }

        // Transition node (IEC 61131-3 SFC Transition style — with condition LAD graphics)
        private static FrameworkElement CreateTransitionNode(GraphTransition tr)
        {
            var outerStack = new StackPanel();

            // ── Header bar: T{Number} + Name ──
            var headerBorder = new Border
            {
                Width = TransitionBarWidth,
                Background = new SolidColorBrush(TransitionBg),
                BorderBrush = new SolidColorBrush(TransitionBorder),
                BorderThickness = new Thickness(1.5, 1.5, 1.5, 0),
                CornerRadius = new CornerRadius(3, 3, 0, 0),
                Padding = new Thickness(8, 4, 8, 4),
                SnapsToDevicePixels = true
            };

            var headerStack = new StackPanel { Orientation = Orientation.Horizontal };

            // Transition bar symbol
            headerStack.Children.Add(new Border
            {
                Width = 20, Height = 4,
                Background = new SolidColorBrush(TransitionBorder),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });

            headerStack.Children.Add(new TextBlock
            {
                Text = $"T{tr.Number}",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(240, 200, 80)),
                VerticalAlignment = VerticalAlignment.Center
            });

            if (tr.Name.Length > 0 && tr.Name != $"T{tr.Number}")
            {
                headerStack.Children.Add(new TextBlock
                {
                    Text = $"  {tr.Name}",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            if (tr.IsMissing)
            {
                headerStack.Children.Add(new TextBlock
                {
                    Text = "  ⚠ MISSING",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(InterlockColor),
                    FontStyle = FontStyles.Italic,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            headerBorder.Child = headerStack;
            outerStack.Children.Add(headerBorder);

            // ── Condition LAD rendering area ──
            var conditionView = TryRenderFlgNet(tr.FlgNetXml);
            if (conditionView != null)
            {
                var condBorder = new Border
                {
                    Width = TransitionBarWidth,
                    Background = new SolidColorBrush(Color.FromRgb(30, 32, 38)),
                    BorderBrush = new SolidColorBrush(TransitionBorder),
                    BorderThickness = new Thickness(1.5, 0, 1.5, 1.5),
                    CornerRadius = new CornerRadius(0, 0, 3, 3),
                    Padding = new Thickness(4, 2, 4, 2),
                    MaxHeight = 120,
                    ClipToBounds = true
                };

                // Use a Viewbox to scale the LAD graphic to fit the limited space
                var viewbox = new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    MaxHeight = 110,
                    Child = conditionView
                };
                condBorder.Child = viewbox;
                outerStack.Children.Add(condBorder);
            }
            else
            {
                // Fallback: show the condition text
                var condText = ExtractCondition(tr.FlgNetXml);
                var textBorder = new Border
                {
                    Width = TransitionBarWidth,
                    Background = new SolidColorBrush(Color.FromRgb(30, 32, 38)),
                    BorderBrush = new SolidColorBrush(TransitionBorder),
                    BorderThickness = new Thickness(1.5, 0, 1.5, 1.5),
                    CornerRadius = new CornerRadius(0, 0, 3, 3),
                    Padding = new Thickness(8, 4, 8, 4)
                };
                textBorder.Child = new TextBlock
                {
                    Text = condText.Length > 0 ? condText : "TRUE",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxHeight = 40
                };
                outerStack.Children.Add(textBorder);
            }

            return outerStack;
        }

        /// <summary>
        /// Tries to render FlgNet XML as a LAD graphic. Returns a FrameworkElement on success, null on failure.
        /// </summary>
        private static FrameworkElement TryRenderFlgNet(string flgNetXml)
        {
            if (string.IsNullOrWhiteSpace(flgNetXml)) return null;

            try
            {
                var flgNetElement = XElement.Parse(flgNetXml);
                var model = TiaXmlParseHelper.ParseFlgNet(flgNetElement);

                if (model == null || model.Parts.Count == 0)
                    return null;

                // Filter out GRAPH-internal virtual coils (TrCoil/SvCoil/IlCoil) — not real PLC code
                var virtualCoils = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "TrCoil", "SvCoil", "IlCoil" };

                var virtualUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in model.Parts)
                {
                    if (virtualCoils.Contains(part.Name ?? ""))
                        virtualUids.Add(part.Uid);
                }

                if (virtualUids.Count > 0)
                {
                    model.Parts.RemoveAll(p => virtualUids.Contains(p.Uid));
                    // Remove wires connected to virtual coils (NameConnections referencing the virtual coil Uid)
                    model.Wires.RemoveAll(w =>
                        w.NameConnections != null && w.NameConnections.Any(c => virtualUids.Contains(c.Uid ?? "")));
                }

                // Do not render if no real Part remains after filtering
                if (model.Parts.Count == 0)
                    return null;

                return LadNetworkRenderer.RenderNetwork(model);
            }
            catch
            {
                return null;
            }
        }

        // Branch bar (parallel/alternative)
        private static FrameworkElement CreateBranchBar(GraphBranch branch, double width)
        {
            bool isParallel = branch.Type.Contains("Par");
            var barBrush = new SolidColorBrush(BranchBarColor);

            var canvas = new Canvas
            {
                Width = width,
                Height = 12,
                SnapsToDevicePixels = true
            };

            // Parallel branch = double line, alternative branch = single line
            if (isParallel)
            {
                canvas.Children.Add(new Line
                {
                    X1 = 0, Y1 = 4, X2 = width, Y2 = 4,
                    Stroke = barBrush, StrokeThickness = 3
                });
                canvas.Children.Add(new Line
                {
                    X1 = 0, Y1 = 9, X2 = width, Y2 = 9,
                    Stroke = barBrush, StrokeThickness = 3
                });
            }
            else
            {
                canvas.Children.Add(new Line
                {
                    X1 = 0, Y1 = 6, X2 = width, Y2 = 6,
                    Stroke = barBrush, StrokeThickness = 3
                });
            }

            return canvas;
        }

        // Title info panel
        private static FrameworkElement CreateTitlePanel(GraphSequence seq)
        {
            var panel = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 36, 42)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(50, 60, 75)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(16, 8, 16, 8)
            };

            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            stack.Children.Add(new TextBlock
            {
                Text = "S7-GRAPH",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 180, 240)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0)
            });

            if (seq.Title.Length > 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = seq.Title,
                    FontSize = 12,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 16, 0)
                });
            }

            stack.Children.Add(new TextBlock
            {
                Text = $"Steps: {seq.Steps.Count}  Transitions: {seq.Transitions.Count}  Branches: {seq.Branches.Count}",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(140, 150, 160)),
                VerticalAlignment = VerticalAlignment.Center
            });

            panel.Child = stack;
            return panel;
        }

        // Layout engine (SFC three-column layout: left=conditions | middle=step flow | right=actions)
        private static List<LayoutNode> ComputeLayout(GraphSequence seq)
        {
            var result = new List<LayoutNode>();
            var stepMap = seq.Steps.ToDictionary(s => s.Number);
            var trMap = seq.Transitions.ToDictionary(t => t.Number);

            var orderedSteps = TopologicalSort(seq);

            // Three-column layout constants
            const double transitionColumnX = 30;                    // left column: transition condition X start
            double stepColumnX = transitionColumnX + TransitionBarWidth + 60;  // middle column: step flow
            double actionColumnX = stepColumnX + StepWidth + 60;    // right column: step actions (right of the step node)

            double y = InitialY;

            // Identify the steps in a branch
            var branchColumns = ComputeBranchColumns(seq, orderedSteps);

            for (int i = 0; i < orderedSteps.Count; i++)
            {
                var step = orderedSteps[i];
                double stepHeight = CalculateStepHeight(step);
                double xOffset = branchColumns.ContainsKey(step.Number)
                    ? branchColumns[step.Number] * (StepWidth + BranchHGap)
                    : 0;

                // Middle column: step node
                result.Add(new LayoutNode
                {
                    Id = step.Number,
                    Type = "Step",
                    X = stepColumnX + xOffset,
                    Y = y,
                    Width = StepWidth,
                    Height = stepHeight,
                    Layer = i
                });

                // Find the transition leaving the current step (= precondition to enter the next step)
                var outTransNums = seq.Connections
                    .Where(c => c.FromType == "StepRef" && c.FromNumber == step.Number
                             && c.ToType == "TransitionRef")
                    .Select(c => c.ToNumber)
                    .Distinct()
                    .ToList();

                // Place the transition between the step bottom and the next step top, but in the left column
                double transY = y + stepHeight + VerticalGap * 0.3;

                foreach (var trNum in outTransNums)
                {
                    if (!trMap.ContainsKey(trNum)) continue;
                    // Left column: transition condition
                    result.Add(new LayoutNode
                    {
                        Id = trNum,
                        Type = "Transition",
                        X = transitionColumnX + xOffset,
                        Y = transY,
                        Width = TransitionBarWidth,
                        Height = TransitionBarHeight,
                        Layer = i
                    });
                }

                y += stepHeight + VerticalGap;
            }

            // Lay out the branch bars
            foreach (var branch in seq.Branches)
            {
                var toConns = seq.Connections
                    .Where(c => c.ToType == "BranchRef" && c.ToNumber == branch.Number)
                    .ToList();

                double barY = y;
                double barX = stepColumnX;
                double barWidth = StepWidth;

                foreach (var tc in toConns)
                {
                    var srcLayout = result.FirstOrDefault(n =>
                        (n.Type == "Step" && tc.FromType == "StepRef" && n.Id == tc.FromNumber) ||
                        (n.Type == "Transition" && tc.FromType == "TransitionRef" && n.Id == tc.FromNumber));
                    if (srcLayout != null)
                    {
                        barY = srcLayout.Y + srcLayout.Height + VerticalGap / 4;
                        barX = srcLayout.X;
                        break;
                    }
                }

                if (branch.Cardinality > 1)
                {
                    barWidth = branch.Cardinality * (StepWidth + BranchHGap) - BranchHGap;
                }

                result.Add(new LayoutNode
                {
                    Id = branch.Number,
                    Type = "Branch",
                    X = barX,
                    Y = barY,
                    Width = barWidth,
                    Height = 12,
                    Layer = -1
                });
            }

            return result;
        }

        // ═══ Branch column assignment ═══
        private static Dictionary<int, int> ComputeBranchColumns(GraphSequence seq, List<GraphStep> orderedSteps)
        {
            var columns = new Dictionary<int, int>();

            // Find the steps in a parallel branch
            foreach (var branch in seq.Branches.Where(b => b.Type == "ParBegin" || b.Type == "AltBegin"))
            {
                // Connections leaving the branch
                var outConns = seq.Connections
                    .Where(c => c.FromType == "BranchRef" && c.FromNumber == branch.Number)
                    .ToList();

                int col = 0;
                foreach (var conn in outConns)
                {
                    int targetNum = -1;
                    if (conn.ToType == "StepRef")
                        targetNum = conn.ToNumber;
                    else if (conn.ToType == "TransitionRef")
                    {
                        // Find the step after this transition
                        var nextStep = seq.Connections
                            .FirstOrDefault(c => c.FromType == "TransitionRef"
                                && c.FromNumber == conn.ToNumber
                                && c.ToType == "StepRef");
                        if (nextStep != null)
                            targetNum = nextStep.ToNumber;
                    }

                    if (targetNum >= 0 && !columns.ContainsKey(targetNum))
                    {
                        columns[targetNum] = col;
                        col++;
                    }
                }
            }

            return columns;
        }

        // ═══ Step height calculation ═══
        private static double CalculateStepHeight(GraphStep step)
        {
            // Header(30) + table header(22) + action rows + supervision + interlock + padding
            double h = 30 + 22; // header + table header
            int actionRows = Math.Max(1, step.Actions.Count);
            if (step.Actions.Count == 0 && step.ActionsTitle.Length > 0) actionRows = 1;
            h += actionRows * StepActionRowHeight;
            if (step.Supervisions.Count > 0) h += 26 + 85; // label + LAD graphics space
            if (step.Interlocks.Count > 0) h += 26 + 85;   // label + LAD graphics space
            return Math.Max(StepMinHeight, h);
        }

        // ═══ Condition extraction ═══
        private static string ExtractCondition(string flgNetXml)
        {
            if (string.IsNullOrWhiteSpace(flgNetXml)) return "";

            // Extract Symbol and Component Name
            var symbols = new List<string>();
            int idx = 0;

            // Try Component Name first (more precise)
            while ((idx = flgNetXml.IndexOf("<Component Name=\"", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                idx += 17;
                int end = flgNetXml.IndexOf('"', idx);
                if (end > idx)
                {
                    var name = flgNetXml.Substring(idx, end - idx).Trim();
                    if (name.Length > 0 && !symbols.Contains(name))
                        symbols.Add(name);
                }
                idx = end + 1;
            }

            if (symbols.Count == 0)
            {
                // Fall back to the Symbol attribute
                idx = 0;
                while ((idx = flgNetXml.IndexOf("Symbol=\"", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    idx += 8;
                    int end = flgNetXml.IndexOf('"', idx);
                    if (end > idx)
                    {
                        var sym = flgNetXml.Substring(idx, end - idx).Trim();
                        if (sym.Length > 0 && !symbols.Contains(sym))
                            symbols.Add(sym);
                    }
                    idx = end + 1;
                }
            }

            // Build a concise condition expression
            if (symbols.Count == 0) return "";
            if (symbols.Count <= 3)
                return string.Join(" & ", symbols);
            return string.Join(" & ", symbols.Take(3)) + " ...";
        }

        // ═══ Topological sort: BFS from the initial step ═══
        private static List<GraphStep> TopologicalSort(GraphSequence seq)
        {
            var result = new List<GraphStep>();
            var stepMap = seq.Steps.ToDictionary(s => s.Number);

            var initStep = seq.Steps.FirstOrDefault(s => s.Init)
                        ?? (seq.Steps.Count > 0 ? seq.Steps[0] : null);
            if (initStep == null) return result;

            var visited = new HashSet<int>();
            var queue = new Queue<GraphStep>();
            queue.Enqueue(initStep);

            while (queue.Count > 0)
            {
                var step = queue.Dequeue();
                if (!visited.Add(step.Number)) continue;
                result.Add(step);

                var nextTransNums = seq.Connections
                    .Where(c => c.FromType == "StepRef" && c.FromNumber == step.Number
                             && c.ToType == "TransitionRef")
                    .Select(c => c.ToNumber);

                foreach (var trNum in nextTransNums)
                {
                    var nextStepNums = seq.Connections
                        .Where(c => c.FromType == "TransitionRef" && c.FromNumber == trNum
                                 && c.ToType == "StepRef")
                        .Select(c => c.ToNumber);

                    foreach (var nextNum in nextStepNums)
                        if (stepMap.TryGetValue(nextNum, out var ns) && !visited.Contains(nextNum))
                            queue.Enqueue(ns);
                }
            }

            // Add steps that were not visited
            foreach (var step in seq.Steps)
                if (!visited.Contains(step.Number))
                    result.Add(step);

            return result;
        }
    }
}
