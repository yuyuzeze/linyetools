"""
Excel (.xlsx) → Markdown：内嵌 HTML 表格以支持合并单元格。

默认不读取文本框/形状（设计书里的示意图可忽略）。
需要形状内文字时请加 --include-shapes（顺序与箭头拓扑不保真）。

依赖：openpyxl；形状解析另用标准库 zipfile + xml.etree。
"""

from __future__ import annotations

import argparse
import html
import re
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable
from xml.etree import ElementTree as ET

from openpyxl import load_workbook
from openpyxl.worksheet.worksheet import Worksheet


def _escape_cell(v: object) -> str:
    if v is None:
        return ""
    return html.escape(str(v))


def _sheet_bounds(ws: Worksheet) -> tuple[int, int, int, int]:
    min_row = ws.min_row or 1
    min_col = ws.min_column or 1
    max_row = ws.max_row or 1
    max_col = ws.max_column or 1
    return min_row, min_col, max_row, max_col


def _merge_maps(ws: Worksheet) -> tuple[dict[tuple[int, int], tuple[int, int]], set[tuple[int, int]]]:
    """左上角单元格 -> (rowspan, colspan)；被合并覆盖的非主格坐标集合。"""
    master: dict[tuple[int, int], tuple[int, int]] = {}
    covered: set[tuple[int, int]] = set()
    for mr in ws.merged_cells.ranges:
        rs = mr.max_row - mr.min_row + 1
        cs = mr.max_col - mr.min_col + 1
        top = (mr.min_row, mr.min_col)
        master[top] = (rs, cs)
        for r in range(mr.min_row, mr.max_row + 1):
            for c in range(mr.min_col, mr.max_col + 1):
                if (r, c) != top:
                    covered.add((r, c))
    return master, covered


def sheet_to_html_table(ws: Worksheet, max_rows: int | None = None) -> str:
    min_row, min_col, max_row, max_col = _sheet_bounds(ws)
    if max_rows is not None:
        max_row = min(max_row, min_row + max_rows - 1)

    master, covered = _merge_maps(ws)
    lines = ["<table>", "<tbody>"]

    for r in range(min_row, max_row + 1):
        lines.append("<tr>")
        for c in range(min_col, max_col + 1):
            if (r, c) in covered:
                continue
            cell = ws.cell(r, c)
            val = _escape_cell(cell.value)
            attrs: list[str] = []
            if (r, c) in master:
                rs, cs = master[(r, c)]
                if rs > 1:
                    attrs.append(f'rowspan="{rs}"')
                if cs > 1:
                    attrs.append(f'colspan="{cs}"')
            attr_s = (" " + " ".join(attrs)) if attrs else ""
            lines.append(f"<td{attr_s}>{val}</td>")
        lines.append("</tr>")

    lines.extend(["</tbody>", "</table>"])
    return "\n".join(lines)


def _local_tag(tag: str) -> str:
    return tag.rsplit("}", 1)[-1] if "}" in tag else tag


def _drawing_zip_path(target: str) -> str:
    """sheet rels 中 Target 多为 ../drawings/drawing1.xml。"""
    target = target.replace("\\", "/")
    name = target.split("/")[-1]
    return f"xl/drawings/{name}"


def extract_shape_texts_from_xlsx(xlsx_path: Path, sheet_index: int) -> list[str]:
    """
    从 DrawingML 收集 a:t 文本；去重、顺序不保证。
    sheet_index 为 1-based，与 xl/worksheets/sheet{N}.xml 一致。
    """
    texts: list[str] = []
    seen: set[str] = set()
    ns_a = "http://schemas.openxmlformats.org/drawingml/2006/main"

    with zipfile.ZipFile(xlsx_path, "r") as zf:
        sheet_part = f"xl/worksheets/sheet{sheet_index}.xml"
        rels_part = f"xl/worksheets/_rels/sheet{sheet_index}.xml.rels"
        if sheet_part not in zf.namelist() or rels_part not in zf.namelist():
            return []

        root = ET.fromstring(zf.read(sheet_part))
        r_ids: list[str] = []
        for el in root.iter():
            if _local_tag(el.tag) == "drawing":
                rid = el.attrib.get(
                    "{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id"
                )
                if rid:
                    r_ids.append(rid)

        rels_root = ET.fromstring(zf.read(rels_part))
        rid_to_target: dict[str, str] = {}
        for rel in rels_root:
            if _local_tag(rel.tag) != "Relationship":
                continue
            rid, tgt = rel.attrib.get("Id"), rel.attrib.get("Target")
            if rid and tgt:
                rid_to_target[rid] = tgt

        drawing_paths = [_drawing_zip_path(rid_to_target[r]) for r in r_ids if r in rid_to_target]

        for dp in drawing_paths:
            if dp not in zf.namelist():
                continue
            droot = ET.fromstring(zf.read(dp))
            for el in droot.iter():
                if el.tag == f"{{{ns_a}}}t" and el.text:
                    t = el.text.strip()
                    if t and t not in seen:
                        seen.add(t)
                        texts.append(t)

    return texts


