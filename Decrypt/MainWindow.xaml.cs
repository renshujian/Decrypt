using Microsoft.Extensions.Configuration;
using Microsoft.Win32;
using ScottPlot;
using ScottPlot.DataSources;
using ScottPlot.Plottables;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Sensor;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public static IConfigurationRoot Config { get; } = new ConfigurationBuilder().AddIniFile("config.ini", optional: true).Build();
    private readonly Client _client = new(100);
    private readonly DispatcherTimer _timer = new();
    private List<List<double>> _batches = [];
    private List<double> _ch0Data = new(40_000);
    private Signal _signal1;
    private Crosshair _crosshair;
    private Text _text;
    private DataPoint _dataPoint;

    public MainWindow()
    {
        InitializeComponent();

        _client.OnBatchData += (id, batch) =>
        {
            _batches.Add(batch);
        };
        _client.OnError += e =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                _timer.Stop();
                StopButton.Visibility = Visibility.Hidden;
                MessageBox.Show(e.ToString(), "接收数据错误");
            });
        };
        _timer.Tick += (o, e) =>
        {
            var batches = _batches;
            _batches = [];
            _ch0Data.Clear();
            foreach (var batch in batches)
            {
                _ch0Data.AddRange(batch);
            }
            DataCount.Content = $"数据数：{_ch0Data.Count}";
            if (_ch0Data.Count > 0)
            {
                DataMax.Content = $"最大值：{_ch0Data.Max()}";
            }
            else
            {
                DataMax.Content = "最大值：";
            }
            WpfPlot1.Plot.Axes.AutoScale();
            WpfPlot1.Refresh();
        };
        _timer.Interval = TimeSpan.FromSeconds(3);

        // 初始化数据图表
        WpfPlot1.Plot.ScaleFactor = WpfPlot1.DisplayScale;
        _signal1 = WpfPlot1.Plot.Add.Signal(new SignalSourceDouble(_ch0Data, 1));
        _signal1.LegendText = "CH0";
        _signal1.Data.XOffset = 1;
        _crosshair = WpfPlot1.Plot.Add.Crosshair(0, 0);
        _crosshair.IsVisible = false;
        _text = WpfPlot1.Plot.Add.Text(string.Empty, 0, 0);
        _text.IsVisible = false;
        _text.LabelBold = true;
    }

    private void StartButton_Click(object sender, RoutedEventArgs e) => Start();

    private void Start()
    {
        _ch0Data.Clear();
        WpfPlot1.Refresh();
        _client.Start();
        _timer.Start();
        StopButton.Visibility = Visibility.Visible;
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => Stop();

    private void Stop()
    {
        _client.Stop();
        _timer.Stop();
        StopButton.Visibility = Visibility.Hidden;
    }

    private void WpfPlot1_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var point = e.GetPosition(WpfPlot1);
        var pixel = new Pixel(point.X * WpfPlot1.DisplayScale, point.Y * WpfPlot1.DisplayScale);
        var coordinates = WpfPlot1.Plot.GetCoordinates(pixel);
        var dataPoint = _signal1.GetNearest(coordinates, WpfPlot1.Plot.LastRender);
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
            _ch0Data.RemoveAt(index);
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
