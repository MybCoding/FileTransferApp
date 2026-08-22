// در فایل MauiProgram.cs

using FileTransferApp.Services;
using CommunityToolkit.Maui;
using Microsoft.Maui.LifecycleEvents; // اضافه شدن برای رویدادها

#if WINDOWS
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;
#endif

namespace FileTransferApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            })
            .ConfigureLifecycleEvents(events =>
            {
#if WINDOWS
                events.AddWindows(wndLifeCycleBuilder =>
                {
                    wndLifeCycleBuilder.OnWindowCreated(window =>
                    {
                        // اینجا می‌توان تنظیمات دیگر پنجره را انجام داد
                        // اما تنظیم FlowDirection روی خود Window در WinUI 3 مستقیماً پشتیبانی نمی‌شود
                        // و باعث خطای کامپایل می‌شود.
                    });
                });
#endif
            });

        return builder.Build();
    }
}