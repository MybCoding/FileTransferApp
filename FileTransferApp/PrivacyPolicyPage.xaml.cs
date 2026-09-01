using FileTransferApp.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace FileTransferApp
{
    public partial class PrivacyPolicyPage : ContentPage
    {
        public PrivacyPolicyPage()
        {
            InitializeComponent();
            LocalizationResourceManager.ApplyPageDirection(this);
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            LocalizationResourceManager.ApplyPageDirection(this);
        }

        private async void OnBackButtonClicked(object sender, EventArgs e)
        {
            try
            {
                await Navigation.PopAsync();
            }
            catch
            {
                await Shell.Current.GoToAsync("..");
            }
        }

        protected override bool OnBackButtonPressed()
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try { await Navigation.PopAsync(); }
                catch { await Shell.Current.GoToAsync(".."); }
            });
            return true;
        }

        private async void OnEmailTapped(object sender, EventArgs e)
        {
            try
            {
                await Launcher.Default.OpenAsync(new Uri("mailto:Mostafa.Yazdani65@gmail.com"));
            }
            catch
            {
                await DisplayAlert(
                    LocalizationResourceManager.T("Error"),
                    LocalizationResourceManager.T("EmailErrorBody"),
                    LocalizationResourceManager.T("OK"));
            }
        }
    }
}