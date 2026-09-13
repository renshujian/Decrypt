using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Report;

/// <summary>
/// 接口日志相关的宿主侧接线：这里创建的 HttpClient 由 MainWindow 通过脚本参数传给 Hooks.cs，
/// Hooks.cs 调用 MOM 接口时复用它（不再自己 new HttpClient）。
/// </summary>
public partial class MainWindow
{
    /// <summary>接口日志目录（相对软件目录）</summary>
    private const string LogDir = "MOMLog";

    private static readonly HttpClient Http = new HttpClient(new HttpLogHandler());

    /// <summary>
    /// 接口日志：把每次 MOM 请求的 请求报文 / 响应报文 / 状态码 / 耗时 / 异常 记录到 MOMLog\MOM_yyyyMMdd.log。
    /// 日志文件以 FileShare.ReadWrite 打开，软件运行中可以直接用记事本查看。
    /// </summary>
    private sealed class HttpLogHandler : DelegatingHandler
    {
        private static readonly object SyncRoot = new object();
        private static StreamWriter? _writer;
        private static string _file = "";

        public HttpLogHandler()
            : base(new HttpClientHandler())
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string requestBody = "";
            if (request.Content != null)
            {
                // 请求内容是流式的（JsonContent 默认不带 Content-Length），记录日志前先缓冲，
                // 否则读不到报文、且内容只能发送一次；
                // 缓冲后 HttpClient 会算出 Content-Length，请求照常发送。已缓冲时该调用直接返回
                await request.Content.LoadIntoBufferAsync();
                requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            Write($"[MOM请求] {request.Method} {request.RequestUri}{Environment.NewLine}{requestBody}");

            var watch = Stopwatch.StartNew();
            try
            {
                HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

                string responseBody = "";
                if (response.Content != null)
                {
                    // 读的是缓冲副本，不影响调用方后续 ReadFromJsonAsync
                    responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                }
                Write($"[MOM响应] {(int)response.StatusCode} {response.StatusCode} 耗时={watch.ElapsedMilliseconds}ms{Environment.NewLine}{responseBody}");
                return response;
            }
            catch (Exception ex)
            {
                Write($"[MOM异常] {request.Method} {request.RequestUri} 耗时={watch.ElapsedMilliseconds}ms{Environment.NewLine}{ex}");
                throw;
            }
        }

        private static void Write(string message)
        {
            try
            {
                lock (SyncRoot)
                {
                    Directory.CreateDirectory(LogDir);
                    string file = Path.Combine(LogDir, $"MOM_{DateTime.Now:yyyyMMdd}.log");
                    if (_writer == null || _file != file)
                    {
                        _writer?.Dispose();
                        _writer = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
                        {
                            AutoFlush = true,
                        };
                        _file = file;
                    }
                    _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
                }
            }
            catch
            {
                // 日志写失败不影响接口调用
            }
        }
    }
}
