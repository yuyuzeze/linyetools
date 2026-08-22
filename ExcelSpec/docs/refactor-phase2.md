# ExcelSpec 重构 · 阶段 2（稀疏 OOXML 摄取）

在保持阶段 1 全部收益、**golden 业务内容零变化**、旧模板/exporter/CLI 兼容的前提下，
实现了真正的稀疏 XLSX 读取，解决：openpyxl 双次加载、有效矩形膨胀、大量空 CellIR、
公式/缓存重复读取、远距离样式放大、模板引擎重复扫描。

## 新增 / 修改文件

新增：
- `src/excelspec/ingest/sparse_model.py` — `SparseWorkbookIR` / `SparseSheet` / `SparseCell`
- `src/excelspec/ingest/sparse.py` — `SparseOoxmlIngestor`
- `src/excelspec/ingest/adapter.py` — `SparseWorkbookIR → DocumentIR`，含 `materialize_region`
- `src/excelspec/ingest/base.py` — `WorkbookIngestor` 协议、`UnsupportedWorkbookError`、`ingest_with_engine`
- `tests/test_phase2_sparse.py` — 20 项测试（含压力 fixture、回退边界）

修改：
- `ingest/workbook.py` — 抽出共享 `attach_drawings` / `bind_manifest_assets`；`LegacyOpenpyxlIngestor` 别名
- `ingest/__init__.py` — `ingest_xlsx(..., engine="auto|sparse|legacy")`
- `pipeline.py` / `cli.py` / `jpspec_cli.py` — `--ingest-engine`
- `templates/engine.py` — 接入 `SheetIndex`（每 Sheet 只构建一次）
- `benchmark.py` — 引擎、稀疏统计、`--compare` sparse↔legacy
- `tests/test_fixtures_acceptance.py` — 归一化 ingestor metadata（业务内容不变）

## SparseOoxmlIngestor 架构

1. `load_workbook(data_only=False)` **单次**加载 → 样式表、合并、properties、drawing、sheet 名/状态。
   openpyxl 的 `_cells` 本身稀疏（只含 XML 中真实存在的 cell），不做任何 densify。
2. 对含公式的 sheet，用 `zipfile + ElementTree.iterparse` **流式**读取 `worksheets/sheetN.xml`，
   从**同一个 `<c>` 节点**取 `<f>` 公式与 `<v>` 缓存值 —— 不再第二次 `load_workbook(data_only=True)`。
3. 构建 `SparseWorkbookIR`：value/formula cell、style-only cell（单独记录、**不进正文范围**）、
   合并范围（只记 range + 主格 span，不实例化成员）、workbook 级 style 表（cell 只存 `style_id`）。
4. **内容范围**（content bounds）= value cell + 合并角点，**不含远距离 style-only cell**。
5. `adapter.sparse_to_document` 只在**内容范围**内 materialize `raw-grid`，因此
   dimension 被放大到 XFD1048576、或角落有一个孤立样式格，都不会生成百万行矩形。

## 支持的 OOXML 特性

shared string、inline string、plain string、number、boolean、error、formula +
同节点 cached value、空公式结果、typed-empty（如空 inlineStr，保留 data_type）、
合并主格/成员、workbook 样式表、图片/Shape（复用既有 drawing 提取）、行/列/隐藏状态、多 Sheet 关系映射。

## 暂不支持 / 回退条件

`engine="auto"` **仅在**以下情况回退 legacy，并写 `INGEST_LEGACY_FALLBACK` 诊断 +
`metadata.legacy_fallback/fallback_reason`：

- `UnsupportedWorkbookError`（sparse 明确判定不支持）
- 损坏 / 非 OOXML zip（`BadZipFile`）
- openpyxl `InvalidFileException`（如加密工作簿）

**不作为回退理由**（局部降级：保守保存 + 诊断 + 继续）：任意 Exception、单元格解析失败、
未知 number format、单个 Drawing 失败。普通代码 bug **不会**被吞掉（`auto` 只捕获上述特定异常）。

局部降级示例：公式无缓存值 → `FORMULA_CACHE_MISSING` 诊断，cached=None，继续。

数组公式 / 共享公式 / 公式内日期缓存：当前保守保存原公式文本；精确 cached 类型（尤其日期）
为已知限制，见"未覆盖特性"。

