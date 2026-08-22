using System.Collections.ObjectModel;

namespace FileTransferApp.Models
{
    public sealed class DeviceGroup : ObservableCollection<DeviceModel>
    {
        public string IPAddress { get; }

        public DeviceGroup(string ipAddress)
        {
            IPAddress = string.IsNullOrWhiteSpace(ipAddress) ? "(unknown)" : ipAddress;
        }

        public string Title => IPAddress;
    }
}



