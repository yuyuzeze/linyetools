"""
Excel (.xlsx) → Markdown。

表格一律为 **GFM 管道表**（`| ... |`）。合并单元格在导出时对每个格子 **重复左上角格的值**，
以便用纯 Markdown 表达（不再使用 <table>/<tr>/<td>）。

跳过整表无文字行；输出折叠连续空行。

默认不读取文本框/形状；需要时加 --include-shapes（顺序与箭头拓扑不保真）。
加 --export-images 时从嵌入图导出图片文件，并为每张图生成独立 HTML（文件名形如「图1-1 xxx」）。

依赖：openpyxl；形状/图片解析用标准库 zipfile + xml.etree。
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
from urllib.parse import quote
from xml.etree import ElementTree as ET

from openpyxl import load_workbook
from openpyxl.worksheet.worksheet import Worksheet


def _sheet_bounds(ws: Worksheet) -> tuple[int, int, int, int]:
    min_row = ws.min_row or 1
    min_col = ws.min_column or 1
    max_row = ws.max_row or 1
    max_col = ws.max_column or 1
    return min_row, min_col, max_row, max_col


def _merge_master_map(ws: Worksheet) -> dict[tuple[int, int], tuple[int, int]]:
    """合并区内每个坐标 → 该区左上角坐标。"""
    m: dict[tuple[int, int], tuple[int, int]] = {}
    for mr in ws.merged_cells.ranges:
        top = (mr.min_row, mr.min_col)
        for r in range(mr.min_row, mr.max_row + 1):
            for c in range(mr.min_col, mr.max_col + 1):
                m[(r, c)] = top
    return m


def _effective_cell_value(
    ws: Worksheet, r: int, c: int, master_map: dict[tuple[int, int], tuple[int, int]]
) -> object:
    top = master_map.get((r, c), (r, c))
    return ws.cell(top[0], top[1]).value


def _value_is_blank(v: object) -> bool:
    if v is None:
        return True
    if isinstance(v, str):
        return v.strip() == ""
    return False


def _row_has_visible_text(
    ws: Worksheet,
    r: int,
    min_col: int,
    max_col: int,
    master_map: dict[tuple[int, int], tuple[int, int]],
) -> bool:
    for c in range(min_col, max_col + 1):
        if not _value_is_blank(_effective_cell_value(ws, r, c, master_map)):
            return True
    return False


def _format_pipe_cell(v: object) -> str:
    if v is None:
        return ""
    s = str(v).replace("\r\n", "\n").replace("\r", "\n")
    s = " ".join(line.strip() for line in s.split("\n") if line.strip())
    return s.replace("\\", "\\\\").replace("|", "\\|")


def sheet_to_pipe_markdown_table(ws: Worksheet, max_rows: int | None = None) -> str:
    """GFM 管道表；合并格按 Excel 语义用左上角值填满各格。"""
    min_row, min_col, max_row, max_col = _sheet_bounds(ws)
    if max_rows is not None:
        max_row = min(max_row, min_row + max_rows - 1)
    ncols = max_col - min_col + 1
    if ncols < 1:
        return "_（空表）_"

    master_map = _merge_master_map(ws)
    body_lines: list[str] = []
    for r in range(min_row, max_row + 1):
        if not _row_has_visible_text(ws, r, min_col, max_col, master_map):
            continue
        cells = [
            _format_pipe_cell(_effective_cell_value(ws, r, c, master_map))
            for c in range(min_col, max_col + 1)
        ]
        body_lines.append("| " + " | ".join(cells) + " |")

    if not body_lines:
        return "_（空表）_"

    sep = "| " + " | ".join(["---"] * ncols) + " |"
    return "\n".join([body_lines[0], sep] + body_lines[1:])


def collapse_extra_blank_lines(text: str) -> str:
    """去掉连续空行，只保留至多一个换行。"""
    lines = text.splitlines()
    out: list[str] = []
    prev_blank = False
    for line in lines:
        blank = line.strip() == ""
        if blank and prev_blank:
            continue
        out.append(line)
        prev_blank = blank
    return "\n".join(out).strip() + "\n"


def _local_tag(tag: str) -> str:
    return tag.rsplit("}", 1)[-1] if "}" in tag else tag


def _drawing_zip_path(target: str) -> str:
    """sheet rels 中 Target 多为 ../drawings/drawing1.xml。"""
    target = target.replace("\\", "/")
    name = target.split("/")[-1]
    return f"xl/drawings/{name}"


def _drawing_rels_zip_path(drawing_part: str) -> str:
    """xl/drawings/drawing1.xml -> xl/drawings/_rels/drawing1.xml.rels"""
    name = drawing_part.rsplit("/", 1)[-1]
    return f"xl/drawings/_rels/{name}.rels"


def _resolve_media_zip_path(drawing_part: str, target: str) -> str:
    """把 drawing 的 rel Target（如 ../media/image1.png）解析为 zip 内路径。"""
    target = target.replace("\\", "/").lstrip("/")
    if target.startswith("xl/"):
        return target
    base = drawing_part.rsplit("/", 1)[0]
    parts = base.split("/")
    for piece in target.split("/"):
        if piece == "..":
            if parts:
                parts.pop()
        elif piece and piece != ".":
            parts.append(piece)
    return "/".join(parts)


def _md_uri_path(relative_posix: str) -> str:
    """Markdown 链接里用的相对 URI（含空格等则分段编码）。"""
    parts = [p for p in relative_posix.replace("\\", "/").split("/") if p != ""]
    return "/".join(quote(p, safe="") for p in parts)


def _load_rels_id_to_target(zf: zipfile.ZipFile, rels_part: str) -> dict[str, str]:
    if rels_part not in zf.namelist():
        return {}
    root = ET.fromstring(zf.read(rels_part))
    m: dict[str, str] = {}
    for rel in root:
        if _local_tag(rel.tag) != "Relationship":
            continue
        rid, tgt = rel.attrib.get("Id"), rel.attrib.get("Target")
        if rid and tgt:
            m[rid] = tgt
    return m


def _anchor_sort_tuple(anchor_el: ET.Element) -> tuple[int, int]:
    """用锚点左上角行列排序（无则极大值）。"""
    for child in anchor_el:
        if _local_tag(child.tag) != "from":
            continue
        row, col = 10**9, 10**9
        for sub in child:
            t = _local_tag(sub.tag)
            if t == "row" and sub.text is not None and sub.text.strip().isdigit():
                row = int(sub.text)
            elif t == "col" and sub.text is not None and sub.text.strip().isdigit():
                col = int(sub.text)
        return (row, col)
    return (10**9, 10**9)


def _pic_embed_rid(pic_el: ET.Element) -> str | None:
    r_ns = "{http://schemas.openxmlformats.org/officeDocument/2006/relationships}embed"
    for el in pic_el.iter():
        if _local_tag(el.tag) == "blip":
            rid = el.attrib.get(r_ns)
            if rid:
                return rid
    return None


def _pic_caption_from_excel(pic_el: ET.Element) -> str:
    """优先用图片「说明」再「名称」；排除默认 Picture N。"""
    name, descr = "", ""
    for el in pic_el.iter():
        if _local_tag(el.tag) == "cNvPr":
            name = (el.attrib.get("name") or "").strip()
            descr = (el.attrib.get("descr") or "").strip()
            break
    for candidate in (descr, name):
        if not candidate:
            continue
        if re.match(r"^Picture\s*\d+$", candidate, re.I):
            continue
        return candidate
    return ""


def _sanitize_figure_label(s: str, max_len: int = 120) -> str:
    s = re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", s)
    s = re.sub(r"\s+", " ", s).strip()
    return s[:max_len] if s else ""


def _unique_stem(figures_dir: Path, base: str, ext: str) -> str:
    """base 不含扩展名；返回可用文件名（不含路径） base.ext 或 base_2.ext。"""
    stem = base + ext
    if not (figures_dir / stem).exists():
        return stem
    for n in range(2, 10_000):
        alt = f"{base}_{n}{ext}"
        if not (figures_dir / alt).exists():
            return alt
    return base + ext


def export_sheet_embedded_images(
    xlsx_path: Path,
    figures_dir: Path,
    sheet_index: int,
) -> tuple[list[Path], str]:
    """
    导出当前 sheet 的嵌入位图：图片文件 + 同主名的 .html。
    文件名：图{sheet_index}-{序号} [Excel中图片说明/名称].ext
    返回 (生成的 html 路径列表, 可追加到 md 的 Markdown 片段)。
    """
    figures_dir.mkdir(parents=True, exist_ok=True)
    rel_fig = figures_dir.name
    html_paths: list[Path] = []
    md_lines: list[str] = []

    r_ns = "{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id"
    sheet_part = f"xl/worksheets/sheet{sheet_index}.xml"
    rels_part = f"xl/worksheets/_rels/sheet{sheet_index}.xml.rels"

    with zipfile.ZipFile(xlsx_path, "r") as zf:
        if sheet_part not in zf.namelist() or rels_part not in zf.namelist():
            return [], ""

        sheet_root = ET.fromstring(zf.read(sheet_part))
        r_ids: list[str] = []
        for el in sheet_root.iter():
            if _local_tag(el.tag) == "drawing":
                rid = el.attrib.get(r_ns)
                if rid:
                    r_ids.append(rid)

        sheet_rid_to_target = _load_rels_id_to_target(zf, rels_part)
        drawing_parts = [
            _drawing_zip_path(sheet_rid_to_target[r])
            for r in r_ids
            if r in sheet_rid_to_target
        ]

        entries: list[tuple[tuple[int, int], int, str, str, str]] = []
        # (sort_row, sort_col), order, drawing_part, embed_rid, caption
        seq = 0
        for dp in drawing_parts:
            if dp not in zf.namelist():
                continue
            droot = ET.fromstring(zf.read(dp))
            drawing_rels = _drawing_rels_zip_path(dp)
            rid_to_media = _load_rels_id_to_target(zf, drawing_rels)

            for anchor in droot:
                if _local_tag(anchor.tag) not in ("twoCellAnchor", "oneCellAnchor", "absoluteAnchor"):
                    continue
                sort_key = _anchor_sort_tuple(anchor)
                for child in anchor:
                    if _local_tag(child.tag) != "pic":
                        continue
                    rid = _pic_embed_rid(child)
                    if not rid or rid not in rid_to_media:
                        continue
                    target = rid_to_media[rid]
                    media_zip = _resolve_media_zip_path(dp, target)
                    if media_zip not in zf.namelist():
                        continue
                    cap = _pic_caption_from_excel(child)
                    entries.append((sort_key, seq, media_zip, rid, cap))
                    seq += 1

        entries.sort(key=lambda t: (t[0][0], t[0][1], t[1]))

        for idx, (_sk, _seq, media_zip, _rid, caption) in enumerate(entries, start=1):
            raw = zf.read(media_zip)
            ext = Path(media_zip).suffix.lower() or ".bin"
            if ext not in (".png", ".jpeg", ".jpg", ".gif", ".bmp", ".tif", ".tiff", ".wmf", ".emf"):
                ext = ".bin"

            label = _sanitize_figure_label(caption)
            if label:
                base = f"图{sheet_index}-{idx} {label}"
            else:
                base = f"图{sheet_index}-{idx}"
            base = base[:120].rstrip(" .")

            img_name = _unique_stem(figures_dir, base, ext)
            html_name = Path(img_name).with_suffix(".html").name
            img_path = figures_dir / img_name
            html_path = figures_dir / html_name
            img_path.write_bytes(raw)

            title_esc = html.escape(label or f"图{sheet_index}-{idx}")
            img_esc = html.escape(img_name)
            html_body = (
                "<!DOCTYPE html>\n"
                '<html lang="ja">\n<head>\n'
                '<meta charset="utf-8">\n'
                f"<title>{title_esc}</title>\n"
                "</head>\n<body>\n"
                f'<figure><img src="{img_esc}" alt="{title_esc}">'
                f"<figcaption>{title_esc}</figcaption></figure>\n"
                "</body>\n</html>\n"
            )
            html_path.write_text(html_body, encoding="utf-8")
            html_paths.append(html_path)

            href = _md_uri_path(f"{rel_fig}/{html_path.name}")
            md_lines.append(f"- [{html.escape(html_path.name)}]({href})\n")

    if not md_lines:
        return [], ""

    appendix = (
        f"\n### 嵌入图片（{rel_fig}/）\n\n"
        + "".join(md_lines)
        + "\n"
    )
    return html_paths, appendix


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
    figures_appendix: str = "",
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

    parts.append(sheet_to_pipe_markdown_table(ws, max_rows=max_rows))
    parts.append("\n")

    if include_shapes:
        shape_lines = extract_shape_texts_from_xlsx(workbook_path, sheet_index)
        parts.append("\n### 形状内文字（DrawingML，顺序不保证）\n")
        if shape_lines:
            for line in shape_lines:
                parts.append(f"- {html.escape(line)}\n")
        else:
            parts.append("（未发现可解析文本）\n")

    if figures_appendix:
        parts.append(figures_appendix)

    return collapse_extra_blank_lines("".join(parts))


def convert_workbook(
    xlsx_path: Path,
    out_dir: Path,
    *,
    one_file: bool,
    include_shapes: bool,
    max_rows: int | None,
    export_images: bool,
) -> list[Path]:
    xlsx_path = xlsx_path.resolve()
    out_dir = out_dir.resolve()
    out_dir.mkdir(parents=True, exist_ok=True)

    wb = load_workbook(xlsx_path, data_only=True)
    written: list[Path] = []
    figures_dir = out_dir / f"{xlsx_path.stem}_images" if export_images else None

    if one_file:
        chunks = [f"# {xlsx_path.stem}\n\n"]
        for i, ws in enumerate(wb.worksheets, start=1):
            fig_append = ""
            fig_html: list[Path] = []
            if figures_dir is not None:
                fig_html, fig_append = export_sheet_embedded_images(xlsx_path, figures_dir, i)
                written.extend(fig_html)
            chunks.append(
                build_markdown_for_sheet(
                    ws,
                    workbook_path=xlsx_path,
                    sheet_index=i,
                    include_shapes=include_shapes,
                    max_rows=max_rows,
                    figures_appendix=fig_append,
                )
            )
            chunks.append("\n")
        out_path = out_dir / f"{xlsx_path.stem}.md"
        out_path.write_text(collapse_extra_blank_lines("".join(chunks)), encoding="utf-8")
        written.append(out_path)
    else:
        for i, ws in enumerate(wb.worksheets, start=1):
            fig_append = ""
            fig_html: list[Path] = []
            if figures_dir is not None:
                fig_html, fig_append = export_sheet_embedded_images(xlsx_path, figures_dir, i)
                written.extend(fig_html)
            safe = re.sub(r'[<>:"/\\|?*]', "_", ws.title) or f"sheet{i}"
            out_path = out_dir / f"{xlsx_path.stem}__{safe}.md"
            body = build_markdown_for_sheet(
                ws,
                workbook_path=xlsx_path,
                sheet_index=i,
                include_shapes=include_shapes,
                max_rows=max_rows,
                figures_appendix=fig_append,
            )
            out_path.write_text(
                collapse_extra_blank_lines(f"# {ws.title}\n\n{body}"),
                encoding="utf-8",
            )
            written.append(out_path)

    wb.close()
    return written


def main(argv: Iterable[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="excel_to_md",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        description="将 .xlsx 转为 Markdown：GFM 管道表（合并格重复主格值）；可选导出嵌入图及配套 HTML（--export-images）。UTF-8。",
        epilog="""
默认行为（重要）：
  不读取文本框/形状/示意图层，只导出单元格网格。
  若说明只写在文本框里而未写入单元格，默认模式下不会出现在 Markdown 中。

可选：
  --include-shapes  从 DrawingML 抽取形状内可见文字（列表形式）；
                    顺序可能与画面上不一致，且不表示箭头/连接线拓扑。
  --export-images   导出嵌入图片到「工作簿名_images/」，文件名 图sheet-序号 及 Excel 图片名称/说明；
                    每张图配一个同主名 .html；对应 .md 末尾追加链接列表。

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
    parser.add_argument(
        "--export-images",
        action="store_true",
        help="导出嵌入图片与同主名 HTML 到 工作簿名_images/，并在 md 中追加链接",
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
        export_images=args.export_images,
    ):
        print(path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
