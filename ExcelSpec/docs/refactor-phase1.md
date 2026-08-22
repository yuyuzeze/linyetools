# ExcelSpec 重构 · 阶段 1（性能与基础设施）

本阶段在**不改变任何 golden 输出**、保持既有 CLI/exporters/模板兼容的前提下，
完成了目标方案中「二、先实施的性能优化」里风险最低、收益最大的部分。

## 变更总览

| 主题 | 变更 | 默认行为 |
|------|------|----------|
| Schema 校验模式 | `validate_document(..., strict_schema=)`；XLSX 默认走轻量结构检查 `validate_ir_structure`，不再对每个 CellIR 递归 JSON Schema | XLSX convert/parse：快检；validate 命令 / JSON 输入 / `--strict-schema`：全量 |
| 校验器缓存 | `schemas.get_validator()` 用 `lru_cache` 缓存已编译的 `Draft202012Validator` 和 schema | 全进程复用 |
| SheetIndex | 新增 `excelspec.index.SheetIndex` / `WorkbookIndex`，一次性构建 coordinate / (row,col) / normalized_text / row / column / style_id 索引 | 供后续定位、区域检测、字段匹配复用 |
| Excel COM 复用 | 新增 `ExcelCaptureSession`（一个工作簿只启动一次 Excel、只打开一次），`capture_excel_range` 兼容保留并自动复用活动 session；`extract_with_template` 用惰性 session 包裹整簿截图 | 多区域截图复用同一进程 |
| Benchmark | 新增 `excelspec.benchmark`（`python -m excelspec.benchmark` / `excelspec-bench`），分阶段计时 | — |

## CLI 新增参数

- `excelspec convert|inspect --strict-schema`：对 XLSX 也执行完整递归 Schema 校验。
- `jpspec parse --strict-schema`：同上。
- `validate` 命令始终执行完整 Schema（未受影响）。

## 何时回退到旧行为

- 传入外部 DocumentIR JSON 时，始终执行完整 Schema（数据不可信）。
- 显式 `--strict-schema` 时执行完整 Schema。
- 截图：若没有活动 `ExcelCaptureSession`，`capture_excel_range` 会自建一次性 session（等价旧行为）。

## Benchmark 结果（本机，默认快检 vs 旧默认全量 Schema）

`validate_fast` 为新默认，`validate_strict` 为旧默认，二者由同一次 benchmark 同时报告：

| 工作簿 | sheets | cells | validate_strict(旧) | validate_fast(新) | 总时间(旧→新) |
|--------|-------:|------:|--------------------:|------------------:|---------------|
| SCR-A0010 (6 sheet) | 6 | 746 | ~0.78 s | ~0.00004 s | ~1.02 s → ~0.24 s |
| RPT-B0020 (5 sheet) | 5 | 714 | ~0.58 s | ~0.00001 s | ~0.75 s → ~0.18 s |
| api-spec | 3 | 36 | ~0.011 s | ~0.00003 s | ~0.028 s → ~0.017 s |

验证阶段原本占核心 pipeline 约 3/4 时间，现基本归零，整簿 convert 提速约 4 倍。

## 尚未做（后续阶段）

阶段 2（SparseWorkbookIR / SparseOoxmlIngestor / 稀疏读取 / 单次加载）、
阶段 3（RegionDetector / RegionRouter / 零配置 fast 模式 / 语义 Profile）、
阶段 4（SemanticDocumentIR / KnowledgeChunkIR / 缓存）仍未实现。
当前 ingest 仍走 openpyxl 双次加载、按有效矩形建 CellIR —— 这是阶段 2 的核心目标。
