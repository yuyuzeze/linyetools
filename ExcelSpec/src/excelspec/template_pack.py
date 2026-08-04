"""Template package layout: template.xlsx + template.yaml + schema + examples."""

from __future__ import annotations

import json
import re
import shutil
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import yaml

from .inspection import sheet_summary, workbook_summary
from .ingest import ingest_xlsx
from .models.template import TemplateSpec
from .schemas import load_schema
from .serialization import to_json
from .templates.loader import load_template


PACK_YAML_NAMES = ("template.yaml", "template.yml")
PACK_JSON_NAMES = ("template.json",)


@dataclass(slots=True)
class TemplatePack:
    root: Path
    template_yaml: Path
    template_xlsx: Path | None
    schema_json: Path | None
    examples_dir: Path | None
    prompts_dir: Path | None

    def load(self) -> TemplateSpec:
        return load_template(self.template_yaml)


def is_template_pack(path: Path) -> bool:
    if not path.is_dir():
        return False
    return any((path / name).is_file() for name in (*PACK_YAML_NAMES, *PACK_JSON_NAMES))


def resolve_template_file(path: str | Path) -> Path:
    """Resolve a YAML/JSON file or a template pack directory to the executable file."""

    candidate = Path(path)
    if candidate.is_file():
        return candidate
    if not candidate.is_dir():
        raise FileNotFoundError(f"模板不存在: {candidate}")
    for name in (*PACK_YAML_NAMES, *PACK_JSON_NAMES):
        file_path = candidate / name
        if file_path.is_file():
            return file_path
    raise FileNotFoundError(f"模板包缺少 template.yaml/json: {candidate}")


def open_template_pack(path: str | Path) -> TemplatePack:
    root = Path(path)
    yaml_path = resolve_template_file(root if root.is_dir() else root.parent)
    pack_root = yaml_path.parent
    xlsx = next(
        (
            item
            for item in (
                pack_root / "template.xlsx",
                pack_root / "template.xlsm",
            )
            if item.is_file()
        ),
        None,
    )
    schema = pack_root / "schema.json"
    examples = pack_root / "examples"
    prompts = pack_root / "prompts"
    return TemplatePack(
        root=pack_root,
        template_yaml=yaml_path,
        template_xlsx=xlsx,
        schema_json=schema if schema.is_file() else None,
        examples_dir=examples if examples.is_dir() else None,
        prompts_dir=prompts if prompts.is_dir() else None,
    )


def _slug(value: str) -> str:
    text = re.sub(r"[^0-9A-Za-z_-]+", "-", value.strip()).strip("-").lower()
    return text or "template"


def _guess_document_type(type_name: str | None, sheet_names: list[str]) -> str:
    if type_name:
        return type_name
    joined = " ".join(sheet_names)
    if "帳票" in joined:
        return "report-design"
    if "画面" in joined or "表紙" in joined:
        return "screen-design"
    return "generic"


def _seed_template_yaml(
    *,
    template_id: str,
    document_type: str,
    sheet_names: list[str],
    header_candidates: list[str],
) -> dict[str, Any]:
    patterns = [f"^{re.escape(name)}$" for name in sheet_names[:6]]
    first_sheet = sheet_names[0] if sheet_names else "Sheet1"
    headers = header_candidates[:8] or ["No.", "項目名"]
    column_semantics = {
        header: re.sub(r"\W+", "_", header).strip("_").lower() or f"col_{index}"
        for index, header in enumerate(headers, start=1)
    }
    return {
        "schema_version": "1.0",
        "template_id": template_id,
        "version": "1.0",
        "name": template_id,
        "description": f"Auto-generated skeleton for {document_type}",
        "match": {
            "sheet_name_patterns": patterns,
            "fingerprints": [
                {
                    "sheet_name_pattern": patterns[0] if patterns else ".*",
                    "required_text": headers[:3],
                    "weight": 1.0,
                }
            ],
            "minimum_score": 0.55,
        },
        "sheets": [
            {
                "sheet_id": "primary",
                "name_pattern": f"^{re.escape(first_sheet)}$",
                "order": 10,
                "regions": [
                    {
                        "region_id": "main-table",
                        "region_type": "table",
                        "title": "主表",
                        "required": True,
                        "locator": {
                            "mode": "anchor",
                            "anchor_text": headers[0],
                            "width": max(len(headers), 4),
                            "repeat_anchor": False,
                        },
                        "extractor": {
                            "kind": "table",
                            "header_rows": 1,
                            "column_semantics": column_semantics,
                            "options": {
                                "stop_after_blank_rows": 2,
                                "shrink_to_content": True,
                                "trim_empty_columns": True,
                            },
                        },
                    }
                ],
            }
        ],
        "validation_rules": [],
        "metadata": {
            "document_type": document_type,
            "generated_by": "jpspec template init",
        },
    }


def _canonical_schema_stub(template_id: str) -> dict[str, Any]:
    return {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "$id": f"https://linye.local/jpspec/canonical/{template_id}",
        "title": f"{template_id} canonical output",
        "type": "object",
        "required": ["document_id", "title", "sheets", "schema_version"],
        "properties": {
            "schema_version": {"type": "string"},
            "document_id": {"type": "string"},
            "title": {"type": "string"},
            "template_id": {"type": ["string", "null"]},
            "template_version": {"type": ["string", "null"]},
            "sheets": {"type": "array"},
            "diagnostics": {"type": "array"},
        },
        "additionalProperties": True,
        "description": "Stable parse result contract for agents and downstream tools.",
    }


