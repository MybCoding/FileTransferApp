using System.Collections.ObjectModel;
using FileTransferApp.Services;

namespace FileTransferApp.Models
{
    public sealed class DeviceGroup : ObservableCollection<DeviceModel>
    {
        public string IPAddress { get; }

        public DeviceGroup(string ipAddress)
        {
            IPAddress = string.IsNullOrWhiteSpace(ipAddress)
                ? LocalizationResourceManager.T("Unknown")
                : ipAddress;
        }

        public string Title => IPAddress;
    }
}



