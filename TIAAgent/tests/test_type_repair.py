"""Deterministic compile loop + type-level repair (diff baseline, matrix-driven)."""

import json
import os
import sys
import unittest

_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ROOT not in sys.path:
    sys.path.insert(0, _ROOT)
_pydeps = os.path.join(_ROOT, "_pydeps")
if os.path.isdir(_pydeps) and _pydeps not in sys.path:
    sys.path.insert(0, _pydeps)

from agent.type_repair import auto_compile_and_repair, repair_types  # noqa: E402
from tools.siemens_platform import replacement_for, replacements_for  # noqa: E402


class FakeCall:
    """Scripted MCP surface: records calls, answers from a per-tool map (lists rotate)."""

    def __init__(self, answers):
        self.answers = answers
        self.calls = []

    def __call__(self, name, arguments):
        self.calls.append((name, arguments))
        answer = self.answers.get(name)
        if callable(answer):
            answer = answer(arguments)
        if isinstance(answer, list):
            return answer.pop(0) if answer else "{}"
        return answer if answer is not None else "{}"

    def names(self):
        return [n for n, _ in self.calls]


_DIFF_CLEAN = json.dumps({"baseline_missing": False, "new_count": 0, "new_errors": [],
                          "pre_existing_errors": [], "resolved_errors": []})
_DIFF_BASELINE_MISSING = json.dumps({"baseline_missing": True})
_STRUCTURED_BLOCKLESS = json.dumps(
    {"success": False, "errors": [{"block": "Unknown", "message": "Data type DTL is unknown."}]})
_REPAIR_OK = json.dumps({
    "candidates": 1,
    "repaired": [{"block_id": "PlcType:AlarmRecord", "replaced": 1}],
    "skipped": [],
    "compile": {"success": True, "error_count": 0},
})


class ReplacementsTest(unittest.TestCase):
    def test_auto_retype_only_datatypes(self):
        mapping = replacements_for("S7-300/400")
        self.assertEqual(mapping["DTL"], "DATE_AND_TIME")
        self.assertEqual(mapping["LDT"], "DATE_AND_TIME")
        self.assertEqual(mapping["WSTRING"], "STRING")
        # instruction/attribute rows must never be auto-rewritten
        self.assertNotIn("R_TRIG / F_TRIG", mapping)
        self.assertNotIn("optimized block access", mapping)

    def test_supporting_family_has_no_replacements(self):
        self.assertEqual(replacements_for("S7-1500"), {})

    def test_instruction_rows_are_reported_but_not_applied(self):
        self.assertEqual(replacement_for("R_TRIG / F_TRIG", "S7-300/400"), "BOOL")


class AutoLoopTest(unittest.TestCase):
    def test_clean_diff_is_a_pass_without_repair(self):
        call = FakeCall({"compile_diff": _DIFF_CLEAN})
        result = auto_compile_and_repair(call, "S7-300/400")
        self.assertTrue(result["success"])
        self.assertEqual(call.names(), ["compile_diff"])

    def test_named_block_new_error_is_left_to_the_coder(self):
        call = FakeCall({
            "compile_diff": json.dumps({"baseline_missing": False, "new_count": 1,
                                        "new_errors": [{"block": "FB_Motor", "message": "syntax error"}]}),
        })
        result = auto_compile_and_repair(call, "S7-300/400")
        self.assertFalse(result["success"])
        self.assertEqual(result["repairs"], [])
        # A named error has a subject: it must never be handed to the user for attribution.
        self.assertEqual(result["unattributed"], [])
        self.assertNotIn("repair_inconsistent_types", call.names())

    def test_blockless_error_that_survives_repair_is_reported_as_unattributed(self):
        call = FakeCall({
            "compile_diff": [_DIFF_BASELINE_MISSING, _DIFF_BASELINE_MISSING],
            "compile": [_STRUCTURED_BLOCKLESS, _STRUCTURED_BLOCKLESS],
            "repair_inconsistent_types": _REPAIR_OK,
        })
        result = auto_compile_and_repair(call, "S7-300/400")
        self.assertFalse(result["success"])
        self.assertEqual(len(result["unattributed"]), 1)
        self.assertEqual(result["unattributed"][0]["message"], "Data type DTL is unknown.")

    def test_blockless_new_error_reaches_the_snapshot_repair_tool(self):
        call = FakeCall({
            "compile_diff": [_DIFF_BASELINE_MISSING, _DIFF_CLEAN],
            "compile": _STRUCTURED_BLOCKLESS,
            "repair_inconsistent_types": _REPAIR_OK,
        })
        result = auto_compile_and_repair(call, "S7-300/400")
        self.assertTrue(result["success"])
        self.assertEqual(result["repairs"][0]["block_id"], "PlcType:AlarmRecord")
        repair_args = [a for n, a in call.calls if n == "repair_inconsistent_types"][0]
        self.assertEqual(repair_args["replacements"]["DTL"], "DATE_AND_TIME")

    def test_unsupported_family_does_not_attempt_writes(self):
        call = FakeCall({
            "compile_diff": _DIFF_BASELINE_MISSING,
            "compile": _STRUCTURED_BLOCKLESS,
            "get_environment_profile": json.dumps({"PlcFamily": "S7-1500"}),
        })
        result = auto_compile_and_repair(call, None)
        self.assertFalse(result["success"])
        self.assertEqual(result["repairs"], [])
        self.assertIn("no matrix replacement", result["repair_note"])
        self.assertNotIn("repair_inconsistent_types", call.names())

    def test_repair_tool_reports_skipped_candidates(self):
        call = FakeCall({"repair_inconsistent_types": json.dumps(
            {"repaired": [], "skipped": [{"block_id": "PlcType:X", "reason": "no_index_snapshot"}]})})
        report = repair_types(call, "S7-300/400")
        self.assertEqual(report["repaired"], [])
        self.assertEqual(report["skipped"][0]["reason"], "no_index_snapshot")


if __name__ == "__main__":
    unittest.main()
