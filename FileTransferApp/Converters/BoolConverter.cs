using System;
using System.Globalization;
using Microsoft.Maui.Controls;

namespace FileTransferApp.Converters;

public class BoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            // Check if we need to reverse the result
            bool reverse = false;
            if (parameter is string paramString && 
                paramString.ToLowerInvariant() == "reverse")
            {
                reverse = true;
            }

            return reverse ? !boolValue : boolValue;
        }

        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            // Check if we need to reverse the result
            bool reverse = false;
            if (parameter is string paramString && 
                paramString.ToLowerInvariant() == "reverse")
            {
                reverse = true;
            }

            return reverse ? !boolValue : boolValue;
        }

        return value;
    }
} 