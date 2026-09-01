namespace FileTransferApp
{
    using FileTransferApp.Services;

    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();
            Routing.RegisterRoute("chatpage", typeof(ChatPage));
            Routing.RegisterRoute("ChatPage", typeof(ChatPage));
            Routing.RegisterRoute("PrivacyPolicy", typeof(PrivacyPolicyPage));
            Routing.RegisterRoute("About", typeof(AboutPage));

            FlowDirection = LocalizationResourceManager.Instance.IsRtl
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;
        }
    }
}
