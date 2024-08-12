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
    public static async Task OnInput(string sn, IConfiguration config)
    {
        // 对接 MES
        using HttpClient httpClient = new HttpClient();
        string endpoint = config["SiteControl:Endpoint"] ?? throw new ArgumentNullException();
        string site = config["SiteControl:MySite"] ?? throw new ArgumentNullException();
        HttpResponseMessage response = await httpClient.PostAsync($"{endpoint}?data={site};{sn}", null);
        response.EnsureSuccessStatusCode();
        SiteControlRes res = await response.Content.ReadFromJsonAsync<SiteControlRes>() ?? throw new InvalidOperationException("HandleSiteControl失败");
        string[] data = res.data.Split(';');

        // 对接 PLC
        using ModbusTcpClient modbusClient = new ModbusTcpClient();
        modbusClient.Connect("192.168.1.1"/*应该设置为大端后PLC把256改1。, ModbusEndianness.BigEndian*/);
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

    public static async Task GenerateReport(string sn, IConfiguration config, List<string> files)
    {
        double InitialLowerLimit = Math.Round(double.Parse(config["Report:InitialLowerLimit"]!), 1);
        double InitialUpperLimit = Math.Round(double.Parse(config["Report:InitialUpperLimit"]!), 1);
        double FinalLowerLimit = Math.Round(double.Parse(config["Report:FinalLowerLimit"]!), 1);
        double FinalUpperLimit = Math.Round(double.Parse(config["Report:FinalUpperLimit"]!), 1);
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
            plot.SavePng("temp.png", 600, 500);
            var worksheet = report.Workbook.Worksheet(1);
            var picture = worksheet.AddPicture("temp.png").MoveTo(worksheet.Cell(row + 28 * i, col));
            File.Delete("temp.png");
        }

        report.SaveAs(ok ? $@"{config["Report:OkDir"]}\OK_{sn}_{DateTime.Now.ToString("yyyyMMddhhmmss")}.xlsx" : $@"{config["Report:NgDir"]}\NG_{sn}_{DateTime.Now.ToString("yyyyMMddhhmmss")}.xlsx");
    }

    private static Data ReadCsv(string path)
    {
        Data data = new();
        using var reader = new CsvReader(new StreamReader(path), new CsvConfiguration(CultureInfo.InvariantCulture) { IgnoreBlankLines = false, MissingFieldFound = null, BadDataFound = null });
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
                    data.basepoint_y = Math.Round(json.RootElement.GetProperty("value").GetDouble(), 1);
                }
                string? basepoint_x = reader["basepoint_x"];
                if (basepoint_x != null)
                {
                    using var json = JsonDocument.Parse(basepoint_x.Replace('$', ','));
                    data.basepoint_x = Math.Round(json.RootElement.GetProperty("value").GetDouble(), 1);
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
                    data.XMax_Y = Math.Round(json.RootElement.GetProperty("value").GetDouble(), 1);
                }
                string? XMax_X = reader["XMax_X"];
                if (XMax_X != null)
                {
                    using var json = JsonDocument.Parse(XMax_X.Replace('$', ','));
                    var XMax_X_Raw = Math.Round(json.RootElement.GetProperty("value").GetDouble(), 1);
                    data.XMax_X = XMax_X_Raw >= 2.5 && XMax_X_Raw <= 2.9 ? 2.7 : XMax_X_Raw;
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
        public List<double> x { get; set; } = [];
        public List<double> y { get; set; } = [];
    }
}
