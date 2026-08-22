# ExcelSpec 重构 · 阶段 4（语义模型 / 知识块 / 缓存 / Provider 接口）

在保持阶段 1–3 全部收益、旧 exporter/golden 不变、**142 passed** 的前提下，新增
SemanticDocumentIR、KnowledgeChunkIR、结构化 JSONL、公式引用、确定性 chunking、
内容哈希缓存、OCR/VLM 可插拔接口（仅 Null 实现，无外部服务），并完成阶段 3 默认行为收尾。

## 阶段 3 默认行为收尾

默认命令即零配置 fast：`jpspec parse input.xlsx -o out` == `--mode fast`。默认
`SparseWorkbookIR → RegionDetector → RegionRouter → DocumentIR → exporters`，
不加载 bundled 模板、不启 Excel、不跑完整 Schema。legacy 仅在 `--template` /
`--legacy-template` / `--template-dir` / `--auto-legacy-template` 时启用（template 优先）。
`PipelineResult.processing` 记录 `processing_mode/detection_mode/profile_id/legacy_template_id/ingest_engine/cache`。

## 数据流

```
DocumentIR(routed) → SemanticAssembler → SemanticDocumentIR → KnowledgeChunker → KnowledgeChunkIR → JSONL
```

## SemanticDocumentIR（独立于 DocumentIR）

`models/semantic.py`：`SemanticDocumentIR`（schema_version/document_id/title/document_type/
source_path/source_hash/profile_id/processing_mode/sheets/sections/regions/assets/references/
diagnostics/metadata）；`SemanticRegion`（region_id/type/sheet/sheet_role/title/section_path/
source_range/confidence/text/table/key_values/asset_refs/formula_refs/metadata/diagnostics）。
不复制 CellIR；由 `semantic/assembler.py` 前向生成（绝不从 Markdown 反解析）；确定性输出。

## 结构化表格

`SemanticTable`：`columns`（column_id/source_header/semantic_name/display_name/confidence）+
`rows`（row_id/source_range/values[按语义或列id]/source_values[按原表头]/formulas/confidence）。
列/行顺序稳定；缺失 cell 原位 None 不左移；未映射列保留（semantic_name=None）；
公式行同时保留公式文本与缓存显示值；空行不产出知识块。

## 公式与跨 Sheet 引用

`semantic/references.py`：识别 same_sheet / cross_sheet（含带空格引号 Sheet 名）/ external
（`[Book.xlsx]`）/ named_range。不做公式计算；保留缓存显示值；无法解析的复杂公式保留原文并标
`metadata.unparsed`。`ReferenceIR`（source_sheet/source_cell/formula/targets/reference_type/
resolved/display_value）进入 `SemanticDocumentIR.references`，并按 cell 归入相关 region 的 `formula_refs`。

## KnowledgeChunkIR（`models/chunk.py`）

字段含 chunk_id/document_id/chunk_index/chunk_type/schema_version/document_type/sheet/sheet_role/
section_path/region_id/title/text/structured_data/source{workbook,sheet,range}/asset_refs/
formula_refs/confidence/content_hash/metadata。
- `chunk_id` 确定性：`{document_id}:{region_id}:{seq}`，无随机 UUID，重复运行不变。
- `chunk_index` 与顺序稳定；无重复；每个 chunk 必有 source。
- confidence 有依据（区域检测/行均值），非统一 1.0；diagnostics 不混入 text。

## Chunking 策略（`chunking/chunker.py`，确定性）

- text：按段落/行边界，超长按段切分并重复标题上下文。
- key_value：同组尽量单块，超长按 pair 分组重复上下文；`structured_data.key_values` 保留。
- table：整行分组（`--chunk-max-rows`/`--chunk-max-chars`），**不拆行**，每块重复 columns/header
  与 Sheet/标题上下文，`structured_data` 保结构化行。
- layout/image/shape：视觉块，`asset_refs` 必有；无文字不伪造描述（text 可为空）。

## Exporters（新增，旧的不变）

- `semantic-json` → `{stem}.semantic.json`（SemanticDocumentIR，直接从 DocumentIR 前向组装）
- `chunks` → `{stem}.chunks.jsonl`（KnowledgeChunkIR，一行一个合法 JSON，UTF-8 不转义日文）
- 现有 `json`/`md`/`html`/`jsonl`/`kb-jsonl` 保持不变，golden 零变化。

> 兼容取舍：现有 `.jsonl`（`KnowledgeBaseJsonlExporter`，golden 绑定）保留原样；新结构化知识块
> 走 `chunks` → `.chunks.jsonl`，语义文档走 `semantic-json` → `.semantic.json`，均为**追加**能力。

## 内容哈希缓存（`cache/`）

`--cache/--no-cache`、`--cache-dir`（默认 `output/.excelspec-cache/`，**绝不**写在源 Excel 旁）。
缓存零配置 DocumentIR，key = SHA-256(workbook) + parser/sparse/detector/semantic 版本 +
profile 内容 hash + mode + asset_dir。命中跳过 ingest+detect+route，反序列化得到**逐字节一致**输出
（保留插入顺序，不 sort_keys）。原子写入（temp + `os.replace`）；缓存损坏→warning+删除+重建。
chunk 参数不进 doc key（chunk 变化不失效 doc 缓存，仅重算 chunk）。`processing.cache` 报告 hit/miss。
附带 `serialization` 的 `get_type_hints` 记忆化，使命中反序列化快 ~4×。

## OCR/VLM 可插拔接口（`providers/`）

`OcrProvider`/`VlmProvider` Protocol + `ProviderResult`（text/provider/source/confidence）+
`NullOcrProvider`/`NullVlmProvider`（`available=False`）。fast 从不调用；auto/visual 仅当
`provider.available` 才调用；默认 Null；provider 失败写 `provider.{ocr,vlm}_failed` 诊断且
**保留视觉资源**；结果标注 provider/source/confidence。无任何在线 SDK/网络调用。

## Coverage 检查（`semantic/coverage.py`）

统计 semantic_region_count / chunk_count / table_row_count / chunked_table_row_count /
referenced_asset_count / unreferenced_asset_count / source_coverage / average_confidence /
low_confidence_count / reference_count；对区域无 chunk、缺 source、表行遗漏、重复 chunk、
未引用资产产出诊断。

## Benchmark

`python -m excelspec.benchmark --zeroconfig [--mode] [--profile] <xlsx>`：分阶段计时
（hash/ingest_sparse/detect_route/references/semantic_assembly/chunking/semantic_json_export/
chunks_jsonl_export）+ cold/warm 缓存总时长 + 命中状态 + coverage 统计。

本机（SCR-A0010，6 sheet）：cold≈0.114s，warm(hit)≈0.029s，**speedup≈3.9×**，
regions=15 chunks=15 table_rows=36/36 coverage=1.0。

## 测试结果

阶段 3 收尾：+7（102）；阶段 4A：+27（129）；阶段 4B：+13（**142 passed**）。golden 零变化。

## 已知限制 / 后续

- 无样式表头时表头行选择偏保守（少数把节区标题当表头）；Profile 可修正列语义。
- 缓存目前作用于零配置 DocumentIR 层；semantic/chunks 未单独分级缓存（chunk 由 DocumentIR 现算）。
- 公式内日期缓存精确类型、数组/共享公式展开、连接线/流程图语义、真实 OCR/VLM provider 接入待后续。
