# Excel → Markdown 转换工具

将 `.xlsx` 转为 UTF-8 的 Markdown 文件。表格使用 **内嵌 HTML `<table>`**，以正确表示 **合并单元格**（`rowspan` / `colspan`）。  
依赖仅为 **Python 3.10+** 与 **`openpyxl`**（pip），不调用 LibreOffice 或 Excel。

---

## 安装

在 `ExcelToMd` 目录下执行：

```bash
pip install -r requirements.txt
```

---

## 基本用法

```bash
python excel_to_md.py <输入.xlsx> -o <输出目录>
```

**默认：一个工作表对应一个 `.md` 文件**，文件名形如：

`工作簿名__Sheet1.md`、`工作簿名__设计書.md`

工作表名称中的非法路径字符会被替换为 `_`。

---

## 命令行参数

| 参数 | 说明 |
|------|------|
| `xlsx` | 必填。输入的 `.xlsx` 文件路径。 |
| `-o` / `--output-dir` | 必填。输出目录；若不存在会自动创建。 |
| `--one-file` | 可选。不加则「一表一文件」；加上后把所有工作表合并为**一个** `{工作簿名}.md`。 |
| `--include-shapes` | 可选。从 DrawingML 额外抽取**形状/文本框内**可见文字，追加为列表；**顺序不一定与画面一致**，也**不表示箭头/连线拓扑**。 |
| `--max-rows N` | 可选。每个表只导出从该表首行起最多 **N** 行数据行（用于超大表截断）。 |

终端里也可查看：

```bash
python excel_to_md.py --help
```

---

## 默认行为（重要）

- **只导出单元格里的内容**，不读取画布上的文本框、箭头、自选图形等「示意图层」。
- 若重要说明**只写在文本框里**、没有写进单元格，在默认模式下 **不会出现在 Markdown** 里。  
  需要时可：把内容挪到单元格，或使用 `--include-shapes` 尝试抽取文字（仍不保箭头关系），或把原始 xlsx 一并提供给阅读方。

---

## 编码与日语

- 输出的 `.md` 文件为 **UTF-8**，平假名、片假名、汉字等日语字符均可正常保存；现代编辑器与 Git 默认按 UTF-8 处理即可。

---

## 示例

```bash
# 每个 sheet 一个 md，输出到 out 文件夹
python excel_to_md.py 设计书.xlsx -o ./out

# 合并为一个 md
python excel_to_md.py 设计书.xlsx -o ./out --one-file

# 同时尝试导出形状/文本框里的文字
python excel_to_md.py 设计书.xlsx -o ./out --include-shapes

# 每个表最多 500 行
python excel_to_md.py 设计书.xlsx -o ./out --max-rows 500
```

成功时会在标准输出打印生成的每个 `.md` 的完整路径。

---

## 限制摘要

| 内容 | 说明 |
|------|------|
| 合并单元格 | 通过 HTML 表表达。 |
| 文本框示意图（默认） | 忽略。 |
| 箭头、流程拓扑 | Markdown 无法还原；`--include-shapes` 也不保证。 |
| 文件格式 | 仅 `.xlsx`。 |
