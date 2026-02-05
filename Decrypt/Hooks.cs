using ClosedXML.Report;
using CsvHelper;
using CsvHelper.Configuration;
using FluentModbus;
using Microsoft.Extensions.Configuration;
using ScottPlot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

internal static class Hooks
{
    /// <summary>
    /// 扫码后触发
    /// </summary>
    /// <param name="sn">扫码枪输入的字符串</param>
    /// <param name="config"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public static async Task OnInput(string sn, IConfiguration config)
    {
        // 对接 MES
        using HttpClient httpClient = new HttpClient();
        string endpoint = config["SiteControl:Endpoint"] ?? throw new ArgumentNullException("SiteControl:Endpoint");
        string site = config["SiteControl:MySite"] ?? throw new ArgumentNullException("SiteControl:MySite");
        HttpResponseMessage response = await httpClient.PostAsync($"{endpoint}?data={site};{sn}", null);
        response.EnsureSuccessStatusCode();
        SiteControlRes res = await response.Content.ReadFromJsonAsync<SiteControlRes>() ?? throw new InvalidOperationException("HandleSiteControl失败");
        string[] data = res.data.Split(';');

        // 对接 PLC
        using ModbusTcpClient modbusClient = new ModbusTcpClient();
        string tcpEndpoint = config["Modbus:Endpoint"] ?? throw new ArgumentNullException("Modbus:Endpoint");
        bool bigEndian = config["Modbus:BigEndian"]?.ToLower() == "true";
        modbusClient.Connect(tcpEndpoint, bigEndian ? ModbusEndianness.BigEndian : ModbusEndianness.LittleEndian);
        if (data[0] == "OK")
        {
            modbusClient.WriteSingleRegister(unitIdentifier: 1, registerAddress: 400, value: 0);
        }
        else if (data[0] == "NG")
        {
            MessageBox.Show(data[1]);
            modbusClient.WriteSingleRegister(unitIdentifier: 1, registerAddress: 400, value: 1);
        }
        modbusClient.Disconnect();
    }

    internal record class SiteControlRes(string msg, int code, string data);

    /// <summary>
    /// 检测到硬件上传的原始数据文件后触发
    /// </summary>
    /// <param name="sn"></param>
    /// <param name="config"></param>
    /// <param name="file">文件的绝对路径</param>
    /// <returns>要保存进报告队列的字符串</returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static async Task<string> OnCsvCreated(string sn, IConfiguration config, string file)
    {
        using ModbusTcpClient modbusClient = new ModbusTcpClient();
        string tcpEndpoint = config["Modbus:Endpoint"] ?? throw new ArgumentNullException("Modbus:Endpoint");
        bool bigEndian = config["Modbus:BigEndian"]?.ToLower() == "true";
        modbusClient.Connect(tcpEndpoint, bigEndian ? ModbusEndianness.BigEndian : ModbusEndianness.LittleEndian);
        int[] values = modbusClient.ReadHoldingRegisters<int>(unitIdentifier: 1, startingAddress: 410, count: 8).ToArray();
        List<string> results = new();
        results.Add(file);
        for (int i = 0; i < values.Length; i++)
        {
            results.Add(((double)values[i] / 100).ToString());
        }
        return string.Join('|', results);
    }

    /// <summary>
    /// 报告队列长度达到config["Report:CsvCount"]后触发
    /// </summary>
    /// <param name="sn"></param>
    /// <param name="config"></param>
    /// <param name="files">报告队列中保存的字符串</param>
    /// <returns></returns>
    public static async Task GenerateReport(string sn, IConfiguration config, List<string> files)
    {
        // 等待文件上传完成并且不在主线程继续运行。后续的代码有可能并发执行，不允许写固定名称的文件
        await Task.Delay(300).ConfigureAwait(continueOnCapturedContext: false);
        if (double.TryParse(config["Report:InitialLowerLimit"], out double InitialLowerLimit))
        {
            InitialLowerLimit = Math.Round(InitialLowerLimit, 1);
        }
        else
        {
            throw new ArgumentException("Report:InitialLowerLimit");
        }
        if (double.TryParse(config["Report:InitialUpperLimit"], out double InitialUpperLimit))
        {
            InitialUpperLimit = Math.Round(InitialUpperLimit, 1);
        }
        else
        {
            throw new ArgumentException("Report:InitialUpperLimit");
        }
        if (double.TryParse(config["Report:FinalLowerLimit"], out double FinalLowerLimit))
        {
            FinalLowerLimit = Math.Round(FinalLowerLimit, 1);
        }
        else
        {
            throw new ArgumentException("Report:FinalLowerLimit");
        }
        if (double.TryParse(config["Report:FinalUpperLimit"], out double FinalUpperLimit))
        {
            FinalUpperLimit = Math.Round(FinalUpperLimit, 1);
        }
        else
        {
            throw new ArgumentException("Report:FinalUpperLimit");
        }
        List<Data> data = files.ConvertAll(ReadCsv);
        bool ok = true;
        for (int i = 0; i < data.Count; i++)
        {
            data[i].Index = i + 1;
            if (data[i].basepoint_y > InitialLowerLimit && data[i].basepoint_y < InitialUpperLimit && data[i].XMax_Y > FinalLowerLimit && data[i].XMax_Y < FinalUpperLimit)
            {
                data[i].Result = "pass";
            }
            else
            {
                data[i].Result = "fail";
                ok = false;
            }
        }

        using var report = new XLTemplate("template.xlsx");
        report.AddVariable("SN", sn);
        report.AddVariable("InitialLowerLimit", InitialLowerLimit);
        report.AddVariable("InitialUpperLimit", InitialUpperLimit);
        report.AddVariable("FinalLowerLimit", FinalLowerLimit);
        report.AddVariable("FinalUpperLimit", FinalUpperLimit);
        report.AddVariable("Tests", data);
        report.Generate();

        int row = 6 + data.Count;
        int col = 2;
        for (int i = 0; i < data.Count; i++)
        {
            using var plot = new Plot();
            plot.Add.Scatter(data[i].x, data[i].y);
            string pngName = $"{Guid.NewGuid()}.png";
            plot.SavePng(pngName, 600, 500);
            var worksheet = report.Workbook.Worksheet(1);
            var picture = worksheet.AddPicture(pngName).MoveTo(worksheet.Cell(row + 28 * i, col));
            File.Delete(pngName);
        }

        report.SaveAs(ok ? $@"{config["Report:OkDir"]}\OK_{sn}_{DateTime.Now.ToString("yyyyMMddHHmmss.fff")}.xlsx" : $@"{config["Report:NgDir"]}\NG_{sn}_{DateTime.Now.ToString("yyyyMMddHHmmss.fff")}.xlsx");
    }

