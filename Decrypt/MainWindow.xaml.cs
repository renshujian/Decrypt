using Microsoft.Extensions.Configuration;
using Microsoft.Win32;
using ScottPlot;
using ScottPlot.DataSources;
using ScottPlot.Plottables;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Threading;

namespace Sensor;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public static IConfigurationRoot Config { get; } = new ConfigurationBuilder().AddIniFile("config.ini").Build();
    public static int BatchSize { get; } = Config.GetValue<int>("BatchSize");
    private readonly Client _client = new(BatchSize);
    private readonly DispatcherTimer _timer = new();
    private List<(byte[], int)> _batches = [];
    private readonly List<List<double>> _chData = [new(40_000), new(40_000), new(40_000), new(40_000), new(40_000), new(40_000), new(40_000), new(40_000)];
    private readonly List<Signal> _signals = [];
    private Crosshair _crosshair;
    private Text _text;
    private DataPoint _dataPoint;

    public MainWindow()
    {
        InitializeComponent();
        WatchTick.Content = $"计时单位: {1.0 / Stopwatch.Frequency:E0}";
        _client.OnBatchData += (id, batch, count) =>
        {
            var array = ArrayPool<byte>.Shared.Rent(BatchSize * Client.PACKET_SIZE);
            Buffer.BlockCopy(batch, 0, array, 0, count * Client.PACKET_SIZE);
            _batches.Add((array, count));
        };
        _client.OnError += e =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                _timer.Stop();
                MessageBox.Show(e.ToString(), "接收数据错误");
                App.Current.Shutdown();
            });
        };
        _timer.Tick += _timer_Tick;
        _timer.Interval = TimeSpan.FromSeconds(3);

        // 初始化数据图表
        WpfPlot1.Plot.ScaleFactor = WpfPlot1.DisplayScale;
        foreach (var data in _chData)
        {
            var signal = WpfPlot1.Plot.Add.Signal(new SignalSourceDouble(data, 1));
            signal.Data.XOffset = 1;
            _signals.Add(signal);
        }
        _signals[0].LegendText = "CH0";
        _signals[1].LegendText = "CH3";
        _signals[2].LegendText = "CH4";
        _signals[3].LegendText = "CH5";
        _signals[4].LegendText = "CH6";
        _signals[5].LegendText = "CH8";
        _signals[6].LegendText = "CH9";
        _signals[7].LegendText = "CH12";
        _crosshair = WpfPlot1.Plot.Add.Crosshair(0, 0);
        _crosshair.IsVisible = false;
        _text = WpfPlot1.Plot.Add.Text(string.Empty, 0, 0);
        _text.IsVisible = false;
        _text.LabelBold = true;
    }

    private void _timer_Tick(object? sender, EventArgs e)
    {
        var batches = _batches;
        _batches = [];
        foreach (var data in _chData)
        {
            data.Clear();
        }
        foreach (var (batch, count) in batches)
        {
            for (int i = 0; i < count; i += Client.PACKET_SIZE)
            {
                if (batch[i] != 0x10 || batch[i + 1] != 0x02)
                {
                    throw new ProtocolViolationException($"数据包头{batch[i]:X}{batch[i + 1]:X}不正确");
                }
                if (batch[i + 18] != 0x10 || batch[i + 19] != 0x03)
                {
                    throw new ProtocolViolationException($"数据包尾{batch[i + 18]:X}{batch[i + 19]:X}不正确");
                }
                _chData[0].Add(BinaryPrimitives.ReadUInt16BigEndian(batch.AsSpan(i + 2, 2)));
                _chData[1].Add(BinaryPrimitives.ReadUInt16BigEndian(batch.AsSpan(i + 4, 2)));
                _chData[2].Add(BinaryPrimitives.ReadUInt16BigEndian(batch.AsSpan(i + 6, 2)));
                _chData[3].Add(BinaryPrimitives.ReadUInt16BigEndian(batch.AsSpan(i + 8, 2)));
                _chData[4].Add(BinaryPrimitives.ReadUInt16BigEndian(batch.AsSpan(i + 10, 2)));
                _chData[5].Add(BinaryPrimitives.ReadUInt16BigEndian(batch.AsSpan(i + 12, 2)));
                _chData[6].Add(BinaryPrimitives.ReadUInt16BigEndian(batch.AsSpan(i + 14, 2)));
                _chData[7].Add(BinaryPrimitives.ReadUInt16BigEndian(batch.AsSpan(i + 16, 2)));
            }
            ArrayPool<byte>.Shared.Return(batch);
        }
        DataCount.Content = $"数据数：{_chData[0].Count}";
        if (_chData[0].Count > 0)
        {
            double[] max = [_chData[0].Max(), _chData[1].Max(), _chData[2].Max(), _chData[3].Max(), _chData[4].Max(), _chData[5].Max(), _chData[6].Max(), _chData[7].Max()];
            DataMax.Content = $"最大值：{max.Max()}";
        }
        else
        {
            DataMax.Content = "最大值：";
        }
        ReceiveCount.Content = $"收包数：{_client.ReceiveCount}";
        ReceiveTicks.Content = $"收包计时：{_client.ReceiveTicks}";
        ActionTicks.Content = $"拷贝计时：{_client.ActionTicks}";
        WpfPlot1.Plot.Axes.AutoScale();
        WpfPlot1.Refresh();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e) => Start();

    private void Start()
    {
        foreach (var data in _chData)
        {
            data.Clear();
        }
        WpfPlot1.Refresh();
        _client.Start();
        _timer.Start();
        StopButton.Visibility = Visibility.Visible;
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => Stop();

    private void Stop()
    {
        _timer.Stop();
        _client.Stop();
        _timer_Tick(null, EventArgs.Empty);
        StopButton.Visibility = Visibility.Hidden;
    }

    private void WpfPlot1_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var point = e.GetPosition(WpfPlot1);
        var pixel = new Pixel(point.X * WpfPlot1.DisplayScale, point.Y * WpfPlot1.DisplayScale);
        var coordinates = WpfPlot1.Plot.GetCoordinates(pixel);
        var dataPoint = DataPoint.None;
        foreach (var signal in _signals)
        {
            dataPoint = _signals[0].GetNearest(coordinates, WpfPlot1.Plot.LastRender);
            if (dataPoint.IsReal)
            {
                break;
            }
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

    private void RemovePoint_Click(object sender, RoutedEventArgs e)
    {
        if (_crosshair.IsVisible)
        {
            int index = _dataPoint.Index;
            foreach (var data in _chData)
            {
                data.RemoveAt(index);
            }
            WpfPlot1.Refresh();
        }
    }

    private void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            FileName = "plot.png",
            Filter = "PNG Files (*.png)|*.png" +
                     "|JPEG Files (*.jpg, *.jpeg)|*.jpg;*.jpeg" +
                     "|BMP Files (*.bmp)|*.bmp" +
                     "|WebP Files (*.webp)|*.webp" +
                     "|SVG Files (*.svg)|*.svg" +
                     "|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() is true)
        {
            if (string.IsNullOrEmpty(dialog.FileName))
                return;

            ImageFormat format;

            try
            {
                format = ImageFormats.FromFilename(dialog.FileName);
            }
            catch (ArgumentException)
            {
                MessageBox.Show("不支持的文件格式", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            try
            {
                PixelSize lastRenderSize = WpfPlot1.Plot.RenderManager.LastRender.FigureRect.Size;
                WpfPlot1.Plot.Save(dialog.FileName, (int)lastRenderSize.Width, (int)lastRenderSize.Height, format);
            }
            catch (Exception)
            {
                MessageBox.Show("保存图片失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }
    }
}
