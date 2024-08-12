using ClosedXML.Report;
using CsvHelper;
using FluentModbus;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Configuration;
using ScottPlot;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Report;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    public static IConfigurationRoot Config { get; } = new ConfigurationBuilder().AddIniFile("config.ini", optional: false, reloadOnChange: true).Build();
    private readonly DispatcherTimer _delayTimer = new();
    private readonly FileSystemWatcher _fileSystemWatcher = new(Config["Report:WatchCsvDir"]!, "*.csv");
    private string _currentSn = string.Empty;
    public string CurrentSn
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_currentSn) && File.Exists(nameof(CurrentSn)))
            {
                _currentSn = File.ReadAllText(nameof(CurrentSn));
            }
            return _currentSn;
        }
        private set
        {
            File.WriteAllText(nameof(CurrentSn), value);
            _currentSn = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentSn)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ReportQueue ReportQueue { get; } = [];

    public MainWindow()
    {
        // 编译脚本
        var hooks = CSharpScript.Create(File.ReadAllText("Hooks.cs"), ScriptOptions.Default.WithReferences(
            typeof(ModbusTcpClient).Assembly, typeof(IConfiguration).Assembly, typeof(HttpClientJsonExtensions).Assembly, typeof(MessageBox).Assembly,
            typeof(CsvReader).Assembly, typeof(XLTemplate).Assembly, typeof(Plot).Assembly
        ), typeof(HooksArgs));
        hooks.Compile();
        var onInput = hooks.ContinueWith<Task>("Hooks.OnInput(sn, config)").CreateDelegate();
        var generateReport = hooks.ContinueWith<Task>("Hooks.GenerateReport(sn, config, files)").CreateDelegate();

        InitializeComponent();
        DataContext = this;
        input.Focus();

        // 输入停止0.5秒后触发Hooks.OnInput，之后清空输入框
        _delayTimer.Interval = TimeSpan.FromSeconds(0.5);
        _delayTimer.Tick += async (o, e) =>
        {
            _delayTimer.Stop();
            await (await onInput(new HooksArgs() { sn = input.Text, config = Config }));
            if (CurrentSn != input.Text)
            {
                ReportQueue.Clear();
                CurrentSn = input.Text;
            }
            input.Text = "";
        };

        // config.ini修改时修改监视目录
        Config.GetReloadToken().RegisterChangeCallback(_ => _fileSystemWatcher.Path = Config["Report:WatchCsvDir"]!, null);
        _fileSystemWatcher.Created += (o, e) =>
        {
            if (!string.IsNullOrWhiteSpace(CurrentSn))
            {
                Dispatcher.BeginInvoke(async () =>
                {
                    ReportQueue.Enqueue(e.FullPath);
                    int csvCount = int.Parse(Config["Report:CsvCount"]!);
                    if (ReportQueue.Count >= csvCount)
                    {
                        var files = ReportQueue.Dequeue(csvCount);
                        await (await generateReport(new HooksArgs() { sn = CurrentSn, config = Config, files = files }));
                    }
                });
            }
        };
        _fileSystemWatcher.IncludeSubdirectories = true;
        _fileSystemWatcher.EnableRaisingEvents = true;
    }

    private void input_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (input.Text != "")
        {
            _delayTimer.Stop();
            _delayTimer.Start();
        }
    }
}

public class HooksArgs
{
    public required string sn;
    public required IConfiguration config;
    public List<string>? files;
}

public sealed class ReportQueue : IEnumerable<string>, IDisposable
{
    public string Path { get; } = nameof(ReportQueue);
    private StreamWriter _writer;
    public ObservableCollection<string> Items { get; } = [];
    public int Count => Items.Count;

    public ReportQueue()
    {
        if (File.Exists(Path))
        {
            foreach (string item in File.ReadAllLines(Path))
            {
                Items.Add(item);
            }
        }
        _writer = new StreamWriter(Path, append: true)
        {
            AutoFlush = true,
        };
    }

    public void Dispose() => _writer.Dispose();

    public void Enqueue(string item)
    {
        Items.Add(item);
        _writer.WriteLine(item);
    }

    public List<string> Dequeue(int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(Count, count);

        List<string> returns = [];
        for (int i = 0; i < count; i++)
        {
            returns.Add(Items[0]);
            Items.RemoveAt(0);
        }
        _writer.BaseStream.SetLength(0);
        foreach (string item in Items)
        {
            _writer.WriteLine(item);
        }
        return returns;
    }

    public void Clear()
    {
        Items.Clear();
        _writer.BaseStream.SetLength(0);
    }

    public IEnumerator<string> GetEnumerator() => Items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => Items.GetEnumerator();
}