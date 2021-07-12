using System.Windows;
using Microsoft.Extensions.Configuration;

namespace Decrypt
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        IConfigurationRoot config = new ConfigurationBuilder().AddIniFile("config.ini").Build();

        public MainWindow()
        {
            InitializeComponent();
        }
    }
}
