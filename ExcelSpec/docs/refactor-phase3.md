# ExcelSpec 重构 · 阶段 3（区域检测 / 路由 / 零配置模式 / 语义 Profile）

在保持阶段 1/2 全部收益、旧模板/exporter/CLI 兼容、**95 passed** 的前提下，新增：
确定性 RegionDetector、RegionRouter、零配置 fast/auto/visual 模式、简单语义 Profile。
本阶段不含外部 AI/VLM、SemanticDocumentIR、KnowledgeChunkIR（阶段 4）。

## 核心数据流（零配置）

```
SparseWorkbookIR
  -> RegionDetector.detect_sheet_regions   # 只读稀疏 cell，不 materialize
  -> [CandidateRegion]
  -> (Profile 语义增强，可选)
  -> materialize_region(bounds)            # 只对最终选中的有限区域
  -> RegionRouter.route
  -> DocumentIR
```

**关键约束已满足**：检测阶段绝不 materialize 整个 raw-grid。
`test_detection_does_not_materialize_grid` 断言 `detect_sheet` 期间
`materialize_region` 调用次数为 0；只有 RegionRouter 对被选中区域按需 materialize。

## 新增文件

- `detect/models.py` — `CandidateRegion` / `CandidateRegionType` / `CellBounds`
- `detect/features.py` — 20+ 可解释特征（density/border_density/merge_density/
  numeric|text|formula_ratio/repeated_row_score/header_score/key_value_score/
  visual_score/blank_row|column_gaps/nearby_assets/style_transitions…）
- `detect/detector.py` — `RegionDetector`（分段/分类/标题/资产/覆盖多步纯函数）
- `detect/router.py` — `RegionRouter`（table/key_value/text/image/shape/layout/freeform）
- `detect/assemble.py` — 零配置装配 + auto/visual 截图
- `profile/{model,normalize,loader,enrich}.py` — 语义 Profile
- `profiles/screen-design.yaml` — 示例 Profile
- `tests/test_phase3_detect.py` — 28 项测试

修改：`ingest/sparse_model.py`（SparseSheet 加 assets/diagnostics）、
`ingest/sparse.py`（`build_sparse_workbook`）、`ingest/__init__.py`
（`ingest_sparse_workbook`）、`pipeline.py`（`mode`/`profile` + 零配置分支）、
`cli.py` / `jpspec_cli.py`（`--mode` / `--profile` / `--legacy-template`）。

## RegionDetector（确定性、可解释）

信号：稀疏非空连通、空行/空列分隔（可容忍 1 行/列以允许表内空隙）、合并范围、
标题特征（粗体/填充）、重复行结构、文本/数字/公式比例、图片/Shape 锚点、
样式跳变、低文本密度+高合并+邻近 Drawing → layout/visual。

处理：表内少量空行/空列不拆分；标题行剥离为 `title`（不进正文，仍计入覆盖）；
一个 Sheet 多表；Key/Value（无表头、窄列、成对）先于 table 判定；
图片/Shape 锚点独立成区，锚点落在内容块上则将该块升级为 layout；
覆盖检查：所有 value cell 必属某区，未覆盖进 `freeform-residual` 并写诊断。

每个 CandidateRegion 都带 `detection_method` + `features` + `confidence`，低置信度写
`detect.low_confidence_region`，重叠写 `detect.region_overlap`。

## RegionRouter → RegionIR

- table：检测 header_rows（粗体/填充占比）、保留原始列坐标、缺失 cell 原位空值不左移、
  合并表头生成稳定标签（header_labels）。
- key_value：横向成对 label/value，`region.values` 保留，未识别 label 原样保留，且仍保留原始 cells。
- text/freeform：保守输出全部 cell，不逐格拆段。
- image/shape：无 cells，`asset_ids` 关联，保留 anchor/description/source。
- layout：fast 仅保留结构+资源引用+`route.layout_visual` 诊断，不启动 Excel；
  auto/visual 生成截图。

## 语义 Profile

- 仅业务语义：document_type、filename_patterns、sheet role/alias、field concept/alias、
  required_concepts、validation、overrides。
