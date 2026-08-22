# ExcelSpec

把日语 Excel 式样书转换成 Markdown / HTML / 语义 JSON / 知识块 JSONL，用于构建知识库。

## 快速开始（v0.2 · 默认零配置）

```bash
jpspec parse spec.xlsx -o output -f md,html,semantic-json,chunks
```

- **默认零配置 fast**：`SparseWorkbookIR → RegionDetector → RegionRouter → DocumentIR → exporters`，
  无需模板/Profile；不启动 Excel、不执行宏和公式、不跑完整 JSON Schema。
- **不执行宏和公式**：公式保留原文与缓存显示值，绝不计算，绝不加载外部 workbook 链接或访问公式内 URL。
- **visual 模式按需使用 Windows + Excel**：嵌入图片直接复用原图；Shape 按锚点并集、单元格布局按连续边框/合并范围截图。过小的标题区域会跳过；只有真正需要区域截图时才启动 Excel。缺 Excel 时降级并保留结构化内容。
- **legacy 坐标模板仍受支持**：`--template PATH` / `--legacy-template PATH`（template 优先于 mode）。
- **Profile 只负责语义别名**：Sheet role / 字段 concept，不含任何坐标（`--profile PATH`）。

数据流：

```text
XLSX
  → SparseWorkbookIR（稀疏，单次 openpyxl 加载）
  → RegionDetector → CandidateRegion → materialize(选中) → RegionRouter
  → DocumentIR（布局/兼容）
  → SemanticAssembler → SemanticDocumentIR → KnowledgeChunker → KnowledgeChunkIR
  → Markdown / HTML / JSON / semantic-json / chunks(JSONL)
```

命令入口：

- **推荐**：`jpspec`（Typer）— `parse` / `inspect` / `validate` / `audit` / `template`
- **兼容**：`excelspec`（旧 argparse）
- **基准**：`python -m excelspec.benchmark`（`--zeroconfig` / `--compare` / `--directory`）

### 输出格式

| 格式 | 文件 | 内容 |
|------|------|------|
| `json` | `{stem}.json` | DocumentIR（布局/兼容，稳定） |
| `md` / `html` | `{stem}.md/.html` | 人类阅读 |
| `semantic-json` | `{stem}.semantic.json` | SemanticDocumentIR（语义视图，可追溯） |
| `chunks` | `{stem}.chunks.jsonl` | KnowledgeChunkIR（结构化知识块，一行一个 JSON） |
| `jsonl` | `{stem}.jsonl` | 旧 KnowledgeBase JSONL（保留兼容） |

### 模式与缓存

```bash
jpspec parse spec.xlsx --profile profiles/screen-design.yaml -o out -f semantic-json,chunks
jpspec parse spec.xlsx --mode visual -o out -f md,html,chunks        # 需 Windows+Excel
jpspec parse spec.xlsx -o out --cache                                # 内容哈希缓存（output/.excelspec-cache）
jpspec audit spec.xlsx -o audit.html                                 # 人工检查 HTML 审计
```

缓存 key = workbook SHA-256 + parser/sparse/detector/semantic 版本 + profile 内容 hash + mode + asset_dir；
命中跳过 ingest/detect 并**逐字节一致**；损坏自愈；只写在 `output/.excelspec-cache/`，绝不在源 Excel 旁。

### 安全说明

不执行宏/公式、不加载外部 workbook 链接、不访问公式内 URL、不用 pickle 反序列化缓存（仅 JSON）；
HTML/Markdown/JSONL 均转义（`<script>`、`|`、换行、控制字符）；资产文件名净化、只从 zip 成员读取，
不写出 output/cache 之外；单 Sheet 异常局部降级不影响其他 Sheet。

### 已知限制

评估基于合成标注用例（见 `src/excelspec/eval/`），**尚不能声称真实业务解析准确率**（仓库无真实生产式样书）。
无样式表头、border-only 布局、数组/共享公式展开、连接线语义、真实 OCR/VLM 接入仍待完善。

## 工程结构

