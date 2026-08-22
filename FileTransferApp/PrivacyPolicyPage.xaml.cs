using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace FileTransferApp
{
    public partial class PrivacyPolicyPage : ContentPage
    {
        public PrivacyPolicyPage()
        {
            InitializeComponent();
        }

        private async void OnEmailTapped(object sender, EventArgs e)
        {
            try
            {
                await Launcher.Default.OpenAsync(new Uri("mailto:Mostafa.Yazdani65@gmail.com"));
            }
            catch
            {
                await DisplayAlert("خطا", "امکان باز کردن برنامه ایمیل وجود ندارد.", "باشه");
            }
        }
    }
}
