using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileTransferApp.Models;
using FileTransferApp.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace FileTransferApp.ViewModels
{
    public partial class ChatPageViewModel : ObservableObject, IDisposable
    {
        public ObservableCollection<MessageModel> Messages { get; } = new();

        [ObservableProperty] private string _pageTitle;
        [ObservableProperty] private bool _isBusy;

        public DeviceModel TargetDevice { get; private set; }
        private readonly Page _currentPage;
        private readonly ICameraCaptureService _cameraCaptureService;
        public ICommand ShowTextActionsCommand { get; }
        public ICommand SendCommand { get; }
        public ICommand PickFileCommand { get; }
        public ICommand CapturePhotoCommand { get; }
        public ICommand OpenFileCommand { get; }
        public ICommand CopyMessageTextCommand { get; }
        public ICommand SaveFileCommand { get; }
        public ICommand CancelAllTransfersCommand { get; }
        public ICommand PauseTransferCommand { get; }
        public ICommand ResumeTransferCommand { get; }
        public ICommand CancelTransferCommand { get; }

        private string _outgoingMessage;
        private CancellationTokenSource _typingCts;
        private readonly object _incomingTransfersLock = new();
        private readonly Dictionary<string, MessageModel> _incomingTransfersByTempPath = new(StringComparer.OrdinalIgnoreCase);

        public string OutgoingMessage
        {
            get => _outgoingMessage;
            set
            {
                if (SetProperty(ref _outgoingMessage, value))
                {
                    ((AsyncRelayCommand)SendCommand).NotifyCanExecuteChanged();
                    OnTyping();
                }
            }
        }

        private void OnTyping()
        {
            if (TargetDevice == null) return;
            try
            {
                _typingCts?.Cancel();
                _typingCts = new CancellationTokenSource();
                var token = _typingCts.Token;

                _ = Task.Run(async () =>
                {
                     // Send typing status immediately if not recently sent? 
                     // Or just debounce. Let's send "Typing..." and clear it later if no more typing.
                     // But we want to avoid spamming the network on every keystroke.
                     // Debounce sending:
                     await Task.Delay(300, token);
                     if (token.IsCancellationRequested) return;
                     
                     if (!string.IsNullOrEmpty(_outgoingMessage))
                        await Message_Service.SendStatusAsync(TargetDevice.IPAddress, "TYPING");
                     else
                        await Message_Service.SendStatusAsync(TargetDevice.IPAddress, null); // Clear status
                }, token);
            }
            catch { }
        }

        public event EventHandler ScrollToLastMessageRequested;

        private const int MAX_FILENAME_LENGTH = 100;
        private readonly SemaphoreSlim _sendSemaphore = new(2, 2);
        private CancellationTokenSource _operationsCts = new();
        private string _localDeviceId;

        private CancellationTokenSource _scrollDebounceCts;

        public ChatPageViewModel(DeviceModel targetDevice, Page currentPage, ICameraCaptureService? cameraCaptureService = null)
        {
            _cameraCaptureService = cameraCaptureService ?? new CameraCaptureService();
            ShowTextActionsCommand = new AsyncRelayCommand<MessageModel>(ExecuteShowTextActionsCommand);
            TargetDevice = targetDevice ?? throw new ArgumentNullException(nameof(targetDevice));
            _currentPage = currentPage ?? throw new ArgumentNullException(nameof(currentPage));

            PageTitle = TargetDevice.Name ?? LocalizationResourceManager.T("UnknownDevice");
            _localDeviceId = Preferences.Get("DeviceId", string.Empty);

            SendCommand = new AsyncRelayCommand(ExecuteSendCommand, CanExecuteSendCommand);
            PickFileCommand = new AsyncRelayCommand(ExecutePickFileCommand);
            CapturePhotoCommand = new AsyncRelayCommand(ExecuteCapturePhotoCommand);
            OpenFileCommand = new AsyncRelayCommand<MessageModel>(ExecuteOpenFileCommand);
            CopyMessageTextCommand = new AsyncRelayCommand<MessageModel>(ExecuteCopyMessageTextCommand);
            SaveFileCommand = new AsyncRelayCommand<MessageModel>(ExecuteSaveFileCommand);
            CancelAllTransfersCommand = new RelayCommand(ExecuteCancelAllTransfers);
            PauseTransferCommand = new AsyncRelayCommand<MessageModel>(ExecutePauseTransfer);
            ResumeTransferCommand = new AsyncRelayCommand<MessageModel>(ExecuteResumeTransfer);
            CancelTransferCommand = new AsyncRelayCommand<MessageModel>(ExecuteCancelTransfer);

            SubscribeToMessageServiceEvents();
        }

        private bool CanExecuteSendCommand() => !string.IsNullOrWhiteSpace(OutgoingMessage);
        private async Task ExecuteShowTextActionsCommand(MessageModel message)
        {
            try
            {
                if (message == null || message.IsFile || string.IsNullOrWhiteSpace(message.Text))
                    return;

                // ActionSheet ساده برای اندروید/iOS
                var choice = await _currentPage.DisplayActionSheet(
                    LocalizationResourceManager.T("Options"),
                    LocalizationResourceManager.T("Cancel"),
                    null,
                    LocalizationResourceManager.T("CopyText"));
                if (choice == LocalizationResourceManager.T("CopyText"))
                {
                    await ExecuteCopyMessageTextCommand(message);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VM] ShowTextActions error: {ex.Message}");
            }
        }
        private async Task ExecuteSendCommand()
        {
            Debug.WriteLine($"[VM] ExecuteSendCommand called. OutgoingMessage='{OutgoingMessage}', TargetDevice={TargetDevice?.Name}, TargetIP={TargetDevice?.IPAddress}");
            if (string.IsNullOrWhiteSpace(OutgoingMessage) || TargetDevice == null)
            {
                Debug.WriteLine($"[VM] ExecuteSendCommand: SKIPPED - OutgoingMessage empty={string.IsNullOrWhiteSpace(OutgoingMessage)}, TargetDevice null={TargetDevice == null}");
                return;
            }

            string messageText = OutgoingMessage.Trim();

            var messageToSend = new MessageModel
            {
                Id = Guid.NewGuid().ToString(),
                SenderIP = "Me",
                SenderName = DeviceInfo.Current.Name,
                Text = messageText,
                IsMine = true,
                IsFile = false,
                Timestamp = DateTime.Now
            };

            Debug.WriteLine($"[VM] ExecuteSendCommand: Adding message to collection. Text='{messageText}', IsMine={messageToSend.IsMine}, Messages.Count before={Messages.Count}");
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Messages.Add(messageToSend);
                Debug.WriteLine($"[VM] ExecuteSendCommand: Message added. Messages.Count now={Messages.Count}");
                RequestScrollDebounced();
            });

            IsBusy = true;
            Debug.WriteLine($"[VM] ExecuteSendCommand: Sending to {TargetDevice.IPAddress}...");
            bool success = await Message_Service.SendMessageAsync(
                TargetDevice.IPAddress,
                DeviceInfo.Current.Name,
                _localDeviceId,
                messageText,
                _operationsCts.Token);
            IsBusy = false;
            Debug.WriteLine($"[VM] ExecuteSendCommand: Send result={success}");

            if (success)
            {
                OutgoingMessage = string.Empty;
                RequestScrollDebounced();
            }
            else
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await _currentPage.DisplayAlert(LocalizationResourceManager.T("SendTextError"), LocalizationResourceManager.T("SendTextErrorBody"), LocalizationResourceManager.T("OK"));
                });
            }
        }

        private async Task ExecuteCapturePhotoCommand()
        {
            if (TargetDevice == null) return;

            try
            {
                FileResult photo;
                try
                {
                    photo = await _cameraCaptureService.CapturePhotoAsync();
                }
                catch (FeatureNotSupportedException)
                {
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        await _currentPage.DisplayAlert(LocalizationResourceManager.T("NoCamera"), LocalizationResourceManager.T("NoCameraBody"), LocalizationResourceManager.T("OK"));
                    });
                    return;
                }
                catch (PermissionException)
                {
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        await _currentPage.DisplayAlert(LocalizationResourceManager.T("CameraPermission"), LocalizationResourceManager.T("CameraPermissionBody"), LocalizationResourceManager.T("OK"));
                    });
                    return;
                }

                // کاربر عکس نگرفت / پنجره دوربین بسته شد
                if (photo == null) return;

                string fileName = photo.FileName;
                if (string.IsNullOrWhiteSpace(fileName) || photo.FullPath is not { Length: > 0 })
                {
                    fileName = $"photo_{Guid.NewGuid():N}.jpg";
                }

                if (fileName.Length > MAX_FILENAME_LENGTH)
                {
                    fileName = Path.GetExtension(fileName) is { Length: > 0 } ext
                        ? $"{fileName[..^ext.Length]}{ext}"
                        : fileName;
                    if (fileName.Length > MAX_FILENAME_LENGTH)
                        fileName = "photo_" + Guid.NewGuid().ToString("N") + Path.GetExtension(photo.FileName);
                }

                string localPath = await CopyFileResultToCacheAsync(
                    new FileResult(photo.FullPath, fileName),
                    _operationsCts.Token);

                var fi = new FileInfo(localPath);
                long fileSizeBytes = fi.Exists ? fi.Length : 0;

                var fileMessage = new MessageModel
                {
                    Id = Guid.NewGuid().ToString(),
                    SenderIP = "Me",
                    SenderName = DeviceInfo.Current.Name,
                    FileName = fileName,
                    FilePath = localPath,
                    IsMine = true,
                    IsFile = true,
                    Timestamp = DateTime.Now,
                    IsTransferring = true,
                    TransferProgress = 0,
                    FileSizeBytes = fileSizeBytes,
                    FileTypeIcon = GetFileIcon(localPath),
                    PauseCts = new CancellationTokenSource()
                };

                if (Message_Service.IsImageFile(localPath))
                {
                    var preview = Message_Service.GenerateImagePreviewFromFile(localPath);
                    if (preview != null)
                    {
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            fileMessage.ImagePreview = ImageSource.FromStream(() => new MemoryStream(preview));
                            fileMessage.HasImagePreview = true;
                        });
                    }
                }

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Messages.Add(fileMessage);
                    RequestScrollDebounced();
                });

                _ = Task.Run(async () => await SendSingleFileAsync(fileMessage, fileMessage.PauseCts.Token));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VM] CapturePhoto error: {ex.Message}");
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await _currentPage.DisplayAlert(LocalizationResourceManager.T("CameraError"), LocalizationResourceManager.T("CameraErrorBody", ex.Message), LocalizationResourceManager.T("OK"));
                });
            }
        }

        private async Task ExecutePickFileCommand()
        {
            try
            {
                var results = await FilePicker.PickMultipleAsync(PickOptions.Default);
                if (results == null || !results.Any()) return;

                foreach (var fileResult in results)
                {
                    if (fileResult.FileName?.Length > MAX_FILENAME_LENGTH)
                    {
                        await MainThread.InvokeOnMainThreadAsync(async () =>
                        {
                            await _currentPage.DisplayAlert(LocalizationResourceManager.T("LongFilename"), LocalizationResourceManager.T("LongFilenameBody", fileResult.FileName, MAX_FILENAME_LENGTH), LocalizationResourceManager.T("OK"));
                        });
                        continue;
                    }

                    string localPath = await CopyFileResultToCacheAsync(fileResult, _operationsCts.Token);
                    var fi = new FileInfo(localPath);
                    long fileSizeBytes = fi.Exists ? fi.Length : 0;

                    var fileMessage = new MessageModel
                    {
                        Id = Guid.NewGuid().ToString(),
                        SenderIP = "Me",
                        SenderName = DeviceInfo.Current.Name,
                        FileName = fileResult.FileName,
                        FilePath = localPath,
                        IsMine = true,
                        IsFile = true,
                        Timestamp = DateTime.Now,
                        IsTransferring = true,
                        TransferProgress = 0,
                        FileSizeBytes = fileSizeBytes,
                        FileTypeIcon = GetFileIcon(localPath),
                        PauseCts = new CancellationTokenSource()
                    };

                    if (Message_Service.IsImageFile(localPath))
                    {
                        var preview = Message_Service.GenerateImagePreviewFromFile(localPath);
                        if (preview != null)
                        {
                            MainThread.BeginInvokeOnMainThread(() =>
                            {
                                fileMessage.ImagePreview = ImageSource.FromStream(() => new MemoryStream(preview));
                                fileMessage.HasImagePreview = true;
                            });
                        }
                    }
                    else if (IsVideoFile(localPath))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var thumb = await VideoPreviewService.TryGenerateVideoThumbnailAsync(localPath);
                                if (thumb != null)
                                {
                                    MainThread.BeginInvokeOnMainThread(() =>
                                    {
                                        fileMessage.ImagePreview = ImageSource.FromStream(() => new MemoryStream(thumb));
                                        fileMessage.HasImagePreview = true;
                                    });
                                }
                            }
                            catch { }
                        });
                    }

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        Messages.Add(fileMessage);
                        RequestScrollDebounced();
                    });

                    _ = Task.Run(async () => await SendSingleFileAsync(fileMessage, fileMessage.PauseCts.Token));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await _currentPage.DisplayAlert(LocalizationResourceManager.T("SelectFileError"), LocalizationResourceManager.T("SelectFileErrorBody", ex.Message), LocalizationResourceManager.T("OK"));
                });
            }
        }

        public async Task SendSharedFilesAsync(IEnumerable<string> filePaths)
        {
            if (TargetDevice == null || filePaths == null) return;

            try
            {
                foreach (var srcPath in filePaths)
                {
                    if (string.IsNullOrWhiteSpace(srcPath) || !File.Exists(srcPath))
                        continue;

                    var fileName = Path.GetFileName(srcPath);
                    if (string.IsNullOrWhiteSpace(fileName))
                        fileName = "shared_file_" + Guid.NewGuid().ToString("N");

                    if (fileName.Length > MAX_FILENAME_LENGTH)
                    {
                        await MainThread.InvokeOnMainThreadAsync(async () =>
                        {
                            await _currentPage.DisplayAlert(LocalizationResourceManager.T("LongFilename"),
                                LocalizationResourceManager.T("LongFilenameBody", fileName, MAX_FILENAME_LENGTH),
                                LocalizationResourceManager.T("OK"));
                        });
                        continue;
                    }

                    var fi = new FileInfo(srcPath);
                    long fileSizeBytes = fi.Exists ? fi.Length : 0;

                    var fileMessage = new MessageModel
                    {
                        Id = Guid.NewGuid().ToString(),
                        SenderIP = "Me",
                        SenderName = DeviceInfo.Current.Name,
                        FileName = fileName,
                        FilePath = srcPath,
                        IsMine = true,
                        IsFile = true,
                        Timestamp = DateTime.Now,
                        IsTransferring = true,
                        TransferProgress = 0,
                        FileSizeBytes = fileSizeBytes,
                        FileTypeIcon = GetFileIcon(srcPath),
                        PauseCts = new CancellationTokenSource()
                    };

                    if (Message_Service.IsImageFile(srcPath))
                    {
                        var preview = Message_Service.GenerateImagePreviewFromFile(srcPath);
                        if (preview != null)
                        {
                            MainThread.BeginInvokeOnMainThread(() =>
                            {
                                fileMessage.ImagePreview = ImageSource.FromStream(() => new MemoryStream(preview));
                                fileMessage.HasImagePreview = true;
                            });
                        }
                    }
                    else if (IsVideoFile(srcPath))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var thumb = await VideoPreviewService.TryGenerateVideoThumbnailAsync(srcPath);
                                if (thumb != null)
                                {
                                    MainThread.BeginInvokeOnMainThread(() =>
                                    {
                                        fileMessage.ImagePreview = ImageSource.FromStream(() => new MemoryStream(thumb));
                                        fileMessage.HasImagePreview = true;
                                    });
                                }
                            }
                            catch { }
                        });
                    }

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        Messages.Add(fileMessage);
                        RequestScrollDebounced();
                    });

                    _ = Task.Run(async () => await SendSingleFileAsync(fileMessage, fileMessage.PauseCts.Token));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VM] SendSharedFiles error: {ex.Message}");
            }
        }

        private async Task SendSingleFileAsync(MessageModel message, CancellationToken ct)
        {
            await _sendSemaphore.WaitAsync(ct);
            try
            {
                if (string.IsNullOrEmpty(message?.FilePath) || !File.Exists(message.FilePath))
                {
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        await _currentPage.DisplayAlert(LocalizationResourceManager.T("Error"), LocalizationResourceManager.T("FilePathInvalid"), LocalizationResourceManager.T("OK"));
                    });
                    return;
                }

                message.MarkSending();

                using var stream = new FileStream(
                    message.FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 1024 * 1024,
                    useAsync: true);

                long offset = message.BytesTransferred;
                if (offset > 0 && offset < stream.Length)
                    stream.Seek(offset, SeekOrigin.Begin);
                else
                    offset = 0;

                var progress = new Progress<double>(p => UpdateFileProgress(message, p));

                IsBusy = true;
                _ = Message_Service.SendStatusAsync(TargetDevice.IPAddress, GetSendingStatus(message.FileName));

                bool success = await Message_Service.SendFileAsync(
                    TargetDevice.IPAddress,
                    DeviceInfo.Current.Name,
                    _localDeviceId,
                    message.FileName,
                    stream,
                    stream.Length,
                    progress,
                    ct);
                IsBusy = false;
                _ = Message_Service.SendStatusAsync(TargetDevice.IPAddress, null);

                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    if (message.State == TransferState.Paused) return;

                    if (success)
                        message.MarkCompleted();
                    else
                        message.TransferProgress = 0.0;

                    if (!success)
                    {
                        await _currentPage.DisplayAlert(LocalizationResourceManager.T("SendFileError"),
                            LocalizationResourceManager.T("SendFileErrorBody", message.FileName, TargetDevice?.Name ?? LocalizationResourceManager.T("UnknownDevice")),
                            LocalizationResourceManager.T("OK"));
                    }
                    RequestScrollDebounced();
                });
            }
            catch (OperationCanceledException)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (message.State != TransferState.Paused)
                        message.MarkCanceled();
                });
            }
            catch (Exception ex)
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    message.MarkFailed(ex.Message);
                    await _currentPage.DisplayAlert(LocalizationResourceManager.T("Error"), LocalizationResourceManager.T("SendFileGenericErrorBody", ex.Message), LocalizationResourceManager.T("OK"));
                });
            }
            finally
            {
                _sendSemaphore.Release();
            }
        }

        private void UpdateFileProgress(MessageModel message, double currentProgress)
        {
            if (message == null) return;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                message.TransferProgress = currentProgress;
                message.IsTransferring = currentProgress < 1.0;
            });
        }

        private async Task<string> CopyFileResultToCacheAsync(FileResult file, CancellationToken ct)
        {
            string safeName = Path.GetFileName(file.FileName) ?? $"file_{Guid.NewGuid():N}";
            string destPath = Path.Combine(FileSystem.CacheDirectory, $"send_{Guid.NewGuid():N}_{safeName}");

            await using var input = await file.OpenReadAsync();
            // Bigger async buffer speeds up copying large files from SAF/Picker streams.
            await using var output = new FileStream(
                destPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                useAsync: true);
            await input.CopyToAsync(output, 1024 * 1024, ct);
            return destPath;
        }

        private async Task ExecuteOpenFileCommand(MessageModel message)
        {
            if (message?.IsFile != true || string.IsNullOrEmpty(message.FilePath) || !File.Exists(message.FilePath))
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await _currentPage.DisplayAlert(LocalizationResourceManager.T("Error"), LocalizationResourceManager.T("FileNotAvailable"), LocalizationResourceManager.T("OK"));
                });
                return;
            }

            try
            {
#if ANDROID
                bool launched = await AndroidFileStorage.TryOpenWithProviderAsync(message.FilePath, GetMimeTypeLocal(message.FileName));
                if (!launched)
                {
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        await _currentPage.DisplayAlert(LocalizationResourceManager.T("OpenFileError"),
                            LocalizationResourceManager.T("OpenFileErrorBody", message.FileName),
                            LocalizationResourceManager.T("OK"));
                    });
                }
