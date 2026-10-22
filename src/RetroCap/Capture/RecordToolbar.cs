using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using RetroCap.Recording;
using RetroCap.Ui;

namespace RetroCap.Capture
{
    /// <summary>Toolbar shown under the selection in record mode: format choice, Cancel, Record.</summary>
    public sealed class RecordToolbar : UserControl
    {
        private readonly ToggleButton _mp4, _gif;

        public RecordFormat Format { get; private set; }

        public event Action? CancelRequested, RecordRequested;

        public RecordToolbar(RecordFormat format)
        {
            var res = Application.Current;
            FontFamily = (FontFamily)res.FindResource("Sap.Font");
            FontSize = 11;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            _mp4 = FormatToggle("MP4", "MP4 video, 30 fps  (V)");
            _gif = FormatToggle("GIF", "Animated GIF, 10 fps  (G)");
            _mp4.Click += (_, _) => SetFormat(RecordFormat.Mp4);
            _gif.Click += (_, _) => SetFormat(RecordFormat.Gif);

            var cancelRow = new StackPanel { Orientation = Orientation.Horizontal };
            cancelRow.Children.Add(new Image { Source = (ImageSource)res.FindResource("Icon.x"), Style = (Style)res.FindResource("Sap.Icon") });
            cancelRow.Children.Add(new TextBlock { Text = "Cancel", Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var cancel = new Button { Style = (Style)res.FindResource("Sap.ToolButton"), Content = cancelRow, Padding = new Thickness(4, 2, 4, 2), ToolTip = "Cancel  (Esc)" };
            cancel.Click += (_, _) => CancelRequested?.Invoke();

            var recRow = new StackPanel { Orientation = Orientation.Horizontal };
            recRow.Children.Add(new Ellipse
            {
                Width = 10, Height = 10, Fill = (Brush)res.FindResource("Sap.StatusError"),
                Stroke = Brushes.Black, StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Center,
            });
            recRow.Children.Add(new TextBlock { Text = "Record", Margin = new Thickness(4, 0, 0, 0), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            var record = new Button
            {
                Style = (Style)res.FindResource("Sap.Button.Primary"), Content = recRow, Margin = new Thickness(4, 0, 0, 0),
                MinWidth = 0, Padding = new Thickness(6, 1, 6, 1), Focusable = false, ToolTip = "Start Recording  (Enter)",
            };
            record.Click += (_, _) => RecordRequested?.Invoke();

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = "Format:", Margin = new Thickness(2, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(_mp4);
            row.Children.Add(_gif);
            row.Children.Add(new Border { Style = (Style)res.FindResource("Sap.ToolSeparator") });
            row.Children.Add(cancel);
            row.Children.Add(record);

            Content = new Bevel
            {
                Kind = BevelKind.OutStrong, Background = (Brush)res.FindResource("Sap.Surface"),
                Child = new Border { Style = (Style)res.FindResource("Sap.Toolbar"), Child = row },
            };
            PreviewMouseRightButtonDown += (_, e) => e.Handled = true;
            SetFormat(format);
        }

        public void SetFormat(RecordFormat format)
        {
            Format = format;
            _mp4.IsChecked = format == RecordFormat.Mp4;
            _gif.IsChecked = format == RecordFormat.Gif;
        }

        private static ToggleButton FormatToggle(string text, string tip) => new()
        {
            Style = (Style)Application.Current.FindResource("Sap.ToolToggle"),
            Content = new TextBlock { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center },
            ToolTip = tip,
            Focusable = false,
        };
    }
}
