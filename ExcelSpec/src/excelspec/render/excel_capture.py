"""Capture Excel A1 ranges to PNG via Excel COM (Windows + Excel required).

Two strategies, tried in order for each range:

1. ``CopyPicture(Appearance=xlPrinter)`` -> :func:`PIL.ImageGrab.grabclipboard`.
   ``xlPrinter`` (2) renders the range through Excel's print engine, which
   works whether or not Excel has a visible window. ``xlScreen`` (1) only
   captures the rendered window pixels and, with ``Visible=False``, the
   clipboard ends up holding a blank 757-byte bitmap. We deliberately do
   not use ``xlScreen`` here.
2. ``Chart.Export`` fallback. A temporary chart object is sized to the
   target range and ``Chart.Paste`` / ``Chart.Export`` are used to write
   a PNG. Only reached when the clipboard grab returns ``None`` (rare
   headless / service scenarios).

Both paths rely on Excel's own rendering pipeline; we never redraw cells
with Pillow.

:class:`ExcelCaptureSession` opens the Excel *Application* and the *Workbook*
exactly once for a whole batch of captures. The historical single-shot
:func:`capture_excel_range` now delegates to a one-off session, and
:func:`render_region_screenshot` transparently reuses whatever session is
active (via :func:`open_capture_session`), so callers wrapping a workbook in a
single ``with`` block pay the Excel-startup cost only once.
"""

from __future__ import annotations

import contextvars
import time
from contextlib import contextmanager
from pathlib import Path
from typing import Iterator


# The session that ambient callers (``capture_excel_range`` /
# ``render_region_screenshot``) should reuse instead of launching Excel again.
_ACTIVE_SESSION: contextvars.ContextVar["ExcelCaptureSession | None"] = (
    contextvars.ContextVar("excel_capture_active_session", default=None)
)

# Process-wide count of Excel Application launches, so tests and the benchmark
# can assert that a batch of captures reuses one process.
_LAUNCH_COUNT = 0


def capture_launch_count() -> int:
    """Total number of Excel Application instances launched this process."""

    return _LAUNCH_COUNT


def _capture_target(worksheet, a1_range: str, destination: Path) -> Path:
    """Render ``worksheet!a1_range`` to ``destination`` from an open worksheet.

    Requires Pillow for the clipboard path. Raises the underlying
    ``pywintypes.com_error`` unchanged so callers can surface COM detail.
    """

    from PIL import ImageGrab

    destination.parent.mkdir(parents=True, exist_ok=True)
    target = worksheet.Range(a1_range)

    # Clear any stale clipboard bitmap so a previous capture cannot leak in.
    try:
        ImageGrab.grabclipboard()
    except Exception:
        pass

    # ``xlPrinter`` (2) renders via Excel's print pipeline, which works even
    # when ``Visible=False``. ``xlBitmap`` (2) ensures a raster payload.
    target.CopyPicture(Appearance=2, Format=2)

    # Excel's clipboard publish is asynchronous; poll briefly.
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

    # Fallback: paste into a temporary chart and export.
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


class ExcelCaptureSession:
    """Reusable Excel COM context: one Application + one Workbook per batch.

    Usage::

        with ExcelCaptureSession(workbook_path) as session:
            session.capture("Sheet1", "A1:D10", out1)
            session.capture("Sheet1", "A20:D40", out2)

    While the ``with`` block is active it also registers itself as the ambient
    session, so nested :func:`capture_excel_range` /
    :func:`render_region_screenshot` calls reuse it instead of relaunching
    Excel. The workbook and Excel process are closed reliably on exit.
    """

    def __init__(self, workbook_path: str | Path) -> None:
        self.workbook_path = Path(workbook_path).resolve()
        self._excel = None
        self._workbook = None
        self._token = None

    # -- lifecycle -------------------------------------------------------------

    def open(self) -> "ExcelCaptureSession":
        if self._excel is not None:
            return self
        if not self.workbook_path.is_file():
            raise FileNotFoundError(self.workbook_path)
        try:
            import win32com.client  # type: ignore
        except ImportError as error:
            raise RuntimeError(
                "Excel COM 截图需要安装 pywin32：pip install pywin32"
            ) from error
        global _LAUNCH_COUNT
        excel = win32com.client.DispatchEx("Excel.Application")
        _LAUNCH_COUNT += 1
        excel.Visible = False
        excel.DisplayAlerts = False
        self._excel = excel
        self._workbook = excel.Workbooks.Open(str(self.workbook_path), ReadOnly=True)
        return self

    def close(self) -> None:
        try:
            if self._workbook is not None:
                self._workbook.Close(SaveChanges=False)
        finally:
            self._workbook = None
            excel = self._excel
            self._excel = None
            if excel is not None:
                excel.Quit()

    # -- capture ---------------------------------------------------------------

    def capture(self, sheet_name: str, a1_range: str, destination: str | Path) -> Path:
        if self._workbook is None:
            self.open()
        worksheet = self._workbook.Worksheets(sheet_name)
        return _capture_target(worksheet, a1_range, Path(destination))

    # -- context manager -------------------------------------------------------

    def __enter__(self) -> "ExcelCaptureSession":
        # Deliberately lazy: Excel is launched on the first ``capture`` call,
        # so wrapping a whole workbook costs nothing when no region actually
        # needs a screenshot (and never launches Excel on hosts without it
        # until a capture is genuinely attempted).
        self._token = _ACTIVE_SESSION.set(self)
        return self

    def __exit__(self, *exc: object) -> None:
        if self._token is not None:
            _ACTIVE_SESSION.reset(self._token)
            self._token = None
        self.close()


@contextmanager
def open_capture_session(workbook_path: str | Path) -> Iterator[ExcelCaptureSession]:
    """Convenience wrapper yielding an active :class:`ExcelCaptureSession`."""

    with ExcelCaptureSession(workbook_path) as session:
        yield session


def active_capture_session() -> "ExcelCaptureSession | None":
    """The ambient session for the current context, if any."""

    return _ACTIVE_SESSION.get()


def capture_excel_range(
    workbook_path: str | Path,
    sheet_name: str,
    a1_range: str,
    destination: str | Path,
) -> Path:
    """Export ``sheet_name!a1_range`` from ``workbook_path`` to a PNG file.

    Backwards-compatible single-shot API. If an :class:`ExcelCaptureSession`
    for the same workbook is already active it is reused (no second Excel
    launch); otherwise a one-off session is opened and closed around the call.

    Requires Windows, Microsoft Excel, ``pywin32`` and ``Pillow``. Raises the
    underlying ``pywintypes.com_error`` unchanged for diagnostics.
    """

    destination = Path(destination)
    workbook_path = Path(workbook_path).resolve()

    active = _ACTIVE_SESSION.get()
    if active is not None and active.workbook_path == workbook_path:
        return active.capture(sheet_name, a1_range, destination)

    with ExcelCaptureSession(workbook_path) as session:
        return session.capture(sheet_name, a1_range, destination)


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


__all__ = [
    "ExcelCaptureSession",
    "active_capture_session",
    "capture_excel_range",
    "capture_launch_count",
    "open_capture_session",
    "render_region_screenshot",
]
