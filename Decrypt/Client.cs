using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Sensor;

public class Client : IDisposable
{
    public const int PACKET_SIZE = 20;

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

    #region 信号

    public long ReceiveCount { get; private set; }
    public long ReceiveTicks { get; private set; }
    public long ActionTicks { get; private set; }

    #endregion

    private Socket? _socket = null;
    private CancellationTokenSource _cts = new CancellationTokenSource();
    private Task _task = Task.CompletedTask;

    private int _batchSize;
    private readonly byte[] _batch;
    private long _batchId;
    private int _batchIndex;

    /// <summary>
    /// 批量接收扭矩数据。在读取传感器数据的线程上运行，不可以执行耗时操作，不可以操作UI。参数为batchId, batch, count，其中batch在该委托返回后不允许再访问，不应该保留引用。
    /// </summary>
    public event Action<long, byte[], int>? OnBatchData;

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
        _batchSize = batchSize;
        _batch = new byte[batchSize * PACKET_SIZE];
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
            _socket.ReceiveTimeout = 1000;
            _socket.Bind(LocalEndPoint);
            _socket.ReceiveFrom(_batch, ref RemoteEndPoint);
            _socket.Connect(RemoteEndPoint);
        }
        if (_task.IsCompleted)
        {
            _cts = new CancellationTokenSource();
            _task = Task.Factory.StartNew(ReadThreadEntry, TaskCreationOptions.LongRunning);
            _task.ContinueWith(task =>
            {
                OnError?.Invoke(task.Exception!);
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket = null;
    }

    public void Start()
    {
        _batchId = 0;
        _batchIndex = 0;
        Connect();
    }

    public void Stop()
    {
        _cts.Cancel();
        _task.Wait();
        _socket?.Close();
        _socket = null;
        if (_batchIndex > 0)
        {
            OnBatchData?.Invoke(_batchId, _batch, _batchIndex);
            _batchId++;
            _batchIndex = 0;
        }
    }

    private void ReadThreadEntry()
    {
        long tick = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!_cts.IsCancellationRequested)
        {
            tick = stopwatch.ElapsedTicks;
            int length = _socket!.Receive(_batch, _batchIndex * PACKET_SIZE, PACKET_SIZE, SocketFlags.None);
            ReceiveTicks += stopwatch.ElapsedTicks - tick;
            ReceiveCount++;
            if (length < PACKET_SIZE)
            {
                throw new ProtocolViolationException($"数据包长度{length}不足{PACKET_SIZE}");
            }
            _batchIndex++;
            if (_batchIndex == _batchSize)
            {
                tick = stopwatch.ElapsedTicks;
                OnBatchData?.Invoke(_batchId, _batch, _batchIndex);
                ActionTicks += stopwatch.ElapsedTicks - tick;
                _batchId++;
                _batchIndex = 0;
            }
        }
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Packet
{
    public byte Header0;
    public byte Header1;
    public short Ch0;
    public short Ch3;
    public short Ch4;
    public short Ch5;
    public short Ch6;
    public short Ch8;
    public short Ch9;
    public short Ch12;
    public byte Tail0;
    public byte Tail1;
}