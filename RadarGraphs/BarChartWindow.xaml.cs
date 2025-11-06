using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using System.IO;
using System.Windows.Media.Imaging;

/****************************
 * Made by: Michal Švrèek   *
 * Date: 06. 11. 2025       *
 ****************************/

namespace RadarGraphs
{
    public partial class BarChartWindow : Window
    {
        private readonly List<(string Label, DateTime Date, int Count, Brush Brush)> _items;
        private readonly int _totalFiles;

        // Fixed bar metrics
        private const double FixedBarWidth = 50.0;
        private const double FixedBarGap = 30.0;

        public BarChartWindow(List<(string Label, DateTime Date, int Count, Brush Brush)> items, int totalFiles)
        {
            InitializeComponent();
            _items = items ?? new List<(string, DateTime, int, Brush)>();
            _totalFiles = Math.Max(0, totalFiles);
            SizeChanged += (_, __) => Redraw();
        }

        private void Window_Loaded(object? sender, RoutedEventArgs e) => Redraw();

        private void Redraw()
        {
            ChartCanvas.Children.Clear();
            LegendPanel.Children.Clear();

            if (_items.Count == 0)
            {
                StatusText.Text = "No data";
                StatusText.Visibility = Visibility.Visible;
                TotalText.Text = $"Files: {_totalFiles}";
                return;
            }

            StatusText.Visibility = Visibility.Collapsed;

            double w = ChartCanvas.ActualWidth;
            double h = ChartCanvas.ActualHeight;
            if (w <= 0 || h <= 0)
            {
                // fallback to window size
                w = Math.Max(300, this.Width - 64);
                h = Math.Max(200, this.Height - 160);
            }

            int n = _items.Count;
            int maxCount = Math.Max(1, _items.Max(i => i.Count));
            double padding = 24;
            double availW = Math.Max(10, w - 2 * padding);

            // Use fixed bar width and gap
            double barW = FixedBarWidth;
            double barGap = FixedBarGap;
            double requiredWidth = n * barW + Math.Max(0, (n - 1)) * barGap;

            // If required width is larger than available, expand canvas width so bars keep fixed size.
            double canvasWidth = Math.Max(availW, requiredWidth) + 2 * padding;
            ChartCanvas.Width = canvasWidth;
            ChartCanvas.Height = h;

            double left = padding;
            double top = padding;
            double chartH = Math.Max(40, h - 2 * padding - 28); // leave space for labels

            // Draw baseline
            var baseLine = new Line
            {
                X1 = left - 4,
                X2 = left + requiredWidth + 4,
                Y1 = top + chartH,
                Y2 = top + chartH,
                Stroke = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
                StrokeThickness = 1.0
            };
            ChartCanvas.Children.Add(baseLine);

            // Bars
            for (int i = 0; i < n; i++)
            {
                var it = _items[i];
                double frac = (double)it.Count / maxCount;
                double bh = frac * chartH;
                double bx = left + i * (barW + barGap);
                double by = top + (chartH - bh);

                var rect = new Rectangle
                {
                    Width = barW,
                    Height = Math.Max(1, bh),
                    Fill = it.Brush,
                    Stroke = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                    StrokeThickness = 1
                };
                Canvas.SetLeft(rect, bx);
                Canvas.SetTop(rect, by);
                ChartCanvas.Children.Add(rect);

                // Value above bar
                var vlbl = new TextBlock
                {
                    Text = it.Count.ToString(),
                    Foreground = Brushes.White,
                    FontSize = 11
                };
                vlbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(vlbl, bx + (barW - vlbl.DesiredSize.Width) / 2);
                Canvas.SetTop(vlbl, by - vlbl.DesiredSize.Height - 2);
                ChartCanvas.Children.Add(vlbl);

                // Bar label (date) below baseline
                var lbl = new TextBlock
                {
                    Text = it.Label,
                    Foreground = Brushes.White,
                    FontSize = 11,
                    TextAlignment = TextAlignment.Center
                };
                lbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double lblX = bx + (barW - lbl.DesiredSize.Width) / 2;
                Canvas.SetLeft(lbl, lblX);
                Canvas.SetTop(lbl, top + chartH + 6);
                ChartCanvas.Children.Add(lbl);
            }

            // Legend (right)
            foreach (var it in _items)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                var sw = new Rectangle { Width = 18, Height = 12, Fill = it.Brush, Stroke = Brushes.Transparent, Margin = new Thickness(0, 2, 8, 0) };
                var tb = new TextBlock { Text = $"{it.Label} — {it.Count}", Foreground = Brushes.White, FontSize = 12 };
                row.Children.Add(sw);
                row.Children.Add(tb);
                LegendPanel.Children.Add(row);
            }

            // Show number of files (requested)
            TotalText.Text = $"Files: {_totalFiles}";
        }

        private void ExportBar_Click(object sender, RoutedEventArgs e)
        {
            ExportElementToImage(RootGrid, $"BarChart_{DateTime.Now:yyyyMMdd_HHmmss}");
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

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

            Rect bounds = VisualTreeHelper.GetDescendantBounds(element);
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            {
                double w = element.ActualWidth > 0 ? element.ActualWidth : element.RenderSize.Width;
                double h = element.ActualHeight > 0 ? element.ActualHeight : element.RenderSize.Height;
                bounds = new Rect(new Point(0, 0), new Size(Math.Max(1, w), Math.Max(1, h)));
            }

            double scale = 2.0;
            int pxWidth = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
            int pxHeight = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));

            var rtb = new RenderTargetBitmap(pxWidth, pxHeight, 96, 96, PixelFormats.Pbgra32);

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                bool isPng = sfd.FilterIndex == 1;
                Brush bg = isPng ? Brushes.Transparent : Brushes.White;

                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));

                dc.DrawRectangle(bg, null, new Rect(bounds.TopLeft, bounds.Size));

                var vb = new VisualBrush(element)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top
                };
                dc.DrawRectangle(vb, null, new Rect(bounds.TopLeft, bounds.Size));

                dc.Pop();
                dc.Pop();
            }

            rtb.Render(dv);

            BitmapEncoder encoder = sfd.FilterIndex == 1
                ? new PngBitmapEncoder()
                : new JpegBitmapEncoder { QualityLevel = 95 };

            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(sfd.FileName);
            encoder.Save(fs);
        }
    }
}