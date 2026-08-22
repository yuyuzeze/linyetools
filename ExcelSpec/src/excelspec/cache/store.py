"""Atomic, corruption-tolerant JSON file cache."""

from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any


class FileCache:
    """A namespaced JSON cache under ``directory``.

    * Never written next to the source workbook — the caller chooses a directory
      (conventionally ``<output>/.excelspec-cache``).
    * Atomic writes: a temp file is fully written then ``os.replace``-d in.
    * Corruption-tolerant: an unreadable entry is deleted, a warning recorded,
      and treated as a miss so the value is regenerated.
    * The cache never changes output — a hit deserialises to the same value a
      miss would have produced.
    """

    def __init__(self, directory: str | Path, *, enabled: bool = True) -> None:
        self.directory = Path(directory)
        self.enabled = enabled
        self.hits = 0
        self.misses = 0
        self.warnings: list[str] = []

    def _path(self, namespace: str, key: str) -> Path:
        return self.directory / namespace / f"{key}.json"

    def get(self, namespace: str, key: str) -> dict[str, Any] | None:
        if not self.enabled:
            return None
        path = self._path(namespace, key)
        if not path.is_file():
            self.misses += 1
            return None
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError, ValueError) as error:
            self.warnings.append(f"缓存损坏，已忽略并重建: {path} ({error})")
            try:
                path.unlink()
            except OSError:
                pass
            self.misses += 1
            return None
        self.hits += 1
        return data

    def put(self, namespace: str, key: str, value: dict[str, Any]) -> None:
        if not self.enabled:
            return
        path = self._path(namespace, key)
        path.parent.mkdir(parents=True, exist_ok=True)
        temp = path.with_name(f"{path.name}.tmp-{os.urandom(6).hex()}")
        try:
            # Preserve insertion order (do NOT sort keys) so a deserialised
            # value reproduces the same dict iteration order as the original —
            # required for a cache hit to be byte-identical to a cold run.
            temp.write_text(
                json.dumps(value, ensure_ascii=False),
                encoding="utf-8",
            )
            os.replace(temp, path)
        except OSError as error:
            self.warnings.append(f"缓存写入失败: {path} ({error})")
            try:
                temp.unlink()
            except OSError:
                pass

    @property
    def stats(self) -> dict[str, int]:
        return {"hits": self.hits, "misses": self.misses}


__all__ = ["FileCache"]
