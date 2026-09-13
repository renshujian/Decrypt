# ~~AGENTS.md~~deepseek悔过书

## 1. 这是什么

工控上位机软件：扫码 → 调 MOM 接口校验条码 → 写 PLC 寄存器 → 采集设备产生的 CSV 曲线数据 → 生成 Excel 报告存本地 → 调 MOM 接口上传工艺参数。

- `Decrypt/` 是唯一的项目，输出 `Report.exe`。分支 `modbus`。
- TFM `net6.0-windows7.0`，`SelfContained=true`，WPF。
- 依赖：ClosedXML.Report 0.2.10 / CsvHelper 33.0.1 / FluentModbus 5.2.0 / Microsoft.CodeAnalysis.CSharp.Scripting 4.3.1 / Microsoft.Extensions.Configuration.Ini 8.0.0 / .Binder 8.0.2 / ScottPlot 5.0.37。

### 文件职责

| 文件 | 说明 |
|---|---|
| `Hooks.cs` | **业务逻辑全在这里**，运行时用 Roslyn 脚本编译 |
| `MainWindow.xaml.cs` | 宿主：编译脚本、扫码输入、`FileSystemWatcher` 监听 CSV、报告队列 |
| `MainWindow.Logging.cs` | 宿主：带接口日志的 `HttpClient`（`DelegatingHandler`） |
| `config.ini` | 现场配置，**GBK 编码** |
| `template.xlsx` | Excel 报告模板，带 `Tests` 命名区域 |

### 运行流程

```
扫码枪 → input 防抖 500ms → Hooks.OnInput(sn, config, http)   // 校验接口 + 写 PLC 400
CSV 落地 → FileSystemWatcher.Created → Hooks.OnCsvCreated(...) // 只返回文件名，宿主入队
队列达到 CsvCount → Hooks.GenerateReport(sn, config, files, http)
                      → 解析判定 → 出 Excel 报告 → 按配置调上传接口
```

---

## 2. 核心机制：Hooks.cs 是脚本，不是普通源文件

这是本项目最大的坑源。

```csharp
// MainWindow.xaml.cs
var hooks = CSharpScript.Create(File.ReadAllText("Hooks.cs"), ScriptOptions.Default.WithReferences(...), typeof(HooksArgs));
var onInput = hooks.ContinueWith<Task>("Hooks.OnInput(sn, config, http)").CreateDelegate();
```

由此产生 5 条硬约束：

**2.1 所有用到的程序集必须登记**
`WithReferences(...)` 里列出的程序集，脚本才能 `using`。新增依赖（例如换掉 ClosedXML）时必须同步在这里加，否则脚本编译报"未能找到类型或命名空间名"。当前列表：Modbus / IConfiguration / HttpClientJsonExtensions / MessageBox / CsvReader / XLTemplate / Plot / ConfigurationBinder。

**2.2 `Hooks.cs` 同时也是项目的普通源文件**
它被 csproj 的默认 `Compile` glob 收进去（不是只当 `None` 拷贝），所以**它必须同时是合法的独立 C#**：不能引用脚本全局变量（`sn`/`config`/`files`/`file`/`index`/`http`）。

**2.3 全局变量只有在宿主表达式里出现过的才能用**
`ContinueWith<T>("Hooks.OnInput(sn, config, http)")` 这个表达式里出现的名字（`sn`、`config`、`files`、`file`、`index`、`http`）在脚本里可用；**没在表达式里出现的新名字会编译失败（CS0120，"对象引用对于非静态的字段是必需的"）**。

> 踩坑记录：给 `HooksArgs` 加 `http` 字段后，脚本里直接写 `http` 报 CS0120；必须在宿主表达式里显式传 `http`（`"Hooks.OnInput(sn, config, http)"`），并作为方法的参数使用。**新增全局变量时，宿主表达式、方法签名、`HooksArgs` 三处要一起改。**

**2.4 脚本委托返回的是嵌套 Task**
`CreateDelegate()` 得到的是 `ScriptRunner<T>`，签名是 `(object globals, CancellationToken)`，返回值是 `Task<Task>` / `Task<Task<T>>`。宿主里的 `await await onInput(...)` 就是为此。改动这部分时不要想当然地"简化"成单个 `await`。

**2.5 脚本内容变化不会自动重新拷贝到输出目录**
`CopyToOutputDirectory="PreserveNewest"` 靠时间戳，实测会漏拷。若改了 Hooks.cs 却发现行为没变，先确认输出目录里的 `Hooks.cs` / `template.xlsx` 是最新的。

---

## 3. Excel 报告（template.xlsx + ClosedXML.Report）

**3.1 表格靠命名区域识别，不是靠"表头下一行"**
模板里有定义名称 `Tests`，指向 `Sheet1!$B$5:$L$6`。名字必须和 `report.AddVariable("Tests", data)` 一致，区域覆盖"表头行 + 数据模板行"。**在 Excel 里改模板时不要删除或改名这个命名区域**（公式 → 名称管理器），否则表格不展开，所有 `{{item.*}}` 会输出 `Unknown identifier 'item'`。

