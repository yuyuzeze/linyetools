"""User-supplied screenshot manifest contract and JSON loader."""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any


@dataclass(slots=True)
class ManifestAsset:
    asset_id: str
    path: Path
    sheet: str
    region_id: str | None = None
    asset_type: str = "screenshot"
    description: str | None = None
    anchor: str | None = None
    ocr: dict[str, Any] = field(default_factory=lambda: {"status": "pending"})
    vlm: dict[str, Any] = field(default_factory=lambda: {"status": "pending"})
    metadata: dict[str, Any] = field(default_factory=dict)


@dataclass(slots=True)
class ScreenshotManifest:
    version: str
    assets: list[ManifestAsset]
    source_path: Path


def load_screenshot_manifest(path: str | Path) -> ScreenshotManifest:
    """Load a version-1 JSON manifest, resolving asset paths beside the manifest."""

    manifest_path = Path(path).resolve()
    try:
        data = json.loads(manifest_path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise ValueError(
            f"invalid screenshot manifest JSON at line {error.lineno}, column {error.colno}"
        ) from error
    if not isinstance(data, dict):
        raise ValueError("screenshot manifest root must be an object")

    version = str(data.get("version", "1"))
    if version != "1":
        raise ValueError(f"unsupported screenshot manifest version: {version}")
    raw_assets = data.get("assets")
    if not isinstance(raw_assets, list):
        raise ValueError("screenshot manifest 'assets' must be an array")

    assets: list[ManifestAsset] = []
    seen: set[str] = set()
    for index, item in enumerate(raw_assets):
        label = f"screenshot manifest assets[{index}]"
        if not isinstance(item, dict):
            raise ValueError(f"{label} must be an object")
        asset_id = item.get("asset_id")
        asset_path = item.get("path")
        sheet = item.get("sheet")
        if not isinstance(asset_id, str) or not asset_id:
            raise ValueError(f"{label}.asset_id must be a non-empty string")
        if asset_id in seen:
            raise ValueError(f"duplicate screenshot asset_id: {asset_id}")
        if not isinstance(asset_path, str) or not asset_path:
            raise ValueError(f"{label}.path must be a non-empty string")
        if not isinstance(sheet, str) or not sheet:
            raise ValueError(f"{label}.sheet must be a non-empty string")
        seen.add(asset_id)

        resolved = Path(asset_path)
        if not resolved.is_absolute():
            resolved = manifest_path.parent / resolved
        ocr = item.get("ocr", {"status": "pending"})
        vlm = item.get("vlm", {"status": "pending"})
        metadata = item.get("metadata", {})
        if not isinstance(ocr, dict) or not isinstance(vlm, dict) or not isinstance(metadata, dict):
            raise ValueError(f"{label} ocr, vlm, and metadata values must be objects")
        assets.append(
            ManifestAsset(
                asset_id=asset_id,
                path=resolved.resolve(),
                sheet=sheet,
                region_id=item.get("region_id"),
                asset_type=str(item.get("asset_type", "screenshot")),
                description=item.get("description"),
                anchor=item.get("anchor"),
                ocr=ocr,
                vlm=vlm,
                metadata=metadata,
            )
        )
    return ScreenshotManifest(version=version, assets=assets, source_path=manifest_path)


__all__ = ["ManifestAsset", "ScreenshotManifest", "load_screenshot_manifest"]