def build_markdown_for_sheet(
    ws: Worksheet,
    *,
    workbook_path: Path,
    sheet_index: int,
    include_shapes: bool,
    max_rows: int | None,
) -> str:
    parts: list[str] = [f"## {ws.title}\n"]
    parts.append(
        "<!-- "
        f"source: {workbook_path.name} | sheet: {sheet_index} | "
        f"exported: {datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')}"
        " -->\n"
    )
    if not include_shapes:
        parts.append(
            "<!-- 已跳过形状/文本框（默认）。仅单元格进入下方表格；"
            "需要形状内文字请加 --include-shapes（箭头关系仍不保真） -->\n"
        )

    parts.append(sheet_to_html_table(ws, max_rows=max_rows))
    parts.append("\n")

    if include_shapes:
        shape_lines = extract_shape_texts_from_xlsx(workbook_path, sheet_index)
        parts.append("\n### 形状内文字（DrawingML，顺序不保证）\n\n")
        if shape_lines:
            for line in shape_lines:
                parts.append(f"- {html.escape(line)}\n")
        else:
            parts.append("（未发现可解析文本）\n")

    return "".join(parts)


def convert_workbook(
    xlsx_path: Path,
    out_dir: Path,
    *,
    one_file: bool,
    include_shapes: bool,
    max_rows: int | None,
) -> list[Path]:
    xlsx_path = xlsx_path.resolve()
    out_dir = out_dir.resolve()
    out_dir.mkdir(parents=True, exist_ok=True)

    wb = load_workbook(xlsx_path, data_only=True)
    written: list[Path] = []

    if one_file:
        chunks = [f"# {xlsx_path.stem}\n\n"]
        for i, ws in enumerate(wb.worksheets, start=1):
            chunks.append(
                build_markdown_for_sheet(
                    ws,
                    workbook_path=xlsx_path,
                    sheet_index=i,
                    include_shapes=include_shapes,
                    max_rows=max_rows,
                )
            )
            chunks.append("\n")
        out_path = out_dir / f"{xlsx_path.stem}.md"
        out_path.write_text("".join(chunks), encoding="utf-8")
        written.append(out_path)
    else:
        for i, ws in enumerate(wb.worksheets, start=1):
            safe = re.sub(r'[<>:"/\\|?*]', "_", ws.title) or f"sheet{i}"
            out_path = out_dir / f"{xlsx_path.stem}__{safe}.md"
            body = build_markdown_for_sheet(
                ws,
                workbook_path=xlsx_path,
                sheet_index=i,
                include_shapes=include_shapes,
                max_rows=max_rows,
            )
            out_path.write_text(f"# {ws.title}\n\n{body}", encoding="utf-8")
            written.append(out_path)

    wb.close()
    return written


def main(argv: Iterable[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="excel_to_md",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        description="将 .xlsx 转为 Markdown，表格使用内嵌 HTML（支持合并单元格）。输出 UTF-8。",
        epilog="""
默认行为（重要）：
  不读取文本框/形状/示意图层，只导出单元格网格。
  若说明只写在文本框里而未写入单元格，默认模式下不会出现在 Markdown 中。

可选：
  --include-shapes  从 DrawingML 抽取形状内可见文字（列表形式）；
                    顺序可能与画面上不一致，且不表示箭头/连接线拓扑。

依赖：仅 pip 安装 openpyxl；不调用 LibreOffice 或 Excel。
""".strip(),
    )
    parser.add_argument("xlsx", type=Path, help="输入 .xlsx 文件路径")
    parser.add_argument("-o", "--output-dir", type=Path, required=True, help="输出目录（将创建）")
    parser.add_argument("--one-file", action="store_true", help="所有工作表写入单个 .md")
    parser.add_argument(
        "--include-shapes",
        action="store_true",
        help="额外解析形状/文本框内文字（见上方说明）",
    )
    parser.add_argument(
        "--max-rows",
        type=int,
        default=None,
        metavar="N",
        help="每个表最多导出 N 行（从该表 min_row 起计数）",
    )
    args = parser.parse_args(list(argv) if argv is not None else None)

    if not args.xlsx.is_file():
        print(f"错误：文件不存在 {args.xlsx}", file=sys.stderr)
        return 2
    if args.xlsx.suffix.lower() != ".xlsx":
        print("错误：仅支持 .xlsx", file=sys.stderr)
        return 2

    for path in convert_workbook(
        args.xlsx,
        args.output_dir,
        one_file=args.one_file,
        include_shapes=args.include_shapes,
        max_rows=args.max_rows,
    ):
        print(path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
