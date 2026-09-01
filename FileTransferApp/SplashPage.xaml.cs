using FileTransferApp.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using System;
using System.Threading.Tasks;

namespace FileTransferApp
{
    public partial class SplashPage : ContentPage
    {
        public SplashPage()
        {
            InitializeComponent();
            LocalizationResourceManager.ApplyPageDirection(this);
            SplashVersionLabel.Text = LocalizationResourceManager.T("Version", AppInfo.Current.VersionString);
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            LocalizationResourceManager.ApplyPageDirection(this);
            await Task.Delay(2500);
            Application.Current.MainPage = new AppShell();
        }
    }
}