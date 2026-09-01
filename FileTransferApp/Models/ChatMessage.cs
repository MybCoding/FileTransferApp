using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileTransferApp.Models
{

    public class ChatMessage
    {
        public string SenderIP { get; set; }
        public string ReceiverIP { get; set; }
        public string Text { get; set; } // در صورت پیام متنی
        public string FilePath { get; set; } // در صورت پیام فایلی
        public DateTime Timestamp { get; set; } = DateTime.Now;

        public bool IsIncoming { get; set; } // مشخص می‌کنه پیام دریافتیه یا ارسالی
        public bool HasFile => !string.IsNullOrEmpty(FilePath);

        public string Content { get; internal set; }
    }
}