"""Deterministic region detection and routing over the sparse workbook IR.

Data flow (zero-config)::

    SparseWorkbookIR
      -> RegionDetector.detect_sheet   (works on sparse cells only)
      -> list[CandidateRegion]
      -> materialize_region(bounds)     (only the selected finite regions)
      -> RegionRouter.route
      -> DocumentIR

No step materialises the whole ``raw-grid``; only the bounds of a finally
selected region are ever densified.
"""

from .models import (
    CandidateRegion,
    CandidateRegionType,
    CellBounds,
)
from .detector import RegionDetector, detect_sheet
from .router import RegionRouter, route_candidates

__all__ = [
    "CandidateRegion",
    "CandidateRegionType",
    "CellBounds",
    "RegionDetector",
    "RegionRouter",
    "detect_sheet",
    "route_candidates",
]
