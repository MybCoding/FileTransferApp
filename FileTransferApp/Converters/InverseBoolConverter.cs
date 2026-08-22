using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileTransferApp.Converters
{

    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool booleanValue)
            {
                // parameter="reverse" در XAML شما استفاده شده است که ممکن است
                // نشان‌دهنده منطق خاصی باشد. اگر صرفاً برعکس کردن bool مد نظر است:
                return !booleanValue;
                // اگر پارامتر برای کنترل رفتار است، منطق آن را اینجا اضافه کنید.
            }
            return value; // در غیر این صورت مقدار اصلی را برمی‌گرداند
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool booleanValue)
            {
                return !booleanValue;
            }
            return value;
        }
    }
}