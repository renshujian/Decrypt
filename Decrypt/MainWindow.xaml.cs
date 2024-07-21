using FluentModbus;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Decrypt
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        static readonly IConfigurationRoot config = new ConfigurationBuilder().AddIniFile("config.ini").Build();
        readonly DispatcherTimer decryptDelay = new();
        readonly FileSystemWatcher fileSystemWatcher = new(config.GetSection("FileWatcher")["Directory"], "*.csv");
        string sn = string.Empty;

        public MainWindow()
        {
            var hooks = CSharpScript.Create(File.ReadAllText("Hooks.cs"), ScriptOptions.Default.WithReferences(
                typeof(ModbusTcpClient).Assembly, typeof(IConfiguration).Assembly, typeof(HttpClientJsonExtensions).Assembly, typeof(MessageBox).Assembly
            ), typeof(HooksArgs));
            hooks.Compile();
            var onInput = hooks.ContinueWith<Task>("Hooks.OnInput(sn, config)").CreateDelegate();
            var onFileCreated = hooks.ContinueWith<Task>("Hooks.OnFileCreated(sn, file)").CreateDelegate();
            InitializeComponent();
            input.Focus();
            decryptDelay.Interval = System.TimeSpan.FromSeconds(0.5);
            decryptDelay.Tick += async (o, e) =>
            {
                decryptDelay.Stop();
                sn = input.Text;
                await (await onInput(new HooksArgs() { sn = sn, config = config }));
                //await Hooks.OnInput(sn, config);
                input.Text = "";

            };
            fileSystemWatcher.Created += async (o, e) =>
            {
                FileInfo file = new FileInfo(e.FullPath);
                if (!string.IsNullOrWhiteSpace(sn) && file.Exists)
                {
                    await Task.Delay(300);
                    await (await onFileCreated(new HooksArgs() { sn = sn, file = file }));
                    //await Hooks.OnFileCreated(sn, file);
                }
            };
            fileSystemWatcher.IncludeSubdirectories = true;
            fileSystemWatcher.EnableRaisingEvents = true;
        }

        private void input_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (input.Text != "")
            {
                decryptDelay.Stop();
                decryptDelay.Start();
            }
        }

        static void ProcessFile(string path, string newPartID)
        {
            var text = File.ReadAllText(path);
            var match = Regex.Match(text, @"\bCurve Info.+?\n\n\n", RegexOptions.Singleline);
            var info = match.Success ? match.Value : throw new ApplicationException("csv中未找到Curve Info，请确认文件格式");
            var lines = info.Split('\n');
            match = Regex.Match(lines[1], @"(\w+?,)*partID");
            var columnIndex = match.Success ? match.Groups[1].Captures.Count : throw new ApplicationException("Curve Info中未找到partID，请确认文件格式");
            var columns = lines[2].Split(',');
            var partID = columns[columnIndex].Trim('"');
            columns[columnIndex] = '"' + newPartID + '"';
            lines[2] = string.Join(',', columns);
            var newInfo = string.Join('\n', lines);
            File.WriteAllText(path, text.Replace(info, newInfo));
            newPartID = newPartID.Replace('/', '_').Replace('*', '_');
            File.Move(path, Path.GetFullPath(path.Replace(partID, newPartID)), true);
        }
    }

    public class HooksArgs
    {
        public required string sn;
        public IConfiguration? config;
        public FileInfo? file;
    }
}