```text
ExcelSpec/
├── pyproject.toml              # 包配置；入口 jpspec / excelspec
├── README.md
├── schemas/                    # 对外 JSON Schema
│   ├── document-ir.schema.json # DocumentIR / canonical 结构
│   └── template.schema.json    # 可执行模板规则
├── templates/                  # 内置扁平模板（自动匹配用）
│   ├── linye-screen-design-v1.yaml
│   └── linye-api-spec-v1.json
├── src/excelspec/              # 核心代码
│   ├── jpspec_cli.py           # jpspec 命令行
│   ├── cli.py                  # excelspec 兼容命令行
│   ├── pipeline.py             # 摄取 → 模板 → 校验 → 导出编排
│   ├── inspection.py           # inspect 落盘
│   ├── template_pack.py        # 模板包 init / compare / 加载
│   ├── serialization.py        # JSON 序列化
│   ├── schemas.py              # 加载内置 schema
│   ├── models/                 # 数据模型
│   │   ├── document_ir.py      # DocumentIR（解析结果）
│   │   └── template.py         # TemplateSpec（模板规则）
│   ├── ingest/                 # 读 Excel
│   │   ├── workbook.py         # openpyxl 网格 / 合并 / 样式
│   │   ├── ooxml.py            # 图片、形状文字
│   │   └── manifest.py         # 截图清单
│   ├── templates/              # 模板引擎
│   │   ├── loader.py           # YAML/JSON / 模板包加载
│   │   └── engine.py           # 匹配、区域抽取、freeform
│   ├── validate/               # Schema + 业务规则校验
│   ├── exporters/              # json / md / html / jsonl
│   └── extract/                # 预留（语义抽取扩展）
├── tests/                      # 单元 / 集成 / golden
│   ├── fixtures/               # 脱敏 xlsx、模板、期望快照
│   └── test_*.py
└── demo/                       # 本地演示（可删，不影响包）
    ├── build_demo_workbooks.py
    ├── workbooks/              # 两类式样书样例
    ├── templates/              # demo 用 YAML
    └── out/                    # 转换输出（生成物）
```

### 数据流

```text
客户.xlsx
   │
   ├─ jpspec inspect ──► inspection/{workbook,sheets,preview}
   │
   ├─ jpspec template init ──► templates/<id>/{template.xlsx,yaml,schema.json,...}
   │
   └─ jpspec parse --template <包>
          │
          ▼
     pipeline: ingest → match/extract → validate → export
          │
          ├─ output/canonical.json   # 主结果（给 AI / 下游）
          ├─ output/document.md|html
          └─ output/diagnostics.json
```

### 两类“模板”别混

| 位置 | 作用 |
|------|------|
| `ExcelSpec/templates/*.yaml` | 内置扁平模板，给自动匹配 |
| `templates/<id>/` 模板包 | 客户/项目级：xlsx 外观 + yaml 规则 + schema + examples |

可执行规则永远在 **`template.yaml`**；`template.xlsx` 只做外观与对照。

### 和仓库其它目录的关系

```text
linyetools/
├── ExcelSpec/       # 新核心（本工程）← 继续演进
├── ExcelToMd/       # 旧工具，对照用
└── ExcelConverter/  # 旧批量导出，对照用
```

当前核心包含：

- `DocumentIR` 与模板配置的数据模型；
- Draft 2020-12 JSON Schema；
- JSON 序列化/反序列化基础；
- merge-aware XLSX 原始网格、公式缓存、样式与 OOXML 图片/形状文字摄取；
- 用户截图清单绑定，以及 OCR/VLM/LibreOffice 的 provider-neutral 接口；
- YAML/JSON 模板加载、Schema 校验和候选模板自动评分；
- 固定/锚点区域、键值对和多行表头表格语义抽取；
- 动态行表：`stop_after_blank_rows` / `end_anchor_*` / `repeat_anchor`；
- 空白精简：`trim_empty_columns`（表格默认开启）与 `shrink_to_content`；
- 低分匹配时保守的 freeform 网格回退。
- DocumentIR Schema、模板结构及模板业务规则校验；
- 支持批量输入、严格模式和 JSON diagnostics 的命令行工作流；
- 可安装的 `excelspec` 命令及 Schema 查看命令。

