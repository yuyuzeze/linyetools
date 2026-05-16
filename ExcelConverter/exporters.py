from __future__ import annotations

import csv
import html
from pathlib import Path


def _escape_md_cell(text: str) -> str:
    return text.replace("|", "\\|").replace("\n", " ").replace("\r", "")


def export_markdown(grid: list[list[str]], path: Path) -> None:
    if not grid:
        path.write_text("", encoding="utf-8")
        return

    lines: list[str] = []
    header = grid[0]
    lines.append("| " + " | ".join(_escape_md_cell(c) for c in header) + " |")
    lines.append("| " + " | ".join("---" for _ in header) + " |")
    for row in grid[1:]:
        padded = row + [""] * (len(header) - len(row)) if len(row) < len(header) else row[: len(header)]
        lines.append("| " + " | ".join(_escape_md_cell(c) for c in padded) + " |")

    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def export_csv(grid: list[list[str]], path: Path) -> None:
    with path.open("w", encoding="utf-8-sig", newline="") as f:
        writer = csv.writer(f, quoting=csv.QUOTE_MINIMAL)
        writer.writerows(grid)


def export_html(grid: list[list[str]], path: Path) -> None:
    parts = [
        "<!DOCTYPE html>",
        "<html>",
        "<head>",
        '<meta charset="utf-8">',
        "<title>Sheet</title>",
        "</head>",
        "<body>",
        '<table border="1">',
    ]

    if not grid:
        parts.append("</table></body></html>")
        path.write_text("\n".join(parts), encoding="utf-8")
        return

    header = grid[0]
    parts.append("<thead><tr>")
    for cell in header:
        parts.append(f"<th>{html.escape(cell)}</th>")
    parts.append("</tr></thead><tbody>")

    for row in grid[1:]:
        padded = row + [""] * (len(header) - len(row)) if len(row) < len(header) else row[: len(header)]
        parts.append("<tr>")
        for cell in padded:
            parts.append(f"<td>{html.escape(cell)}</td>")
        parts.append("</tr>")

    parts.append("</tbody></table></body></html>")
    path.write_text("\n".join(parts), encoding="utf-8")


FORMAT_EXPORTERS = {
    1: ("md", export_markdown),
    2: ("csv", export_csv),
    3: ("html", export_html),
}


def export(grid: list[list[str]], path: Path, format_type: int) -> None:
    _, exporter = FORMAT_EXPORTERS[format_type]
    exporter(grid, path)
