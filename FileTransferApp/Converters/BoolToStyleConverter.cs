using System;
using System.Globalization;
using Microsoft.Maui.Controls;

namespace FileTransferApp.Converters;

public class BoolToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isMine = (bool)value;
        return isMine ? Color.FromArgb("#DCF8C6") : Color.FromArgb("#FFFFFF");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
