using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using Microsoft.VisualBasic.FileIO;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Controls.Primitives;

namespace RadarGraphs
{
    public partial class MainWindow : Window
    {
        private readonly List<Series> _series = new();
        private readonly List<Brush> _palette = new()
        {
            Brushes.DeepSkyBlue, Brushes.OrangeRed, Brushes.LimeGreen, Brushes.MediumVioletRed,
            Brushes.Goldenrod, Brushes.MediumTurquoise, Brushes.MediumOrchid, Brushes.SandyBrown,
            Brushes.DodgerBlue, Brushes.IndianRed
        };

        // Plot area margins for axes/labels
        private const double LeftMargin = 80;
        private const double RightMargin = 20;
        private const double TopMargin = 20;
        private const double BottomMargin = 60;

        // Data bounds
        private double _minX = 0, _maxX = 1, _minY = 0, _maxY = 1;
        private bool _hasData = false;

        // Axis titles (auto-detected)
        private string _xAxisTitle = "X";
        private string _yAxisTitle = "Y";

        // Hover UI
        private Popup? _hoverPopup;
        private Border? _hoverBorder;
        private TextBlock? _hoverText;
        private Line? _hoverLine;
        private Ellipse? _hoverDot;

        // Cache for sorted points by X (built once per series if needed)
        private readonly Dictionary<Series, List<Point>> _sortedCache = new();

        // Cache for peak indices per series (local maxima)
        private readonly Dictionary<Series, List<int>> _peakIndexCache = new();

        // Snap settings
        private const double SnapPixelRadius = 10.0; // how close (in px) the mouse must be to snap to a peak

        public MainWindow()
        {
            InitializeComponent();
            SizeChanged += (_, __) => Redraw();
        }
        private void Window_Loaded(object? sender, RoutedEventArgs e)
        {
            // Hook to the root visual to capture mouse moves anywhere over the plot
            if (PlotRoot != null)
            {
                PlotRoot.MouseMove += PlotRoot_MouseMove;
                PlotRoot.MouseLeave += PlotRoot_MouseLeave;
            }
            Redraw();
        }

        private void OpenCsv_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Title = "Open CSV",
                Filter = "CSV Files (*.csv;*.txt)|*.csv;*.txt|All Files (*.*)|*.*",
                Multiselect = true
            };
            if (ofd.ShowDialog(this) == true)
            {
                int before = _series.Count;
                foreach (var path in ofd.FileNames)
                {
                    try
                    {
                        var added = LoadCsv(path);
                        InfoText.Text = $"Loaded {added} series from {System.IO.Path.GetFileName(path)}";
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, $"Failed to load '{path}': {ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }

                _hasData = _series.Count > 0;
                FitToData();
                Redraw();
                int delta = _series.Count - before;
                StatusText.Text = _hasData ? "" : "Open CSV to plot";
                InfoText.Text = _hasData
                    ? $"Total series: {_series.Count} (added {delta})"
                    : "Ready";
            }
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _series.Clear();
            _sortedCache.Clear();
            _peakIndexCache.Clear();

            _hasData = false;
            _minX = 0; _maxX = 1; _minY = 0; _maxY = 1;
            _xAxisTitle = "X";
            _yAxisTitle = "Y";
            StatusText.Text = "Open CSV to plot";
            InfoText.Text = "Cleared";
            Redraw();
        }

        private void FitToData_Click(object sender, RoutedEventArgs e)
        {
            FitToData();
            Redraw();
        }

        private int LoadCsv(string path)
        {
            using var parser = new TextFieldParser(path);
            parser.TextFieldType = FieldType.Delimited;
            parser.SetDelimiters(",", ";", "\t");
            parser.HasFieldsEnclosedInQuotes = true;

            var rows = new List<string[]>();
            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields() ?? Array.Empty<string>();
                if (fields.Length == 0) continue;
                if (fields.All(f => string.IsNullOrWhiteSpace(f))) continue;
                rows.Add(fields);
            }

            if (rows.Count == 0)
                throw new InvalidOperationException("No rows detected.");

            var fileName = System.IO.Path.GetFileNameWithoutExtension(path);

            if (TryBuildSeriesFromHeaders(rows, fileName, out var created, out var xTitle, out var yTitle))
            {
                MergeAxisTitles(xTitle, yTitle);
                _series.AddRange(created);
                return created.Count;
            }

            var generic = BuildSeriesFromRows(rows, fileName);
            _series.AddRange(generic);

            // Fallback axis titles
            if (_xAxisTitle == "X") _xAxisTitle = "Sample";
            if (_yAxisTitle == "Y") _yAxisTitle = "Value";
            return generic.Count;
        }

        private void MergeAxisTitles(string xTitle, string yTitle)
        {
            // If first time, adopt; if later and mismatch, keep a generic title
            if (_xAxisTitle == "X") _xAxisTitle = xTitle;
            else if (!xTitle.Equals(_xAxisTitle, StringComparison.OrdinalIgnoreCase)) _xAxisTitle = "Time [s]";

            if (_yAxisTitle == "Y") _yAxisTitle = yTitle;
            else if (!yTitle.Equals(_yAxisTitle, StringComparison.OrdinalIgnoreCase)) _yAxisTitle = "Value";
        }

