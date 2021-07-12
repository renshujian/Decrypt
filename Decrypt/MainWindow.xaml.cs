using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;

namespace Decrypt
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        static readonly IConfigurationRoot config = new ConfigurationBuilder().AddIniFile("config.ini").Build();
        readonly DecryptService decryptService = new(config.GetSection(nameof(DecryptService))[nameof(DecryptService.Endpoint)]);
        readonly DispatcherTimer decryptDelay = new();

        public MainWindow()
        {
            InitializeComponent();
            input.Focus();
            decryptDelay.Interval = System.TimeSpan.FromSeconds(0.3);
            decryptDelay.Tick += async (o, e) =>
            {
                decryptDelay.Stop();
                partID.Text = await decryptService.Decrypt(input.Text);
                input.Text = "";
            };
        }

        private void input_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (input.Text != "")
            {
                decryptDelay.Stop();
                decryptDelay.Start();
            }
        }
    }
}
