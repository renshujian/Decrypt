using CsvHelper;
using CsvHelper.Configuration;
using FluentModbus;
using Microsoft.Extensions.Configuration;
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
        if (string.Equals(config["Mes:Enable"], "false", StringComparison.OrdinalIgnoreCase)) return;

        // 调用 MES 校验接口（产品条码验证 VerifyProductSn）
        using HttpClient httpClient = new HttpClient();
        string endpoint = config["Mes:Endpoint"] ?? throw new ArgumentNullException("Mes:Endpoint");
        string process = config["Mes:Process"] ?? throw new ArgumentNullException("Mes:Process");
        int invOrgId = int.TryParse(config["Mes:InvOrgId"], out int org) ? org : 1;
        string language = string.IsNullOrWhiteSpace(config["Mes:Language"]) ? "zh-CN" : config["Mes:Language"]!;

        var request = new
        {
            ApiType = "MomApiController",
            Parameters = new object[]
            {
                new { Value = new { Sn = sn, Process = process } },
                new { Value = invOrgId },
            },
            Method = "VerifyProductSn",
            Context = new { InvOrgId = invOrgId, Language = language },
        };

        HttpResponseMessage response = await httpClient.PostAsJsonAsync(endpoint, request);
        response.EnsureSuccessStatusCode();
        VerifyRes res = await response.Content.ReadFromJsonAsync<VerifyRes>() ?? throw new InvalidOperationException("VerifyProductSn 返回为空");

        // 对接 PLC：校验通过写 0，不通过写 1
        using ModbusTcpClient modbusClient = new ModbusTcpClient();
        string tcpEndpoint = config["Modbus:Endpoint"] ?? throw new ArgumentNullException("Modbus:Endpoint");
        bool bigEndian = config["Modbus:BigEndian"]?.ToLower() == "true";
        modbusClient.Connect(tcpEndpoint, bigEndian ? ModbusEndianness.BigEndian : ModbusEndianness.LittleEndian);
        if (res.Success && IsValidResult(res.Result))
        {
            modbusClient.WriteSingleRegister(unitIdentifier: 1, registerAddress: 400, value: 0);
        }
        else
        {
            string message = !string.IsNullOrWhiteSpace(res.Message)
                ? res.Message!
                : res.Result.ValueKind == JsonValueKind.False ? "条码工序不一致" : "条码校验失败";
            MessageBox.Show(message);
            modbusClient.WriteSingleRegister(unitIdentifier: 1, registerAddress: 400, value: 1);
        }
        modbusClient.Disconnect();
    }

    private static bool IsValidResult(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.False) return false;
        if (result.ValueKind == JsonValueKind.Null || result.ValueKind == JsonValueKind.Undefined) return false;
        if (result.ValueKind == JsonValueKind.String)
        {
            string s = result.GetString() ?? "";
            return !string.IsNullOrWhiteSpace(s) && !s.Equals("false", StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    internal record class VerifyRes(bool Success, string? Message, JsonElement Result);

    /// <summary>
    /// 检测到硬件上传的原始数据文件后触发，解析参数并通过上传工艺参数接口上传
    /// </summary>
    /// <param name="sn"></param>
    /// <param name="config"></param>
    /// <param name="file">文件的绝对路径</param>
    /// <param name="index">该产品内 CSV 的序号，从 0 开始</param>
    /// <returns>上传成功后返回文件名</returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static async Task<string> OnCsvCreated(string sn, IConfiguration config, string file, int index)
    {
        // 等待文件写入完成后再解析
        await Task.Delay(500).ConfigureAwait(continueOnCapturedContext: false);

        // 解析 CSV 得到测量参数
        Data data = ReadCsv(file);

        // 读取所有上下限组（数组形式：[LimitGroups:0]、[LimitGroups:1] ...）
        List<LimitGroup> groups = config.GetSection("LimitGroups").Get<List<LimitGroup>>() ?? new List<LimitGroup>();
        if (groups.Count == 0)
        {
            throw new InvalidOperationException("未配置 [LimitGroups:0...] 上下限组");
        }

        // 根据产品内 CSV 顺序（index 从 0 开始）确定使用的上下限组索引
        int groupIndex = ResolveLimitGroupIndex(config, index);
        if (groupIndex < 0 || groupIndex >= groups.Count)
        {
            throw new InvalidOperationException($"LimitGroupMap 引用了不存在的上下限组索引 {groupIndex}，LimitGroups 共 {groups.Count} 组");
        }
        LimitGroup group = groups[groupIndex];

        // 判定是否合格
        bool ok = data.basepoint_y > group.InitialLowerLimit && data.basepoint_y < group.InitialUpperLimit
            && data.XMax_Y > group.FinalLowerLimit && data.XMax_Y < group.FinalUpperLimit;

        // 采集设备参数集合（含实际使用的上下限）
        List<object> details = new()
        {
            new { ParameterName = "起始浮动力", ParameterValue = data.basepoint_y, Unit = data.basepoint_y_unit },
            new { ParameterName = "起始浮动位移", ParameterValue = data.basepoint_x, Unit = data.basepoint_x_unit },
            new { ParameterName = "最终浮动力", ParameterValue = data.XMax_Y, Unit = data.XMax_Y_unit },
            new { ParameterName = "最终浮动位移", ParameterValue = data.XMax_X, Unit = data.XMax_X_unit },
            new { ParameterName = "起始浮动力下限", ParameterValue = group.InitialLowerLimit, Unit = "" },
            new { ParameterName = "起始浮动力上限", ParameterValue = group.InitialUpperLimit, Unit = "" },
            new { ParameterName = "最终浮动力下限", ParameterValue = group.FinalLowerLimit, Unit = "" },
            new { ParameterName = "最终浮动力上限", ParameterValue = group.FinalUpperLimit, Unit = "" },
        };

        string endpoint = config["Mes:Endpoint"] ?? throw new ArgumentNullException("Mes:Endpoint");
        string equipAccountCode = config["Mes:EquipAccountCode"] ?? "";
        string snType = string.IsNullOrWhiteSpace(config["Mes:SnType"]) ? "1" : config["Mes:SnType"]!;
        int invOrgId = int.TryParse(config["Mes:InvOrgId"], out int org) ? org : 1;
        string language = string.IsNullOrWhiteSpace(config["Mes:Language"]) ? "zh-CN" : config["Mes:Language"]!;
        string method = string.IsNullOrWhiteSpace(config["Mes:Method"]) ? "UploadIctEquipmentWaferData" : config["Mes:Method"]!;

        var request = new
        {
            ApiType = "MomApiController",
            Parameters = new object[]
            {
                new
                {
                    Value = new
                    {
                        EquipAccountCode = equipAccountCode,
                        Sn = sn,
                        SnType = snType,
                        CollectionDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        TestResult = ok ? "OK" : "NG",
                        Details = details,
                    },
                },
                new { Value = invOrgId },
            },
            Method = method,
            Context = new { InvOrgId = invOrgId, Language = language },
        };

        using HttpClient httpClient = new HttpClient();
        HttpResponseMessage response = await httpClient.PostAsJsonAsync(endpoint, request);
        response.EnsureSuccessStatusCode();
        UploadRes res = await response.Content.ReadFromJsonAsync<UploadRes>() ?? throw new InvalidOperationException("上传工艺参数返回为空");
        if (!res.Success)
        {
            throw new InvalidOperationException("上传工艺参数失败：" + (res.Message ?? ""));
        }

        return file;
    }

    /// <summary>
    /// 报告队列长度达到config["Report:CsvCount"]后触发
    /// </summary>
    /// <param name="sn"></param>
    /// <param name="config"></param>
    /// <param name="files">报告队列中保存的字符串</param>
    public static async Task GenerateReport(string sn, IConfiguration config, List<string> files)
    {
        await Task.CompletedTask;
    }

    internal record class UploadRes(bool Success, string? Message);

    private static int ResolveLimitGroupIndex(IConfiguration config, int index)
    {
        string? mapRaw = config["Report:LimitGroupMap"];
        if (string.IsNullOrWhiteSpace(mapRaw))
        {
            return 0;
        }

        List<int> map = new();
        foreach (string part in mapRaw.Split(','))
        {
            if (int.TryParse(part.Trim(), out int n) && n >= 0)
            {
                map.Add(n);
            }
        }

        if (map.Count == 0)
        {
            return 0;
        }
        // 超出配置长度时循环使用
        return map[index % map.Count];
    }

    private static Data ReadCsv(string file)
    {
        Data data = new();
        using var reader = new CsvReader(new StreamReader(file), new CsvConfiguration(CultureInfo.InvariantCulture) { IgnoreBlankLines = false, MissingFieldFound = null, BadDataFound = null });
        while (reader.Read())
        {
            if (reader[0] == "EV_1")
            {
                reader.Read();
                reader.ReadHeader();
                reader.Read();
                (data.basepoint_y, data.basepoint_y_unit) = ReadValueUnit(reader["basepoint_y"]);
                (data.basepoint_x, data.basepoint_x_unit) = ReadValueUnit(reader["basepoint_x"]);
            }
            else if (reader[0] == "EV_CURVE")
            {
                reader.Read();
                reader.ReadHeader();
                reader.Read();
                (data.XMax_Y, data.XMax_Y_unit) = ReadValueUnit(reader["XMax_Y"]);
                (double xMaxX, string xMaxXUnit) = ReadValueUnit(reader["XMax_X"]);
                data.XMax_X = xMaxX >= 2.5 && xMaxX <= 2.9 ? 2.7 : xMaxX;
                data.XMax_X_unit = xMaxXUnit;
            }
        }
        return data;
    }

    private static (double Value, string Unit) ReadValueUnit(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (0, "");
        }
        using var json = JsonDocument.Parse(raw.Replace('$', ','));
        JsonElement root = json.RootElement;
        double value = 0;
        string unit = "";

        JsonElement v;
        if ((root.TryGetProperty("value", out v) || root.TryGetProperty("Value", out v)) && double.TryParse(v.GetRawText(), out double parsed))
        {
            value = parsed;
        }

        JsonElement u;
        if (root.TryGetProperty("unit", out u) || root.TryGetProperty("Unit", out u))
        {
            unit = u.ValueKind == JsonValueKind.String ? (u.GetString() ?? "") : u.GetRawText();
        }

        return (Math.Round(value, 1), unit);
    }

    internal class LimitGroup
    {
        public double InitialLowerLimit { get; set; }
        public double InitialUpperLimit { get; set; }
        public double FinalLowerLimit { get; set; }
        public double FinalUpperLimit { get; set; }
    }

    internal class Data
    {
        public double basepoint_x { get; set; }
        public string basepoint_x_unit { get; set; } = "";
        public double basepoint_y { get; set; }
        public string basepoint_y_unit { get; set; } = "";
        public double XMax_X { get; set; }
        public string XMax_X_unit { get; set; } = "";
        public double XMax_Y { get; set; }
        public string XMax_Y_unit { get; set; } = "";
    }
}