## SparseWorkbookIR → DocumentIR 适配

`adapter.materialize_region(sheet, bounds, styles, path)` 在给定 bounds 内逐格生成 `CellIR`：
真实 cell 用其 value/type/formula/style；缺失格 → 空值（`data_type='n'`、无 style、无值），
**缺失即原位空值，绝不左移后续列**。合并成员从 `merge_members` 取 `merged_master`；
样式合并边框成员保留其 style。产出的 `raw-grid` region 与 legacy 逐字节一致（已验证 fixtures）。

## SheetIndex 接入位置（每 Sheet 构建一次）

- `_scan_sheet(sheet)` 每个模板 Sheet 构建一次 `_SheetScan`（coordinate dict + 单次排序列表 +
  bounds + `SheetIndex`），在 `extract_with_template` 传给该 Sheet 所有 region 的 `locate_regions`。
- anchor / repeat-anchor / end-anchor 复用预排序列表，不再每次 `sorted(cells.values())`。
- `score_template` / `_fingerprint_score` 用 `cells_cache` 跨规则、跨候选模板复用坐标图。
- 公开函数保持兼容（`scan` / `cells_cache` 均为可选参数）。

## 测试结果

- 阶段 1：49 → 阶段 2：**67 passed（+18，含 2 subtests）**，golden 业务内容零变化。
- 覆盖：sparse↔legacy 等价、公式+同节点缓存、shared/inline string、number/bool/error、
  合并只记 range、缺失格不左移、远距离样式不膨胀、整行/整列格式不实例化、多 Sheet 关系、
  图片/Shape 一致、auto 用 sparse、不支持回退+原因、bug 不被吞、SheetIndex 每 Sheet 一次、
  anchor 行为不变、动态压力 fixture（不入库）。

## Benchmark（sparse ↔ legacy，本机）

`python -m excelspec.benchmark --compare <xlsx>...`

| 工作簿 | legacy ingest | sparse ingest | speedup | output 一致 | value/materialized cells |
|--------|--------------:|--------------:|--------:|:-----------:|--------------------------|
| SCR-A0010 (6 sheet) | 0.193 s | 0.149 s | 1.3× | ✔ | 321 / 852 |
| RPT-B0020 (5 sheet) | 0.162 s | 0.126 s | 1.29× | ✔ | 164 / 714 |
| api-spec | 0.0147 s | 0.0119 s | 1.24× | ✔ | 38 / 56 |

- 干净小文件上 sparse 已快 ~1.3×（省去第二次 load）。**稀疏中间层只保存真实内容**
  （SCR-A0010：321 个 value cell，而非 852 个稠密网格位）。
- 决定性差异在含远距离样式的文件：legacy 因 `has_style` 撑大有效矩形而 densify（压力 fixture
  会尝试 densify ~170 亿格而挂死），sparse 秒级完成（见 `StressFixtureTests`）。

## 完成标准核对

默认不再双次 load ✔ ｜ 默认 SparseOoxmlIngestor ✔ ｜ 稀疏 cell ∝ 真实 XML 内容 ✔ ｜
远距离样式不产生稠密 CellIR ✔ ｜ 公式+缓存正确 ✔ ｜ 旧模板/exporter 正常 ✔ ｜
SheetIndex 已接入引擎 ✔ ｜ legacy 回退可观察可诊断 ✔ ｜ 全部测试通过 ✔ ｜
benchmark 有前后数据 ✔ ｜ 未改无关文件 ✔ ｜ 无半成品 ✔

## 未覆盖特性 / 下一步

- 公式内**日期/时间缓存值**的精确 Python 类型转换（当前 number/str/bool/error）；建议：
  按 numFmt 判定后复用 openpyxl 的 serial→datetime。
- 数组公式 / 共享公式：目前保存主公式文本，未展开范围。
- style-only 边框格进入独立 layout/style span（阶段 3 的 layout/visual 识别）。
- `DocumentIR` 的 `raw-grid` 仍在内容范围内 materialize（为保 golden 与模板引擎兼容）；
  阶段 3 的 RegionDetector 落地后，可改为只对已识别 region 按需 materialize，进一步降密。
