namespace FileTransferApp
{

    public partial class AppShell : Shell
    {
        
        public AppShell()
        {
            InitializeComponent();
            Routing.RegisterRoute("chatpage", typeof(ChatPage));
            Routing.RegisterRoute("ChatPage", typeof(ChatPage));
            Routing.RegisterRoute("PrivacyPolicy", typeof(PrivacyPolicyPage));
            Routing.RegisterRoute("About", typeof(AboutPage));
        }
    }
}
