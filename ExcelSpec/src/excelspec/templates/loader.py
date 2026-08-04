"""Load and validate declarative ExcelSpec templates."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import yaml
from jsonschema import Draft202012Validator

from ..models.template import TemplateSpec
from ..schemas import load_schema


class TemplateValidationError(ValueError):
    """Raised when a template does not conform to the bundled schema."""

    def __init__(self, path: Path, errors: list[str]) -> None:
        self.path = path
        self.errors = errors
        super().__init__(f"invalid template {path}: " + "; ".join(errors))


def _load_mapping(path: Path) -> dict[str, Any]:
    try:
        text = path.read_text(encoding="utf-8-sig")
        if path.suffix.lower() == ".json":
            value = json.loads(text)
        elif path.suffix.lower() in {".yaml", ".yml"}:
            value = yaml.safe_load(text)
        else:
            raise ValueError("template extension must be .json, .yaml, or .yml")
    except (OSError, json.JSONDecodeError, yaml.YAMLError) as error:
        raise ValueError(f"cannot load template {path}: {error}") from error
    if not isinstance(value, dict):
        raise ValueError(f"template root must be an object: {path}")
    return value


def validate_template_data(data: dict[str, Any], *, path: Path | None = None) -> None:
    validator = Draft202012Validator(load_schema("template"))
    messages: list[str] = []
    for error in sorted(
        validator.iter_errors(data),
        key=lambda item: tuple(str(part) for part in item.absolute_path),
    ):
        location = ".".join(str(part) for part in error.absolute_path) or "$"
        messages.append(f"{location}: {error.message}")
    if messages:
        raise TemplateValidationError(path or Path("<memory>"), messages)


def load_template(path: str | Path) -> TemplateSpec:
    """Load YAML/JSON or a template pack directory, validate, then create the model."""

    from ..template_pack import resolve_template_file

    template_path = resolve_template_file(path)
    data = _load_mapping(template_path)
    validate_template_data(data, path=template_path)
    return TemplateSpec.from_dict(data)


def load_templates(directory: str | Path) -> list[TemplateSpec]:
    """Load templates and template packs in deterministic order."""

    from ..template_pack import is_template_pack

    root = Path(directory)
    if is_template_pack(root):
        return [load_template(root)]

    items: list[Path] = []
    for path in sorted(root.iterdir(), key=lambda item: item.name.casefold()):
        if path.is_file() and path.suffix.lower() in {".json", ".yaml", ".yml"}:
            items.append(path)
        elif path.is_dir() and is_template_pack(path):
            items.append(path)
    return [load_template(path) for path in items]


__all__ = [
    "TemplateValidationError",
    "load_template",
    "load_templates",
    "validate_template_data",
]