    private static Data ReadCsv(string pathWithParam)
    {
        string[] p = pathWithParam.Split('|');
        Data data = new()
        {
            XDirMax = Double.Parse(p[1]),
            XDirMin = Double.Parse(p[2]),
            YDirMax = Double.Parse(p[3]),
            YDirMin = Double.Parse(p[4]),
        };
        using var reader = new CsvReader(new StreamReader(p[0]), new CsvConfiguration(CultureInfo.InvariantCulture) { IgnoreBlankLines = false, MissingFieldFound = null, BadDataFound = null });
        while (reader.Read())
        {
            if (reader[0] == "EV_1")
            {
                reader.Read();
                reader.ReadHeader();
                reader.Read();
                string? basepoint_y = reader["basepoint_y"];
                if (basepoint_y != null)
                {
                    using var json = JsonDocument.Parse(basepoint_y.Replace('$', ','));
                    if (json.RootElement.TryGetProperty("value", out var element) && double.TryParse(element.GetRawText(), out double value))
                    {
                        data.basepoint_y = Math.Round(value, 1);
                    }
                }
                string? basepoint_x = reader["basepoint_x"];
                if (basepoint_x != null)
                {
                    using var json = JsonDocument.Parse(basepoint_x.Replace('$', ','));
                    if (json.RootElement.TryGetProperty("value", out var element) && double.TryParse(element.GetRawText(), out double value))
                    {
                        data.basepoint_x = Math.Round(value, 1);
                    }
                }
            }
            else if (reader[0] == "EV_CURVE")
            {
                reader.Read();
                reader.ReadHeader();
                reader.Read();
                string? XMax_Y = reader["XMax_Y"];
                if (XMax_Y != null)
                {
                    using var json = JsonDocument.Parse(XMax_Y.Replace('$', ','));
                    if (json.RootElement.TryGetProperty("value", out var element) && double.TryParse(element.GetRawText(), out double value))
                    {
                        data.XMax_Y = Math.Round(value, 1);
                    }
                }
                string? XMax_X = reader["XMax_X"];
                if (XMax_X != null)
                {
                    using var json = JsonDocument.Parse(XMax_X.Replace('$', ','));
                    if (json.RootElement.TryGetProperty("value", out var element) && double.TryParse(element.GetRawText(), out double value))
                    {
                        var XMax_X_Raw = Math.Round(value, 1);
                        data.XMax_X = XMax_X_Raw >= 2.5 && XMax_X_Raw <= 2.9 ? 2.7 : XMax_X_Raw;
                    }
                }
            }
            else if (reader[0] == "curveDatas")
            {
                int count = reader.GetField<int>(1);
                reader.Read();
                reader.ReadHeader();
                while (reader.Read())
                {
                    if (string.IsNullOrWhiteSpace(reader[0]))
                    {
                        break;
                    }
                    data.x.Add(reader.GetField<double>("x"));
                    data.y.Add(reader.GetField<double>("y"));
                }
            }
        }

        return data;
    }

    internal class Data
    {
        public int Index { get; set; }
        public double basepoint_x { get; set; }
        public double basepoint_y { get; set; }
        public double XMax_X { get; set; }
        public double XMax_Y { get; set; }
        public string Result { get; set; } = string.Empty;
        public List<double> x { get; set; } = new();
        public List<double> y { get; set; } = new();
        public double XDirMax { get; set; }
        public double XDirMin { get; set; }
        public double YDirMax { get; set; }
        public double YDirMin { get; set; }
    }
}
