using LiteDB;
using Microsoft.Extensions.Configuration;
using ScottPlot;
using ScottPlot.DataSources;
using ScottPlot.Interactivity.UserActionResponses;
using ScottPlot.Plottables;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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
    private Signal _signal1;
    private Signal _signal2;
    private Crosshair _crosshair;
    private Text _text;
    private DataPoint _dataPoint;

    public MainWindow()
    {
        InitializeComponent();
        Application.Current.Exit += App_Exit;

        // config.ini修改时修改监视目录
        Config.GetReloadToken().RegisterChangeCallback(_ => _fileSystemWatcher.Path = Config["WatchCsvDir"]!, null);
        _fileSystemWatcher.Created += (o, e) =>
        {
            // 等待文件上传完成
            Thread.Sleep(300);
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
                _stream1 = File.Open(lastMeasure.Files[0], FileMode.Open);
                _stream1.Position = _stream1.Length;
                _stream2 = File.Open(lastMeasure.Files[1], FileMode.Open);
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
        _signal1 = WpfPlot1.Plot.Add.Signal(new SignalSourceDouble(_maxAxialForce, 1));
        _signal1.LegendText = "Maximum Axial Force";
        _signal1.Data.XOffset = 1;
        _signal2 = WpfPlot1.Plot.Add.Signal(new SignalSourceDouble(_minAxialForce, 1));
        _signal2.LegendText = "Minimum Axial Force";
        _signal2.Data.XOffset = 1;
        _crosshair = WpfPlot1.Plot.Add.Crosshair(0, 0);
        _crosshair.IsVisible = false;
        _text = WpfPlot1.Plot.Add.Text(string.Empty, 0, 0);
        _text.IsVisible = false;
        _text.LabelBold = true;
        WpfPlot1.Plot.XLabel("N/cycles");
        WpfPlot1.Plot.YLabel("Force/kN");
        WpfPlot1.Plot.Title("(-1.6mm,-2.6mm)");
        WpfPlot1.UserInputProcessor.IsEnabled = true;
        // ScottPlot.WPF的右键菜单不能定制关闭事件，且使用的就是WPF的ContextMenu。这里使用WPF的右键菜单而不是ScottPlot的右键菜单
        WpfPlot1.UserInputProcessor.RemoveAll<SingleClickContextMenu>();
        // 移动鼠标去点击菜单的过程中不要更新标记点
        WpfPlot1.ContextMenu.Opened += (_, _) => WpfPlot1.MouseMove -= WpfPlot1_MouseMove;
        WpfPlot1.ContextMenu.Closed += (_, _) => WpfPlot1.MouseMove += WpfPlot1_MouseMove;
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
        _stream1.Close();
        _stream1 = File.Create(_measure.Files[0]);
        _stream2.Close();
        _stream2 = File.Create(_measure.Files[1]);
        _maxAxialForce.Clear();
        _minAxialForce.Clear();
        WpfPlot1.Refresh();
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
            Helper.Measurements.Update(_measure);
        }
        StopButton.Visibility = Visibility.Hidden;
    }

    private void WpfPlot1_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var point = e.GetPosition(WpfPlot1);
        var pixel = new Pixel(point.X * WpfPlot1.DisplayScale, point.Y * WpfPlot1.DisplayScale);
        var coordinates = WpfPlot1.Plot.GetCoordinates(pixel);
        var dataPoint = _signal1.GetNearest(coordinates, WpfPlot1.Plot.LastRender);
        if (!dataPoint.IsReal)
        {
            dataPoint = _signal2.GetNearest(coordinates, WpfPlot1.Plot.LastRender);
        }
        if (dataPoint.IsReal)
        {
            _dataPoint = dataPoint;
            _crosshair.IsVisible = true;
            _crosshair.Position = dataPoint.Coordinates;
            _text.IsVisible = true;
            _text.Location = dataPoint.Coordinates;
            _text.LabelText = $"({dataPoint.Coordinates.X}, {dataPoint.Coordinates.Y:f1})";
            WpfPlot1.Refresh();
        }
        else if (_crosshair.IsVisible)
        {
            _crosshair.IsVisible = false;
            _text.IsVisible = false;
            WpfPlot1.Refresh();
        }
    }

    private void MenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_crosshair.IsVisible)
        {
            int index = _dataPoint.Index;
            long position = index * sizeof(double);
            long nextPosition = position + sizeof(double);
            long moveLength = _stream1.Length - nextPosition;
            if (moveLength > 25_000_000 || moveLength < 0)
            {
                return;
            }
            byte[] buffer = new byte[moveLength];

            _maxAxialForce.RemoveAt(index);
            _stream1.Position = nextPosition;
            _stream1.ReadExactly(buffer);
            _stream1.SetLength(position);
            _stream1.Write(buffer);

            _minAxialForce.RemoveAt(index);
            _stream2.Position = nextPosition;
            _stream2.ReadExactly(buffer);
            _stream2.SetLength(position);
            _stream2.Write(buffer);

            WpfPlot1.Refresh();
        }
    }
}

public record class Measurement(ObjectId Id, int Status, List<string> Files);
