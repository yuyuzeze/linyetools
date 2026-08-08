"""Capture an Excel A1 range to PNG via Excel COM (Windows + Excel required)."""

from __future__ import annotations

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
    - Microsoft Excel installed
    - ``pywin32`` (``pip install pywin32``)
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

    excel = win32com.client.DispatchEx("Excel.Application")
    excel.Visible = False
    excel.DisplayAlerts = False
    workbook = None
    try:
        workbook = excel.Workbooks.Open(str(workbook_path), ReadOnly=True)
        worksheet = workbook.Worksheets(sheet_name)
        target = worksheet.Range(a1_range)
        # Appearance=1 (xlScreen), Format=2 (xlBitmap)
        target.CopyPicture(Appearance=1, Format=2)
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
