using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace FileTransferApp.Services
{
    /// <summary>
    /// دوزبانه‌سازی ساده برنامه (فارسی / انگلیسی) بدون نیاز به فایل‌های resx.
    /// از یک فرهنگ لغت کلید-مقدار استفاده می‌کند و با ایندکس‌ر خودش برای
    /// Binding در XAML (Source={x:Static ...Instance}) قابل استفاده است.
    /// </summary>
    public class LocalizationResourceManager : INotifyPropertyChanged
    {
        private const string PrefKey = "AppLanguage";

        public static LocalizationResourceManager Instance { get; } = new();

        private string _lang = "en";

        public LocalizationResourceManager()
        {
            var saved = Preferences.Get(PrefKey, string.Empty);
            if (string.Equals(saved, "fa", System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(saved, "en", System.StringComparison.OrdinalIgnoreCase))
            {
                _lang = saved.ToLowerInvariant();
            }
            else
            {
                var systemLang = CultureInfo.CurrentCulture?.TwoLetterISOLanguageName ?? "en";
                _lang = string.Equals(systemLang, "fa", System.StringComparison.OrdinalIgnoreCase) ? "fa" : "en";
            }
        }

        public string CurrentLanguage => _lang;

        public bool IsRtl => _lang == "fa";

        public string this[string key]
        {
            get
            {
                if (key == null) return null;
                if (Strings.TryGetValue(key, out var pair))
                    return _lang == "fa" ? pair.Fa : pair.En;
                return key;
            }
        }

        public string Get(string key, params object[] args)
        {
            var text = this[key];
            if (args == null || args.Length == 0) return text;
            try { return string.Format(CultureInfo.CurrentCulture, text, args); }
            catch { return text; }
        }

        public static string T(string key, params object[] args) => Instance.Get(key, args);

        /// <summary>
        /// جهت صفحه را بر اساس زبان فعال تنظیم می‌کند.
        /// در اندروید تنظیم FlowDirection در constructor اثر نمی‌کند (هندلر هنوز ساخته نشده)؛
        /// بنابراین باید در OnAppearing هم دوباره اعمال شود.
        /// </summary>
        public static void ApplyPageDirection(ContentPage page)
        {
            if (page == null) return;
            var dir = Instance.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            page.FlowDirection = dir;
            if (page.Content is VisualElement ve && ve.FlowDirection != dir)
                ve.FlowDirection = dir;
        }

        public void SetLanguage(string lang)
        {
            if (lang != "fa" && lang != "en") return;
            if (_lang == lang) return;
            _lang = lang;
            Preferences.Default.Set(PrefKey, lang);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        /// <summary>
        /// وضعیت ارسال‌شده روی شبکه (به‌صورت کلید) را به زبان محلی تبدیل می‌کند.
        /// اگر رشته شناخته‌شده نباشد، بدون تغییر برمی‌گردد (سازگاری با ورژن قدیمی).
        /// </summary>
        public string TranslateRemoteStatus(string rawStatus)
        {
            if (string.IsNullOrWhiteSpace(rawStatus)) return rawStatus;
            return this[rawStatus];
        }

        public static string TR(string rawStatus) => Instance.TranslateRemoteStatus(rawStatus);

        public event PropertyChangedEventHandler PropertyChanged;

        private static readonly Dictionary<string, (string En, string Fa)> Strings = new Dictionary<string, (string, string)>
        {
            // ============ عمومی ============
            ["OK"] = ("OK", "باشه"),
            ["Cancel"] = ("Cancel", "لغو"),
            ["Yes"] = ("Yes", "بله"),
            ["No"] = ("No", "خیر"),
            ["Error"] = ("Error", "خطا"),
            ["ChooseFolder"] = ("Choose folder", "انتخاب پوشه"),
            ["Later"] = ("Later", "بعداً"),
            ["Delete"] = ("Delete", "حذف"),
            ["Version"] = ("Version {0}", "نسخه {0}"),

            // ============ صفحه اصلی (MainPage) ============
            ["PageTitle"] = ("Device Sharing", "اشتراک‌گذاری دستگاه"),
            ["NearbyDevices"] = ("Nearby Devices", "دستگاه‌های نزدیک"),
            ["NearbyDevicesSubtitle"] = ("Tap a device to share files or messages", "برای اشتراک فایل یا پیام روی یک دستگاه بزنید"),
            ["IPLabel"] = ("IP:", "IP:"),
            ["NoDevicesFound"] = ("No devices found.", "دستگاهی پیدا نشد."),
            ["NoDevicesHint"] = ("Try refreshing or check your network.", "با تازه‌سازی مجدد تلاش کنید یا شبکه را بررسی کنید."),
            ["Connect"] = ("Connect", "اتصال"),
            ["Pair"] = ("Pair", "جفت‌سازی"),
            ["RefreshDevices"] = ("Refresh Devices", "تازه‌سازی دستگاه‌ها"),
            ["DownloadWindows"] = ("Download Windows version", "دانلود نسخه ویندوز"),
            ["AboutUs"] = ("About Us", "درباره ما"),
            ["LanStep1"] = ("1) Phone and computer must be on the same Wi-Fi.", "1) موبایل و کامپیوتر روی یک Wi-Fi باشند."),
            ["LanStep2"] = ("2) Tap Start to create a LAN address.", "2) دکمه Start را بزنید تا یک آدرس LAN ساخته شود."),
            ["LanStep3"] = ("3) On your PC, type this address into a browser.", "3) در ویندوز، همین آدرس را داخل مرورگر وارد کنید."),
            ["CopyUrl"] = ("Copy URL", "کپی آدرس"),
            ["LanNote"] = ("Note: If the page does not open, check AP Isolation on the router.", "Note: اگر صفحه باز نشد، AP Isolation روتر را بررسی کنید."),
            ["DeveloperLine"] = ("Developer: Mostafa Yazdani", "توسعه دهنده: مصطفی یزدانی"),
            ["PrivacyPolicy"] = ("Privacy Policy", "سیاست حریم خصوصی"),
            ["ReadyToDiscover"] = ("Ready to discover devices", "آماده شناسایی دستگاه‌ها"),
            ["StartLanLink"] = ("Start LAN link", "شروع لینک LAN"),
            ["StopLanLink"] = ("Stop LAN link", "توقف لینک LAN"),
            ["Refreshing"] = ("Refreshing device list...", "در حال تازه‌سازی فهرست دستگاه‌ها..."),
            ["Refreshed"] = ("Device list refreshed", "فهرست دستگاه‌ها تازه شد"),
            ["RefreshError"] = ("Error refreshing: {0}", "خطا در تازه‌سازی: {0}"),
            ["DeleteDeviceTitle"] = ("Delete device", "حذف دستگاه"),
            ["DeleteDeviceBody"] = ("Remove this device from the list?\n\n{0}\n{1}", "این دستگاه از فهرست حذف شود؟\n\n{0}\n{1}"),
            ["NavigationError"] = ("Navigation Error", "خطای ناوبری"),
            ["NavErrorBody"] = ("Failed to navigate to chat: {0}", "انتقال به صفحه چت انجام نشد: {0}"),
            ["PairingMissingIp"] = ("Device IP address is missing.", "آدرس IP دستگاه موجود نیست."),
            ["PairingWith"] = ("Pairing with {0}...", "در حال جفت‌سازی با {0}..."),
            ["PairingError"] = ("Pairing error: {0}", "خطای جفت‌سازی: {0}"),
            ["PairingFailed"] = ("Pairing with {0} failed.", "جفت‌سازی با {0} ناموفق بود."),
            ["ConfirmPairing"] = ("Confirm pairing", "تأیید جفت‌سازی"),
            ["ConfirmPairingBody"] = ("Device: {0} ({1})\n\nCompare the code shown here with the code on the other device.\n\nSAS: {2}\n\nDo the codes match?", "دستگاه: {0} ({1})\n\nکد نمایش‌داده‌شده را با کد دستگاه مقابل مقایسه کنید.\n\nSAS: {2}\n\nکدها مطابقت دارند؟"),
            ["TrustThisDevice"] = ("Yes, trust this device", "بله، به این دستگاه اعتماد کن"),
            ["Paired"] = ("Paired with {0}.", "با {0} جفت شد."),
            ["PairingFailedOrCancelled"] = ("Pairing with {0} failed or was cancelled.", "جفت‌سازی با {0} ناموفق بود یا لغو شد."),
            ["LanLinkReady"] = ("LAN link ready", "لینک LAN آماده است"),
            ["LanLinkReadyBody"] = ("On your PC, open a browser and enter:{0}\n\n{1}{2}", "در کامپیوتر، یک مرورگر باز کنید و وارد کنید:{0}\n\n{1}{2}"),
            ["DownloadNotConfigured"] = ("\n\n(Download link is not configured yet. The page will open but download may not start.)", "\n\n(لینک دانلود هنوز تنظیم نشده است. صفحه باز می‌شود اما ممکن است دانلود شروع نشود.)"),
            ["LanLinkError"] = ("LAN link error", "خطای لینک LAN"),
            ["Copied"] = ("Copied", "کپی شد"),
            ["AddressCopied"] = ("Address copied to clipboard.", "آدرس در حافظه کپی شد."),
            ["CopyError"] = ("Copy error", "خطا در کپی"),
            ["EmailErrorBody"] = ("Could not open the email app.", "امکان باز کردن برنامه ایمیل وجود ندارد."),

            // ============ صفحه چت (ChatPage) ============
            ["MessagePlaceholder"] = ("Write your message...", "پیام خود را بنویسید..."),
            ["CancelTransfer"] = ("✖ Cancel", "✖ لغو"),
            ["OpenFile"] = ("📂 Open", "📂 باز کردن"),
            ["SaveFile"] = ("💾 Save", "💾 ذخیره"),
            ["Options"] = ("Options", "گزینه‌ها"),
            ["CopyText"] = ("Copy text", "کپی متن"),
            ["SendTextError"] = ("Message send error", "خطا در ارسال"),
            ["SendTextErrorBody"] = ("Sending the text message failed.", "ارسال پیام متنی موفق نبود."),
            ["NoCamera"] = ("No camera found", "دوربین موجود نیست"),
            ["NoCameraBody"] = ("No camera was found on this device for taking photos.", "بر روی این دستگاه دوربینی برای عکس‌برداری پیدا نشد."),
            ["CameraPermission"] = ("Camera access", "دسترسی دوربین"),
            ["CameraPermissionBody"] = ("Camera access was not granted.", "دسترسی به دوربین داده نشد."),
            ["CameraError"] = ("Camera error", "خطا در دوربین"),
            ["CameraErrorBody"] = ("Could not take a photo: {0}", "امکان عکس‌برداری وجود ندارد: {0}"),
            ["LongFilename"] = ("File name too long", "نام فایل طولانی"),
            ["LongFilenameBody"] = ("The file name '{0}' is longer than {1} characters.", "نام فایل '{0}' بیش از {1} کاراکتر است."),
            ["SelectFileError"] = ("File selection error", "خطا در انتخاب فایل"),
            ["SelectFileErrorBody"] = ("Could not select the file: {0}", "امکان انتخاب فایل وجود ندارد: {0}"),
            ["FilePathInvalid"] = ("The file path is not valid.", "مسیر فایل معتبر نیست."),
            ["SendFileError"] = ("File send error", "خطا در ارسال فایل"),
            ["SendFileErrorBody"] = ("Sending file '{0}' to '{1}' failed.", "ارسال فایل '{0}' به '{1}' موفق نبود."),
            ["SendFileGenericErrorBody"] = ("Sending the file failed: {0}", "ارسال فایل با خطا مواجه شد: {0}"),
            ["FileNotAvailable"] = ("The file is not available for opening.", "فایل برای باز کردن در دسترس نیست."),
            ["OpenFileError"] = ("Open file error", "خطا در باز کردن فایل"),
            ["OpenFileErrorBody"] = ("Could not open the file '{0}'.", "امکان باز کردن فایل '{0}' وجود ندارد."),
            ["CopyDone"] = ("Copied", "کپی شد"),
            ["CopyDoneBody"] = ("Message text copied to the clipboard.", "متن پیام در حافظه کپی شد."),
            ["FileNotSavable"] = ("This file cannot be saved.", "این فایل قابل ذخیره نیست."),
            ["SaveFileError"] = ("Save file error", "خطا در ذخیره فایل"),
            ["SaveFileErrorBody"] = ("Saving file '{0}' failed.", "ذخیره فایل '{0}' ناموفق بود."),
            ["FileSaved"] = ("Saved", "ذخیره شد"),
            ["FileSavedBody"] = ("The file was saved to:\n{0}", "فایل در مسیر زیر ذخیره شد:\n{0}"),
            ["MessageFrom"] = ("Message from '{0}'", "پیام از '{0}'"),
            ["SenderSentFile"] = ("'{0}' sent a file ({1})", "'{0}' یک فایل ({1}) فرستاده"),
            ["TrustOnce"] = ("Just this once", "فقط این بار"),
            ["TrustAlways"] = ("Always trust", "همیشه اعتماد کن"),
            ["InvalidReceivedFile"] = ("The received file is invalid.", "فایل دریافتی نامعتبر است."),
            ["UnknownDevice"] = ("Unknown Device", "دستگاه ناشناخته"),

            // وضعیت‌های ردوبدل‌شده روی شبکه (کلید هستند)
            ["TYPING"] = ("typing...", "در حال نوشتن..."),
            ["SENDING_IMAGE"] = ("sending a photo...", "در حال ارسال عکس..."),
            ["SENDING_VIDEO"] = ("sending a video...", "در حال ارسال ویدیو..."),
            ["SENDING_FILE"] = ("sending a file...", "در حال ارسال فایل..."),
            ["RECEIVING_IMAGE"] = ("receiving a photo...", "در حال دریافت عکس..."),
            ["RECEIVING_VIDEO"] = ("receiving a video...", "در حال دریافت ویدیو..."),
            ["RECEIVING_FILE"] = ("receiving a file...", "در حال دریافت فایل..."),

            // ============ مدل پیام (MessageModel) ============
            ["UnitGB"] = ("{0:0.##} GB", "{0:0.##} گیگابایت"),
            ["UnitMB"] = ("{0:0.##} MB", "{0:0.##} مگابایت"),
            ["UnitKB"] = ("{0:0.##} KB", "{0:0.##} کیلوبایت"),
            ["UnitBytes"] = ("{0} Bytes", "{0} بایت"),
            ["SpeedGB"] = ("{0:0.##} GB/s", "{0:0.##} گیگابایت/ثانیه"),
            ["SpeedMB"] = ("{0:0.##} MB/s", "{0:0.##} مگابایت/ثانیه"),
            ["SpeedKB"] = ("{0:0.##} KB/s", "{0:0.##} کیلوبایت/ثانیه"),
            ["SpeedB"] = ("{0:0} B/s", "{0:0} بایت/ثانیه"),
            ["TransferStatusSending"] = ("Sending...", "در حال ارسال..."),
            ["TransferStatusReceiving"] = ("Receiving...", "در حال دریافت..."),
            ["TransferStatusPaused"] = ("Paused", "متوقف شده"),
            ["TransferStatusCompleted"] = ("Completed", "تکمیل شد"),
            ["TransferStatusFailed"] = ("Failed", "ناموفق"),
            ["TransferStatusCanceled"] = ("Canceled", "لغو شده"),
            ["TransferStatusQueued"] = ("Queued...", "در صف..."),

            // ============ مدل دستگاه (DeviceModel) ============
            ["Online"] = ("Online", "آنلاین"),
            ["JustNow"] = ("Just now", "اخیراً"),
            ["LastSeenMin"] = ("Last seen {0} min ago", "آخرین حضور: {0} دقیقه پیش"),
            ["LastSeenHour"] = ("Last seen {0} h ago", "آخرین حضور: {0} ساعت پیش"),
            ["LastSeenDay"] = ("Last seen {0} d ago", "آخرین حضور: {0} روز پیش"),

            // ============ گروه دستگاه ============
            ["Unknown"] = ("(unknown)", "(ناشناخته)"),

            // ============ App ============
            ["SharedFilesTitle"] = ("Incoming shared files", "دریافت فایل اشتراکی"),
            ["SharedFilesBody"] = ("Files were received for sending:\n{0}\n\nDo you want to choose a device to send them to?", "فایل‌های زیر برای ارسال دریافت شدند:\n{0}\n\nآیا می‌خواهید دستگاهی را برای ارسال انتخاب کنید؟"),
            ["UnknownSender"] = ("Unknown Sender", "فرستنده ناشناخته"),

            // ============ صفحه درباره ما ============
            ["AboutTitle"] = ("About Us", "درباره ما"),
            ["DeveloperLabel"] = ("Developer:", "توسعه دهنده:"),
            ["EmailLabel"] = ("Email:", "ایمیل:"),
            ["DesignedWith"] = ("Made with ❤️ and .NET MAUI", "طراحی شده با ❤️ و .NET MAUI"),

            // ============ صفحه حریم خصوصی ============
            ["PrivacyTitle"] = ("Privacy Policy", "حریم خصوصی"),
            ["Back"] = ("← Back", "➡ بازگشت"),
            ["LastUpdated"] = ("Last updated: August 2026", "آخرین به‌روزرسانی: مرداد ۱۴۰۵"),
            ["PrivacySection1Title"] = ("1. Information Collection", "۱. جمع‌آوری اطلاعات"),
            ["PrivacySection1Body"] = ("FileTransferApp does not send any personal information, user data, or your files to external servers. All data transfers are done directly, peer-to-peer, between your devices over your local network (Wi-Fi).", "اپلیکیشن FileTransferApp هیچ‌گونه اطلاعات شخصی، داده کاربری یا فایل‌های شما را به سرورهای خارجی ارسال نمی‌کند. تمامی انتقال داده‌ها به‌صورت مستقیم و نفر به نفر بین دستگاه‌های شما و از طریق شبکه محلی (Wi-Fi) انجام می‌شود."),
            ["PrivacySection2Title"] = ("2. Permissions", "۲. دسترسی‌ها"),
            ["PrivacySection2Body"] = ("This application uses the following permissions:\n\n• Internet access: to connect devices on the local network\n• Network and Wi-Fi status: to discover devices on the network\n• File storage: using the Android Storage Access Framework without needing extra permissions\n\nThis application does not access your camera, microphone, contacts, or location.", "این اپلیکیشن از مجوزهای زیر استفاده می‌کند:\n\n• دسترسی به اینترنت: برای برقراری ارتباط بین دستگاه‌ها در شبکه محلی\n• دسترسی به وضعیت شبکه و Wi-Fi: برای شناسایی دستگاه‌های موجود در شبکه\n• ذخیره فایل‌ها: با استفاده از سیستم انتخاب فایل اندروید (Storage Access Framework) و بدون نیاز به مجوزهای اضافی\n\nاین اپلیکیشن به دوربین، میکروفون، مخاطبین یا موقعیت مکانی شما دسترسی ندارد."),
            ["PrivacySection3Title"] = ("3. Data Storage", "۳. ذخیره‌سازی داده"),
            ["PrivacySection3Body"] = ("All transferred files are stored only on your device. No file or information is stored on our servers. The only information stored on the device includes:\n\n• Device pairing settings (locally)\n• The selected folder path for saving files\n• Default application settings", "تمامی فایل‌های انتقال‌یافته فقط در دستگاه شما ذخیره می‌شوند. هیچ فایل یا اطلاعاتی روی سرورهای ما ذخیره نمی‌شود. تنها اطلاعات ذخیره‌شده در دستگاه شامل موارد زیر است:\n\n• تنظیمات جفت‌شدن دستگاه‌ها (به‌صورت محلی)\n• مسیر انتخاب‌شده برای ذخیره فایل‌ها\n• تنظیمات پیش‌فرض اپلیکیشن"),
            ["PrivacySection4Title"] = ("4. Transfer Security", "۴. امنیت انتقال"),
            ["PrivacySection4Body"] = ("All data transfers are encrypted using AES-256-GCM. Communications only happen on your local network and no data passes over the internet.", "تمامی انتقال داده‌ها با استفاده از رمزنگاری AES-256-GCM انجام می‌شود. ارتباطات فقط در شبکه محلی شما صورت می‌گیرد و هیچ داده‌ای از اینترنت عبور نمی‌کند."),
            ["PrivacySection5Title"] = ("5. Children's Privacy", "۵. حریم خصوصی کودکان"),
            ["PrivacySection5Body"] = ("This application is not designed for children under 13 and does not collect children's personal information.", "این اپلیکیشن برای استفاده افراد زیر ۱۳ سال طراحی نشده است و اطلاعات شخصی کودکان را جمع‌آوری نمی‌کند."),
            ["PrivacySection6Title"] = ("6. Policy Changes", "۶. تغییرات سیاست"),
            ["PrivacySection6Body"] = ("Any changes to this policy will be announced through application updates. We recommend checking for the new version of the application.", "هرگونه تغییر در این سیاست از طریق به‌روزرسانی اپلیکیشن اطلاع‌رسانی خواهد شد. توصیه می‌شود نسخه جدید اپلیکیشن را بررسی کنید."),
            ["PrivacySection7Title"] = ("7. Contact Us", "۷. تماس با ما"),
            ["PrivacySection7Body"] = ("If you have any questions or concerns about your privacy, please contact us via the following email:", "در صورت داشتن هرگونه سؤال یا نگرانی درباره حریم خصوصی خود، لطفاً با ما از طریق ایمیل زیر تماس بگیرید:"),
            ["FooterCopyright"] = ("© 2026 FileTransferApp. All rights reserved.", "© 2026 FileTransferApp. تمامی حقوق محفوظ است."),

            // ============ صفحه اسپلش ============
            ["SplashTagline"] = ("Secure & fast file sharing", "ارسال فایل امن و سریع"),

            // ============ اپ‌شل ============
            ["DevicesTab"] = ("Devices", "دستگاه‌ها"),

            // ============ انتخاب زبان ============
            ["Language"] = ("Language", "زبان"),
            ["English"] = ("English", "انگلیسی"),
            ["Persian"] = ("فارسی", "فارسی"),

            // ============ اندروید: ذخیره‌سازی ============
            ["FolderPickerTitle"] = ("Choose document storage folder", "تنظیم پوشه ذخیره اسناد"),
            ["FolderPickerBody"] = ("To automatically save documents and archives, choose a destination folder. This only needs to be done once.", "برای ذخیره خودکار اسناد و فایل‌های فشرده، یک پوشه مقصد انتخاب کنید. این انتخاب فقط یک بار لازم است."),

            // ============ اندروید: سرویس پس‌زمینه ============
            ["ChannelName"] = ("Device Discovery", "شناسایی دستگاه"),
            ["ChannelDesc"] = ("Running in background to discover nearby devices", "برای شناسایی دستگاه‌های نزدیک در پس‌زمینه در حال اجراست"),
            ["SearchingStatus"] = ("Searching for nearby devices...", "در حال جستجوی دستگاه‌های نزدیک..."),
            ["StopAction"] = ("Stop", "توقف"),

            // ============ ویندوز: SendTo ============
            ["SendToDescription"] = ("Send file to another device via FileTransferApp", "ارسال فایل به دستگاه دیگر از طریق FileTransferApp"),
        };
    }
}