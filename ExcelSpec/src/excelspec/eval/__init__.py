"""Repeatable evaluation harness for region detection and semantic extraction.

This measures *structural* accuracy against hand-annotated synthetic cases that
mimic common Japanese specification layouts. It does NOT claim real-world
business accuracy — there are no real production workbooks in this repo, so the
numbers here reflect the synthetic cases only.
"""

from .models import EvalCase, ExpectedRegion
from .metrics import evaluate_case
from .runner import run_all_cases

__all__ = ["EvalCase", "ExpectedRegion", "evaluate_case", "run_all_cases"]
