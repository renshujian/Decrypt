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
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

internal static class Hooks
{
    /// <summary>
    /// MOM 接口的 JSON 序列化选项：以 Web 默认值为基础（camelCase 命名、不区分大小写等，
    /// 与原 PostAsJsonAsync 的行为一致），只把转义器换成不转义中文的那种，便于接口日志直接阅读。
    /// 只创建一次，避免每次请求都新建。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 扫码后触发
    /// </summary>
    /// <param name="sn">扫码枪输入的字符串</param>
    /// <param name="config"></param>
    /// <param name="http">调用 MOM 接口的 HttpClient（由宿主提供，已挂接口日志）</param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public static async Task OnInput(string sn, IConfiguration config, HttpClient http)
    {
        // 调用 MES 校验接口（产品条码验证 VerifyProductSn）
        string endpoint = config["Mes:Endpoint"] ?? throw new ArgumentNullException("Mes:Endpoint");
        string process = config["Mes:Process"] ?? throw new ArgumentNullException("Mes:Process");
        int invOrgId = config.GetValue("Mes:InvOrgId", 1);
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

        VerifyRes res = await PostMomAsync<VerifyRes>(http, endpoint, request);

        // 对接 PLC：校验通过写 0，不通过写 1
        string tcpEndpoint = config["Modbus:Endpoint"] ?? throw new ArgumentNullException("Modbus:Endpoint");
        bool bigEndian = config["Modbus:BigEndian"]?.ToLower() == "true";
        using ModbusTcpClient modbusClient = new ModbusTcpClient();
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
    /// 检测到硬件上传的原始数据文件后触发，把文件排入报告队列
    /// </summary>
    /// <param name="sn"></param>
    /// <param name="config"></param>
    /// <param name="file">文件的绝对路径</param>
    /// <param name="index">该产品内 CSV 的序号，从 0 开始</param>
    /// <param name="http">调用 MOM 接口的 HttpClient（由宿主提供，已挂接口日志）</param>
    /// <returns>文件名</returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static Task<string> OnCsvCreated(string sn, IConfiguration config, string file, int index, HttpClient http)
    {
        return Task.FromResult(file);
    }

    /// <summary>
    /// 报告队列长度达到config["Report:CsvCount"]后触发：生成报告并按配置调用工艺参数上传接口
    /// </summary>
    /// <param name="sn"></param>
    /// <param name="config"></param>
    /// <param name="files">报告队列中保存的原始数据文件路径</param>
    /// <param name="http">调用 MOM 接口的 HttpClient（由宿主提供，已挂接口日志）</param>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public static async Task GenerateReport(string sn, IConfiguration config, List<string> files, HttpClient http)
    {
        // 等待文件写入完成并且不在主线程继续运行。后续的代码有可能并发执行，不允许写固定名称的文件
        await Task.Delay(300).ConfigureAwait(continueOnCapturedContext: false);

        // 读取所有上下限组（数组形式：[LimitGroups:0]、[LimitGroups:1] ...）
        List<LimitGroup> groups = config.GetSection("LimitGroups").Get<List<LimitGroup>>() ?? new List<LimitGroup>();
        if (groups.Count == 0)
        {
            throw new InvalidOperationException("未配置 [LimitGroups:0...] 上下限组");
        }

        List<Data> data = new List<Data>();
        bool ok = true;
        for (int i = 0; i < files.Count; i++)
        {
            Data item = ReadCsv(files[i]);
            item.Index = i + 1;

            // 根据产品内 CSV 位置（i 从 0 开始）确定使用的上下限组索引
            int groupIndex = ResolveLimitGroupIndex(config, i);
            if (groupIndex < 0 || groupIndex >= groups.Count)
            {
                throw new InvalidOperationException($"LimitGroupMap 引用了不存在的上下限组索引 {groupIndex}，LimitGroups 共 {groups.Count} 组");
            }
            item.LoadGroup(groups[groupIndex], groupIndex);

            // 产品条码的结果 = 总结果：有一个 NG 则总结果 NG，全部 OK 则总结果 OK
            if (!item.GroupOk)
            {
                ok = false;
            }
            data.Add(item);
        }

        // 生成报告
        GenerateExcelReport(sn, config, data, ok);

        // 调用工艺参数上传接口
        if (config.GetValue("Mes:EnableUpload", true))
        {
            if (ok)
            {
                // OK 结果正常调用
                await UploadProcessParametersAsync(http, sn, config, data, "OK");
            }
            else
            {
                // NG 结果：本次检测任务的 NG 数超过“允许NG次数”才调用（例：允许 3 次，则第 4 个 NG 起调用）
                // 每次扫码都是独立的检测任务，不受之前状态影响
                int allowNgCount = config.GetValue("Mes:AllowNgCount", 0);
                int ngCount = data.Count(d => !d.GroupOk);
                if (ngCount > allowNgCount)
                {
                    await UploadProcessParametersAsync(http, sn, config, data, "NG");
                }
            }
        }
    }

    /// <summary>
    /// 生成 Excel 报告并保存到本地，文件名格式：OK/NG_产品条码_时间(yyyyMMddHHmmss.fff)
    /// </summary>
    /// <returns>报告文件的绝对路径</returns>
    private static string GenerateExcelReport(string sn, IConfiguration config, List<Data> data, bool ok)
    {
        string dir = (ok ? config["Report:OkDir"] : config["Report:NgDir"]) ?? throw new ArgumentNullException("Report:OkDir/Report:NgDir");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{(ok ? "OK" : "NG")}_{sn}_{DateTime.Now:yyyyMMddHHmmss.fff}.xlsx");

        using var report = new XLTemplate("template.xlsx");
        report.AddVariable("SN", sn);
        report.AddVariable("Tests", data);
        report.Generate();

        // 每组插入一张曲线图
        var worksheet = report.Workbook.Worksheet(1);
        int row = 6 + data.Count;
        int col = 2;
        for (int i = 0; i < data.Count; i++)
        {
            using var plot = new Plot();
            plot.Add.Scatter(data[i].x, data[i].y);
            // 后续的代码有可能并发执行，不允许写固定名称的文件
            string pngName = $"{Guid.NewGuid()}.png";
            plot.SavePng(pngName, 600, 500);
            worksheet.AddPicture(pngName).MoveTo(worksheet.Cell(row + 28 * i, col));
            File.Delete(pngName);
        }

        report.SaveAs(path);
        return path;
    }

    /// <summary>
    /// 调用 MOM 工艺参数上传接口。一个产品条码调用一次，同一条码内的多组数据用“参数名称-1/参数名称-2”区分
    /// </summary>
    private static async Task UploadProcessParametersAsync(HttpClient http, string sn, IConfiguration config, List<Data> data, string testResult)
    {
        string endpoint = config["Mes:Endpoint"] ?? throw new ArgumentNullException("Mes:Endpoint");
        string equipAccountCode = config["Mes:EquipAccountCode"] ?? "";
        string snType = string.IsNullOrWhiteSpace(config["Mes:SnType"]) ? "1" : config["Mes:SnType"]!;
        int invOrgId = config.GetValue("Mes:InvOrgId", 1);
        string language = string.IsNullOrWhiteSpace(config["Mes:Language"]) ? "zh-CN" : config["Mes:Language"]!;
        string method = string.IsNullOrWhiteSpace(config["Mes:Method"]) ? "UploadIctEquipmentWaferData" : config["Mes:Method"]!;

        // 采集设备参数集合（含实际使用的上下限），多组数据加 -N 后缀
        List<object> details = new List<object>();
        for (int i = 0; i < data.Count; i++)
        {
            Data item = data[i];
            string suffix = data.Count > 1 ? "-" + (i + 1) : "";
            details.Add(new { ParameterName = "起始浮动力" + suffix, ParameterValue = item.basepoint_y, Unit = item.basepoint_y_unit });
            details.Add(new { ParameterName = "起始浮动位移" + suffix, ParameterValue = item.basepoint_x, Unit = item.basepoint_x_unit });
            details.Add(new { ParameterName = "最终浮动力" + suffix, ParameterValue = item.XMax_Y, Unit = item.XMax_Y_unit });
            details.Add(new { ParameterName = "最终浮动位移" + suffix, ParameterValue = item.XMax_X, Unit = item.XMax_X_unit });
            details.Add(new { ParameterName = "起始浮动力下限" + suffix, ParameterValue = item.InitialLowerLimit, Unit = "" });
            details.Add(new { ParameterName = "起始浮动力上限" + suffix, ParameterValue = item.InitialUpperLimit, Unit = "" });
            details.Add(new { ParameterName = "最终浮动力下限" + suffix, ParameterValue = item.FinalLowerLimit, Unit = "" });
            details.Add(new { ParameterName = "最终浮动力上限" + suffix, ParameterValue = item.FinalUpperLimit, Unit = "" });
        }

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
                        TestResult = testResult,
                        Details = details,
                    },
                },
                new { Value = invOrgId },
            },
            Method = method,
            Context = new { InvOrgId = invOrgId, Language = language },
        };

        UploadRes res = await PostMomAsync<UploadRes>(http, endpoint, request);
        if (!res.Success)
        {
            throw new InvalidOperationException("上传工艺参数失败：" + (res.Message ?? ""));
        }
    }

    /// <summary>
    /// 调用 MOM 接口。HttpClient 由宿主（MainWindow）传入，已挂接口日志
    /// </summary>
    private static async Task<T> PostMomAsync<T>(HttpClient http, string endpoint, object request)
    {
        HttpResponseMessage response = await http.PostAsJsonAsync(endpoint, request, JsonOptions).ConfigureAwait(continueOnCapturedContext: false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(continueOnCapturedContext: false) ?? throw new InvalidOperationException("MOM 接口返回为空");
    }

    internal record class UploadRes(bool Success, string? Message);

    private static int ResolveLimitGroupIndex(IConfiguration config, int index)
    {
        string? mapRaw = config["Report:LimitGroupMap"];
        if (string.IsNullOrWhiteSpace(mapRaw))
        {
            return 0;
        }

        List<int> map = new List<int>();
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
        public int Index { get; set; }
        public double basepoint_x { get; set; }
        public string basepoint_x_unit { get; set; } = "";
        public double basepoint_y { get; set; }
        public string basepoint_y_unit { get; set; } = "";
        public double XMax_X { get; set; }
        public string XMax_X_unit { get; set; } = "";
        public double XMax_Y { get; set; }
        public string XMax_Y_unit { get; set; } = "";
        public int LimitGroup { get; set; }
        public bool GroupOk { get; set; }
        public string Result { get; set; } = string.Empty;
        public double InitialLowerLimit { get; set; }
        public double InitialUpperLimit { get; set; }
        public double FinalLowerLimit { get; set; }
        public double FinalUpperLimit { get; set; }
        public List<double> x { get; set; } = new List<double>();
        public List<double> y { get; set; } = new List<double>();

        /// <summary>记录该组数据使用的上下限并判定是否合格</summary>
        public void LoadGroup(LimitGroup group, int groupIndex)
        {
            LimitGroup = groupIndex;
            InitialLowerLimit = group.InitialLowerLimit;
            InitialUpperLimit = group.InitialUpperLimit;
            FinalLowerLimit = group.FinalLowerLimit;
            FinalUpperLimit = group.FinalUpperLimit;
            GroupOk = basepoint_y > group.InitialLowerLimit && basepoint_y < group.InitialUpperLimit
                && XMax_Y > group.FinalLowerLimit && XMax_Y < group.FinalUpperLimit;
            Result = GroupOk ? "pass" : "fail";
        }
    }
}
