using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileTransferApp.Converters
{

    public class FirstLetterConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // انتظار داریم value یک string (نام دستگاه) باشد
            if (value is string name && !string.IsNullOrEmpty(name))
            {
                // اولین حرف را برگردانید و آن را به حروف بزرگ تبدیل کنید
                return name.Trim().ToUpper().Substring(0, 1);
            }

            // اگر نام خالی یا null بود، یک مقدار پیش فرض برگردانید (مثلاً یک حرف خاص یا فضای خالی)
            return "?"; // یا هر حرف پیش‌فرض دیگری که می‌خواهید نمایش دهید
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // نیازی به تبدیل برگشت در این سناریو نداریم
            throw new NotImplementedException();
        }
    }
}
