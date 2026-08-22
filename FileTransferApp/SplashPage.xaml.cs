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
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await Task.Delay(2500);
            Application.Current.MainPage = new AppShell();
        }
    }
}
