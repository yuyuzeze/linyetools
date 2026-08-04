"""Access to the JSON Schemas shipped with ExcelSpec."""

from __future__ import annotations

import json
import sysconfig
from pathlib import Path
from typing import Any

SCHEMA_FILENAMES = {
    "document-ir": "document-ir.schema.json",
    "template": "template.schema.json",
}


def schema_path(name: str) -> Path:
    try:
        filename = SCHEMA_FILENAMES[name]
    except KeyError as error:
        choices = ", ".join(sorted(SCHEMA_FILENAMES))
        raise ValueError(f"unknown schema {name!r}; expected one of: {choices}") from error

    candidates = (
        Path(__file__).resolve().parents[2] / "schemas" / filename,
        Path(sysconfig.get_path("data")) / "share" / "excelspec" / "schemas" / filename,
    )
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    raise FileNotFoundError(f"bundled schema is missing: {filename}")


def load_schema(name: str) -> dict[str, Any]:
    with schema_path(name).open(encoding="utf-8") as schema_file:
        return json.load(schema_file)
