using FluentModbus;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Windows;

internal static class Hooks
{
    public static async Task OnInput(string sn, IConfiguration config)
    {
        using HttpClient httpClient = new HttpClient();
        string endpoint = config.GetRequiredSection("SiteControl")["Endpoint"] ?? throw new ArgumentNullException();
        string site = config.GetRequiredSection("SiteControl")["MySite"] ?? throw new ArgumentNullException();
        HttpResponseMessage response = await httpClient.PostAsync($"{endpoint}?data={site};{sn}", null);
        response.EnsureSuccessStatusCode();
        SiteControlRes res = await response.Content.ReadFromJsonAsync<SiteControlRes>() ?? throw new InvalidOperationException("HandleSiteControl失败");
        string[] data = res.data.Split(';');
        if (data[0] == "OK")
        {
            using ModbusTcpClient modbusClient = new ModbusTcpClient();
            modbusClient.Connect("192.168.1.1");
            modbusClient.WriteSingleRegister(1, 400, 0);
            modbusClient.Disconnect();
            // csv文件名的OK/NG是本次测量结果，不是SiteControl的OK/NG吧？
        }
        else if (data[0] == "NG")
        {
            using ModbusTcpClient modbusClient = new ModbusTcpClient();
            modbusClient.Connect("192.168.1.1");
            modbusClient.WriteSingleRegister(1, 400, 1);
            modbusClient.Disconnect();
            MessageBox.Show(data[1]);
        }
    }

    internal record class SiteControlRes(string msg, int code, string data);

    public static async Task OnFileCreated(string sn, FileInfo file)
    {
        // 文件名为OK_{seq}_{timestamp}.csv，替换为OK_{sn}_{timestamp}.csv
        string[] nameParts = file.Name.Split('_');
        nameParts[1] = sn;
        string newName = string.Join('_', nameParts);
        file.MoveTo(Path.Combine(file.DirectoryName, newName));
    }
}
