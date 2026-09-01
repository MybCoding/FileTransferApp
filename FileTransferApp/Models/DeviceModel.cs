using FileTransferApp.Services;
using Microsoft.Maui.Graphics;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace FileTransferApp.Models
{
    public class DeviceModel : INotifyPropertyChanged
    {
        private string _avatarSource;
        private string _name = string.Empty;
        private string _ipAddress = string.Empty;
        private string _os = string.Empty;
        private bool _isOnline = true;
        private string _statusMessage;
        private DateTime _lastSeen = DateTime.Now;
        private string _deviceId;

        public string DeviceId
        {
            get => _deviceId;
            set
            {
                if (_deviceId == value) return;
                _deviceId = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPaired));
                OnPropertyChanged(nameof(ShowPairButton));
            }
        }

        /// <summary>True when this device has already been paired (trusted) and the trust is persisted.</summary>
        public bool IsPaired => !string.IsNullOrEmpty(DeviceId) && TrustService.IsTrusted(DeviceId);

        /// <summary>Inverse of <see cref="IsPaired"/> — used to hide the Pair button.</summary>
        public bool ShowPairButton => !IsPaired;

        public void RefreshPairState()
        {
            OnPropertyChanged(nameof(IsPaired));
            OnPropertyChanged(nameof(ShowPairButton));
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                if (_statusMessage == value) return;
                _statusMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }

        public string Name
        {
            get => _name;
            set { if (_name == value) return; _name = value; OnPropertyChanged(); }
        }

        public string IPAddress
        {
            get => _ipAddress;
            set { if (_ipAddress == value) return; _ipAddress = value; OnPropertyChanged(); }
        }

        public string OS
        {
            get => _os;
            set
            {
                if (_os == value) return;
                _os = value;
                OnPropertyChanged();
                _ = LoadAvatarAsync();
            }
        }

        public bool IsOnline
        {
            get => _isOnline;
            set
            {
                if (_isOnline == value) return;
                _isOnline = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(StatusColor));
            }
        }

        public DateTime LastSeen
        {
            get => _lastSeen;
            set
            {
                if (_lastSeen == value) return;
                _lastSeen = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Status));
            }
        }

        public string StatusDisplay
        {
            get
            {
                if (!string.IsNullOrEmpty(StatusMessage)) return StatusMessage;
                if (IsOnline) return LocalizationResourceManager.T("Online");
                var delta = DateTime.Now - LastSeen;
                if (delta.TotalSeconds < 60) return LocalizationResourceManager.T("JustNow");
                if (delta.TotalMinutes < 60) return LocalizationResourceManager.T("LastSeenMin", Math.Floor(delta.TotalMinutes));
                if (delta.TotalHours < 24) return LocalizationResourceManager.T("LastSeenHour", Math.Floor(delta.TotalHours));
                return LocalizationResourceManager.T("LastSeenDay", Math.Floor(delta.TotalDays));
            }
        }

        public string Status => StatusDisplay; // Backward compatibility

        public Color StatusColor => IsOnline ? Colors.Green : Colors.Gray;

        public string AvatarSource
        {
            get
            {
                if (string.IsNullOrEmpty(_avatarSource))
                {
                    _ = LoadAvatarAsync();
                    return "device_icon.png";
                }
                return _avatarSource;
            }
        }

        public string InitialLetter => string.IsNullOrEmpty(Name) ? "?" : Name.Trim()[0].ToString().ToUpper();

        private async Task LoadAvatarAsync()
        {
            try
            {
                _avatarSource = await IconService.GetIconForOSAsync(OS);
                OnPropertyChanged(nameof(AvatarSource));
            }
            catch
            {
                _avatarSource = "device_icon.png";
                OnPropertyChanged(nameof(AvatarSource));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public override bool Equals(object obj)
        {
            if (obj is not DeviceModel other) return false;

            if (!string.IsNullOrEmpty(DeviceId) && !string.IsNullOrEmpty(other.DeviceId))
                return DeviceId == other.DeviceId;

            return IPAddress == other.IPAddress;
        }

        public override int GetHashCode()
        {
            if (!string.IsNullOrEmpty(DeviceId)) return DeviceId.GetHashCode();
            return IPAddress?.GetHashCode() ?? 0;
        }
    }
}