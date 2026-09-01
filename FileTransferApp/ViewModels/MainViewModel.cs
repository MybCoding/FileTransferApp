using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using FileTransferApp.Models;

namespace FileTransferApp.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<ChatMessage> Messages { get; set; } = new();
        public string OutgoingMessage { get; set; }

        public ICommand SendMessageCommand { get; }
        public ICommand SelectFileCommand { get; }

        public string DeviceName { get; set; } = "Milo";
        public string DeviceStatus { get; set; } = "Online";

        public MainViewModel()
        {
            SendMessageCommand = new Command(SendMessage);
            SelectFileCommand = new Command(SelectFile);
        }

        private void SendMessage()
        {
            if (!string.IsNullOrWhiteSpace(OutgoingMessage))
            {
                Messages.Add(new ChatMessage { Content = OutgoingMessage, IsIncoming = false });
                OutgoingMessage = string.Empty;
                OnPropertyChanged(nameof(OutgoingMessage));

                // Send over socket here...
            }
        }

        private void SelectFile()
        {
            // فایل‌ انتخاب شود و ارسال شود
        }

        public event PropertyChangedEventHandler PropertyChanged;
        void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