> 踩坑记录：用代码从零画一个模板（漏了命名区域）就是这个症状。正确做法是**在原有 template.xlsx 基础上改**，保留它的样式、列宽、打印设置和命名区域。

**3.2 版式（当前）**
- 第 1 行：`产品编码` / `{{SN}}`；第 3 行空；第 4 行表头；第 5 行数据模板
- 列 B..L：位置 `{{item.Index}}`、上下限参数组 `{{item.LimitGroup}}`、起始浮动力/位移、最终浮动力/位移、起始浮动力下限/上限、最终浮动力下限/上限、结果 `{{item.Result}}`
- **每行数据自带它所用的上下限**，因此有几组 `LimitGroups` 就显示几组（早期模板把两组上下限写死在第 1、2 行，无法扩展）

**3.3 图表插入位置**
`row = 6 + data.Count`，`row + 28 * i`。28 是实测值：**600×500 像素的 PNG，按 96 DPI 插入 = 375 磅高，除以默认行高 13.5 磅 ≈ 28 行**。改图片尺寸或行高就要重算 `高(px)/96*72/13.5`。

**3.4 报告文件名**（需求描述里的"OK/NG产品条码时间"是简写，以代码为准）

```
{OK|NG}_{sn}_{yyyyMMddHHmmss.fff}.xlsx
```

带毫秒是为了避免同秒生成两份互相覆盖。

**3.5 保存很慢**：4 张图约 2.6 秒。**任何"等报告生成"的逻辑必须等文件大小稳定**，不能一看到文件存在就去读——会读到写了一半的包（缺 `drawings`/图片、`sharedStrings` 不全）。

---

## 4. 请求时机与判定逻辑

- **一个产品条码调一次上传接口**，在 `GenerateReport` 里收齐 `CsvCount` 个 CSV 后调用；`OnCsvCreated` 不再调接口，只返回文件名。
- 多组数据参数名加后缀：`起始浮动力-1` … `最终浮动力上限-4`。
- `TestResult` 传条码总结果：有一个 NG 则 `"NG"`，全 OK 则 `"OK"`。
- `Mes:EnableUpload=false` 时不调上传接口，**但仍生成本地报告**。
- 允许 NG 次数**只看本次检测任务**（`data.Count(d => !d.GroupOk)`），不跨扫码累计、不落盘。每次扫码都是独立任务。
- **`CsvCount` 必须等于现场每个条码实际产生的 CSV 数**，否则队列到不了阈值，而新扫码会清空队列 → 不生成任何报告、不上传、无任何提示。这是当前的静默失效点。
- 最后一个 CSV 可能还在写就触发 `GenerateReport`（只在开头 `await Task.Delay(300)`），文件残缺会抛异常导致本次报告与上传全丢。**去掉了 `OnCsvCreated` 里原来的 500ms 等待后，这条链上就没有"等文件写完"的保护了。**

### 线程
`GenerateReport` 开头 `await Task.Delay(300).ConfigureAwait(continueOnCapturedContext: false)` 之后**已经在池线程上**（实测：进入时线程 1/非池线程，Delay 后线程 11/池线程）。**不需要再套 `Task.Run`。**

### MOM 请求
- `HttpClient` 由宿主经 `HooksArgs.http` → 脚本参数传入，`Hooks.cs` 内不 `new HttpClient()`。带日志、超时 60 秒。
- 序列化选项**必须是静态字段**且**从 Web 默认值构造**：
  ```csharp
  private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
  {
      Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 中文不转义，日志可读
  };
  ```
  - 直接 `new JsonSerializerOptions { ... }` 会丢掉 Web 默认值（camelCase 等）→ 报文键名变成 PascalCase。
  - `JsonContent.Create(request, options: ...)` 传入自定义 options 时**不会**补 Web 默认值；`PostAsJsonAsync(endpoint, request, options)` 才有那套规则。
  - 每次请求都 new 会丢掉内部 `JsonTypeInfo` 缓存。
- 请求体由 `HttpLogHandler` 调 `LoadIntoBufferAsync()` 后记录（读请求体前必须缓冲，否则读不到、且内容只能发一次）。

### `ReadCsv` 与基线 `38f2f24` 的关系
结构一致：`EV_1` / `EV_CURVE` / `curveDatas`（含先取 `count`）。差异仅为新增 `unit` 提取带来的副作用：
- `value` 是非数字字符串（如 `"None"`）、缺 `value` 字段、坏 JSON、JSON 数组 —— **两版行为完全相同**
- 空字符串：基线抛 `JsonReaderException`，当前返回 0（更宽松）
- 大写 `Value`/`Unit`：基线取不到，当前能取到

CSV 列名是**现场可配置**的，不同现场格式不同（样例里有 `entry`、`basepoint_y` 等多种叫法），所以"字段名对不上"不代表代码有问题，判据是"与基线一致"。

---

## 5. 编码与文件约定（很容易踩）

| 文件 | 编码 | 行尾 |
|---|---|---|
| `Hooks.cs`、`MainWindow*.cs`、`App.xaml*`、`MainWindow.xaml`、`*.csproj` | **UTF-8 带 BOM** | CRLF |
| `config.ini` | **GBK（无 BOM）** | CRLF |

