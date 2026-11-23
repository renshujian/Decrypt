# XY 方向力上下限 + 多组参数（Report 1.3.1）

## 1. 需求

1. X 方向力、Y 方向力各自有上/下限参数，参与 `Result` 的 pass/fail 判定。
2. 上下限参数改为「多组」，报告里的参数表**一组参数一行**，组数可扩展（不再写死两组）。

## 2. 涉及文件

| 文件 | 改动 |
|---|---|
| `Decrypt/Hooks.cs` | 判定抽成 `Judge` / `InRange`；按 `[LimitGroups:N]` + `Report:LimitGroupMap` 读取和使用多组参数；配置缺项逐项报错 |
| `Decrypt/config.ini` | `[LimitGroup1]/[LimitGroup2]` → `[LimitGroups:0]/[LimitGroups:1]`，每组 8 项；`[Report]` 新增 `LimitGroupMap` |
| `Decrypt/template.xlsx` | Sheet1 只删掉第 1、2 行的参数列（其余与改动前一致）；新增「参数」工作表做参数表 |
| `Decrypt/Decrypt.csproj` | 版本 `1.3.0` → `1.3.1` |

## 3. 判定规则

每个样品按 `Report:LimitGroupMap` 取一组上下限，以下条件**全部**满足才 `pass`（严格不等号，实测值等于限值即 `fail`）：

```
basepoint_y        ∈ (InitialLowerLimit, InitialUpperLimit)
XMax_Y             ∈ (FinalLowerLimit, FinalUpperLimit)
XDirMin、XDirMax   都 ∈ (XDirLowerLimit, XDirUpperLimit)
YDirMin、YDirMax   都 ∈ (YDirLowerLimit, YDirUpperLimit)
```

X/Y 方向力的**实测最大值和最小值各自**与上限、下限比较，不依赖「最小值 ≤ 最大值」这个前提（PLC 给的数不保证大小顺序）。任一样品 `fail`，报告文件名记 `NG_`，否则 `OK_`。

## 4. 配置（`config.ini`，GBK 编码）

```ini
[Report]
CsvCount=12
# 每个样品（每份原始数据）使用的上下限组索引（逗号分隔，按 CSV 顺序；索引对应 [LimitGroups:0]、[LimitGroups:1] ...，超出长度时循环使用）
LimitGroupMap=0,0,0,0,1,1,1,1,0,0,0,0

[LimitGroups:0]
InitialLowerLimit=
InitialUpperLimit=
FinalLowerLimit=
FinalUpperLimit=
XDirLowerLimit=
XDirUpperLimit=
YDirLowerLimit=
YDirUpperLimit=

[LimitGroups:1]
（同上，8 项）
```

| 键 | 含义 |
|---|---|
| `InitialLowerLimit` / `InitialUpperLimit` | 起始浮动力下限 / 上限 |
| `FinalLowerLimit` / `FinalUpperLimit` | 最终浮动力下限 / 上限 |
| `XDirLowerLimit` / `XDirUpperLimit` | X 方向力下限 / 上限 |
| `YDirLowerLimit` / `YDirUpperLimit` | Y 方向力下限 / 上限 |
| `Report:LimitGroupMap` | 样品 → 参数组索引（0 起），逗号分隔，按 CSV 顺序；比 `CsvCount` 短时循环使用；不填则全部用第 0 组 |

组数 = `[LimitGroups:0...]` 的节数，加一组就加一节（索引必须从 0 开始**连续**）。

## 5. 报告版式

