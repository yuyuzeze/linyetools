# v0.2.0 Release Candidate 审计报告

> 重要限定：本仓库**没有真实生产日本式样书**。以下准确率均基于
> `src/excelspec/eval/` 的**合成标注用例**，反映的是**结构检测正确性**，
> **不能替代真实业务解析准确率**。真实文件仍需人工确认。

## 基线
- 修改前测试：142 passed。修改后：**159 passed**（+17 RC 测试）。golden 零变化，未改无关用户文件。

## 端到端审计（现有 4 个 workbook，fast 模式）

| workbook | sheets | regions | chunks | table_rows | assets | source_coverage | avg_conf | low_conf | freeform | 冷启动 |
|----------|-------:|--------:|-------:|-----------:|-------:|----------------:|---------:|---------:|---------:|-------:|
| screen-design | 3 | 6 | 6 | 7 | 2 | 1.0 | 0.79 | 1 | 0 | ~38ms |
| api-spec | 3 | 3 | 3 | 6 | 0 | 1.0 | 0.93 | 0 | 0 | ~10ms |
| SCR-A0010 | 6 | 15 | 15 | 31 | 1 | 1.0 | 0.82 | 3 | 0 | ~107ms |
| RPT-B0020 | 5 | 16 | 16 | 12 | 1 | 1.0 | 0.82 | 3 | 0 | ~75ms |

HTML 审计报告：`out_audit/*.audit.html`（按 sheet/region 浏览：range/type/confidence/detection_method/
features/header decision+evidence/semantic 字段映射/assets/diagnostics/原始 cell/chunk）。
生成命令：`jpspec audit <xlsx> -o audit.html`。

## 评估框架（15 个合成标注用例）

`python -c "from excelspec.eval.runner import run_all_cases; print(run_all_cases()[1])"`

| 指标 | 值 |
|------|----|
| region_recall | 0.967 |
| region_precision | 0.967 |
| region_type_accuracy | 1.0 |
| table_recall / precision | 1.0 / 0.933 |
| header_row_accuracy | 1.0 |
| table_row_count_accuracy | 1.0 |
| content_loss | 0 |
| duplicate_content | 0 |

覆盖用例：单/双/三行表头、节区标题、表内空行、多表、横向 KV、一行多 KV、修正履历、
画面项目（合并 2 行表头 + 字段别名）、Action 列表、方眼纸布局、跨 Sheet 引用、远距离样式、隐藏行/隐藏 Sheet。

## 发现的问题与证据驱动修复

| 优先级 | 问题（证据用例） | 修复 |
|--------|------------------|------|
| P1 | 多 KV / 带样式标签列被误判为 table（multi_kv_per_row, cross_sheet_formula） | 新增 `col_header_score` 列向表头信号，KV 判定先于 table |
| P1 | 表头行数错误（three_row_header header_acc=0） | `detect_header_rows` 综合合并/样式/数据类型跳变，输出 evidence+confidence，低置信度保守回退 |
| P1 | 标题单元格被计为内容丢失（title_above_table LOSS=1） | 标题可追溯 `title_range`，纳入覆盖 |
| P2 | border-only 方眼纸布局未检测（graph_paper_layout recall=0） | 保守 border-box 检测（仅无值的带边框 style-only 格） |
| P2 | 缓存命中反序列化慢 | `serialization.from_dict` 记忆化 `get_type_hints`（~4×） |

修复后合成用例 region_recall 0.93→0.97、type 0.87→1.0、header 0.875→1.0、content_loss 2→0。
所有修复基于**可解释通用特征**，未硬编码任何 workbook 名或坐标。

## 安全审计（结论：现状安全）

| 项 | 结论 |
|----|------|
| 公式/宏执行 | 从不执行；保留公式文本+缓存值；不加载外部 workbook 链接；不访问公式内 URL |
| HTML 转义 | `<script>` → `&lt;script&gt;`，无 XSS |
| Markdown | `|` 转义、换行→`<br>`，表格不被破坏 |
| JSONL | 控制字符经 `json.dumps` 转义，一行一个合法 object，日文不转 `\uXXXX` |
| 资产路径逃逸 | `_safe_filename` 净化，仅从 zip 成员读取，写出限定 asset_dir |
| 损坏/非 OOXML | 明确报错或回退 legacy，不挂死；单 Sheet 局部降级 |
| 缓存 | key 为十六进制摘要（安全文件名）；仅 JSON（无 pickle）；原子替换；损坏自愈 |
| pickle | 未使用 |

（覆盖测试见 `tests/test_rc_security.py`；ZIP bomb / 超多 merge / 极长文本等由稀疏读取与 `_safe_filename` 截断缓解。）

## 缓存审计
- cold/warm 逐字节一致；workbook/profile/mode/asset_dir/版本 变化均失效；chunk 参数不失效 doc 缓存（仅重算 chunk）。
- **asset_dir 计入 key 是刻意的**：保证命中不复用错误资产路径（正确性优先于命中率）；仅输出位置变化时会 miss，这是安全取舍。
- 原子写入（temp+`os.replace`）→ 并发写同 key 为最后写者胜出、不损坏。
- 清理方式：删除 `output/.excelspec-cache/`（内容寻址，可安全重建）。

## 确定性
- 同文件连续 3 次运行：DocumentIR / SemanticDocumentIR / chunks JSONL 逐字节一致；chunk_id 与 content_hash 稳定；无重复 chunk。
- 标题在 chunk 的 section_path/title 出现但不重复进正文；合并成员不重复；无单元格同时进 table 与 freeform。

## 性能（本机基线，非绝对要求）
- 零配置 fast 冷启动：api-spec ~10ms，screen-design ~38ms，RPT ~75ms，SCR ~107ms。
- 批量 `--directory`（demo/workbooks，2 文件）：cold p50 ~0.083s，warm p50 ~0.021s。
- 缓存 warm 命中 ≈ 3.9× 于 cold；稀疏摄取 ≈ 1.3× 于 legacy。
- 命令：`python -m excelspec.benchmark --zeroconfig <xlsx>` / `--directory <dir> [--csv out.csv]` / `--compare`。

## 仍需人工确认（真实式样书）
- 无样式表头、跨多行说明块、真实合并表头变体的表头行判定。
- 方眼纸/流程图布局的边界与视觉资源关联。
- 数组/共享公式、named range 解析、公式内日期缓存类型。
- 真实文件的字段别名命中率（需扩充 Profile 并用真实样例回归）。
