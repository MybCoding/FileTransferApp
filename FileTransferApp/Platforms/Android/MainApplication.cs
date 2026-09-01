using Android.App;
using Android.Runtime;

namespace FileTransferApp;

[Application]
public class MainApplication : MauiApplication
{
    public MainApplication(System.IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}