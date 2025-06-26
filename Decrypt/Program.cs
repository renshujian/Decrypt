using CsvHelper;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Report
{
    internal static class Program
    {
        public static IConfigurationRoot Config { get; } = new ConfigurationBuilder().AddIniFile("config.ini", optional: false).Build();

        public static void Main(string[] args)
        {
            Directory.CreateDirectory(Config["OutputDir"]);
            var path = Path.Combine(Config["OutputDir"], DateTime.Now.ToString("yyyyMMddhhmmss") + ".csv");
            using var csv = new CsvWriter(new StreamWriter(File.Create(path), Encoding.UTF8), CultureInfo.InvariantCulture);
            csv.WriteHeader<Record>();
            csv.NextRecord();
            Console.WriteLine($"创建 {path} 成功，开始监听 {Config["InputDir"]}。");

            var bag = new ConcurrentBag<Record>();
            using var fileSystemWatcher = new FileSystemWatcher(Config["InputDir"]!, "*.csv");
            fileSystemWatcher.Created += (o, e) =>
            {
                // 等待文件上传完成
                Thread.Sleep(300);
                Record record = Helper.ReadCsv(e.FullPath);
                bag.Add(record);
                Console.Write('.');
            };
            fileSystemWatcher.IncludeSubdirectories = true;
            fileSystemWatcher.EnableRaisingEvents = true;

            using var cancellationTokenSource = new CancellationTokenSource();
            AppDomain.CurrentDomain.ProcessExit += (o, e) =>
            {
                Console.WriteLine("进程即将退出，等待文件刷盘");
                cancellationTokenSource.Cancel();
                Thread.Sleep(1500);
            };
            Task.Factory.StartNew(() =>
            {
                while (!cancellationTokenSource.IsCancellationRequested)
                {
                    Thread.Sleep(0);
                    if (bag.TryTake(out var record))
                    {
                        csv.WriteRecord(record);
                        csv.NextRecord();
                    }
                }

                csv.Flush();
            }, TaskCreationOptions.LongRunning).Wait();
        }
    }
}
