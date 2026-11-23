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
        if (string.Equals(config["SiteControl:Enable"], "false", StringComparison.OrdinalIgnoreCase)) return;

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
        // 等待PLC更新数值再读寄存器
        await Task.Delay(500).ConfigureAwait(continueOnCapturedContext: false);
        using ModbusTcpClient modbusClient = new ModbusTcpClient();
        string tcpEndpoint = config["Modbus:Endpoint"] ?? throw new ArgumentNullException("Modbus:Endpoint");
        bool bigEndian = config["Modbus:BigEndian"]?.ToLower() == "true";
        modbusClient.Connect(tcpEndpoint, bigEndian ? ModbusEndianness.BigEndian : ModbusEndianness.LittleEndian);
        var values = modbusClient.ReadHoldingRegisters<short>(unitIdentifier: 1, startingAddress: 410, count: 8).ToArray();
        List<string> results = new();
        results.Add(file);
        for (int i = 0; i < values.Length; i++)
        {
            if (i % 2 == 0)
            {
                // 将 410, 412, 414, 416 的 int16 以浮点除法除以 100 后加入数据集
                results.Add(((double)values[i] / 100).ToString());
            }
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
        List<LimitGroup> groups = ReadLimitGroups(config);
        List<Data> data = files.ConvertAll(ReadCsv);
        bool ok = true;
        for (int i = 0; i < data.Count; i++)
        {
            data[i].Index = i + 1;
            int groupIndex = ResolveLimitGroupIndex(config, i);
            if (groupIndex >= groups.Count)
            {
                throw new InvalidOperationException($"LimitGroupMap 引用了不存在的上下限组索引 {groupIndex}，LimitGroups 共 {groups.Count} 组");
            }
            // 报告里的「上下限参数组」用 1 起编号
            data[i].LimitGroup = groupIndex + 1;

            // 起始浮动力、最终浮动力、X/Y 方向力都必须落在本样品所用上下限组的区间内
            if (Judge(groups[groupIndex], data[i]))
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
        report.AddVariable("Limits", groups);
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

    /// <summary>
    /// 判定一个样品是否合格：起始浮动力、最终浮动力、X/Y 方向力都必须落在该样品所用上下限组的区间内。
    /// X/Y 方向力的实测最小值和最大值都要各自与上限、下限比较，不依赖"最小值 ≤ 最大值"这个前提
    /// （PLC 给的数不保证大小顺序，只查一端就会放过越界的数）。
    /// </summary>
    private static bool Judge(LimitGroup limitGroup, Data data)
    {
        return InRange(data.basepoint_y, limitGroup.InitialLowerLimit, limitGroup.InitialUpperLimit)
            && InRange(data.XMax_Y, limitGroup.FinalLowerLimit, limitGroup.FinalUpperLimit)
            && InRange(data.XDirMin, limitGroup.XDirLowerLimit, limitGroup.XDirUpperLimit)
            && InRange(data.XDirMax, limitGroup.XDirLowerLimit, limitGroup.XDirUpperLimit)
            && InRange(data.YDirMin, limitGroup.YDirLowerLimit, limitGroup.YDirUpperLimit)
            && InRange(data.YDirMax, limitGroup.YDirLowerLimit, limitGroup.YDirUpperLimit);
    }

    /// <summary>严格区间判定：等于上/下限都不合格</summary>
    private static bool InRange(double value, double lowerLimit, double upperLimit)
    {
        return value > lowerLimit && value < upperLimit;
    }

    /// <summary>
    /// 读取所有上下限组。配置写成数组形式：[LimitGroups:0]、[LimitGroups:1] ...（索引从 0 开始且连续），
    /// 样品与组的对应关系由 Report:LimitGroupMap 指定。
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    private static List<LimitGroup> ReadLimitGroups(IConfiguration config)
    {
        IConfigurationSection section = config.GetSection("LimitGroups");
        List<IConfigurationSection> groupSections = new();
        foreach (IConfigurationSection child in section.GetChildren())
        {
            groupSections.Add(child);
        }
        if (groupSections.Count == 0)
        {
            throw new InvalidOperationException("未配置 [LimitGroups:0...] 上下限组");
        }

        // 索引必须从 0 开始连续：Get<List<T>>() 是按子节点顺序取值的，跳号会让报告里的「参数组」
        // 编号和配置里的 [LimitGroups:N] 对不上（例如只配了 [LimitGroups:1] 会被当成第 1 组显示）
        for (int i = 0; i < groupSections.Count; i++)
        {
            if (groupSections[i].Key != i.ToString(CultureInfo.InvariantCulture))
            {
                throw new InvalidOperationException($"config.ini 的上下限组索引必须从 0 开始连续，缺少 [LimitGroups:{i}]（下一个是 [LimitGroups:{groupSections[i].Key}]）");
            }
        }

        // 每组 8 项都必须显式填：缺项会被静默绑成 0（老配置没有 X/Y 四项时会让所有样品判 fail），
        // 留空则是 Binder 的通用转换错误、看不出是哪一项，所以这里先逐项检查
        string[] requiredKeys =
        {
            "InitialLowerLimit", "InitialUpperLimit", "FinalLowerLimit", "FinalUpperLimit",
            "XDirLowerLimit", "XDirUpperLimit", "YDirLowerLimit", "YDirUpperLimit",
        };
        foreach (IConfigurationSection groupSection in groupSections)
        {
            foreach (string key in requiredKeys)
            {
                if (string.IsNullOrWhiteSpace(groupSection[key]))
                {
                    throw new InvalidOperationException($"config.ini 缺少 [LimitGroups:{groupSection.Key}] {key}（每组 8 项上下限都必须填数字）");
                }
            }
        }

        List<LimitGroup> groups = section.Get<List<LimitGroup>>() ?? new List<LimitGroup>();
        for (int i = 0; i < groups.Count; i++)
        {
            // 报告里的「参数组」用 1 起编号，配置里的索引是 0 起
            groups[i].Group = i + 1;
        }
        return groups;
    }

    /// <summary>
    /// 第 index 个样品（从 0 开始）使用的上下限组索引，来自 Report:LimitGroupMap（逗号分隔）。
    /// 没配置时统一用第 0 组；配置项比样品数少时循环使用。
    /// </summary>
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
            string token = part.Trim();
            if (token.Length == 0)
            {
                continue;
            }
            // 写错了不能静默跳过：那会让后面的样品整体错位到别的上下限组
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int groupIndex) || groupIndex < 0)
            {
                throw new InvalidOperationException($"Report:LimitGroupMap 里的 \"{token}\" 不是有效的上下限组索引，应写成逗号分隔的非负整数（例如 0,0,1,1）");
            }
            map.Add(groupIndex);
        }

        if (map.Count == 0)
        {
            return 0;
        }
        return map[index % map.Count];
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

    internal class LimitGroup
    {
        /// <summary>报告「参数表」里的参数组编号，从 1 开始（配置文件里是 0 起的索引）</summary>
        public int Group { get; set; }
        public double InitialLowerLimit { get; set; }
        public double InitialUpperLimit { get; set; }
        public double FinalLowerLimit { get; set; }
        public double FinalUpperLimit { get; set; }
        public double XDirLowerLimit { get; set; }
        public double XDirUpperLimit { get; set; }
        public double YDirLowerLimit { get; set; }
        public double YDirUpperLimit { get; set; }
    }

    internal class Data
    {
        public int Index { get; set; }
        public double basepoint_x { get; set; }
        public double basepoint_y { get; set; }
        public double XMax_X { get; set; }
        public double XMax_Y { get; set; }
        public int LimitGroup { get; set; }
        public string Result { get; set; } = string.Empty;
        public List<double> x { get; set; } = new();
        public List<double> y { get; set; } = new();
        public double XDirMax { get; set; }
        public double XDirMin { get; set; }
        public double YDirMax { get; set; }
        public double YDirMin { get; set; }
    }
}