- **Sheet1**：`B1=产品编码`、`B2={{SN}}`；第 4/5 行是测量表格（命名区域 `Tests` = `Sheet1!$B$5:$L$6`，未改动），每样品一行，列为位置/起始浮动力/位移/最终浮动力/位移/X最小力/X最大力/Y最小力/Y最大力/上下限参数组/结果；曲线图从测量表下方第 2 行开始（12 个样品时是第 18 行）、每样品间隔 28 行（本次未改）。
- **参数**（新增工作表）：一行一组参数，列为 `参数组 / 起始浮动力下限 / 起始浮动力上限 / 最终浮动力下限 / 最终浮动力上限 / X方向力下限 / X方向力上限 / Y方向力下限 / Y方向力上限`，组数变多少就展开多少行。
  - 命名区域 `Limits` = `参数!$B$4:$J$5`。**必须是两行**（第 4 行是模板行）：只选一行时 ClosedXML.Report 会按列横向展开。
  - 参数表列的是**所有已配置的组**（含没有被任何样品引用的组）。
  - 报告里「参数组」编号从 1 开始，配置索引从 0 开始。

## 6. 升级 / 现场注意

1. **`config.ini` 必须一起换**：老配置没有 `[LimitGroups:0...]` 会直接报 `未配置 [LimitGroups:0...] 上下限组`。
2. 每组 8 项都必须填数字，缺项或留空会报：`config.ini 缺少 [LimitGroups:0] InitialLowerLimit（每组 8 项上下限都必须填数字）`（8 项都有此提示；值不是数字时仍是 Binder 的转换错误，消息里带完整键路径）。
3. `LimitGroupMap` 写错不放过：非法项报 `Report:LimitGroupMap 里的 "x" 不是有效的上下限组索引`，引用不存在的组报 `LimitGroupMap 引用了不存在的上下限组索引 5，LimitGroups 共 2 组`；节索引跳号报 `config.ini 的上下限组索引必须从 0 开始连续…`。
4. 部署要同时更新 `Report.exe`、`Hooks.cs`、`template.xlsx`、`config.ini`（`Hooks.cs` 是运行时用 Roslyn 编译的脚本，输出目录那份也要换）。
5. `Hooks.cs`、`template.xlsx` 按当前工作目录解析，从快捷方式/计划任务启动时注意 CWD。

## 7. 验证记录

用与 `MainWindow` 相同的方式（同 `WithReferences`、同 `Hooks.GenerateReport(sn, config, files)` 表达式）脚本编译 `Hooks.cs` 后跑合成数据：

| 场景 | 结果 |
|---|---|
| 1 组 / 2 组 / 3 组 / 12 组 | 参数表分别展开 1/2/3/12 行，限值与 `[LimitGroups:N]` 一一对应（含 `:10`、`:11` 两位数索引） |
| 2 组 + 地图（现场形态） | 12 行 `Result`、`参数组` 列与地图一致；12 张图、第一张锚定 B18；文件名前缀随结果变化 |
| X/Y 最小值越上限、最大值低于下限、等于边界 | 均判 `fail`（旧逻辑会放过「最小值 > 最大值」这种颠倒数据） |
| 缺/留空配置项（抽查 `InitialUpperLimit`、`FinalLowerLimit`、`YDirUpperLimit`） | 报 `config.ini 缺少 [LimitGroups:0] <键>（每组 8 项上下限都必须填数字）`，8 项共用同一段校验 |
| `LimitGroupMap` 非法项、越界、节索引跳号 | 各自给出清晰报错 |
| 老配置（无 `[LimitGroups:0...]`） | `未配置 [LimitGroups:0...] 上下限组` |

另：`dotnet build Decrypt\Decrypt.csproj` 0 error；模板与生成的报告都能被 Excel 正常打开（无修复提示）；`Hooks.cs` 保持 UTF-8 BOM + CRLF，`config.ini` 保持 GBK 无 BOM + CRLF。

## 8. 已知限制 / 后续可做

- 图表锚点仍是硬编码 `6 + data.Count`（隐含「测量表从第 5 行开始」），将来若在 Sheet1 顶部再加内容会错位，可改为 `LastRowUsed().RowNumber() + 2`。
- `LimitGroupMap` 长度比 `CsvCount` 长没问题，短了会循环复用；如需强校验可加一条「地图长度 ≥ 样品数」的检查。
- 同一工作表里不能放两个可展开命名区域（ClosedXML.Report 0.2.10 会把第二个区域渲染坏），所以参数表放在独立的「参数」工作表。
