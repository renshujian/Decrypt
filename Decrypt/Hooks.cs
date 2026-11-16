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
    /// 扫码后触发：调用 MES 站点校验接口，并把结果写 PLC 寄存器
    /// </summary>
    /// <param name="sn">扫码枪输入的字符串</param>
    /// <param name="config"></param>
    /// <param name="http">调用 MES 接口的 HttpClient（由宿主提供，已挂接口日志）</param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public static async Task OnInput(string sn, IConfiguration config, HttpClient http)
    {
        if (string.Equals(config["SiteControl:Enable"], "false", StringComparison.OrdinalIgnoreCase)) return;

        // 对接 MES
        string endpoint = config["SiteControl:Endpoint"] ?? throw new ArgumentNullException("SiteControl:Endpoint");
        string site = config["SiteControl:MySite"] ?? throw new ArgumentNullException("SiteControl:MySite");
        HttpResponseMessage response = await http.PostAsync($"{endpoint}?data={site};{sn}", null);
        response.EnsureSuccessStatusCode();
        SiteControlRes res = await response.Content.ReadFromJsonAsync<SiteControlRes>() ?? throw new InvalidOperationException("HandleSiteControl失败");
        string[] data = res.data.Split(';');

        // 对接 PLC：校验通过写 0，不通过写 1
        string tcpEndpoint = config["Modbus:Endpoint"] ?? throw new ArgumentNullException("Modbus:Endpoint");
        bool bigEndian = config["Modbus:BigEndian"]?.ToLower() == "true";
        using ModbusTcpClient modbusClient = new ModbusTcpClient();
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
    /// 检测到硬件上传的原始数据文件后触发，把文件排入报告队列
    /// </summary>
    /// <param name="sn"></param>
    /// <param name="config"></param>
    /// <param name="file">文件的绝对路径</param>
    /// <param name="index">该产品内 CSV 的序号，从 0 开始</param>
    /// <param name="http">调用 MES 接口的 HttpClient（由宿主提供，已挂接口日志）</param>
    /// <returns>文件名</returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static Task<string> OnCsvCreated(string sn, IConfiguration config, string file, int index, HttpClient http)
    {
        return Task.FromResult(file);
    }

    /// <summary>
    /// 报告队列长度达到 config["Report:SampleCount"] * config["Report:RepeatCount"]（一个产品的 CSV 总数）后触发：生成报告。
    /// </summary>
    /// <param name="sn"></param>
    /// <param name="config"></param>
    /// <param name="files">报告队列中保存的原始数据文件路径，按 CSV 顺序（样品优先：样品1的 repeat1..N，样品2的 repeat1..N ...）</param>
    /// <param name="http">宿主传入的 HttpClient（报告流程不再调接口，保留参数以保持宿主脚本调用签名不变）</param>
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

        int sampleCount = config.GetValue("Report:SampleCount", 1);
        int repeatCount = config.GetValue("Report:RepeatCount", 1);
        if (sampleCount < 1 || repeatCount < 1)
        {
            throw new InvalidOperationException($"Report:SampleCount={sampleCount}、Report:RepeatCount={repeatCount} 必须大于 0");
        }
        int csvCount = sampleCount * repeatCount;
        if (files.Count != csvCount)
        {
            throw new InvalidOperationException($"报告队列中有 {files.Count} 个 CSV，与 SampleCount({sampleCount}) * RepeatCount({repeatCount}) = {csvCount} 不符");
        }

        List<Sample> samples = new List<Sample>(sampleCount);
        bool ok = true;
        for (int i = 0; i < sampleCount; i++)
        {
            // 根据样品位置（i 从 0 开始）确定使用的上下限组索引
            int groupIndex = ResolveLimitGroupIndex(config, i);
            if (groupIndex < 0 || groupIndex >= groups.Count)
            {
                throw new InvalidOperationException($"LimitGroupMap 引用了不存在的上下限组索引 {groupIndex}，LimitGroups 共 {groups.Count} 组");
            }

            Sample sample = new Sample { Index = i + 1, LimitGroup = groupIndex };
            for (int j = 0; j < repeatCount; j++)
            {
                sample.Repeats.Add(ReadCsv(files[i * repeatCount + j]));
            }
            sample.Judge(groups[groupIndex], repeatCount);

            // 产品条码的结果 = 总结果：有一个 NG 则总结果 NG，全部 OK 则总结果 OK
            if (!sample.GroupOk)
            {
                ok = false;
            }
            samples.Add(sample);
        }

        GenerateExcelReport(sn, config, samples, ok);
    }

    /// <summary>
    /// 生成 Excel 报告并保存到本地，文件名格式：OK/NG_产品条码_时间(yyyyMMddHHmmss.fff)
    /// 表格每个样品一行（SampleCount 行），图表每个 CSV 一张（SampleCount * RepeatCount 张）
    /// </summary>
    /// <returns>报告文件的绝对路径</returns>
    private static string GenerateExcelReport(string sn, IConfiguration config, List<Sample> samples, bool ok)
    {
        string dir = (ok ? config["Report:OkDir"] : config["Report:NgDir"]) ?? throw new ArgumentNullException("Report:OkDir/Report:NgDir");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{(ok ? "OK" : "NG")}_{sn}_{DateTime.Now:yyyyMMddHHmmss.fff}.xlsx");

        using var report = new XLTemplate("template.xlsx");
        report.AddVariable("SN", sn);
        report.AddVariable("Tests", samples);
        report.Generate();

        // 图表按“一行一个样品、一列一次 repeat”排列，顺序就是 CSV 入队顺序（样品优先）：
        // 样品1的 repeat1..N 在同一行从左到右，换行才是样品2，读图顺序与报告队列顺序一致。
        // 行距 28 行 = 600x500 图片按 96 DPI 插入后的高度(375 磅) / 默认行高(13.5 磅)；
        // 列距 5 列 = 锚点 B / G / L（图片宽 450 磅，B..F 列宽合计约等于图片宽度）
        var worksheet = report.Workbook.Worksheet(1);
        const int chartRowStep = 28;
        const int chartColumnStep = 5;
        int firstRow = 6 + samples.Count;
        for (int s = 0; s < samples.Count; s++)
        {
            for (int r = 0; r < samples[s].Repeats.Count; r++)
            {
                using var plot = new Plot();
                plot.Add.Scatter(samples[s].Repeats[r].x, samples[s].Repeats[r].y);
                // 后续的代码有可能并发执行，不允许写固定名称的文件
                string pngName = $"{Guid.NewGuid()}.png";
                plot.SavePng(pngName, 600, 500);
                worksheet.AddPicture(pngName).MoveTo(worksheet.Cell(firstRow + chartRowStep * s, 2 + chartColumnStep * r));
                File.Delete(pngName);
            }
        }

        report.SaveAs(path);
        return path;
    }

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
                data.basepoint_y = ReadValue(reader["basepoint_y"]);
                data.basepoint_x = ReadValue(reader["basepoint_x"]);
            }
            else if (reader[0] == "EV_CURVE")
            {
                reader.Read();
                reader.ReadHeader();
                reader.Read();
                data.XMax_Y = ReadValue(reader["XMax_Y"]);
                double xMaxX = ReadValue(reader["XMax_X"]);
                data.XMax_X = xMaxX >= 2.5 && xMaxX <= 2.9 ? 2.7 : xMaxX;
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

    /// <summary>
    /// 取 CSV 单元格里 {value, unit} JSON 的数值（保留 1 位小数），格式不认识的按 0 处理
    /// </summary>
    private static double ReadValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }
        using var json = JsonDocument.Parse(raw.Replace('$', ','));
        JsonElement root = json.RootElement;
        JsonElement v;
        if ((root.TryGetProperty("value", out v) || root.TryGetProperty("Value", out v)) && double.TryParse(v.GetRawText(), out double parsed))
        {
            return Math.Round(parsed, 1);
        }
        return 0;
    }

    internal class LimitGroup
    {
        public double InitialLowerLimit { get; set; }
        public double InitialUpperLimit { get; set; }
        public double FinalLowerLimit { get; set; }
        public double FinalUpperLimit { get; set; }
    }

    /// <summary>一个 repeat：从一个 CSV 解析出的原始数据（曲线用于贴图）</summary>
    internal class Data
    {
        public double basepoint_x { get; set; }
        public double basepoint_y { get; set; }
        public double XMax_X { get; set; }
        public double XMax_Y { get; set; }
        public List<double> x { get; set; } = new List<double>();
        public List<double> y { get; set; } = new List<double>();
    }

    /// <summary>
    /// 一个样品：由 RepeatCount 个 repeat（CSV）组成。判定规则：第 1 个 repeat 不参与结果判定，
    /// 其余 repeat 中分别取最小的起始浮动力、最终浮动力，各自与该样品的上下限比较；位移取该最小浮动力
    /// 所在那次 repeat 的位移。RepeatCount=1（没有其它 repeat 可判定）时退化为用第 1 个 repeat 判定。
    /// </summary>
    internal class Sample
    {
        /// <summary>样品序号，从 1 开始（报告里的“位置”）</summary>
        public int Index { get; set; }
        /// <summary>该样品使用的上下限组索引（对应 [LimitGroups:N]）</summary>
        public int LimitGroup { get; set; }

        // 以下属性名与 template.xlsx 里的 {{item.*}} 对应，不要重命名
        public double basepoint_x { get; set; }
        public double basepoint_y { get; set; }
        public double XMax_X { get; set; }
        public double XMax_Y { get; set; }
        public double InitialLowerLimit { get; set; }
        public double InitialUpperLimit { get; set; }
        public double FinalLowerLimit { get; set; }
        public double FinalUpperLimit { get; set; }
        public bool GroupOk { get; set; }
        public string Result { get; set; } = string.Empty;

        /// <summary>该样品内所有 repeat 的原始数据，顺序即 CSV 顺序（报告按此顺序贴图）</summary>
        public List<Data> Repeats { get; } = new List<Data>();

        /// <summary>记录该样品使用的上下限并判定；repeatCount 用来确定第一个参与判定的 repeat</summary>
        public void Judge(LimitGroup group, int repeatCount)
        {
            InitialLowerLimit = group.InitialLowerLimit;
            InitialUpperLimit = group.InitialUpperLimit;
            FinalLowerLimit = group.FinalLowerLimit;
            FinalUpperLimit = group.FinalUpperLimit;

            // 第 1 个 repeat 不参与判定；只有一个 repeat 时退化用它
            int first = repeatCount > 1 ? 1 : 0;
            Data minInitial = Repeats[first];
            Data minFinal = Repeats[first];
            for (int i = first + 1; i < Repeats.Count; i++)
            {
                if (Repeats[i].basepoint_y < minInitial.basepoint_y)
                {
                    minInitial = Repeats[i];
                }
                if (Repeats[i].XMax_Y < minFinal.XMax_Y)
                {
                    minFinal = Repeats[i];
                }
            }

            // 力值取后续 repeat 的最小值，位移取该最小浮动力所在那次 repeat 的位移
            basepoint_y = minInitial.basepoint_y;
            basepoint_x = minInitial.basepoint_x;
            XMax_Y = minFinal.XMax_Y;
            XMax_X = minFinal.XMax_X;

            GroupOk = basepoint_y > group.InitialLowerLimit && basepoint_y < group.InitialUpperLimit
                && XMax_Y > group.FinalLowerLimit && XMax_Y < group.FinalUpperLimit;
            Result = GroupOk ? "pass" : "fail";
        }
    }
}