业务规则支持必需 sheet/region/字段（列）、正则、枚举、唯一性和跨 sheet
引用一致性。

## 模板表格定位（动态行）

```yaml
# 1) 行数固定
locator:
  mode: fixed
  range: A8:M27

# 2) 起点固定/锚点，行数动态
locator:
  mode: anchor
  anchor_text: "No."
  width: 13
extractor:
  kind: table
  header_rows: 1
  options:
    stop_after_blank_rows: 1
    shrink_to_content: true
    # trim_empty_columns: true   # table 默认 true；可显式 false 关闭

# 3) 同页多表，相同表头重复出现
locator:
  mode: anchor
  anchor_text: "No."
  width: 7
  repeat_anchor: true
extractor:
  kind: table
  header_rows: 1
  options:
    stop_after_blank_rows: 1
```

`repeat_anchor: true` 时会生成 `region_id`、`region_id-2`… 多个区域。
`shrink_to_content` 会把区域收缩到有内容的单元格；`trim_empty_columns`
会去掉整列空白（含非合并、仅占位的空列）。

## 开发安装

```shell
python -m pip install -e .
excelspec --version
excelspec schema document-ir
excelspec schema template
```

## 摄取 API

摄取层只建立可回放的 `freeform` 原始网格，不执行模板匹配或最终导出：

```python
from excelspec.ingest import ingest_xlsx

document = ingest_xlsx(
    "spec.xlsx",
    asset_dir="work/assets",
    screenshot_manifest="screenshots.json",
)
```

截图清单采用版本化 JSON。相对路径以清单所在目录为基准；`region_id` 可省略，
或绑定到摄取层的 `raw-grid`，以及后续模板阶段建立的区域：

```json
{
  "version": "1",
  "assets": [
    {
      "asset_id": "screen-main",
      "path": "screens/main.png",
      "sheet": "画面設計",
      "region_id": "raw-grid",
      "asset_type": "screenshot",
      "description": "主画面",
      "ocr": { "status": "pending" },
      "vlm": { "status": "pending" }
    }
  ]
}
```

`OcrAdapter`、`VlmAdapter` 与 `LibreOfficeRenderer` 目前仅定义请求/结果协议，
摄取过程不会调用具体服务或本机 LibreOffice。

## 命令行工作流

输入可为单个文件或目录。未指定 `--template` 时会从内置模板（或
`--template-dir`）自动匹配；显式模板会直接用于抽取。`--strict` 会将 warning
也作为失败，`--json` 或 `--diagnostics PATH` 可供 CI 读取。

```shell
excelspec inspect specs/ --json
excelspec template match specs/example.xlsx --template-dir templates/
excelspec validate build/document.json --template templates/screen.yaml --strict
excelspec convert specs/ -o build/ -f json --diagnostics build/diagnostics.json
```

`convert` 会先完成摄取、模板抽取和校验；存在 error（或严格模式下存在
warning）时不写出对应文件。支持 `json`、`md`、`html` 与 `jsonl` 输出格式。

## jpspec 命令行核心（推荐）

安装后同时提供 `jpspec` 与兼容入口 `excelspec`：

```shell
pip install -e .
jpspec --version
```

### 1. 检查结构

```shell
jpspec inspect template.xlsx -o inspection/
```

输出：

```text
inspection/
  index.json
  workbook.json
  sheets/
    画面入出力項目一覧.json
  preview/
    画面入出力項目一覧.html
  assets/
```

### 2. 生成模板包

```shell
jpspec template init template.xlsx -o templates/screen-design-v1 --type screen-design
```

模板包：

```text
templates/screen-design-v1/
  template.xlsx
  template.yaml
  schema.json
  examples/
    example-input.xlsx
    expected-output.json
  prompts/
    mapping.md
```

### 3. 解析实际文件

```shell
jpspec parse actual.xlsx --template templates/screen-design-v1 -o output -f json,md,html
```

主要产物：`output/canonical.json`。

### 4. 验证

```shell
jpspec validate output/canonical.json --template templates/screen-design-v1
```

### 5. 比较模板与实际

```shell
jpspec template compare templates/screen-design-v1/template.xlsx actual.xlsx
```
