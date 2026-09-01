using System;
using System.Globalization;
using Microsoft.Maui.Controls;

namespace FileTransferApp.Converters;

public class BoolToAlignConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isMine)
        {
            // اگر پیام مال من است (IsMine == true)، به سمت راست برود (End)
            // اگر پیام مال طرف مقابل است (IsMine == false)، به سمت چپ برود (Start)
            return isMine ? LayoutOptions.End : LayoutOptions.Start;
        }
        return LayoutOptions.Start; // مقدار پیش‌فرض
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
