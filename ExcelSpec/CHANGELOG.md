# Changelog

## v0.2.0 — 零配置语义流水线（RC）

### 重大变化（行为）
- **默认零配置 fast**：不带 `--template` 时不再隐式匹配 bundled 模板，改走
  `SparseWorkbookIR → RegionDetector → RegionRouter → DocumentIR`。显式 `--template` /
  `--legacy-template` / `--template-dir` / `--auto-legacy-template` 保留旧坐标模板行为
  （template 优先于 mode）。**迁移**：未传 template 的旧脚本会迁移到零配置 fast；
  显式传 template 的脚本完全兼容。

### 新增
- 稀疏 OOXML 摄取（单次 openpyxl 加载；公式与缓存值同节点读取；远距离样式不膨胀）。
- RegionDetector / RegionRouter：确定性、可解释区域检测（table/key_value/text/image/shape/layout/freeform）。
- Semantic Profile（`--profile`）：Sheet role / 字段 concept 别名，禁止坐标字段。
- SemanticDocumentIR、KnowledgeChunkIR、结构化表格行、公式引用、确定性 chunking。
- 新输出格式：`semantic-json`（`.semantic.json`）、`chunks`（`.chunks.jsonl`）。
- 内容哈希缓存（`--cache` / `--cache-dir`）：命中逐字节一致、原子写入、损坏自愈、可正确失效。
- OCR/VLM 可插拔接口 + Null 实现（无外部服务；fast 从不调用）。
- 运行模式 `--mode fast|auto|visual`；auto/visual 复用单个 Excel COM session。
- 评估框架 `excelspec.eval`（标注用例 + 结构指标）与 `jpspec audit` HTML 审计报告。
- 基准 `python -m excelspec.benchmark`：`--zeroconfig`（分阶段 + cold/warm）、`--compare`、`--directory`（批量 p50/p95）。

### 修复（证据驱动）
- Key/Value 与 table 误判：新增 col_header_score 列向表头信号（多 KV / 带样式标签列）。
- 表头行检测增强：合并 + 样式 + 数据类型跳变综合判定，输出 evidence 与 confidence，低置信度保守回退。
- 标题可追溯（title_range），消除标题单元格被误判为内容丢失。
- border-only 方眼纸布局检测。
- `serialization.from_dict` 记忆化 `get_type_hints`，缓存命中反序列化快约 4×。

### 性能（本机基线，非绝对要求）
- 校验默认快检（旧全量 Schema 仅在 `validate` / `--strict-schema` / JSON 输入）。
- 稀疏摄取比 legacy 快 ~1.3×；缓存 warm 命中比 cold 快 ~3.9×。

### 兼容
- 旧 exporter（json/md/html/jsonl/kb-jsonl）与 golden 输出零变化。
- 旧 CLI 参数与 `--template` 路径保持兼容。

### 已知限制
- 评估基于合成标注用例；**不代表真实业务解析准确率**（仓库无真实生产式样书）。
- 无样式表头、数组/共享公式展开、连接线语义、真实 OCR/VLM 接入待后续。

## v0.1.0
- 初版：openpyxl + OOXML 摄取、DocumentIR、YAML/JSON 模板引擎、Markdown/HTML/JSON/JSONL exporter、
  diagnostics、jpspec/excelspec CLI、golden 测试。