- 用工具写文件时容易丢掉 BOM 或把行尾变成 LF。提交前用 `git diff` 看是否出现整文件的假差异。
- PowerShell 5.1 没有 `-Encoding utf8NoBOM`；要精确控制编码/行尾就用 .NET API（`UTF8Encoding($true)` + `WriteAllBytes`）。
- 运行态文件 `ReportQueue`、`CurrentSn` 写在程序目录，`NgCounter` 是历史遗留，均未加入 `.gitignore`，注意别误提交。
- **`config.ini` 与 `template.xlsx` 都按当前工作目录解析**，从快捷方式/计划任务启动时 CWD 可能不同（文件放一起的话能规避）。

---

## 6. 这个环境里怎么编译和验证

### 编译
```
dotnet build Decrypt\Decrypt.csproj
```
- dotnet CLI 需要写工作区外的自家 sentinel 与 NuGet 缓存；沙箱若只允许写工作区，会报 `UnauthorizedAccessException: ...toolpath.sentinel`。这不是项目问题，放宽权限或 `DOTNET_CLI_HOME` 指到可写目录即可。
- 本机缓存了所需全部包，离线可还原；没有 NuGet.config。
- **先杀掉正在运行的 `Report.exe`**，否则覆盖 DLL/EXE 会文件占用失败。

### 验证（改业务逻辑后务必做）
推荐建临时验证工程（用完删除），两类：

**A. 直接跑脚本**：引用与 `MainWindow` 相同的程序集，`CSharpScript.Create(File.ReadAllText("Hooks.cs"), ..., typeof(HooksArgs))`，把 `GenerateReport` 当方法调，检查生成的 xlsx（用 ClosedXML 回读）与发给假 MOM 的报文。

**B. 端到端**：把**真实的 `MainWindow.xaml.cs` 链进临时工程**（`<Compile Include="..\Decrypt\MainWindow.xaml.cs" Link="..."/>` + `Page` 链 xaml），配假 MOM（原始 `TcpListener` 实现最小 HTTP/1.1，**要支持 chunked 请求体**）与假 PLC（回合法的 Modbus TCP 响应），再做扫码和 CSV 落地。

B 必须注意：
- **等文件大小稳定**再读报告（见 3.5）
- 校验失败路径会弹 `MessageBox` **阻塞 UI 线程与测试续行**，测试要绕开或用无 UI 的方式
- `App.xaml` 需要 `EnableDefaultApplicationDefinition=false` + `StartupObject` 指定自己的 `Main`

---

## 7. 改不同东西时改哪里

| 需求 | 位置 |
|---|---|
| 上传参数项 / 名称 / 后缀规则 | `UploadProcessParametersAsync` |
| 判定规则（上下限比较） | `Data.LoadGroup` |
| 报告内容与列 | `template.xlsx`（保留 `Tests` 命名区域） |
| 报告文件名与存放目录 | `GenerateExcelReport` |
| 图表尺寸/位置 | `GenerateExcelReport` 里 `plot.SavePng(600,500)` 与 `row + 28 * i` |
| 何时调接口 | `GenerateReport` 末尾的 `Mes:EnableUpload` / `Mes:AllowNgCount` 分支 |
| 现场可调项 | `config.ini`（GBK），注释是给现场看的，要写清楚 |
| 接口日志 | `MainWindow.Logging.cs` |
| 脚本能用哪些库 | `MainWindow.xaml.cs` 的 `WithReferences` |

---

## 8. 血泪教训（方法论）

这次任务里我因为"看起来对"而走错的每一步，都会再次发生：

1. **别在读文件的同时判定它的内容完整**。我基于读到一半的 xlsx（10KB、无图片）断言"`SaveAs` 死锁"，加了没必要的 `Task.Run` 并写了错误注释。真相是写盘要 2.6 秒。
2. **别把测试工具的缺陷当成产品缺陷**。"报告卡住"、"PLC 没写"两次都是测试的问题：一次读了半个文件，一次被模态弹窗挡住。
3. **`await ... ConfigureAwait(false)` 之后就换线程了**，不要凭直觉断言"还在主线程"。
4. **闭源库的行为要实测，不要推理**。`ClosedXML.Report` 靠命名区域识别表格、`JsonContent.Create` 不补 Web 默认值，这两条都是靠最小化实验才确认的，推理全错。
5. **改动要贴着需求做**。我曾自作主张重构模板生成、重算图表位置、加 `Task.Run`、给布尔配置项做"true/是/1/no"式多格式兼容、加 NG 清零按钮与一堆调试用配置键，全部被要求回退。**需求描述是简写，以现有代码/历史提交为准**（例如文件名格式、`row + 28 * i`），布尔配置项直接用 `config.GetValue<bool>(...)`。
6. **回归验证要挑明基线**。`ReadCsv` 的判据是"与 `38f2f24` 一致"，不是我事后找的一版。
7. **改完检查 BOM/行尾**，别让整个文件出现假 diff。
