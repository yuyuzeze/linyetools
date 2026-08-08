"""Capture an Excel A1 range to PNG via Excel COM (Windows + Excel required).

Two strategies, tried in order:

1. Clipboard grab via :func:`PIL.ImageGrab.grabclipboard`. ``Range.CopyPicture``
   pushes the rendered bitmap to the Windows clipboard; ``ImageGrab`` reads
   it back. This is the documented high-fidelity approach — no Pillow
   redrawing of cells is involved, only the bitmap Excel itself produced.
2. ``Chart.Export`` fallback. A temporary chart object is sized to the
   target range and ``Chart.Paste`` / ``Chart.Export`` are used to write
   a PNG. This is the fallback for hosts where the clipboard grab returns
   ``None`` (for example, headless / service accounts).

Both paths rely on Excel's own rendering pipeline; we never redraw cells
with Pillow.
"""

from __future__ import annotations

import time
from pathlib import Path


def capture_excel_range(
    workbook_path: str | Path,
    sheet_name: str,
    a1_range: str,
    destination: str | Path,
) -> Path:
    """Export ``sheet_name!a1_range`` from ``workbook_path`` to a PNG file.

    Requires:
    - Windows
    - Microsoft Excel installed (the host running this script, not the
      dev's machine)
    - ``pywin32`` (``pip install pywin32``)

    Raises the underlying ``pywintypes.com_error`` unchanged so the caller
    can surface ``hr``, ``msg``, ``source`` and ``exception`` verbatim in
    ``diagnostics.json``.
    """

    destination = Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    workbook_path = Path(workbook_path).resolve()
    if not workbook_path.is_file():
        raise FileNotFoundError(workbook_path)

    try:
        import win32com.client  # type: ignore
    except ImportError as error:
        raise RuntimeError(
            "Excel COM 截图需要安装 pywin32：pip install pywin32"
        ) from error
    try:
        from PIL import ImageGrab
    except ImportError as error:
        raise RuntimeError("Excel COM 截图需要 Pillow：pip install Pillow") from error

    # Clear any stale clipboard bitmap so a previous run cannot leak into
    # this capture.
    try:
        ImageGrab.grabclipboard()
    except Exception:
        pass

    excel = win32com.client.DispatchEx("Excel.Application")
    excel.Visible = False
    excel.DisplayAlerts = False
    workbook = None
    try:
        workbook = excel.Workbooks.Open(str(workbook_path), ReadOnly=True)
        worksheet = workbook.Worksheets(sheet_name)
        target = worksheet.Range(a1_range)

        # ``xlScreen`` (1) keeps displayed colours/gridlines, ``xlBitmap``
        # (2) ensures the clipboard payload is a bitmap so
        # ``ImageGrab.grabclipboard`` returns a ``PIL.Image``.
        target.CopyPicture(Appearance=1, Format=2)

        # Excel's clipboard publish is asynchronous. Poll briefly so we
        # don't grab a stale (or empty) bitmap.
        image = None
        for _ in range(20):
            try:
                grabbed = ImageGrab.grabclipboard()
            except Exception:
                grabbed = None
            if grabbed is not None:
                image = grabbed
                break
            time.sleep(0.1)

        if image is not None:
            if image.mode not in {"RGB", "RGBA"}:
                image = image.convert("RGB")
            image.save(destination, format="PNG")
            return destination

        # Fallback: paste into a temporary chart and export. Only reached
        # when the clipboard grab returned ``None``, which is rare on a
        # normal interactive Windows session.
        width = max(float(target.Width), 40.0)
        height = max(float(target.Height), 20.0)
        chart_object = worksheet.ChartObjects().Add(0, 0, width, height)
        try:
            chart_object.Chart.Paste()
            chart_object.Chart.Export(str(destination.resolve()))
        finally:
            chart_object.Delete()
        if not destination.is_file():
            raise RuntimeError("Excel Chart.Export 未生成图片文件")
        return destination
    finally:
        try:
            if workbook is not None:
                workbook.Close(SaveChanges=False)
        finally:
            excel.Quit()


def render_region_screenshot(
    *,
    destination: str | Path,
    workbook_path: str | Path | None = None,
    sheet_name: str | None = None,
    a1_range: str | None = None,
) -> tuple[Path, str]:
    """Capture a worksheet range with Excel COM only (no Pillow redraw)."""

    if not workbook_path or not sheet_name or not a1_range:
        raise ValueError(
            "Excel COM 截图需要 workbook_path / sheet_name / a1_range"
        )
    path = capture_excel_range(workbook_path, sheet_name, a1_range, destination)
    return path, "excel_com"