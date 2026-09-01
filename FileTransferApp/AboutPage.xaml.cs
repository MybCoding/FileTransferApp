using System;
using FileTransferApp.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace FileTransferApp
{
    public partial class AboutPage : ContentPage
    {
        public AboutPage()
        {
            InitializeComponent();
            LocalizationResourceManager.ApplyPageDirection(this);
            VersionLabel.Text = LocalizationResourceManager.T("Version", AppInfo.Current.VersionString);
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            LocalizationResourceManager.ApplyPageDirection(this);
        }

        private async void OnEmailTapped(object sender, EventArgs e)
        {
            try
            {
                await Launcher.Default.OpenAsync(new Uri("mailto:Mostafa.Yazdani65@gmail.com"));
            }
            catch (Exception)
            {
                await DisplayAlert(
                    LocalizationResourceManager.T("Error"),
                    LocalizationResourceManager.T("EmailErrorBody"),
                    LocalizationResourceManager.T("OK"));
            }
        }

        private async void OnPrivacyPolicyTapped(object sender, EventArgs e)
        {
            try
            {
                await Shell.Current.GoToAsync("PrivacyPolicy");
            }
            catch
            {
                await Navigation.PushAsync(new PrivacyPolicyPage());
            }
        }
    }
}