def init_template_pack(
    source_xlsx: str | Path,
    output_dir: str | Path,
    *,
    document_type: str | None = None,
    template_id: str | None = None,
    force: bool = False,
) -> Path:
    """Create a template package skeleton from a sample workbook."""

    source = Path(source_xlsx)
    document = ingest_xlsx(source, asset_dir=None)
    summary = workbook_summary(document)
    sheet_names = [sheet["name"] for sheet in summary["sheets"]]
    doc_type = _guess_document_type(document_type, sheet_names)
    pack_id = template_id or f"{_slug(doc_type)}-v1"
    root = Path(output_dir)
    if root.exists() and any(root.iterdir()) and not force:
        raise FileExistsError(f"目标目录非空，请换目录或加 --force: {root}")
    root.mkdir(parents=True, exist_ok=True)
    (root / "examples").mkdir(exist_ok=True)
    (root / "prompts").mkdir(exist_ok=True)

    shutil.copy2(source, root / "template.xlsx")
    shutil.copy2(source, root / "examples" / "example-input.xlsx")

    header_candidates: list[str] = []
    for sheet in document.sheets:
        header_candidates.extend(sheet_summary(sheet)["header_candidates"])
    header_candidates = list(dict.fromkeys(header_candidates))

    template_data = _seed_template_yaml(
        template_id=pack_id,
        document_type=doc_type,
        sheet_names=sheet_names,
        header_candidates=header_candidates,
    )
    (root / "template.yaml").write_text(
        yaml.safe_dump(template_data, allow_unicode=True, sort_keys=False),
        encoding="utf-8",
    )
    (root / "schema.json").write_text(
        to_json(_canonical_schema_stub(pack_id)) + "\n", encoding="utf-8"
    )
    # Keep a copy of the engine DocumentIR schema for reference.
    (root / "document-ir.schema.json").write_text(
        to_json(load_schema("document-ir")) + "\n", encoding="utf-8"
    )
    (root / "examples" / "expected-output.json").write_text(
        to_json(
            {
                "note": "Run `jpspec parse examples/example-input.xlsx --template . --output examples/out` then copy canonical.json here.",
                "template_id": pack_id,
            }
        )
        + "\n",
        encoding="utf-8",
    )
    (root / "prompts" / "mapping.md").write_text(
        "\n".join(
            [
                f"# {pack_id} 字段映射说明",
                "",
                f"- document_type: `{doc_type}`",
                f"- sheets: {', '.join(sheet_names)}",
                "",
                "## 建议人工调整",
                "",
                "1. 在 `template.yaml` 中确认 sheet 名正则与表头锚点",
                "2. 为每个业务列补全 `column_semantics`",
                "3. 用 `jpspec parse` 生成 `canonical.json` 后替换 `examples/expected-output.json`",
                "4. 需要框选区域时，以 `template.xlsx` 为视觉参考",
                "",
            ]
        ),
        encoding="utf-8",
    )
    (root / "README.md").write_text(
        "\n".join(
            [
                f"# {pack_id}",
                "",
                "模板包内容：",
                "",
                "- `template.xlsx`：外观与结构参考",
                "- `template.yaml`：可执行解析规则",
                "- `schema.json`：canonical 输出契约",
                "- `examples/`：样例输入与期望输出",
                "- `prompts/mapping.md`：字段说明",
                "",
            ]
        ),
        encoding="utf-8",
    )
    return root


def compare_workbooks(
    template_xlsx: str | Path,
    actual_xlsx: str | Path,
) -> dict[str, Any]:
    """Compare sheet names / headers / merge density between template and actual."""

    left = ingest_xlsx(template_xlsx, asset_dir=None)
    right = ingest_xlsx(actual_xlsx, asset_dir=None)
    left_sheets = {sheet.name: sheet_summary(sheet) for sheet in left.sheets}
    right_sheets = {sheet.name: sheet_summary(sheet) for sheet in right.sheets}
    only_left = sorted(set(left_sheets) - set(right_sheets))
    only_right = sorted(set(right_sheets) - set(left_sheets))
    shared = sorted(set(left_sheets) & set(right_sheets))
    sheet_diffs = []
    for name in shared:
        left_headers = set(left_sheets[name]["header_candidates"])
        right_headers = set(right_sheets[name]["header_candidates"])
        sheet_diffs.append(
            {
                "sheet": name,
                "headers_only_in_template": sorted(left_headers - right_headers),
                "headers_only_in_actual": sorted(right_headers - left_headers),
                "template_nonblank": left_sheets[name]["nonblank_count"],
                "actual_nonblank": right_sheets[name]["nonblank_count"],
                "template_merge_hints": left_sheets[name]["merge_hint_count"],
                "actual_merge_hints": right_sheets[name]["merge_hint_count"],
            }
        )
    return {
        "template": str(Path(template_xlsx).resolve()),
        "actual": str(Path(actual_xlsx).resolve()),
        "sheets_only_in_template": only_left,
        "sheets_only_in_actual": only_right,
        "shared_sheets": shared,
        "sheet_diffs": sheet_diffs,
        "similar": not only_left and not only_right and all(
            not item["headers_only_in_template"] and not item["headers_only_in_actual"]
            for item in sheet_diffs
        ),
    }


__all__ = [
    "TemplatePack",
    "compare_workbooks",
    "init_template_pack",
    "is_template_pack",
    "open_template_pack",
    "resolve_template_file",
]
