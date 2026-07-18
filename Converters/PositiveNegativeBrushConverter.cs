using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace 币安量化机器人.Converters;

public class PositiveNegativeBrushConverter : IValueConverter
{
    public Brush PositiveBrush { get; set; } = new SolidColorBrush(Color.FromRgb(34, 197, 94));
    public Brush NegativeBrush { get; set; } = new SolidColorBrush(Color.FromRgb(239, 68, 68));
    public Brush NeutralBrush { get; set; } = new SolidColorBrush(Color.FromRgb(107, 114, 128));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double number)
        {
            if (number > 0.0000001)
                return PositiveBrush;
            if (number < -0.0000001)
                return NegativeBrush;
        }

        return NeutralBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
