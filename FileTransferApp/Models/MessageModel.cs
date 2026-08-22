using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Maui.Controls;
using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace FileTransferApp.Models
{
    public enum TransferState
    {
        None,
        Queued,
        Sending,
        Receiving,
        Paused,
        Completed,
        Failed,
        Canceled
    }

    public partial class MessageModel : ObservableObject
    {
        [ObservableProperty] private string _text;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsImage))]
        [NotifyPropertyChangedFor(nameof(FileExtension))]
        private string _fileName;

        [ObservableProperty] private string _filePath;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsImage))]
        private bool _isFile;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsIncoming))]
        private bool _isMine;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TimestampDisplay))]
        private DateTime _timestamp;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FileSizeDisplay))]
        private long _fileSizeBytes;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TransferPercent))]
        [NotifyPropertyChangedFor(nameof(TransferProgressDisplay))]
        [NotifyPropertyChangedFor(nameof(SpeedDisplay))]
        [NotifyPropertyChangedFor(nameof(EtaDisplay))]
        private double _transferProgress;

        [ObservableProperty] private bool _isTransferring;
        [ObservableProperty] private bool _isPaused;
        [ObservableProperty] private ImageSource _imagePreview;
        [ObservableProperty] private bool _hasImagePreview;
        [ObservableProperty] private string _fileTypeIcon;

        public string SenderIP { get; set; }
        public string SenderName { get; set; }
        public string SenderDeviceId { get; set; }
        public string Id { get; set; }

        public CancellationTokenSource PauseCts { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SpeedDisplay))]
        [NotifyPropertyChangedFor(nameof(EtaDisplay))]
        [NotifyPropertyChangedFor(nameof(BytesTransferredDisplay))]
        private long _bytesTransferred;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SpeedDisplay))]
        [NotifyPropertyChangedFor(nameof(EtaDisplay))]
        private DateTime _transferStartTime;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SpeedDisplay))]
        [NotifyPropertyChangedFor(nameof(EtaDisplay))]
        private DateTime? _lastProgressTime;

        [ObservableProperty] private TransferState _state;
        [ObservableProperty] private bool _isFailed;
        [ObservableProperty] private string _errorMessage;

        public string TimestampDisplay => Timestamp.ToString("HH:mm");

        public string FileSizeDisplay
        {
            get
            {
                if (FileSizeBytes <= 0) return string.Empty;
                const double K = 1024d;
                double b = FileSizeBytes;
                if (b >= K * K * K) return string.Format(CultureInfo.CurrentCulture, "{0:0.##} GB", b / (K * K * K));
                if (b >= K * K) return string.Format(CultureInfo.CurrentCulture, "{0:0.##} MB", b / (K * K));
                if (b >= K) return string.Format(CultureInfo.CurrentCulture, "{0:0.##} KB", b / K);
                return string.Format(CultureInfo.CurrentCulture, "{0} Bytes", FileSizeBytes);
            }
        }

        public string BytesTransferredDisplay
        {
            get
            {
                if (FileSizeBytes <= 0) return string.Empty;
                return $"{FormatSize(BytesTransferred)} / {FileSizeDisplay}";
            }
        }

        public bool IsIncoming => !IsMine;

        public bool IsImage
        {
            get
            {
                if (!IsFile || string.IsNullOrEmpty(FileName)) return false;
                var ext = Path.GetExtension(FileName)?.ToLowerInvariant();
                return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".gif" || ext == ".bmp" || ext == ".webp" || ext == ".heic" || ext == ".heif";
            }
        }

        public string FileExtension => Path.GetExtension(FileName)?.ToLowerInvariant() ?? string.Empty;

        public int TransferPercent => (int)Math.Round(TransferProgress * 100.0);
        public string TransferProgressDisplay => $"{TransferPercent}%";

        public string SpeedDisplay
        {
            get
            {
                if (IsPaused) return "متوقف شده";
                var speed = AverageSpeedBytesPerSec();
                return speed.HasValue ? FormatSpeed(speed.Value) : string.Empty;
            }
        }

        public string EtaDisplay
        {
            get
            {
                if (IsPaused) return string.Empty;
                var speed = AverageSpeedBytesPerSec();
                if (!speed.HasValue || speed.Value <= 0 || FileSizeBytes <= 0) return string.Empty;
                long remaining = Math.Max(0, FileSizeBytes - BytesTransferred);
                if (remaining <= 0) return string.Empty;
                var seconds = remaining / speed.Value;
                if (seconds <= 0) return string.Empty;
                var ts = TimeSpan.FromSeconds(seconds);
                return ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
            }
        }

        public string TransferStatusText
        {
            get
            {
                return State switch
                {
                    TransferState.Sending => "در حال ارسال...",
                    TransferState.Receiving => "در حال دریافت...",
                    TransferState.Paused => "متوقف شده",
                    TransferState.Completed => "تکمیل شد",
                    TransferState.Failed => "ناموفق",
                    TransferState.Canceled => "لغو شده",
                    TransferState.Queued => "در صف...",
                    _ => string.Empty
                };
            }
        }

        public MessageModel()
        {
            Timestamp = DateTime.Now;
            TransferProgress = 0;
            IsTransferring = false;
            IsFile = false;
            IsMine = false;
            HasImagePreview = false;
            State = TransferState.None;
        }

        public void UpdateProgress(double progress)
        {
            TransferProgress = progress;
            IsTransferring = progress < 1.0;

            if (TransferStartTime == default)
                TransferStartTime = DateTime.UtcNow;

            LastProgressTime = DateTime.UtcNow;
            if (FileSizeBytes > 0)
                BytesTransferred = (long)(FileSizeBytes * Math.Clamp(progress, 0, 1));
        }

        public void MarkSending()
        {
            State = TransferState.Sending;
            IsTransferring = true;
            IsPaused = false;
            IsFailed = false;
            ErrorMessage = null;
            TransferStartTime = DateTime.UtcNow;
            LastProgressTime = null;
            BytesTransferred = 0;
            TransferProgress = 0;
        }

        public void MarkReceiving()
        {
            State = TransferState.Receiving;
            IsTransferring = true;
            IsPaused = false;
            IsFailed = false;
            ErrorMessage = null;
            TransferStartTime = DateTime.UtcNow;
            LastProgressTime = null;
            BytesTransferred = 0;
            TransferProgress = 0;
        }

        public void MarkPaused()
        {
            State = TransferState.Paused;
            IsTransferring = true;
            IsPaused = true;
        }

        public void MarkResumed()
        {
            State = TransferState.Sending;
            IsTransferring = true;
            IsPaused = false;
        }

        public void MarkCompleted()
        {
            State = TransferState.Completed;
            IsTransferring = false;
            IsPaused = false;
            UpdateProgress(1.0);
            BytesTransferred = FileSizeBytes;
            LastProgressTime = DateTime.UtcNow;
        }

        public void MarkFailed(string message = null)
        {
            State = TransferState.Failed;
            IsTransferring = false;
            IsPaused = false;
            IsFailed = true;
            ErrorMessage = message;
        }

        public void MarkCanceled()
        {
            State = TransferState.Canceled;
            IsTransferring = false;
            IsPaused = false;
        }

        private double? AverageSpeedBytesPerSec()
        {
            if (TransferStartTime == default) return null;
            var last = LastProgressTime ?? DateTime.UtcNow;
            var elapsedSec = (last - TransferStartTime).TotalSeconds;
            if (elapsedSec <= 0) return null;
            return BytesTransferred / elapsedSec;
        }

        private static string FormatSpeed(double bytesPerSec)
        {
            const double K = 1024d;
            if (bytesPerSec >= K * K * K) return $"{bytesPerSec / (K * K * K):0.##} GB/s";
            if (bytesPerSec >= K * K) return $"{bytesPerSec / (K * K):0.##} MB/s";
            if (bytesPerSec >= K) return $"{bytesPerSec / K:0.##} KB/s";
            return $"{bytesPerSec:0} B/s";
        }

        private static string FormatSize(long bytes)
        {
            const double K = 1024d;
            double b = bytes;
            if (b >= K * K * K) return $"{b / (K * K * K):0.##} GB";
            if (b >= K * K) return $"{b / (K * K):0.##} MB";
            if (b >= K) return $"{b / K:0.##} KB";
            return $"{bytes} B";
        }
    }
}