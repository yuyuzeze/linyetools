"""Render a rectangular CellIR grid to a PNG approximating the Excel look."""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

from ..models.document_ir import CellIR


_DEFAULT_CELL_WIDTH = 72
_DEFAULT_CELL_HEIGHT = 24
_PADDING = 2
_FONT_CANDIDATES = (
    "Yu Gothic UI",
    "Yu Gothic",
    "Meiryo UI",
    "Meiryo",
    "MS Gothic",
    "Microsoft YaHei",
    "Segoe UI",
    "Arial",
)


def _rgb_from_color(color: object) -> tuple[int, int, int] | None:
    if not isinstance(color, dict):
        return None
    value = color.get("value")
    if not isinstance(value, str):
        return None
    hex_value = value.removeprefix("#").upper()
    if len(hex_value) == 8:
        hex_value = hex_value[2:]
    if len(hex_value) != 6:
        return None
    try:
        return int(hex_value[0:2], 16), int(hex_value[2:4], 16), int(hex_value[4:6], 16)
    except ValueError:
        return None


def _fill_color(cell: CellIR) -> tuple[int, int, int]:
    if cell.style is None:
        return (255, 255, 255)
    fill = cell.style.fill or {}
    for key in ("foreground", "background"):
        rgb = _rgb_from_color(fill.get(key))
        if rgb is not None and rgb != (0, 0, 0):
            # Excel theme/indexed blacks often mean "no useful color".
            if fill.get("type") in {None, "none"}:
                continue
            return rgb
    return (255, 255, 255)


def _font_color(cell: CellIR) -> tuple[int, int, int]:
    if cell.style is None or not cell.style.font:
        return (32, 32, 32)
    rgb = _rgb_from_color(cell.style.font.get("color"))
    return rgb or (32, 32, 32)


def _has_border(cell: CellIR, side: str) -> bool:
    if cell.style is None or not cell.style.border:
        return False
    edge = cell.style.border.get(side)
    return isinstance(edge, dict) and bool(edge.get("style"))


def _load_font(size: int) -> ImageFont.ImageFont:
    for name in _FONT_CANDIDATES:
        try:
            return ImageFont.truetype(name, size=size)
        except OSError:
            continue
    return ImageFont.load_default()


def _cell_text(cell: CellIR) -> str:
    if cell.display_value is not None:
        return cell.display_value
    if cell.raw_value is None:
        return ""
    return str(cell.raw_value)


def render_cells_to_png(
    cells: list[CellIR],
    destination: str | Path,
    *,
    cell_width: int = _DEFAULT_CELL_WIDTH,
    cell_height: int = _DEFAULT_CELL_HEIGHT,
) -> Path:
    """Draw ``cells`` into a PNG at ``destination`` and return the path."""

    if not cells:
        raise ValueError("cannot screenshot an empty cell range")
    destination = Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)

    min_row = min(cell.row for cell in cells)
    max_row = max(cell.row + cell.row_span - 1 for cell in cells)
    min_col = min(cell.column for cell in cells)
    max_col = max(cell.column + cell.col_span - 1 for cell in cells)
    cols = max_col - min_col + 1
    rows = max_row - min_row + 1
    width = cols * cell_width + 1
    height = rows * cell_height + 1
    image = Image.new("RGB", (width, height), (255, 255, 255))
    draw = ImageDraw.Draw(image)
    font = _load_font(12)
    bold_font = _load_font(12)

    by_coord = {(cell.row, cell.column): cell for cell in cells}
    covered: set[tuple[int, int]] = set()
    for cell in cells:
        if cell.merged_master:
            continue
        key = (cell.row, cell.column)
        if key in covered:
            continue
        x0 = (cell.column - min_col) * cell_width
        y0 = (cell.row - min_row) * cell_height
        x1 = x0 + cell.col_span * cell_width
        y1 = y0 + cell.row_span * cell_height
        draw.rectangle([x0, y0, x1, y1], fill=_fill_color(cell))
        for row in range(cell.row, cell.row + cell.row_span):
            for column in range(cell.column, cell.column + cell.col_span):
                covered.add((row, column))

        text = _cell_text(cell)
        if text:
            use_font = bold_font if (cell.style and cell.style.font.get("bold")) else font
            draw.text(
                (x0 + _PADDING, y0 + _PADDING),
                text,
                fill=_font_color(cell),
                font=use_font,
            )

        # Draw borders for the master cell (and members that keep edge styles).
        if _has_border(cell, "left"):
            draw.line([(x0, y0), (x0, y1)], fill=(80, 80, 80), width=1)
        if _has_border(cell, "right"):
            draw.line([(x1, y0), (x1, y1)], fill=(80, 80, 80), width=1)
        if _has_border(cell, "top"):
            draw.line([(x0, y0), (x1, y0)], fill=(80, 80, 80), width=1)
        if _has_border(cell, "bottom"):
            draw.line([(x0, y1), (x1, y1)], fill=(80, 80, 80), width=1)

    # Light grid for empty positions so the legend block stays readable.
    for row in range(min_row, max_row + 1):
        for column in range(min_col, max_col + 1):
            if (row, column) in by_coord or any(
                cell.row <= row < cell.row + cell.row_span
                and cell.column <= column < cell.column + cell.col_span
                for cell in cells
                if not cell.merged_master
            ):
                continue
            x0 = (column - min_col) * cell_width
            y0 = (row - min_row) * cell_height
            draw.rectangle(
                [x0, y0, x0 + cell_width, y0 + cell_height],
                outline=(220, 220, 220),
            )

    image.save(destination, format="PNG")
    return destination