        // Header-driven mapping
        private bool TryBuildSeriesFromHeaders(List<string[]> rows, string baseName,
            out List<Series> series, out string xTitle, out string yTitle)
        {
            series = new();
            xTitle = "Time [s]";
            yTitle = "Value";

            // Detect header row (first row non-numeric)
            int dataStart = 0;
            bool firstRowHasTwoNumbers = TryParseDouble(rows[0][0], out _) &&
                                         rows[0].Skip(1).Any(v => TryParseDouble(v, out _));
            string[] headers = Array.Empty<string>();
            if (!firstRowHasTwoNumbers)
            {
                headers = rows[0];
                dataStart = 1;
            }
            else
            {
                return false; // without headers stick to generic logic
            }

            // Map columns
            var map = DetectSchema(headers);
            if (map.XIndex is null)
                return false;

            int xIdx = map.XIndex.Value;

            // Build doubles for all columns
            int maxCols = rows.Skip(dataStart).DefaultIfEmpty(Array.Empty<string>()).Max(r => r.Length);
            var columns = Enumerable.Range(0, maxCols).Select(_ => new List<double>(rows.Count - dataStart)).ToList();

            for (int i = dataStart; i < rows.Count; i++)
            {
                var r = rows[i];
                for (int c = 0; c < maxCols; c++)
                {
                    if (c < r.Length && TryParseDouble(r[c], out var v))
                        columns[c].Add(v);
                    else
                        columns[c].Add(double.NaN);
                }
            }

            // Build X (numeric or DateTime->seconds since first)
            List<double> xs;
            bool xIsDateTime = IsLikelyDateTimeColumn(rows, dataStart, xIdx);
            if (xIsDateTime)
            {
                xs = BuildXsFromDateTime(rows, dataStart, xIdx);
                xTitle = "Time [s]";
            }
            else
            {
                xs = columns[xIdx].Select(v => map.TimeScaleToSeconds != 0 ? v * map.TimeScaleToSeconds : v).ToList();
                xTitle = map.XTitle ?? "Time [s]";
            }

            // Prefer only radar_voltage and radar_adc if present
            var radarY = Enumerable.Range(0, maxCols)
                .Where(i => i != xIdx)
                .Where(i =>
                {
                    string h = i < headers.Length ? headers[i] : string.Empty;
                    return IsRadarVoltageHeader(h) || IsRadarAdcHeader(h);
                })
                .ToList();

            // Otherwise fall back to the detected Y candidates
            var yCandidates = radarY.Count > 0
                ? radarY
                : (map.YIndices.Count > 0 ? map.YIndices : Enumerable.Range(0, maxCols).Where(i => i != xIdx).ToList());

            var finalYIndices = PrioritizeY(headers, yCandidates);
            if (finalYIndices.Count == 0)
                return false;

            // Axis Y title
            if (radarY.Count > 0)
                yTitle = "Voltage/ADC";
            else
                yTitle = BestYTitle(headers, finalYIndices).Title;

            // Build series for each Y
            foreach (var yi in finalYIndices)
            {
                var ys = columns[yi];
                var pts = MakePairedPoints(xs, ys);
                if (pts.Count == 0) continue;

                string yHeader = yi < headers.Length ? headers[yi] : $"Y{yi}";
                string name = $"{baseName} - {CleanHeaderForLegend(yHeader)}";
                series.Add(new Series { Name = name, Points = pts });
            }

            return series.Count > 0;
        }

