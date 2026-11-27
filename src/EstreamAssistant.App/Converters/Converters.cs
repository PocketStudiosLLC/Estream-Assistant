using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using EstreamAssistant.App.ViewModels;

namespace EstreamAssistant.App.Converters;

/// <summary>Status light colour.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly Brush Ok = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x4F)));
    private static readonly Brush Warn = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x9B, 0x1A)));
    private static readonly Brush Bad = Freeze(new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)));
    private static readonly Brush Unknown = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        StatusLevel.Ok => Ok,
        StatusLevel.Warn => Warn,
        StatusLevel.Bad => Bad,
        _ => Unknown,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value switch { bool x => x, null => false, int i => i != 0, string s => s.Length > 0, _ => true };
        return b ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Reports > Colors: [0] a stock count, [1] whether Colors is on. Red below 0, yellow under 4,
/// green 4 and up; no colour when Colors is off or the cell is empty.
/// </summary>
public sealed class StockCountBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[1] is not true) return DependencyProperty.UnsetValue;
        decimal? count = values[0] switch
        {
            decimal d => d,
            int i => i,
            long l => l,
            double x => (decimal)x,
            _ => null,
        };
        if (count is null) return DependencyProperty.UnsetValue;
        var key = Core.Reports.StockColors.For(count.Value) switch
        {
            Core.Reports.StockColor.Negative => "StockNegativeBg",
            Core.Reports.StockColor.Low => "StockLowBg",
            _ => "StockOkBg",
        };
        return Application.Current?.TryFindResource(key) ?? DependencyProperty.UnsetValue;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Pending tab pictures: [0] the scroll viewer's width, [1] the zoom (1 = fit the width).</summary>
public sealed class ZoomWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [double width, double zoom] && width > 20 ? Math.Max(20, (width - 4) * Math.Max(1, zoom)) : double.NaN;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Scrolls an element into view when the bound value turns true (the row being asked about).</summary>
public static class BringIntoView
{
    public static readonly DependencyProperty WhenProperty = DependencyProperty.RegisterAttached(
        "When", typeof(bool), typeof(BringIntoView), new PropertyMetadata(false, (d, e) =>
        {
            if (e.NewValue is not true || d is not FrameworkElement fe) return;
            void Show() => fe.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                () => fe.BringIntoView(new Rect(0, -40, fe.ActualWidth, fe.ActualHeight + 80)));
            if (fe.IsLoaded) { Show(); return; }
            void OnLoaded(object? s, RoutedEventArgs a) { fe.Loaded -= OnLoaded; Show(); }
            fe.Loaded += OnLoaded;
        }));

    public static bool GetWhen(DependencyObject d) => (bool)d.GetValue(WhenProperty);
    public static void SetWhen(DependencyObject d, bool value) => d.SetValue(WhenProperty, value);
}
