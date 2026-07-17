using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace UndertaleModTool
{
    /// <summary>
    /// Interaction logic for McpConfigView.xaml
    /// </summary>
    public partial class McpConfigView : UserControl
    {
        public McpConfigView()
        {
            InitializeComponent();
            DataContext = new McpConfigViewModel();
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is McpConfigViewModel vm)
                vm.Start();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is McpConfigViewModel vm)
                vm.Stop();
        }

        private void OpenStatus_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is McpConfigViewModel vm)
                MainWindow.OpenBrowser($"http://localhost:{vm.Port}/status");
        }
    }

    /// <summary>
    /// Converts an MCP status string into a colored brush (green = running, red = stopped/error).
    /// </summary>
    public class McpStatusBrushConverter : IValueConverter
    {
        public static readonly McpStatusBrushConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            string status = value as string ?? "";
            return status switch
            {
                "Running" => new SolidColorBrush(Color.FromRgb(46, 160, 67)),
                "Stopped" => new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                _ => new SolidColorBrush(Color.FromRgb(200, 120, 30))
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }
}