using FileTransferApp.Models;
using FileTransferApp.ViewModels;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using System;
using System.Diagnostics;
using System.Linq;

namespace FileTransferApp
{
    [QueryProperty(nameof(TargetIP), "ip")]
    public partial class ChatPage : ContentPage, IDisposable
    {
        public string TargetIP { get; set; }

        private ChatPageViewModel ViewModel => BindingContext as ChatPageViewModel;
        private CollectionView _messagesCollection;

        public ChatPage()
        {
            InitializeComponent();
            _messagesCollection = this.FindByName<CollectionView>("MessagesCollection");
        }

        public ChatPage(DeviceModel targetDevice)
        {
            InitializeComponent();
            _messagesCollection = this.FindByName<CollectionView>("MessagesCollection");

            if (targetDevice != null)
            {
                var vm = new ChatPageViewModel(targetDevice, this);
                BindingContext = vm;
                HookViewModel(vm);
            }
        }

        protected override void OnNavigatedTo(NavigatedToEventArgs args)
        {
            base.OnNavigatedTo(args);
            if (!string.IsNullOrWhiteSpace(TargetIP) && BindingContext == null)
            {
                var app = Application.Current as App;
                var device = app?.FindOrCreateDevice(TargetIP, null);
                if (device != null)
                {
                    var vm = new ChatPageViewModel(device, this);
                    BindingContext = vm;
                    HookViewModel(vm);
                }
            }
        }

        private void HookViewModel(ChatPageViewModel vm)
        {
            vm.ScrollToLastMessageRequested += ViewModel_ScrollToLastMessageRequested;

            if (vm.Messages is System.Collections.Specialized.INotifyCollectionChanged obs)
            {
                obs.CollectionChanged += Messages_CollectionChanged;
            }
        }

        private void UnhookViewModel(ChatPageViewModel vm)
        {
            if (vm == null) return;

            vm.ScrollToLastMessageRequested -= ViewModel_ScrollToLastMessageRequested;

            if (vm.Messages is System.Collections.Specialized.INotifyCollectionChanged obs)
            {
                obs.CollectionChanged -= Messages_CollectionChanged;
            }
        }

        private void Messages_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            Debug.WriteLine($"[ChatPage] Messages changed: {e.Action}, count={ViewModel?.Messages?.Count}");
        }

        private void ViewModel_ScrollToLastMessageRequested(object sender, EventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(ScrollToLatestMessage);
        }

        public void ScrollToLatestMessage()
        {
            try
            {
                if (_messagesCollection == null || ViewModel?.Messages == null || ViewModel.Messages.Count == 0)
                    return;

                var lastItem = ViewModel.Messages.LastOrDefault();
                if (lastItem != null)
                    _messagesCollection.ScrollTo(lastItem, position: ScrollToPosition.End, animate: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatPage] Scroll error: {ex.Message}");
            }
        }

        private async void OnBackButtonClicked(object sender, EventArgs e)
        {
            try
            {
                if (Shell.Current?.Navigation?.NavigationStack?.Count > 1)
                    await Shell.Current.GoToAsync("..");
                else
                    await Navigation.PopAsync();
            }
            catch
            {
                await Navigation.PopAsync();
            }
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            var vm = ViewModel;
            UnhookViewModel(vm);
            if (vm is IDisposable d) d.Dispose();
        }

        public void Dispose()
        {
            var vm = ViewModel;
            UnhookViewModel(vm);
            if (vm is IDisposable d) d.Dispose();
        }
    }
}