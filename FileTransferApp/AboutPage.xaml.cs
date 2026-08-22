using System;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace FileTransferApp
{
    public partial class AboutPage : ContentPage
    {
        public AboutPage()
        {
            InitializeComponent();
            VersionLabel.Text = $"نسخه {AppInfo.Current.VersionString}";
        }

        private async void OnEmailTapped(object sender, EventArgs e)
        {
            try
            {
                await Launcher.Default.OpenAsync(new Uri("mailto:Mostafa.Yazdani65@gmail.com"));
            }
            catch (Exception)
            {
                await DisplayAlert("خطا", "امکان باز کردن برنامه ایمیل وجود ندارد.", "باشه");
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
