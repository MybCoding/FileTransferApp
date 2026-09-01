using Foundation;
using UIKit;
using System.IO;
using System.Collections.Generic;

namespace FileTransferApp
{
    [Register("AppDelegate")]
    public class AppDelegate : MauiUIApplicationDelegate
    {
        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

        public override bool OpenUrl(UIApplication app, NSUrl url, NSDictionary options)
        {
            if (url != null && url.IsFileUrl)
            {
                string filePath = url.Path;
                if (File.Exists(filePath))
                {
                    // فایل‌ها در iOS معمولاً در پوشه Inbox برنامه کپی می‌شوند.
                    // ما آن‌ها را به App.HandleSharedFiles ارسال می‌کنیم.
                    App.HandleSharedFiles(new List<string> { filePath });
                    return true;
                }
            }
            return base.OpenUrl(app, url, options);
        }
    }
}
