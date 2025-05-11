using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Sensor;

public class Client : IDisposable
{
    #region 参数

    private double _sensitivity = 1.121;
    /// <summary>
    /// 灵敏度，单位mV/V
    /// </summary>
    public double Sensitivity
    {
        get => _sensitivity;
        set
        {
            _sensitivity = value;
            Coefficient = CalcCoefficient(_sensitivity, _range);
        }
    }

    private double _range = 15;
    /// <summary>
    /// 量程，单位Nm
    /// </summary>
    public double Range
    {
        get => _range;
        set
        {
            _range = value;
            Coefficient = CalcCoefficient(_sensitivity, _range);
        }
    }

    /// <summary>
    /// 数字量换算为扭矩的系数，单位Nm
    /// </summary>
    public double Coefficient { get; private set; }

    /// <summary>
    /// 采集盒IP端口
    /// </summary>
    public EndPoint RemoteEndPoint = new IPEndPoint(IPAddress.Parse("192.168.250.100"), 1053);

    /// <summary>
    /// 本机IP端口
    /// </summary>
    public IPEndPoint LocalEndPoint { get; set; } = new IPEndPoint(IPAddress.Parse("192.168.100.235"), 6666);

    #endregion

    private Socket? _socket = null;
    private CancellationTokenSource _cts = new CancellationTokenSource();
    private Task _task = Task.CompletedTask;

    private readonly double[] _batch;
    private long _batchId;
    private int _batchIndex;
    private bool _working;
    private byte _channel = 255;

    /// <summary>
    /// 批量接收扭矩数据。在读取传感器数据的线程上运行，不可以执行耗时操作，不可以操作UI。接收的List不会被Client保留或修改，应用程序可以安全的将List保留到需要的时候再处理。
    /// </summary>
    public event Action<long, List<double>>? OnBatchData;

    /// <summary>
    /// 接收读取线程发生的异常，不可以操作UI。
    /// </summary>
    public event Action<AggregateException>? OnError;

    public Client(int batchSize)
    {
        if (batchSize < 1)
        {
            throw new ArgumentException("invalid batch size");
        }
        _batch = new double[batchSize];
        Coefficient = CalcCoefficient(Sensitivity, Range);
    }

    /// <summary>
    /// 从灵敏度和量程计算系数的纯函数。
    /// </summary>
    /// <param name="sensitivity"></param>
    /// <param name="range"></param>
    /// <returns></returns>
    private double CalcCoefficient(double sensitivity, double range) => 2500 * range / 32768 / 200 / 5 / sensitivity;

    /// <summary>
    /// 连接网络。Start时会调用，应用可以不调用。
    /// </summary>
    public void Connect()
    {
        if (_socket == null)
        {
            _socket = new Socket(SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(LocalEndPoint);
            //_socket.Connect(RemoteEndPoint);
        }
        if (_task.IsCompleted)
        {
            _cts = new CancellationTokenSource();
            _task = Task.Factory.StartNew(ReadThreadEntry, TaskCreationOptions.LongRunning).ContinueWith(task =>
            {
                OnError?.Invoke(task.Exception!);
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket?.Close();
    }

    public void Start(byte channel = 255)
    {
        _batchId = 0;
        _batchIndex = 0;
        Connect();
        if (channel != _channel)
        {
            byte[] selectChannel = { 0XFA, 0XAF, channel };
            _socket!.Send(selectChannel);
            _channel = channel;
            // 采集盒收到报文并切换通道前的数据是无效的，需要丢弃。等10ms再开始保存数据
            Thread.Sleep(10);
        }
        _working = true;
    }

    public void Stop()
    {
        _working = false;
        if (!SpinWait.SpinUntil(() => _batchIndex == 0, TimeSpan.FromSeconds(1)))
        {
            throw new TimeoutException("batch flush timeout");
        }
    }

    private void ReadThreadEntry()
    {
        const int dataSize = 20;
        byte[] buffer = new byte[dataSize];
        while (!_cts.IsCancellationRequested)
        {
            int length = _socket!.ReceiveFrom(buffer, ref RemoteEndPoint);
            if (length < buffer.Length)
            {
                throw new ProtocolViolationException($"数据包长度{length}不足20");
            }
            if (buffer[0] != 0x10 || buffer[1] != 0x02)
            {
                throw new ProtocolViolationException($"数据包头{buffer[0]:X}{buffer[1]:X}不正确");
            }
            if (buffer[18] != 0x10 || buffer[19] != 0x03)
            {
                throw new ProtocolViolationException($"数据包尾{buffer[18]:X}{buffer[19]:X}不正确");
            }
            if (!_working)
            {
                if (_batchIndex > 0)
                {
                    OnBatchData?.Invoke(_batchId, _batch.Take(_batchIndex).ToList());
                    _batchId++;
                    _batchIndex = 0;
                }
                continue;
            }
            double value = BinaryPrimitives.ReadInt16BigEndian(buffer.AsSpan(2, 2));
            _batch[_batchIndex++] = value;
            if (_batchIndex == _batch.Length)
            {
                OnBatchData?.Invoke(_batchId, _batch.ToList());
                _batchId++;
                _batchIndex = 0;
            }
        }
    }
}
