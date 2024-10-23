using LiteDB;
using Microsoft.Extensions.Configuration;
using ScottPlot.DataSources;
using ScottPlot.Plottables;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Report;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public static IConfigurationRoot Config { get; } = new ConfigurationBuilder().AddIniFile("config.ini", optional: false, reloadOnChange: true).Build();
    private readonly FileSystemWatcher _fileSystemWatcher = new(Config["WatchCsvDir"]!, "*.csv");

    private Measurement? _measure;
    public bool Running => _measure?.Status == 1;

    private readonly List<double> _maxAxialForce = new(4_000_000);
    private readonly List<double> _minAxialForce = new(4_000_000);
    private Stream _stream1 = Stream.Null;
    private Stream _stream2 = Stream.Null;

    public MainWindow()
    {
        InitializeComponent();
        Application.Current.Exit += App_Exit;

        // config.ini修改时修改监视目录
        Config.GetReloadToken().RegisterChangeCallback(_ => _fileSystemWatcher.Path = Config["WatchCsvDir"]!, null);
        _fileSystemWatcher.Created += (o, e) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                (double entry1, double entry2) = Helper.ReadCsv(e.FullPath);
                _maxAxialForce.Add(entry1);
                _stream1.Write(BitConverter.GetBytes(entry1));
                _minAxialForce.Add(entry2);
                _stream2.Write(BitConverter.GetBytes(entry2));
                if (ScaleCheckBox.IsChecked == true)
                {
                    WpfPlot1.Plot.Axes.AutoScale();
                }
                WpfPlot1.Refresh();
            });
        };
        _fileSystemWatcher.IncludeSubdirectories = true;

        Measurement? lastMeasure = Helper.Measurements.Query().Where(x => x.Status == 1).OrderByDescending(x => x.Id).FirstOrDefault();
        if (lastMeasure != null)
        {
            if (MessageBox.Show("是否继续上次未结束的采集？", string.Empty, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _measure = lastMeasure;
                _maxAxialForce.AddRange(MemoryMarshal.Cast<byte, double>(File.ReadAllBytes(lastMeasure.Files[0])));
                _minAxialForce.AddRange(MemoryMarshal.Cast<byte, double>(File.ReadAllBytes(lastMeasure.Files[1])));
                _stream1 = File.OpenWrite(lastMeasure.Files[0]);
                _stream1.Position = _stream1.Length;
                _stream2 = File.OpenWrite(lastMeasure.Files[1]);
                _stream2.Position = _stream2.Length;
                _fileSystemWatcher.EnableRaisingEvents = true;
                StopButton.Visibility = Visibility.Visible;
            }
            else
            {
                Helper.Measurements.Update(lastMeasure with { Status = 2 });
            }
        }

        // 初始化数据图表
        WpfPlot1.Plot.ScaleFactor = WpfPlot1.DisplayScale;
        Signal maxAxialForce = WpfPlot1.Plot.Add.Signal(new SignalSourceDouble(_maxAxialForce, 1));
        maxAxialForce.LegendText = "Maximum Axial Force";
        Signal minAxialForce = WpfPlot1.Plot.Add.Signal(new SignalSourceDouble(_minAxialForce, 1));
        minAxialForce.LegendText = "Minimum Axial Force";
        WpfPlot1.Plot.XLabel("N/cycles");
        WpfPlot1.Plot.YLabel("Force/kN");
        WpfPlot1.Plot.Title("(-1.6mm,-2.6mm)");
    }

    private void App_Exit(object sender, ExitEventArgs e)
    {
        _stream1.Close();
        _stream2.Close();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e) => Start();

    private void Start()
    {
        _measure = new Measurement(ObjectId.NewObjectId(), 1, []);
        _measure.Files.Add($"data/{_measure.Id}_stream1");
        _measure.Files.Add($"data/{_measure.Id}_stream2");
        Helper.Measurements.Insert(_measure);
        _stream1 = File.Create(_measure.Files[0]);
        _stream2 = File.Create(_measure.Files[1]);
        _fileSystemWatcher.EnableRaisingEvents = true;
        StopButton.Visibility = Visibility.Visible;
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => Stop();

    private void Stop()
    {
        _fileSystemWatcher.EnableRaisingEvents = false;
        if (_measure != null)
        {
            _measure = _measure with { Status = 2 };
            Helper.Measurements.Update(_measure with { Status = 2 });
            _stream1.Close();
            _stream2.Close();
        }
        StopButton.Visibility = Visibility.Hidden;
    }
}

public record class Measurement(ObjectId Id, int Status, List<string> Files);
