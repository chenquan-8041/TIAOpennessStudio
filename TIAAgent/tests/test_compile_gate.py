"""Unit tests for the gentle T5 post-write compile gate (pure functions, no TIA/LLM).

The gate must stay soft: a skipped/unavailable block-compile only annotates the task; only real
compile errors on the block that was just written fail that task. Whole-PLC compiles are opt-in
at the end of a plan and never re-judge tasks.

Run (from the TIAAgent directory):
    python -m unittest tests/test_compile_gate.py
    # or: pytest tests/test_compile_gate.py
"""

import os
import sys
import unittest

_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ROOT not in sys.path:
    sys.path.insert(0, _ROOT)
_pydeps = os.path.join(_ROOT, "_pydeps")
if os.path.isdir(_pydeps) and _pydeps not in sys.path:
    sys.path.insert(0, _pydeps)

from agent.executor import (  # noqa: E402
    _apply_compile_gate,
    _compile_written_block,
    _run_compile_tool,
)
from agent.plan_models import Task  # noqa: E402


def _task(kind="FB", name="FbMotor"):
    return Task(id="t1", kind=kind, block_name=name)


class CompileGateTests(unittest.TestCase):
    def test_no_payload_keeps_success(self):
        task = _task()
        self.assertIsNone(_apply_compile_gate(task, None))
        self.assertEqual(task.warnings, [])

    def test_clean_compile_keeps_success(self):
        task = _task()
        payload = {"success": True, "error_count": 0, "warning_count": 1,
                   "errors": [], "warnings": [{"block": "FbMotor", "message": "minor"}]}
        self.assertIsNone(_apply_compile_gate(task, payload))
        self.assertEqual(task.warnings, [])

    def test_skipped_check_only_warns(self):
        task = _task()
        reason = _apply_compile_gate(task, {"skipped": "project_has_unsaved_changes"})
        self.assertIsNone(reason)                       # success is preserved
        self.assertTrue(task.warnings)
        self.assertIn("skipped", task.warnings[0].lower())

    def test_unavailable_check_only_warns(self):
        task = _task()
        reason = _apply_compile_gate(task, {"unavailable": "TimeoutError: slow"})
        self.assertIsNone(reason)
        self.assertIn("unavailable", task.warnings[0].lower())

    def test_block_errors_fail_the_task(self):
        task = _task()
        payload = {
            "success": False,
            "error_count": 2,
            "errors": [
                {"block": "FbMotor", "message": "Data type DTL is unknown."},
                {"block": "FbMotor", "message": "Operand not found."},
            ],
        }
        reason = _apply_compile_gate(task, payload)
        self.assertIsNotNone(reason)
        self.assertIn("Block compile failed", reason)
        self.assertIn("DTL", reason)
        self.assertEqual(task.warnings, [])             # failure is not a warning

    def test_error_count_without_details_still_fails(self):
        reason = _apply_compile_gate(_task(), {"success": False, "error_count": 1, "errors": []})
        self.assertEqual(reason, "Block compile failed: ")

    def test_non_block_kinds_are_not_compiled(self):
        # UDT is a PlcType and TagTable is not a block: the gate stays out of the way without any call.
        self.assertIsNone(_compile_written_block(_task(kind="UDT", name="TypeA")))
        self.assertIsNone(_compile_written_block(_task(kind="TagTable", name="Tags")))
        self.assertIsNone(_compile_written_block(_task(kind="FB", name="")))

    def test_unknown_compile_tool_is_none(self):
        self.assertIsNone(_run_compile_tool("no_such_tool"))


if __name__ == "__main__":
    unittest.main()