        private static bool TryParseDateTime(string? s, out DateTime dt)
        {
            dt = default;
            if (string.IsNullOrWhiteSpace(s)) return false;

            string[] formats =
            {
                "yyyy-MM-dd HH:mm:ss",
                "yyyy-MM-dd HH:mm:ss.fff",
                "yyyy-MM-ddTHH:mm:ss",
                "yyyy-MM-ddTHH:mm:ss.fff",
                "dd.MM.yyyy HH:mm:ss",
                "dd.MM.yyyy HH:mm:ss.fff",
                "MM/dd/yyyy HH:mm:ss",
                "MM/dd/yyyy HH:mm:ss.fff"
            };

            if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out dt))
                return true;

            if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dt))
                return true;

            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
                return true;

            return false;
        }

        private static bool IsLikelyDateTimeColumn(List<string[]> rows, int startRow, int xIndex)
        {
            int total = Math.Min(20, Math.Max(0, rows.Count - startRow));
            if (total == 0) return false;

            int ok = 0;
            for (int i = 0; i < total; i++)
            {
                var r = rows[startRow + i];
                var s = xIndex < r.Length ? r[xIndex] : null;
                if (TryParseDateTime(s, out _)) ok++;
            }
            return ok >= Math.Max(1, total / 2);
        }

        private static List<double> BuildXsFromDateTime(List<string[]> rows, int startRow, int xIndex)
        {
            var xs = new List<double>(Math.Max(0, rows.Count - startRow));
            DateTime? t0 = null;

            for (int i = startRow; i < rows.Count; i++)
            {
                var r = rows[i];
                string? cell = xIndex < r.Length ? r[xIndex] : null;
                if (TryParseDateTime(cell, out var dt))
                {
                    t0 ??= dt;
                    xs.Add((dt - t0.Value).TotalSeconds);
                }
                else
                {
                    xs.Add(double.NaN);
                }
            }
            return xs;
        }

        private static bool IsRadarVoltageHeader(string h) => Like(h, "radar voltage", "radar_voltage");
        private static bool IsRadarAdcHeader(string h) => Like(h, "radar adc", "radar_adc");

        private sealed class SchemaMap
        {
            public int? XIndex;
            public double TimeScaleToSeconds = 1.0; // multiply raw time by this to get seconds
            public string? XTitle;
            public List<int> YIndices = new();
        }

        private static string CleanHeaderForLegend(string header)
        {
            // Keep readable header but strip excessive whitespaces
            return Regex.Replace(header.Trim(), @"\s+", " ");
        }

        private static (string Base, string? Unit) SplitHeaderUnit(string header)
        {
            // e.g., "Voltage [V]" -> ("Voltage", "V"), "time_ms" -> ("time", "ms")
            var h = header.Trim();
            var m = Regex.Match(h, @"^(.*?)[\s]*\[(.+?)\]\s*$");
            if (m.Success)
            {
                return (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim());
            }

            // suffix _ms/_us/_s
            var lower = h.ToLowerInvariant();
            if (lower.EndsWith("_ms")) return (h[..^3], "ms");
            if (lower.EndsWith("_us")) return (h[..^3], "us");
            if (lower.EndsWith("_s")) return (h[..^2], "s");
            return (h, null);
        }

        private static bool Like(string header, params string[] keys)
        {
            var h = header.Trim().ToLowerInvariant();
            h = Regex.Replace(h, @"[\s_\-]+", " ");
            foreach (var k in keys)
            {
                var kk = k.Trim().ToLowerInvariant();
                if (h.Contains(kk)) return true;
            }
            return false;
        }

        private static SchemaMap DetectSchema(string[] headers)
        {
            var map = new SchemaMap();

            // Find X/time column
            for (int i = 0; i < headers.Length; i++)
            {
                var h = headers[i];
                var (baseName, unit) = SplitHeaderUnit(h);
                if (Like(baseName, "time", "timestamp", "t", "sample time"))
                {
                    map.XIndex = i;
                    double scale = 1.0;
                    string unitLower = (unit ?? "").ToLowerInvariant();

                    if (unitLower is "s" or "sec" or "second" or "seconds") scale = 1.0;
                    else if (unitLower is "ms" or "millisecond" or "milliseconds") scale = 1.0 / 1000.0;
                    else if (unitLower is "us" or "µs" or "microsecond" or "microseconds") scale = 1.0 / 1_000_000.0;
                    else
                    {
                        // Try suffixes in raw name
                        var lower = h.ToLowerInvariant();
                        if (lower.Contains("ms")) scale = 1.0 / 1000.0;
                        else if (lower.Contains("us") || lower.Contains("µs")) scale = 1.0 / 1_000_000.0;
                        else scale = 1.0;
                    }

                    map.TimeScaleToSeconds = scale;

                    // Axis title
                    string timeUnit = scale == 1.0 ? "s" : (Math.Abs(scale - 1.0 / 1000.0) < 1e-12 ? "s" : "s");
                    map.XTitle = "Time [s]"; // normalize to seconds
                    break;
                }
            }

            // Voltage-like first
            var yPriorityGroups = new List<Func<int, bool>>
            {
                i => Like(headers[i], "voltage", "volt", "v", "analog in", "analog input", "ai"),
                i => Like(headers[i], "speed", "velocity"),
                i => Like(headers[i], "range", "distance"),
                i => Like(headers[i], "amplitude", "snr", "signal", "strength")
            };

            var allY = Enumerable.Range(0, headers.Length).ToList();

            foreach (var group in yPriorityGroups)
            {
                foreach (int i in allY)
                {
                    if (map.XIndex.HasValue && i == map.XIndex.Value) continue;
                    if (group(i)) map.YIndices.Add(i);
                }
                if (map.YIndices.Count > 0) break; // keep first matching group
            }

            // If still none, leave empty to be filled later
            return map;
        }

        private static List<int> PrioritizeY(string[] headers, List<int> candidates)
        {
            // Keep original order but de-dup and ensure numeric-looking headers first
            var ordered = new List<int>();

            // Prefer Voltage then Speed/Velocity then Range/Distance then others
            int Score(string h)
            {
                if (Like(h, "voltage", "volt", "v", "analog")) return 0;
                if (Like(h, "speed", "velocity")) return 1;
                if (Like(h, "range", "distance")) return 2;
                if (Like(h, "amplitude", "snr", "signal")) return 3;
                return 4;
            }

            foreach (var idx in candidates.OrderBy(i => Score(headers.ElementAtOrDefault(i) ?? "")))
                ordered.Add(idx);

            // Cap to a reasonable number if too many columns
            if (ordered.Count > 12) ordered = ordered.Take(12).ToList();
            return ordered;
        }

        private void EnsureHoverUI()
        {
            if (_hoverPopup != null) return;

            _hoverText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap
            };

            _hoverBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(225, 30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8),
                Child = _hoverText,
                IsHitTestVisible = false
            };

            _hoverPopup = new Popup
            {
                Placement = PlacementMode.Relative,
                StaysOpen = true,
                AllowsTransparency = true,
                Focusable = false,
                Child = _hoverBorder
            };

            // Vertical guide line (drawn on axes canvas so it sits above grid but within plot)
            _hoverLine = new Line
            {
                Stroke = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 },
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            AxesCanvas.Children.Add(_hoverLine);

            // Dot marker on snapped peak (inside data canvas so it clips to plot area)
            _hoverDot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = Brushes.White,
                Stroke = Brushes.Black,
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            DataCanvas.Children.Add(_hoverDot);
        }

        private void PlotRoot_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_hasData) { HideHover(); return; }

            EnsureHoverUI();

            // Determine plot rectangle
            var w = AxesCanvas.ActualWidth;
            var h = AxesCanvas.ActualHeight;
            if (w <= 0 || h <= 0) { HideHover(); return; }

            Rect plotRect = GetPlotRect(w, h);

            // Mouse position relative to the drawing canvases
            Point posAxes = e.GetPosition(AxesCanvas);
            if (!plotRect.Contains(posAxes))
            {
                HideHover();
                return;
            }

            // Compute data-space X at mouse
            double xUnderMouse = InvMapX(posAxes.X, plotRect, _minX, _maxX);

            // Try snap-to-peak
            bool snapped = TryGetSnapPeak(xUnderMouse, plotRect, out var snapSeries, out var snapPoint);

            // Use snapped X if available, otherwise free hover X
            double xForQuery = snapped ? snapPoint.X : xUnderMouse;

            // Build info text with interpolated Y for all series
            var lines = new List<string>
            {
                snapped ? $"{_xAxisTitle}: {xForQuery:G6}  (snap)" : $"{_xAxisTitle}: {xForQuery:G6}"
            };

            foreach (var s in _series)
            {
                if (TryInterpolateYAtX(GetSortedPoints(s), xForQuery, out double y))
                {
                    string prefix = snapped && s == snapSeries ? "★ " : "";
                    lines.Add($"{prefix}{s.Name}: {y:G6}");
                }
                else
                {
                    lines.Add($"{s.Name}: (n/a)");
                }
            }

            if (_hoverText != null)
                _hoverText.Text = string.Join(Environment.NewLine, lines);

            // Position popup near the cursor within the PlotRoot
            if (_hoverPopup != null && PlotRoot != null)
            {
                _hoverPopup.PlacementTarget = PlotRoot;
                Point posRoot = e.GetPosition(PlotRoot);
                _hoverPopup.HorizontalOffset = posRoot.X + 14;
                _hoverPopup.VerticalOffset = posRoot.Y + 14;
                _hoverPopup.IsOpen = true;
            }

            // Update vertical guide line
            if (_hoverLine != null)
            {
                double px = MapX(xForQuery, plotRect, _minX, _maxX);
                _hoverLine.X1 = px;
                _hoverLine.X2 = px;
                _hoverLine.Y1 = plotRect.Top;
                _hoverLine.Y2 = plotRect.Bottom;
                _hoverLine.Visibility = Visibility.Visible;
            }

            // Update dot marker on snapped peak
            if (_hoverDot != null)
            {
                if (snapped)
                {
                    double px = MapX(snapPoint.X, plotRect, _minX, _maxX);
                    double py = MapY(snapPoint.Y, plotRect, _minY, _maxY);

                    // Colorize marker with snapped series color
                    int idx = _series.IndexOf(snapSeries!);
                    var col = idx >= 0 ? _palette[idx % _palette.Count] : Brushes.White;
                    _hoverDot.Fill = col;

                    Canvas.SetLeft(_hoverDot, px - _hoverDot.Width / 2);
                    Canvas.SetTop(_hoverDot, py - _hoverDot.Height / 2);
                    _hoverDot.Visibility = Visibility.Visible;
                }
                else
                {
                    _hoverDot.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void PlotRoot_MouseLeave(object sender, MouseEventArgs e) => HideHover();

        private void HideHover()
        {
            if (_hoverPopup != null) _hoverPopup.IsOpen = false;
            if (_hoverLine != null) _hoverLine.Visibility = Visibility.Collapsed;
            if (_hoverDot != null) _hoverDot.Visibility = Visibility.Collapsed;
        }

        private static (string Title, string? Unit) BestYTitle(string[] headers, List<int> yIndices)
        {
            string? chosenName = null;
            string? unit = null;

            foreach (var yi in yIndices)
            {
                if (yi >= headers.Length) continue;
                var (baseName, u) = SplitHeaderUnit(headers[yi]);
                if (chosenName == null) chosenName = baseName.Trim();
                if (unit == null && u != null) unit = u;
                // If this header clearly states voltage, prefer that
                if (Like(baseName, "voltage", "volt", "v", "analog"))
                {
                    chosenName = "Voltage";
                    if (u == null) unit ??= "V";
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(chosenName)) chosenName = "Value";
            string title = unit != null ? $"{chosenName} [{unit}]" : chosenName;
            return (title, unit);
        }

        private List<Series> BuildSeriesFromRows(List<string[]> rows, string baseName)
        {
            var result = new List<Series>();

            // Detect header
            int startRow = 0;
            bool firstRowHasTwoNumbers = TryParseDouble(rows[0][0], out _) &&
                                         rows[0].Skip(1).Any(v => TryParseDouble(v, out _));
            string[] headers = Array.Empty<string>();
            if (!firstRowHasTwoNumbers)
            {
                headers = rows[0];
                startRow = 1;
            }

            if (rows.Count - startRow <= 0)
                return result;

            int maxCols = rows.Skip(startRow).Max(r => r.Length);

            var columns = new List<List<double>>();
            for (int c = 0; c < maxCols; c++)
                columns.Add(new List<double>(rows.Count - startRow));

            for (int i = startRow; i < rows.Count; i++)
            {
                var r = rows[i];
                for (int c = 0; c < maxCols; c++)
                {
                    if (c < r.Length && TryParseDouble(r[c], out var val))
                        columns[c].Add(val);
                    else
                        columns[c].Add(double.NaN);
                }
            }

            // Try DateTime timestamps in first column
            List<double> xs;
            if (IsLikelyDateTimeColumn(rows, startRow, 0))
            {
                xs = BuildXsFromDateTime(rows, startRow, 0);
                if (_xAxisTitle == "X") _xAxisTitle = "Time [s]";
            }
            else
            {
                xs = columns[0];
                if (_xAxisTitle == "X") _xAxisTitle = startRow == 1 ? $"{rows[0][0].Trim()}" : "X";
            }

            // If headers exist, try radar_voltage/radar_adc only
            var yIndices = new List<int>();
            if (startRow == 1 && headers.Length > 0)
            {
                for (int c = 1; c < maxCols; c++)
                {
                    string h = c < headers.Length ? headers[c] : string.Empty;
                    if (IsRadarVoltageHeader(h) || IsRadarAdcHeader(h))
                        yIndices.Add(c);
                }
            }

            if (yIndices.Count == 0)
            {
                // Fallback: use any numeric Y columns
                for (int c = 1; c < maxCols; c++)
                    yIndices.Add(c);
            }

            int added = 0;
            foreach (int c in yIndices)
            {
                var pts = MakePairedPoints(xs, columns[c]);
                if (pts.Count == 0) continue;

                string name = baseName;
                if (startRow == 1 && c < rows[0].Length && !string.IsNullOrWhiteSpace(rows[0][c]))
                    name = $"{baseName} - {rows[0][c].Trim()}";
                else if (maxCols > 2)
                    name = $"{baseName} - Y{c}";

                result.Add(new Series { Name = name, Points = pts });
                added++;
            }

            if (_yAxisTitle == "Y")
                _yAxisTitle = (startRow == 1 && yIndices.Count > 0 &&
                              yIndices.Select(i => i < headers.Length ? headers[i] : "Value")
                                      .Any(h => IsRadarVoltageHeader(h) || IsRadarAdcHeader(h)))
                    ? "Voltage/ADC"
                    : "Value";

            return result;
        }

        private static List<Point> MakePairedPoints(List<double> xs, List<double> ys)
        {
            int n = Math.Min(xs.Count, ys.Count);
            var pts = new List<Point>(n);
            for (int i = 0; i < n; i++)
            {
                double x = xs[i], y = ys[i];
                if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y)) continue;
                pts.Add(new Point(x, y));
            }
            return pts;
        }

        private static bool TryParseDouble(string? s, out double value)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                value = double.NaN;
                return false;
            }

            // Try current culture (e.g., cs-CZ uses comma), then invariant
            if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value))
                return true;
            if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value))
                return true;

            // Replace comma <-> dot and retry
            var swapped = s.Replace(',', '.');
            if (double.TryParse(swapped, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value))
                return true;

            value = double.NaN;
            return false;
        }

        private void FitToData()
        {
            if (_series.Count == 0)
                return;

            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

            foreach (var s in _series)
            {
                foreach (var p in s.Points)
                {
                    if (double.IsNaN(p.X) || double.IsNaN(p.Y)) continue;
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Y > maxY) maxY = p.Y;
                }
            }

            if (!double.IsFinite(minX) || !double.IsFinite(maxX) ||
                !double.IsFinite(minY) || !double.IsFinite(maxY))
                return;

            // Expand bounds by 5%
            ExpandBounds(ref minX, ref maxX);
            ExpandBounds(ref minY, ref maxY);

            if (AlmostEqual(maxX, minX)) { minX -= 0.5; maxX += 0.5; }
            if (AlmostEqual(maxY, minY)) { minY -= 0.5; maxY += 0.5; }

            _minX = minX; _maxX = maxX; _minY = minY; _maxY = maxY;
        }

        private static void ExpandBounds(ref double min, ref double max)
        {
            double span = max - min;
            if (span <= 0) span = Math.Max(Math.Abs(min), Math.Abs(max));
            if (span == 0) span = 1;
            double pad = span * 0.05;
            min -= pad; max += pad;
        }

        private static bool AlmostEqual(double a, double b) => Math.Abs(a - b) <= 1e-12;

        private void Redraw()
        {
            AxesCanvas.Children.Clear();
            DataCanvas.Children.Clear();

            var w = AxesCanvas.ActualWidth;
            var h = AxesCanvas.ActualHeight;
            if (w <= 0 || h <= 0)
                return;

            if (!_hasData)
                return;

            // Plot area rectangle
            Rect plotRect = new Rect(LeftMargin, TopMargin, Math.Max(0, w - LeftMargin - RightMargin), Math.Max(0, h - TopMargin - BottomMargin));

            // Ensure series never render outside the plot area
            DataCanvas.Clip = new RectangleGeometry(plotRect);

            DrawAxesAndGrid(AxesCanvas, plotRect, _minX, _maxX, _minY, _maxY);
            DrawSeries(DataCanvas, plotRect);
            DrawLegend(AxesCanvas, plotRect);

            // Ensure hover UI remains on top after redraw
            _hoverLine = null;
            _hoverPopup = null;
            _hoverDot = null;
            EnsureHoverUI();
        }

        private static Rect GetPlotRect(double canvasWidth, double canvasHeight)
        {
            return new Rect(LeftMargin, TopMargin,
                Math.Max(0, canvasWidth - LeftMargin - RightMargin),
                Math.Max(0, canvasHeight - TopMargin - BottomMargin));
        }

        private static double InvMapX(double px, Rect plotRect, double minX, double maxX)
        {
            double t = (px - plotRect.Left) / Math.Max(1e-12, plotRect.Width);
            return minX + t * (maxX - minX);
        }

        private static bool IsMonotonicIncreasingByX(List<Point> pts)
        {
            for (int i = 1; i < pts.Count; i++)
            {
                if (pts[i].X < pts[i - 1].X) return false;
            }
            return true;
        }

        private IReadOnlyList<Point> GetSortedPoints(Series s)
        {
            if (_sortedCache.TryGetValue(s, out var cached)) return cached;
            var pts = s.Points;
            var sorted = IsMonotonicIncreasingByX(pts) ? pts : pts.OrderBy(p => p.X).ToList();
            _sortedCache[s] = sorted;
            return sorted;
        }

        private static bool TryInterpolateYAtX(IReadOnlyList<Point> pts, double x, out double y)
        {
            y = double.NaN;
            int n = pts.Count;
            if (n == 0) return false;
            if (n == 1) { y = pts[0].Y; return true; }

            // Clamp to ends
            if (x <= pts[0].X) { y = pts[0].Y; return true; }
            if (x >= pts[n - 1].X) { y = pts[n - 1].Y; return true; }

            // Binary search for segment [lo, hi]
            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (pts[mid].X <= x) lo = mid; else hi = mid;
            }

            var p0 = pts[lo];
            var p1 = pts[hi];
            double dx = p1.X - p0.X;
            if (dx == 0) { y = p0.Y; return true; }

            double t = (x - p0.X) / dx;
            y = p0.Y + t * (p1.Y - p0.Y);
            return true;
        }

        // Peak detection (local maxima) with basic plateau handling
        private static List<int> GetPeakIndices(IReadOnlyList<Point> pts)
        {
            var peaks = new List<int>();
            int n = pts.Count;
            if (n < 2) return peaks;

            int i = 1;
            while (i < n - 1)
            {
                double y0 = pts[i - 1].Y;
                double y1 = pts[i].Y;
                double y2 = pts[i + 1].Y;

                // Strict increase then decrease -> peak at i
                if (y1 > y0 && y1 > y2)
                {
                    peaks.Add(i);
                    i++;
                    continue;
                }

                // Handle plateau peaks (y stays flat then goes down)
                if (y1 > y0 && Math.Abs(y1 - y2) < 1e-12)
                {
                    int start = i;
                    int j = i + 1;
                    while (j < n && Math.Abs(pts[j].Y - y1) < 1e-12) j++;
                    // if it goes down after plateau, treat midpoint of plateau as peak
                    if (j < n && pts[j].Y < y1)
                    {
                        int mid = (start + (j - 1)) / 2;
                        peaks.Add(mid);
                    }
                    i = j;
                    continue;
                }

                i++;
            }
            return peaks;
        }

        private IReadOnlyList<int> GetPeakIndicesCached(Series s, IReadOnlyList<Point> sortedPts)
        {
            if (_peakIndexCache.TryGetValue(s, out var cached)) return cached;
            var indices = GetPeakIndices(sortedPts);
            _peakIndexCache[s] = indices;
            return indices;
        }

        private bool TryGetSnapPeak(double x, Rect plotRect, out Series? bestSeries, out Point bestPoint)
        {
            bestSeries = null;
            bestPoint = new Point(double.NaN, double.NaN);
            if (_series.Count == 0) return false;

            double dxTol = Math.Abs(SnapPixelRadius / Math.Max(1e-12, plotRect.Width)) * (_maxX - _minX);

            // Use locals that the local function can capture safely
            Series? bestSeriesLocal = null;
            Point bestPointLocal = new Point(double.NaN, double.NaN);
            double bestY = double.NegativeInfinity;
            double bestDx = double.PositiveInfinity;

            foreach (var s in _series)
            {
                var pts = GetSortedPoints(s);
                if (pts.Count == 0) continue;

                var peakIdx = GetPeakIndicesCached(s, pts);
                if (peakIdx.Count == 0) continue;

                // Binary search over peaks by their X
                int lo = 0, hi = peakIdx.Count - 1;
                while (hi - lo > 1)
                {
                    int mid = (lo + hi) >> 1;
                    double xm = pts[peakIdx[mid]].X;
                    if (xm <= x) lo = mid; else hi = mid;
                }

                void Consider(int pidx)
                {
                    if (pidx < 0 || pidx >= peakIdx.Count) return;
                    int iPoint = peakIdx[pidx];
                    var p = pts[iPoint];
                    double dx = Math.Abs(p.X - x);
                    if (dx > dxTol) return;
                    if (p.Y > bestY || (AlmostEqual(p.Y, bestY) && dx < bestDx))
                    {
                        bestY = p.Y;
                        bestDx = dx;
                        bestSeriesLocal = s;
                        bestPointLocal = p;
                    }
                }

                Consider(lo);
                Consider(lo + 1);
            }

            bestSeries = bestSeriesLocal;
            bestPoint = bestPointLocal;
            return bestSeriesLocal != null && double.IsFinite(bestPointLocal.X) && double.IsFinite(bestPointLocal.Y);
        }

        private void DrawAxesAndGrid(Canvas canvas, Rect plotRect, double minX, double maxX, double minY, double maxY)
        {
            // Background
            var bg = new Rectangle
            {
                Width = plotRect.Width,
                Height = plotRect.Height,
                Fill = new SolidColorBrush(Color.FromRgb(20, 20, 20))
            };
            Canvas.SetLeft(bg, plotRect.Left);
            Canvas.SetTop(bg, plotRect.Top);
            canvas.Children.Add(bg);

            // Border
            var border = new Rectangle
            {
                Width = plotRect.Width,
                Height = plotRect.Height,
                Stroke = new SolidColorBrush(Color.FromRgb(64, 64, 64)),
                StrokeThickness = 1
            };
            Canvas.SetLeft(border, plotRect.Left);
            Canvas.SetTop(border, plotRect.Top);
            canvas.Children.Add(border);

            // Ticks
            var (xTicks, xStep) = ComputeNiceTicks(minX, maxX, 8);
            var (yTicks, yStep) = ComputeNiceTicks(minY, maxY, 8);

            // Gridlines and labels
            var gridBrush = new SolidColorBrush(Color.FromRgb(50, 50, 50)) { Opacity = 0.5 };

            // Y grid + labels
            foreach (var y in yTicks)
            {
                if (y < minY || y > maxY) continue;

                double py = MapY(y, plotRect, minY, maxY);
                var line = new Line
                {
                    X1 = plotRect.Left,
                    X2 = plotRect.Right,
                    Y1 = py,
                    Y2 = py,
                    Stroke = gridBrush,
                    StrokeThickness = 1
                };
                canvas.Children.Add(line);

                var lbl = new TextBlock
                {
                    Text = FormatTick(y, yStep),
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180))
                };
                Canvas.SetLeft(lbl, Math.Max(0, plotRect.Left - 8 - MeasureText(lbl)));
                Canvas.SetTop(lbl, py - 10);
                canvas.Children.Add(lbl);
            }

            // X grid + labels
            foreach (var x in xTicks)
            {
                if (x < minX || x > maxX) continue;

                double px = MapX(x, plotRect, minX, maxX);
                var line = new Line
                {
                    X1 = px,
                    X2 = px,
                    Y1 = plotRect.Top,
                    Y2 = plotRect.Bottom,
                    Stroke = gridBrush,
                    StrokeThickness = 1
                };
                canvas.Children.Add(line);

                var lbl = new TextBlock
                {
                    Text = FormatTick(x, xStep),
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180))
                };
                Canvas.SetLeft(lbl, px - MeasureText(lbl) / 2);
                Canvas.SetTop(lbl, plotRect.Bottom + 6);
                canvas.Children.Add(lbl);
            }

            // Axis lines
            var axisPen = new SolidColorBrush(Color.FromRgb(160, 160, 160));
            canvas.Children.Add(new Line
            {
                X1 = plotRect.Left,
                X2 = plotRect.Right,
                Y1 = plotRect.Bottom,
                Y2 = plotRect.Bottom,
                Stroke = axisPen,
                StrokeThickness = 1.5
            });
            canvas.Children.Add(new Line
            {
                X1 = plotRect.Left,
                X2 = plotRect.Left,
                Y1 = plotRect.Top,
                Y2 = plotRect.Bottom,
                Stroke = axisPen,
                StrokeThickness = 1.5
            });

            // Axis titles
            // X title centered below axis
            var xTitle = new TextBlock
            {
                Text = _xAxisTitle,
                Foreground = Brushes.White,
                FontSize = 13
            };
            double xTitleWidth = MeasureText(xTitle);
            Canvas.SetLeft(xTitle, plotRect.Left + (plotRect.Width - xTitleWidth) / 2);
            Canvas.SetTop(xTitle, plotRect.Bottom + 26);
            canvas.Children.Add(xTitle);

            // Y title on the left, rotated
            var yTitle = new TextBlock
            {
                Text = _yAxisTitle,
                Foreground = Brushes.White,
                FontSize = 13
            };
            yTitle.LayoutTransform = new RotateTransform(-90);
            yTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(yTitle, plotRect.Left - 50);
            Canvas.SetTop(yTitle, plotRect.Top + (plotRect.Height - yTitle.DesiredSize.Width) / 2);
            canvas.Children.Add(yTitle);
        }

        private void DrawSeries(Canvas canvas, Rect plotRect)
        {
            for (int i = 0; i < _series.Count; i++)
            {
                var s = _series[i];
                var color = _palette[i % _palette.Count];
                var geom = new StreamGeometry { FillRule = FillRule.Nonzero };
                using (var ctx = geom.Open())
                {
                    bool started = false;
                    foreach (var p in s.Points)
                    {
                        double px = MapX(p.X, plotRect, _minX, _maxX);
                        double py = MapY(p.Y, plotRect, _minY, _maxY);
                        if (!started)
                        {
                            ctx.BeginFigure(new Point(px, py), isFilled: false, isClosed: false);
                            started = true;
                        }
                        else
                        {
                            ctx.LineTo(new Point(px, py), isStroked: true, isSmoothJoin: false);
                        }
                    }
                }
                geom.Freeze();
                var path = new System.Windows.Shapes.Path
                {
                    Stroke = color,
                    StrokeThickness = 1.8,
                    Data = geom,
                    SnapsToDevicePixels = true
                };
                RenderOptions.SetEdgeMode(path, EdgeMode.Unspecified);
                canvas.Children.Add(path);
            }
        }

        private void DrawLegend(Canvas canvas, Rect plotRect)
        {
            if (_series.Count == 0) return;

            double padding = 8;
            double swatch = 18;
            double spacing = 4;

            var entries = _series.Select((s, i) => new
            {
                Text = s.Name,
                Brush = _palette[i % _palette.Count]
            }).ToList();

            double maxWidth = 0;
            double totalHeight = 0;
            foreach (var e in entries)
            {
                var tb = new TextBlock { Text = e.Text, Foreground = Brushes.White, FontSize = 12 };
                double w = MeasureText(tb);
                if (w > maxWidth) maxWidth = w;
                totalHeight += Math.Max(swatch, tb.FontSize + 4) + spacing;
            }
            totalHeight -= spacing;

            double boxW = padding + swatch + 8 + maxWidth + padding;
            double boxH = padding + totalHeight + padding;

            double x = plotRect.Right - boxW - 8;
            double y = plotRect.Top + 8;

            var panel = new Rectangle
            {
                Width = boxW,
                Height = boxH,
                RadiusX = 6,
                RadiusY = 6,
                Fill = new SolidColorBrush(Color.FromArgb(200, 30, 30, 30)),
                Stroke = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                StrokeThickness = 1
            };
            Canvas.SetLeft(panel, x);
            Canvas.SetTop(panel, y);
            canvas.Children.Add(panel);

            double cy = y + padding;
            foreach (var e in entries)
            {
                var line = new Line
                {
                    X1 = x + padding,
                    X2 = x + padding + swatch,
                    Y1 = cy + swatch / 2,
                    Y2 = cy + swatch / 2,
                    Stroke = e.Brush,
                    StrokeThickness = 2
                };
                canvas.Children.Add(line);

                var tb = new TextBlock
                {
                    Text = e.Text,
                    Foreground = Brushes.White,
                    FontSize = 12
                };
                Canvas.SetLeft(tb, x + padding + swatch + 8);
                Canvas.SetTop(tb, cy + (swatch - tb.FontSize) / 2 - 2);
                canvas.Children.Add(tb);

                cy += Math.Max(swatch, tb.FontSize + 4) + spacing;
            }
        }

        private static (List<double> ticks, double step) ComputeNiceTicks(double min, double max, int maxTicks)
        {
            if (min > max) (min, max) = (max, min);
            if (AlmostEqual(min, max)) max = min + 1;

            double span = NiceNum(max - min, round: false);
            double step = NiceNum(span / Math.Max(1, maxTicks - 1), round: true);
            double niceMin = Math.Floor(min / step) * step;
            double niceMax = Math.Ceiling(max / step) * step;

            var ticks = new List<double>();
            for (double v = niceMin; v <= niceMax + 0.5 * step; v += step)
                ticks.Add(v);
            return (ticks, step);
        }

        private static double NiceNum(double x, bool round)
        {
            double expv = Math.Floor(Math.Log10(x));
            double f = x / Math.Pow(10, expv);
            double nf;
            if (round)
            {
                if (f < 1.5) nf = 1;
                else if (f < 3) nf = 2;
                else if (f < 7) nf = 5;
                else nf = 10;
            }
            else
            {
                if (f <= 1) nf = 1;
                else if (f <= 2) nf = 2;
                else if (f <= 5) nf = 5;
                else nf = 10;
            }
            return nf * Math.Pow(10, expv);
        }

        private static string FormatTick(double v, double step)
        {
            double mag = Math.Max(Math.Abs(step), 1e-12);
            if (mag >= 1)
                return v.ToString("G6", CultureInfo.CurrentCulture);
            int decimals = Math.Clamp((int)Math.Ceiling(-Math.Log10(mag)) + 1, 0, 8);
            return v.ToString("F" + decimals, CultureInfo.CurrentCulture);
        }

        private static double MapX(double x, Rect plotRect, double minX, double maxX)
        {
            double t = (x - minX) / (maxX - minX);
            return plotRect.Left + t * plotRect.Width;
        }

        private static double MapY(double y, Rect plotRect, double minY, double maxY)
        {
            double t = (y - minY) / (maxY - minY);
            return plotRect.Bottom - t * plotRect.Height;
        }

        private static double MeasureText(TextBlock tb)
        {
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return tb.DesiredSize.Width;
        }

        // === Pie chart integration ===

        private void OpenPie_Click(object sender, RoutedEventArgs e)
        {
            if (_series.Count == 0)
            {
                MessageBox.Show(this, "No data loaded.", "Pie Chart", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var items = new List<(string Name, double Value, Brush Brush)>();
            for (int i = 0; i < _series.Count; i++)
            {
                var s = _series[i];
                double val = AggregateAreaAbs(s.Points);
                if (!double.IsFinite(val) || val <= 0) continue;

                var brush = _palette[i % _palette.Count];
                items.Add((s.Name, val, brush));
            }

            if (items.Count == 0)
            {
                MessageBox.Show(this, "Data did not produce any positive slice values.", "Pie Chart",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var win = new PieChartWindow(items) { Owner = this };
            win.Show();
        }

        // Integrate |Y| over X using trapezoidal rule (robust to varying X spacing).
        private static double AggregateAreaAbs(List<Point> pts)
        {
            if (pts.Count < 2) return 0;
            double sum = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                var p0 = pts[i - 1];
                var p1 = pts[i];
                if (!double.IsFinite(p0.X) || !double.IsFinite(p0.Y) ||
                    !double.IsFinite(p1.X) || !double.IsFinite(p1.Y)) continue;

                double dx = p1.X - p0.X;
                if (dx == 0) continue;
                double avgAbsY = 0.5 * (Math.Abs(p0.Y) + Math.Abs(p1.Y));
                sum += Math.Abs(dx) * avgAbsY;
            }
            return sum;
        }

        private class Series
        {
            public string Name { get; set; } = "Series";
            public List<Point> Points { get; set; } = new();
        }

        // === Export plot as image ===

        private void ExportPlot_Click(object sender, RoutedEventArgs e)
        {
            ExportElementToImage(PlotRoot, $"Plot_{DateTime.Now:yyyyMMdd_HHmmss}");
        }

        private void ExportElementToImage(FrameworkElement element, string suggestedName)
        {
            if (element == null)
            {
                MessageBox.Show(this, "Nothing to export.", "Export Image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sfd = new SaveFileDialog
            {
                Title = "Export Image",
                FileName = suggestedName,
                Filter = "PNG Image (*.png)|*.png|JPEG Image (*.jpg;*.jpeg)|*.jpg;*.jpeg",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (sfd.ShowDialog(this) != true) return;

            element.UpdateLayout();

            // Full visual extents (includes axis labels/legend even if they overflow).
            Rect bounds = VisualTreeHelper.GetDescendantBounds(element);
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            {
                double w = element.ActualWidth > 0 ? element.ActualWidth : element.RenderSize.Width;
                double h = element.ActualHeight > 0 ? element.ActualHeight : element.RenderSize.Height;
                bounds = new Rect(new Point(0, 0), new Size(Math.Max(1, w), Math.Max(1, h)));
            }

            // Output scale (2x for sharper export). This scales pixels without changing framing.
            double scale = 2.0;
            int pxWidth = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
            int pxHeight = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));

            var rtb = new RenderTargetBitmap(pxWidth, pxHeight, 96, 96, PixelFormats.Pbgra32);

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                bool isPng = sfd.FilterIndex == 1;
                Brush bg = isPng ? Brushes.Transparent : Brushes.White;

                // First scale to the target resolution, then translate so bounds' top-left maps to (0,0).
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));

                // Background
                dc.DrawRectangle(bg, null, new Rect(bounds.TopLeft, bounds.Size));

                // Paint the element
                var vb = new VisualBrush(element)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top
                };
                dc.DrawRectangle(vb, null, new Rect(bounds.TopLeft, bounds.Size));

                dc.Pop(); // translate
                dc.Pop(); // scale
            }

            rtb.Render(dv);

            BitmapEncoder encoder = sfd.FilterIndex == 1
                ? new PngBitmapEncoder()
                : new JpegBitmapEncoder { QualityLevel = 95 };

            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(sfd.FileName);
            encoder.Save(fs);

            if (InfoText != null)
                InfoText.Text = $"Exported: {System.IO.Path.GetFileName(sfd.FileName)}";
        }
    }
}