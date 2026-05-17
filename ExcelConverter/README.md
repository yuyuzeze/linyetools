# Excel 批量转换工具

递归扫描文件夹中的 `.xlsx` 文件，将每个工作表导出为独立的 **Markdown**、**CSV** 或 **HTML** 文件。输出目录会按输入的相对路径保持相同的子目录结构。

依赖为 **Python 3.10+** 与 **openpyxl**（pip），不调用 LibreOffice 或 Excel。

---

## 功能概览

| 特性 | 说明 |
|------|------|
| 批量转换 | 指定输入目录，自动处理其中所有 `.xlsx` |
| 递归扫描 | 包含所有子目录中的 Excel 文件 |
| 目录结构 | 输出路径镜像输入的相对目录，例如 `in/a/b/表.xlsx` → `out/a/b/表_Sheet1.md` |
| 一表一文件 | 每个工作表生成一个文件，命名为 `{工作簿名}_{工作表名}.{扩展名}` |
| 合并单元格 | 合并区域内各格均填充左上角单元格的值（便于 CSV/HTML 使用） |
| 公式 | 使用 `data_only=True`，导出的是缓存的计算结果，而非公式文本 |

空工作表（无数据）会跳过，不生成文件。

---

## 安装

在 `ExcelConverter` 目录下执行：

```bash
pip install -r requirements.txt
```

---

## 基本用法

```bash
python excel_converter.py -i <输入目录> -o <输出目录>
```

默认输出为 **Markdown**（`-f 1`）。

### 目录结构示例

```
输入/
├── 报表.xlsx
└── 子目录/
    └── 明细.xlsx

输出/          # 使用 -f 1
├── 报表_Sheet1.md
└── 子目录/
    └── 明细_Sheet1.md
```

---

## 命令行参数

| 参数 | 说明 |
|------|------|
| `-i` / `--input` | 必填。输入文件夹路径（递归扫描子目录）。 |
| `-o` / `--output` | 必填。输出文件夹路径（按输入相对路径创建子目录）。 |
| `-f` / `--format` | 可选。输出格式：`1`=MD（默认），`2`=CSV，`3`=HTML。 |

查看帮助：

```bash
python excel_converter.py --help
```

---

## 输出格式说明

### Markdown（`-f 1`）

- GFM 管道表格，UTF-8 编码
- 单元格内的 `|` 会转义，换行会替换为空格

### CSV（`-f 2`）

- UTF-8 带 BOM（`utf-8-sig`），便于 Excel 直接打开
- 标准 CSV 引号规则

### HTML（`-f 3`）

- 简单 `<table>` 页面，UTF-8，单元格内容经 HTML 转义

---

## 示例

```bash
# 转为 Markdown（默认）
python excel_converter.py -i ./excel -o ./out

# 转为 CSV
python excel_converter.py -i ./excel -o ./out -f 2

# 转为 HTML
python excel_converter.py -i ./excel -o ./out -f 3
```

成功时会在标准输出打印每个生成文件的完整路径；结束时汇总处理文件数与生成文件数。若部分文件失败，会以非零退出码结束，并在标准错误输出失败列表。

---

## 文件命名

工作簿名与工作表名中的非法文件名字符（`\ / : * ? " < > |`）会替换为 `_`。

若同一目录下出现重名（例如重复的工作表名），会自动追加 `_1`、`_2` 等后缀。

---

## 与 ExcelToMd 的区别

同仓库下的 [ExcelToMd](../ExcelToMd/) 面向**单个** `.xlsx`，支持合并单元格去重、空行压缩、图片导出等高级选项。

**ExcelConverter** 面向**文件夹批量**转换，支持 MD / CSV / HTML 三种格式，适合整目录迁移或批量归档。

---

## 限制

| 内容 | 说明 |
|------|------|
| 文件格式 | 仅 `.xlsx`（不含 `.xls`、`.xlsm` 等） |
| 文本框 / 形状 | 不导出画布上的示意图层文字 |
| 空表 | 无数据的 sheet 不生成文件 |
| 公式未缓存 | 若 Excel 从未保存过计算结果，对应单元格可能为空 |
