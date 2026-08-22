"""Dependency-free JSON serialization for the public data models."""

from __future__ import annotations

import json
import types
from dataclasses import fields, is_dataclass
from enum import Enum
from functools import lru_cache
from pathlib import Path
from typing import Any, TypeVar, Union, get_args, get_origin, get_type_hints

T = TypeVar("T", bound="JsonModel")


@lru_cache(maxsize=None)
def _cached_hints(annotation: type) -> dict[str, Any]:
    """Cache ``get_type_hints`` per dataclass — it dominates ``from_dict`` cost."""

    return get_type_hints(annotation)


@lru_cache(maxsize=None)
def _cached_field_names(annotation: type) -> frozenset[str]:
    return frozenset(field.name for field in fields(annotation))


def _to_data(value: Any) -> Any:
    if is_dataclass(value):
        return {
            field.name: _to_data(getattr(value, field.name))
            for field in fields(value)
            if getattr(value, field.name) is not None
        }
    if isinstance(value, Enum):
        return value.value
    if isinstance(value, (list, tuple)):
        return [_to_data(item) for item in value]
    if isinstance(value, dict):
        return {str(key): _to_data(item) for key, item in value.items()}
    return value


def _from_data(annotation: Any, value: Any) -> Any:
    if value is None:
        return None

    origin = get_origin(annotation)
    arguments = get_args(annotation)
    if origin in (Union, types.UnionType):
        candidates = [candidate for candidate in arguments if candidate is not type(None)]
        return _from_data(candidates[0], value) if candidates else value
    if origin is list:
        item_type = arguments[0] if arguments else Any
        return [_from_data(item_type, item) for item in value]
    if origin is dict:
        value_type = arguments[1] if len(arguments) == 2 else Any
        return {key: _from_data(value_type, item) for key, item in value.items()}
    if annotation is Any:
        return value
    if isinstance(annotation, type) and issubclass(annotation, Enum):
        return annotation(value)
    if isinstance(annotation, type) and is_dataclass(annotation):
        hints = _cached_hints(annotation)
        known_fields = _cached_field_names(annotation)
        unknown = set(value) - known_fields
        if unknown:
            names = ", ".join(sorted(unknown))
            raise ValueError(f"unknown fields for {annotation.__name__}: {names}")
        return annotation(
            **{
                name: _from_data(hints[name], item)
                for name, item in value.items()
            }
        )
    return value


def to_json(value: Any, *, indent: int | None = 2) -> str:
    """Serialize dataclasses, enums, and plain JSON-compatible values."""

    return json.dumps(_to_data(value), ensure_ascii=False, indent=indent)


class JsonModel:
    """Mixin shared by versioned public models."""

    def to_dict(self) -> dict[str, Any]:
        return _to_data(self)

    def to_json(self, *, indent: int | None = 2) -> str:
        return json.dumps(self.to_dict(), ensure_ascii=False, indent=indent)

    def dump_json(self, path: str | Path, *, indent: int | None = 2) -> None:
        Path(path).write_text(self.to_json(indent=indent) + "\n", encoding="utf-8")

    @classmethod
    def from_dict(cls: type[T], data: dict[str, Any]) -> T:
        return _from_data(cls, data)

    @classmethod
    def from_json(cls: type[T], value: str | bytes | bytearray) -> T:
        data = json.loads(value)
        if not isinstance(data, dict):
            raise ValueError(f"{cls.__name__} JSON root must be an object")
        return cls.from_dict(data)

    @classmethod
    def load_json(cls: type[T], path: str | Path) -> T:
        return cls.from_json(Path(path).read_text(encoding="utf-8"))