#else
                bool launched = await Launcher.OpenAsync(new OpenFileRequest { File = new ReadOnlyFile(message.FilePath) });
                if (!launched)
                {
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        await _currentPage.DisplayAlert(LocalizationResourceManager.T("OpenFileError"),
                            LocalizationResourceManager.T("OpenFileErrorBody", message.FileName),
                            LocalizationResourceManager.T("OK"));
                    });
                }
#endif
            }
            catch (Exception ex)
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await _currentPage.DisplayAlert(LocalizationResourceManager.T("OpenFileError"), ex.Message, LocalizationResourceManager.T("OK"));
                });
            }
        }

        private async Task ExecuteCopyMessageTextCommand(MessageModel message)
        {
            if (message == null || string.IsNullOrEmpty(message.Text)) return;
            try
            {
                await Clipboard.SetTextAsync(message.Text);
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await _currentPage.DisplayAlert(LocalizationResourceManager.T("CopyDone"), LocalizationResourceManager.T("CopyDoneBody"), LocalizationResourceManager.T("OK"));
                });
            }
            catch (Exception ex)
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await _currentPage.DisplayAlert(LocalizationResourceManager.T("CopyError"), ex.Message, LocalizationResourceManager.T("OK"));
                });
            }
        }

        private async Task ExecuteSaveFileCommand(MessageModel message)
        {
            if (message?.IsFile != true || message.IsMine || string.IsNullOrEmpty(message.FilePath) || !File.Exists(message.FilePath))
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await _currentPage.DisplayAlert(LocalizationResourceManager.T("Error"), LocalizationResourceManager.T("FileNotSavable"), LocalizationResourceManager.T("OK"));
                });
                return;
            }

            try
            {
                if (Application.Current is App appInstance)
                {
                    string finalSavedPath = await appInstance.SaveToPublicDestinationAsync(message.FilePath, message.FileName, message.SenderName);
                    if (string.IsNullOrEmpty(finalSavedPath))
                    {
                        await MainThread.InvokeOnMainThreadAsync(async () =>
                        {
                            await _currentPage.DisplayAlert(LocalizationResourceManager.T("SaveFileError"), LocalizationResourceManager.T("SaveFileErrorBody", message.FileName), LocalizationResourceManager.T("OK"));
                        });
                    }
                    else
                    {
                        await MainThread.InvokeOnMainThreadAsync(async () =>
                        {
                            await _currentPage.DisplayAlert(LocalizationResourceManager.T("FileSaved"), LocalizationResourceManager.T("FileSavedBody", finalSavedPath), LocalizationResourceManager.T("OK"));
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await _currentPage.DisplayAlert(LocalizationResourceManager.T("SaveFileError"), ex.Message, LocalizationResourceManager.T("OK"));
                });
            }
        }

        private void ExecuteCancelAllTransfers()
        {
            try
            {
                _operationsCts.Cancel();
                _operationsCts.Dispose();
                _operationsCts = new CancellationTokenSource();

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    foreach (var m in Messages.Where(m => m.IsFile && m.IsTransferring))
                        m.MarkCanceled();
                });
            }
            catch { }
        }

        private async Task ExecutePauseTransfer(MessageModel message)
        {
            if (message == null || !message.IsTransferring || message.IsPaused) return;

            try
            {
                message.PauseCts?.Cancel();
                message.MarkPaused();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VM] PauseTransfer error: {ex.Message}");
            }
        }

        private async Task ExecuteResumeTransfer(MessageModel message)
        {
            if (message == null || message.State != TransferState.Paused) return;
            if (string.IsNullOrEmpty(message.FilePath) || !File.Exists(message.FilePath)) return;

            try
            {
                var cts = new CancellationTokenSource();
                message.PauseCts = cts;
                message.MarkResumed();
                _ = Task.Run(async () => await SendSingleFileAsync(message, cts.Token));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VM] ResumeTransfer error: {ex.Message}");
            }
        }

        private async Task ExecuteCancelTransfer(MessageModel message)
        {
            if (message == null) return;

            try
            {
                message.PauseCts?.Cancel();
                message.PauseCts?.Dispose();
                message.PauseCts = null;
                message.MarkCanceled();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VM] CancelTransfer error: {ex.Message}");
            }
        }

        private void SubscribeToMessageServiceEvents()
        {
            Message_Service.TextMessageReceivedEx += HandleTextMessageReceivedEx;
            Message_Service.FileMessageReceivedEx += HandleFileMessageReceivedEx;
            Message_Service.StatusReceived += HandleStatusReceived;
            Message_Service.FileReceivingStartedEx += HandleFileReceivingStartedEx;
            Message_Service.FileReceivingProgressEx += HandleFileReceivingProgressEx;

            // legacy را عمداً subscribe نمی‌کنیم
            // Message_Service.TextMessageReceived += HandleTextMessageReceivedLegacy;
            // Message_Service.FileMessageReceived += HandleFileMessageReceivedLegacy;
        }

        private void UnsubscribeFromMessageServiceEvents()
        {
            Message_Service.TextMessageReceivedEx -= HandleTextMessageReceivedEx;
            Message_Service.FileMessageReceivedEx -= HandleFileMessageReceivedEx;
            Message_Service.StatusReceived -= HandleStatusReceived;
            Message_Service.FileReceivingStartedEx -= HandleFileReceivingStartedEx;
            Message_Service.FileReceivingProgressEx -= HandleFileReceivingProgressEx;

            // Message_Service.TextMessageReceived -= HandleTextMessageReceivedLegacy;
            // Message_Service.FileMessageReceived -= HandleFileMessageReceivedLegacy;
        }

        // جدید با DeviceId
        private void HandleTextMessageReceivedEx(string senderIp, string senderName, string senderDeviceId, string messageText)
        {
            try
            {
                if (!IsFromTarget(senderIp, senderDeviceId)) return;

                // اگر پیش‌تر اعتماد شده، بدون سؤال
                if (TrustService.IsTrusted(TargetDevice?.DeviceId))
                {
                    ProcessReceivedTextMessage(senderIp, senderName, messageText);
                    return;
                }

                // چت باز است؛ فقط اینجا یک‌بار سؤال می‌کنیم
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    var choice = await _currentPage.DisplayActionSheet(
                        LocalizationResourceManager.T("MessageFrom", senderName),
                        LocalizationResourceManager.T("Cancel"), null,
                        LocalizationResourceManager.T("TrustOnce"),
                        LocalizationResourceManager.T("TrustAlways"));

                    if (choice == LocalizationResourceManager.T("TrustOnce"))
                    {
                        TrustService.TrustOnce(TargetDevice.DeviceId);
                        ProcessReceivedTextMessage(senderIp, senderName, messageText);
                    }
                    else if (choice == LocalizationResourceManager.T("TrustAlways"))
                    {
                        TrustService.TrustAlways(TargetDevice.DeviceId);
                        ProcessReceivedTextMessage(senderIp, senderName, messageText);
                    }
                    // در غیر این صورت، نادیده بگیر
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VM] HandleTextMessageReceivedEx error: {ex.Message}");
            }
        }

        private void HandleFileMessageReceivedEx(string senderIp, string senderName, string senderDeviceId, string fileName, string tempFilePath, long fileSize)
        {
            try
            {
                if (!IsFromTarget(senderIp, senderDeviceId)) return;

                if (TrustService.IsTrusted(TargetDevice?.DeviceId))
                {
                    _ = ProcessReceivedFileMessageAsync(senderIp, senderName, fileName, tempFilePath);
                    return;
                }

                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    var choice = await _currentPage.DisplayActionSheet(
                        LocalizationResourceManager.T("SenderSentFile", senderName, fileName),
                        LocalizationResourceManager.T("Cancel"), null,
                        LocalizationResourceManager.T("TrustOnce"),
                        LocalizationResourceManager.T("TrustAlways"));

                    if (choice == LocalizationResourceManager.T("TrustOnce"))
                    {
                        TrustService.TrustOnce(TargetDevice.DeviceId);
                        await ProcessReceivedFileMessageAsync(senderIp, senderName, fileName, tempFilePath);
                    }
                    else if (choice == LocalizationResourceManager.T("TrustAlways"))
                    {
                        TrustService.TrustAlways(TargetDevice.DeviceId);
                        await ProcessReceivedFileMessageAsync(senderIp, senderName, fileName, tempFilePath);
                    }
                    else
                    {
                        try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VM] HandleFileMessageReceivedEx error: {ex.Message}");
            }
        }

        private CancellationTokenSource _statusClearCts;

        private void HandleStatusReceived(string ip, string deviceId, string status)
        {
            if (!IsFromTarget(ip, deviceId)) return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                TargetDevice.StatusMessage = LocalizationResourceManager.TR(status);

                // Auto clear status after 5 seconds if no update
                _statusClearCts?.Cancel();
                if (!string.IsNullOrEmpty(status))
                {
                    _statusClearCts = new CancellationTokenSource();
                    var token = _statusClearCts.Token;
                    Task.Delay(5000, token).ContinueWith(t =>
                    {
                        if (!t.IsCanceled && TargetDevice.StatusMessage == LocalizationResourceManager.TR(status))
                        {
                            MainThread.BeginInvokeOnMainThread(() => TargetDevice.StatusMessage = null);
                        }
                    });
                }
            });
        }

        private void HandleTextMessageReceivedLegacy(string senderIp, string senderName, string messageText)
        {
            try
            {
                if (TargetDevice == null || TargetDevice.IPAddress != senderIp) return;
                ProcessReceivedTextMessage(senderIp, senderName, messageText);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VM] HandleTextMessageReceivedLegacy error: {ex.Message}");
            }
        }

        private void HandleFileMessageReceivedLegacy(string ip, string senderName, string fileName, string tempFilePath)
        {
            try
            {
                if (TargetDevice == null || TargetDevice.IPAddress != ip) return;
                _ = ProcessReceivedFileMessageAsync(ip, senderName, fileName, tempFilePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VM] HandleFileMessageReceivedLegacy error: {ex.Message}");
            }
        }

        private void HandleFileReceivingStartedEx(string senderIp, string senderName, string senderDeviceId, string fileName, string tempPath, long fileSize)
        {
            try
            {
                if (!IsFromTarget(senderIp, senderDeviceId)) return;
                if (string.IsNullOrWhiteSpace(tempPath)) return;

                lock (_incomingTransfersLock)
                {
                    if (_incomingTransfersByTempPath.ContainsKey(tempPath))
                        return;

                    var msg = new MessageModel
                    {
                        Id = Guid.NewGuid().ToString(),
                        SenderIP = senderIp,
                        SenderName = senderName,
                        SenderDeviceId = senderDeviceId,
                        IsFile = true,
                        FileName = fileName,
                        FilePath = tempPath,
                        IsMine = false,
                        Timestamp = DateTime.Now,
                        IsTransferring = true,
                        TransferProgress = 0,
                        FileSizeBytes = fileSize,
                        FileTypeIcon = GetFileIcon(fileName)
                    };

                    msg.MarkReceiving();
                    _incomingTransfersByTempPath[tempPath] = msg;

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        Messages.Add(msg);
                        RequestScrollDebounced();
                    });
                }
            }
            catch { }
        }

        private void HandleFileReceivingProgressEx(string senderIp, string senderName, string senderDeviceId, string fileName, string tempPath, long bytesReceived, long totalBytes)
        {
            try
            {
                if (!IsFromTarget(senderIp, senderDeviceId)) return;
                if (string.IsNullOrWhiteSpace(tempPath)) return;
                if (totalBytes <= 0) return;

                MessageModel msg = null;
                lock (_incomingTransfersLock)
                {
                    _incomingTransfersByTempPath.TryGetValue(tempPath, out msg);
                }
                if (msg == null) return;

                var p = Math.Clamp((double)bytesReceived / totalBytes, 0, 1);
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    msg.FileSizeBytes = totalBytes;
                    msg.UpdateProgress(p);
                    msg.IsTransferring = p < 1.0;
                });
            }
            catch { }
        }

        private void ProcessReceivedTextMessage(string senderIp, string senderName, string messageText)
        {
            var receivedMessage = new MessageModel
            {
                Id = Guid.NewGuid().ToString(),
                SenderIP = senderIp,
                SenderName = senderName,
                Text = messageText,
                IsMine = false,
                IsFile = false,
                Timestamp = DateTime.Now
            };

            MainThread.BeginInvokeOnMainThread(() =>
            {
                Messages.Add(receivedMessage);
                RequestScrollDebounced();
            });
        }

        private async Task ProcessReceivedFileMessageAsync(string ip, string senderName, string fileName, string tempFilePath)
        {
            if (string.IsNullOrEmpty(tempFilePath) || !File.Exists(tempFilePath))
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await _currentPage.DisplayAlert(LocalizationResourceManager.T("Error"), LocalizationResourceManager.T("InvalidReceivedFile"), LocalizationResourceManager.T("OK"));
                });
                return;
            }

            string finalSavedPath = null;
            if (Application.Current is App appInstance)
                finalSavedPath = await appInstance.SaveReceivedFileAutomatically(tempFilePath, fileName, senderName);

            if (string.IsNullOrEmpty(finalSavedPath))
                finalSavedPath = tempFilePath;

            MessageModel receivedFileMessage = null;
            lock (_incomingTransfersLock)
            {
                _incomingTransfersByTempPath.TryGetValue(tempFilePath, out receivedFileMessage);
                if (receivedFileMessage != null)
                    _incomingTransfersByTempPath.Remove(tempFilePath);
            }

            if (receivedFileMessage == null)
            {
                receivedFileMessage = new MessageModel
                {
                    Id = Guid.NewGuid().ToString(),
                    SenderIP = ip,
                    SenderName = senderName,
                    IsFile = true,
                    FileName = fileName,
                    IsMine = false,
                    Timestamp = DateTime.Now,
                    TransferProgress = 1.0,
                    IsTransferring = false,
                    FileTypeIcon = GetFileIcon(fileName)
                };
            }

            receivedFileMessage.FileName = fileName;
            receivedFileMessage.FilePath = finalSavedPath;
            receivedFileMessage.IsTransferring = false;
            receivedFileMessage.TransferProgress = 1.0;
            receivedFileMessage.MarkCompleted();

            try { receivedFileMessage.FileSizeBytes = new FileInfo(finalSavedPath).Length; } catch { receivedFileMessage.FileSizeBytes = 0; }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (Message_Service.IsImageFile(finalSavedPath))
                    {
                        var preview = Message_Service.GenerateImagePreviewFromFile(finalSavedPath);
                        if (preview != null)
                        {
                            MainThread.BeginInvokeOnMainThread(() =>
                            {
                                receivedFileMessage.ImagePreview = ImageSource.FromStream(() => new MemoryStream(preview));
                                receivedFileMessage.HasImagePreview = true;
                            });
                        }
                    }
                    else if (IsVideoFile(finalSavedPath))
                    {
                        var thumb = await VideoPreviewService.TryGenerateVideoThumbnailAsync(finalSavedPath);
                        if (thumb != null)
                        {
                            MainThread.BeginInvokeOnMainThread(() =>
                            {
                                receivedFileMessage.ImagePreview = ImageSource.FromStream(() => new MemoryStream(thumb));
                                receivedFileMessage.HasImagePreview = true;
                            });
                        }
                    }
                }
                catch { }
            });

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!Messages.Contains(receivedFileMessage))
                    Messages.Add(receivedFileMessage);
                RequestScrollDebounced();
            });
        }

        public Task InjectReceivedTextAsync(string senderIp, string senderName, string messageText)
        {
            ProcessReceivedTextMessage(senderIp, senderName, messageText);
            return Task.CompletedTask;
        }

        public Task InjectReceivedFileAsync(string ip, string senderName, string fileName, string tempFilePath)
            => ProcessReceivedFileMessageAsync(ip, senderName, fileName, tempFilePath);

        private bool IsFromTarget(string senderIp, string senderDeviceId)
        {
            if (TargetDevice == null) return false;
            if (!string.IsNullOrWhiteSpace(TargetDevice.DeviceId) && !string.IsNullOrWhiteSpace(senderDeviceId))
                return string.Equals(TargetDevice.DeviceId, senderDeviceId, StringComparison.OrdinalIgnoreCase);
            return string.Equals(TargetDevice.IPAddress, senderIp, StringComparison.OrdinalIgnoreCase);
        }

        private string GetFileIcon(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return "📄";
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "🖼️",
                ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" => "🎥",
                ".mp3" or ".wav" or ".aac" or ".m4a" or ".flac" => "🎵",
                ".pdf" => "📕",
                ".doc" or ".docx" or ".txt" or ".rtf" => "📝",
                ".xls" or ".xlsx" => "📊",
                ".ppt" or ".pptx" => "📉",
                ".zip" or ".rar" or ".7z" => "📦",
                ".exe" or ".apk" or ".msi" => "💾",
                _ => "📄"
            };
        }

        private string GetSendingStatus(string fileName)
        {
            try
            {
                var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
                return ext switch
                {
                    ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "SENDING_IMAGE",
                    ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" => "SENDING_VIDEO",
                    _ => "SENDING_FILE"
                };
            }
            catch
            {
                return "SENDING_FILE";
            }
        }

        private static bool IsVideoFile(string fileNameOrPath)
        {
            if (string.IsNullOrWhiteSpace(fileNameOrPath)) return false;
            var ext = Path.GetExtension(fileNameOrPath)?.ToLowerInvariant();
            return ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv";
        }

        private static string GetMimeTypeLocal(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "application/octet-stream";
            var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".heic" or ".heif" => "image/heic",
                ".mp4" => "video/mp4",
                ".mov" => "video/quicktime",
                ".avi" => "video/x-msvideo",
                ".mkv" => "video/x-matroska",
                ".wmv" => "video/x-ms-wmv",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".aac" => "audio/aac",
                ".ogg" => "audio/ogg",
                ".pdf" => "application/pdf",
                ".txt" => "text/plain",
                ".zip" => "application/zip",
                ".rar" => "application/vnd.rar",
                _ => "application/octet-stream"
            };
        }

        private void RequestScrollDebounced(int delayMs = 120)
        {
            try
            {
                _scrollDebounceCts?.Cancel();
                _scrollDebounceCts = new CancellationTokenSource();
                var token = _scrollDebounceCts.Token;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(delayMs, token);
                        if (token.IsCancellationRequested) return;
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            ScrollToLastMessageRequested?.Invoke(this, EventArgs.Empty);
                        });
                    }
                    catch { }
                }, token);
            }
            catch { }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private bool _disposed;
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                UnsubscribeFromMessageServiceEvents();
                try { _operationsCts.Cancel(); _operationsCts.Dispose(); } catch { }
                try { _sendSemaphore.Dispose(); } catch { }
                try { _scrollDebounceCts?.Cancel(); _scrollDebounceCts?.Dispose(); } catch { }
            }
            _disposed = true;
        }
    }
}