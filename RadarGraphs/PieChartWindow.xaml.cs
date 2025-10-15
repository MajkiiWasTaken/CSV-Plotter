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
 * Date: 09. 10. 2025       *
 ****************************/

namespace RadarGraphs
{
    public partial class PieChartWindow : Window
    {
        private readonly List<(string Name, double Value, Brush Brush)> _items;

        public PieChartWindow(List<(string Name, double Value, Brush Brush)> items)
        {
            InitializeComponent();
            _items = items ?? new List<(string, double, Brush)>();
            SizeChanged += (_, __) => Redraw();
        }

        private void Window_Loaded(object? sender, RoutedEventArgs e)
        {
            Redraw();
        }

        private void Redraw()
        {
            ChartCanvas.Children.Clear();
            LegendPanel.Children.Clear();

            if (_items.Count == 0)
            {
                StatusText.Text = "No data";
                StatusText.Visibility = Visibility.Visible;
                TotalText.Text = "";
                return;
            }

            double total = _items.Sum(i => Math.Max(0, i.Value));
            if (total <= 0)
            {
                StatusText.Text = "All values are zero";
                StatusText.Visibility = Visibility.Visible;
                TotalText.Text = "Total: 0";
                return;
            }

            StatusText.Visibility = Visibility.Collapsed;

            double w = ChartCanvas.ActualWidth;
            double h = ChartCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            double padding = 16;
            double size = Math.Min(w, h) - 2 * padding;
            size = Math.Max(10, size);
            Point center = new Point(w / 2, h / 2);
            double radius = size / 2;

            double startAngle = -90; // start at top
            foreach (var it in _items.Where(i => i.Value > 0))
            {
                double sweep = (it.Value / total) * 360.0;
                DrawSlice(ChartCanvas, center, radius, startAngle, sweep, it.Brush);
                startAngle += sweep;
            }

            foreach (var it in _items.OrderByDescending(i => i.Value))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                var swatch = new Rectangle { Width = 18, Height = 12, Fill = it.Brush, Stroke = Brushes.Transparent, Margin = new Thickness(0, 2, 8, 0) };
                double pct = total > 0 ? (it.Value / total) * 100.0 : 0;
                var tb = new TextBlock
                {
                    Text = $"{it.Name} — {it.Value:G4} ({pct:F1}%)",
                    Foreground = Brushes.White,
                    FontSize = 12
                };
                row.Children.Add(swatch);
                row.Children.Add(tb);
                LegendPanel.Children.Add(row);
            }

            TotalText.Text = $"Total: {total:G6}";
        }

        private static void DrawSlice(Canvas canvas, Point center, double radius, double startAngle, double sweepAngle, Brush brush)
        {
            if (sweepAngle <= 0) return;
            double sweep = Math.Min(sweepAngle, 359.999); // ensures no full-360 arc

            Point Polar(double aDeg)
            {
                double a = aDeg * Math.PI / 180.0;
                return new Point(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a));
            }

            Point p0 = Polar(startAngle);
            Point p1 = Polar(startAngle + sweep);

            bool isLargeArc = sweep > 180.0;

            var fig = new PathFigure { StartPoint = center, IsClosed = true, IsFilled = true };
            fig.Segments.Add(new LineSegment(p0, true));
            fig.Segments.Add(new ArcSegment(p1, new Size(radius, radius), 0, isLargeArc, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(center, true));

            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            geo.Freeze();

            var path = new System.Windows.Shapes.Path
            {
                Data = geo,
                Fill = brush,
                Stroke = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                StrokeThickness = 1
            };
            canvas.Children.Add(path);
        }

        private void ExportPie_Click(object sender, RoutedEventArgs e)
        {
            // Export the entire window content (pie + legend)
            if (Content is FrameworkElement fe)
                ExportElementToImage(fe, $"PieChart_{DateTime.Now:yyyyMMdd_HHmmss}");
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

            // Full visual extents (includes legend and any overflow)
            Rect bounds = VisualTreeHelper.GetDescendantBounds(element);
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            {
                double w = element.ActualWidth > 0 ? element.ActualWidth : element.RenderSize.Width;
                double h = element.ActualHeight > 0 ? element.ActualHeight : element.RenderSize.Height;
                bounds = new Rect(new Point(0, 0), new Size(Math.Max(1, w), Math.Max(1, h)));
            }

            // Sharp export scale (2x logical pixels)
            double scale = 2.0;
            int pxWidth = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
            int pxHeight = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));

            var rtb = new RenderTargetBitmap(pxWidth, pxHeight, 96, 96, PixelFormats.Pbgra32);

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                bool isPng = sfd.FilterIndex == 1;
                Brush bg = isPng ? Brushes.Transparent : Brushes.White;

                // Scale to target resolution, then translate so bounds map to (0,0)
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));

                // Background
                dc.DrawRectangle(bg, null, new Rect(bounds.TopLeft, bounds.Size));

                // Paint the full visual (pie + legend)
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
        }
    }
}