"""Load and validate semantic profiles, rejecting legacy coordinate fields."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import yaml
from openpyxl.utils import range_boundaries

from .model import (
    FieldConcept,
    ProfileOverride,
    SemanticProfile,
    ValidationConcept,
)

# Fields that would turn a profile back into a coordinate template. Allowed only
# inside ``overrides`` (which may carry a small number of manual corrections).
_FORBIDDEN_FIELDS = {
    "locator",
    "range",
    "width",
    "height",
    "row_offset",
    "column_offset",
    "anchor_text",
    "anchor_pattern",
    "repeat_anchor",
    "end_anchor_text",
}


class ProfileValidationError(ValueError):
    def __init__(self, errors: list[str], *, path: Path | None = None) -> None:
        self.errors = errors
        self.path = path
        super().__init__("; ".join(errors))


def _scan_forbidden(node: Any, trail: str, errors: list[str]) -> None:
    if isinstance(node, dict):
        for key, value in node.items():
            if key in _FORBIDDEN_FIELDS:
                errors.append(
                    f"{trail}.{key}: 语义 Profile 不允许坐标字段 '{key}'（应交给 RegionDetector）"
                )
            _scan_forbidden(value, f"{trail}.{key}", errors)
    elif isinstance(node, list):
        for index, item in enumerate(node):
            _scan_forbidden(item, f"{trail}[{index}]", errors)


def load_profile(path: str | Path) -> SemanticProfile:
    profile_path = Path(path)
    text = profile_path.read_text(encoding="utf-8-sig")
    if profile_path.suffix.lower() == ".json":
        data = json.loads(text)
    else:
        data = yaml.safe_load(text)
    if not isinstance(data, dict):
        raise ProfileValidationError(["Profile 根节点必须是对象"], path=profile_path)
    return parse_profile(data, path=profile_path)


def parse_profile(data: dict[str, Any], *, path: Path | None = None) -> SemanticProfile:
    errors: list[str] = []

    # Reject coordinate/locator fields everywhere except under `overrides`.
    for key, value in data.items():
        if key == "overrides":
            continue
        _scan_forbidden(value, key, errors)

    if not data.get("profile_id"):
        errors.append("缺少 profile_id")
    if not data.get("document_type"):
        errors.append("缺少 document_type")
    if errors:
        raise ProfileValidationError(errors, path=path)

    match = data.get("match", {}) or {}
    fields: dict[str, FieldConcept] = {}
    for concept, spec in (data.get("fields", {}) or {}).items():
        spec = spec or {}
        fields[concept] = FieldConcept(
            concept=concept,
            aliases=list(spec.get("aliases", []) or []),
            regex_aliases=list(spec.get("regex_aliases", []) or []),
        )

    validation = [
        ValidationConcept(
            concept=item.get("concept", ""),
            required=bool(item.get("required", False)),
        )
        for item in (data.get("validation", []) or [])
    ]

    raw_overrides = list(data.get("overrides", []) or [])
    for index, item in enumerate(raw_overrides):
        visual_range = item.get("visual_range")
        if visual_range is None:
            continue
        try:
            range_boundaries(str(visual_range))
        except ValueError:
            errors.append(
                f"overrides[{index}].visual_range: 无效的 Excel 范围 '{visual_range}'"
            )
    if errors:
        raise ProfileValidationError(errors, path=path)

    overrides = [
        ProfileOverride(
            sheet_alias=item.get("sheet_alias"),
            sheet=item.get("sheet"),
            ignore=list(item.get("ignore", []) or []),
            force_region_type=item.get("force_region_type"),
            exclude_sheet=bool(item.get("exclude_sheet", False)),
            title=item.get("title"),
            visual_range=item.get("visual_range"),
        )
        for item in raw_overrides
    ]

    return SemanticProfile(
        schema_version=str(data.get("schema_version", "1")),
        profile_id=data["profile_id"],
        document_type=data["document_type"],
        filename_patterns=list(match.get("filename_patterns", []) or []),
        sheet_aliases={
            role: list(aliases or [])
            for role, aliases in (match.get("sheet_aliases", {}) or {}).items()
        },
        fields=fields,
        required_concepts={
            role: list(concepts or [])
            for role, concepts in (data.get("required_concepts", {}) or {}).items()
        },
        validation=validation,
        overrides=overrides,
    )


def match_profile(
    profiles: list[SemanticProfile], *, filename: str, sheet_names: list[str]
) -> SemanticProfile | None:
    """Pick the best profile by filename pattern, then sheet-role coverage."""

    best: SemanticProfile | None = None
    best_score = 0.0
    for profile in profiles:
        score = 0.0
        if profile.filename_patterns and profile.filename_matches(filename):
            score += 2.0
        roles = sum(
            1 for name in sheet_names if profile.sheet_role(name) is not None
        )
        score += roles
        if score > best_score:
            best_score = score
            best = profile
    return best if best_score > 0 else None


__all__ = [
    "ProfileValidationError",
    "load_profile",
    "match_profile",
    "parse_profile",
]