- **拒绝坐标字段**：loader 递归扫描，`locator/range/width/height/row_offset/
  column_offset/anchor_text/anchor_pattern/repeat_anchor/end_anchor_text` 出现在
  overrides 之外即 `ProfileValidationError`。
- 归一化：NFKC + trim + 全/半角空白 + 日文标点 + casefold；exact alias 优先，
  regex alias 可选，模糊匹配默认关闭；一表头匹配多 concept → `profile.ambiguous_header`；
  未匹配表头原样保留。
- 字段映射同时保留 `source_header`/`semantic_name`/`source_column`/`confidence`。
- overrides 支持 ignore ranges / exclude_sheet / force_region_type / title（全部可选，零配置仍可运行）。

顺序严格为：RegionDetector → CandidateRegion → Profile 增强 → RegionRouter（Profile 不做区域发现）。

## 运行模式与默认行为（阶段 3 收尾）

**默认即零配置 fast**：`jpspec parse input.xlsx -o out` 等价于 `--mode fast`。默认数据流
`SparseWorkbookIR → RegionDetector → RegionRouter → DocumentIR → exporters`；默认
**不**加载 bundled legacy 模板、**不**启动 Excel、**不**跑完整 JSON Schema、不需要 Profile/Template。

仅在显式指定时走 legacy 坐标模板：`--template PATH` / `--legacy-template PATH`（别名）/
`--template-dir DIR`（目录自动匹配）/ `--auto-legacy-template`（恢复旧版 bundled 自动匹配）。
`--template` 优先于 `--mode`。

**行为变化**：此前"未传 template 时自动匹配 bundled 模板"的隐式默认已移除；未传 template 的旧脚本
现迁移到零配置 fast。显式传 template 的脚本完全兼容。`PipelineResult.processing` 记录
`processing_mode / detection_mode / profile_id / legacy_template_id / ingest_engine`，
经 excelspec CLI `results[].processing` 与 jpspec `{stem}.diagnostics.json` 输出。

- fast：零配置、sparse ingest、detect+route、不启动 Excel、不 OCR/VLM，layout 仅留结构/资源/诊断。
- auto/visual：对 layout（visual）区域截图，**一个工作簿复用一个 `ExcelCaptureSession`**
  （`test_visual_mode_reuses_single_session_and_captures` 断言只启动一次）；
  截图失败写 `route.screenshot_failed` 警告并**保留结构化内容**
  （`test_visual_mode_screenshot_failure_keeps_structure`）。

## 测试结果

阶段 2：67 → 阶段 3：**95 passed（+28）**。覆盖：多表(空行/空列分隔)、表内空隙不拆、
标题剥离、key_value、图片/Shape 锚点、低置信度诊断、全覆盖无丢失、检测不 materialize、
特征可解释；router 保列/表头/缺失不左移/kv values/image 无 cells/layout visual 无 COM；
Profile 拒绝坐标字段、归一化、sheet role、字段 exact alias、未匹配保留、歧义诊断、override ignore；
fast 无 Excel、profile 语义生效、template 优先、visual 单 session、截图失败保结构。

## CLI 示例

```bash
jpspec parse input.xlsx --mode fast -o out                          # 零配置
jpspec parse input.xlsx --mode fast --profile profiles/screen-design.yaml -o out
jpspec parse input.xlsx --mode visual -o out                        # 视觉区域截图
jpspec parse input.xlsx --legacy-template templates/....yaml -o out # 旧坐标模板
```

## 未覆盖 / 下一步

- 表头行选择在无样式表头时仍偏保守（把节区小标题当表头的少数情况）；可用 Profile 修正列语义。
- 连接线/流程图语义重建未做（Shape 文本/anchor 已保留）。
- 阶段 4：SemanticDocumentIR、KnowledgeChunkIR（JSONL/RAG，结构化行 + asset_refs + source_range + confidence）、
  内容哈希缓存、低置信度区域接入 AI/VLM fallback 接口（本次仅预留空实现思路）。
