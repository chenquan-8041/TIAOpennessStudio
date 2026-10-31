"""Unit tests for the T3 environment profile layer (pure functions, no TIA / LLM required).

Covers:
  - normalize_profile  : PascalCase C# payload → snake_case facts
  - detect_change      : CPU family / order number / TIA version block, project only warns
  - annotate_compile_error : family-specific hints (S7-300/400 only)
  - describe_for_prompt    : facts only, never the family rule text
  - ProjectPlan        : environment facts survive serialization

Run (from the TIAAgent directory):
    python -m unittest tests/test_environment_profile.py
    # or: pytest tests/test_environment_profile.py
"""

import os
import sys
import unittest
from unittest.mock import MagicMock, patch

# Make tests runnable standalone: add the TIAAgent root and _pydeps (vendored langchain_core etc.) to sys.path.
_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ROOT not in sys.path:
    sys.path.insert(0, _ROOT)
_pydeps = os.path.join(_ROOT, "_pydeps")
if os.path.isdir(_pydeps) and _pydeps not in sys.path:
    sys.path.insert(0, _pydeps)

from agent.environment import (  # noqa: E402
    annotate_compile_error,
    describe_for_prompt,
    detect_change,
    normalize_profile,
)
from agent.plan_models import ProjectPlan  # noqa: E402
from tools import tia_client  # noqa: E402


def _payload(**overrides):
    """C# EnvironmentProfileInfo payload shape (PascalCase)."""
    base = {
        "TiaVersion": "V18",
        "ProjectKey": r"C:\Projects\Line1\Line1.ap18_1",
        "ProjectName": "Line1",
        "PlcName": "PLC_1",
        "DeviceName": "PLC_1",
        "CpuName": "CPU 314C-2 PN/DP",
        "OrderNumber": "6ES7 314-6EH04-0AB0",
        "TypeIdentifier": "OrderNumber:6ES7 314-6EH04-0AB0/V2.6",
        "PlcFamily": "S7-300/400",
        "Fingerprint": "a1b2c3d4e5f6",
        "DetectedAt": "2026-10-27T14:00:00+08:00",
        "Live": True,
    }
    base.update(overrides)
    return base


class NormalizeProfileTests(unittest.TestCase):
    def test_maps_pascal_case_to_snake_case(self):
        profile = normalize_profile(_payload())
        self.assertEqual(profile["tia_version"], "V18")
        self.assertEqual(profile["plc_family"], "S7-300/400")
        self.assertEqual(profile["order_number"], "6ES7 314-6EH04-0AB0")
        self.assertEqual(profile["fingerprint"], "a1b2c3d4e5f6")
        self.assertIs(profile["live"], True)

    def test_rejects_non_dict(self):
        self.assertEqual(normalize_profile(None), {})
        self.assertEqual(normalize_profile("Error: unavailable"), {})


class DetectChangeTests(unittest.TestCase):
    def test_identical_environment_has_no_changes(self):
        stored = normalize_profile(_payload())
        live = normalize_profile(_payload())
        change = detect_change(stored, live)
        self.assertEqual(change["blocking"], [])
        self.assertEqual(change["warnings"], [])

    def test_cpu_switch_blocks(self):
        stored = normalize_profile(_payload())
        live = normalize_profile(_payload(
            PlcFamily="S7-1500",
            OrderNumber="6ES7 515-2AM01-0AB0",
            Fingerprint="ffffffffffff",
        ))
        change = detect_change(stored, live)
        joined = " | ".join(change["blocking"])
        self.assertIn("PLC family", joined)
        self.assertIn("CPU order number", joined)
        self.assertEqual(change["warnings"], [])

    def test_tia_version_upgrade_blocks(self):
        stored = normalize_profile(_payload())
        live = normalize_profile(_payload(TiaVersion="V19", Fingerprint="999999999999"))
        change = detect_change(stored, live)
        self.assertTrue(any("TIA version" in item for item in change["blocking"]))

    def test_project_change_only_warns(self):
        stored = normalize_profile(_payload())
        live = normalize_profile(_payload(
            ProjectKey=r"C:\Projects\Line2\Line2.ap18_1",
            ProjectName="Line2",
            Fingerprint="bbbbbbbbbbbb",
        ))
        change = detect_change(stored, live)
        self.assertEqual(change["blocking"], [])
        self.assertTrue(any("Project changed" in item for item in change["warnings"]))

    def test_missing_live_profile_only_warns(self):
        change = detect_change(normalize_profile(_payload()), None)
        self.assertEqual(change["blocking"], [])
        self.assertEqual(len(change["warnings"]), 1)


class AnnotateCompileErrorTests(unittest.TestCase):
    def test_s7_300_400_dtl_gets_hint(self):
        text = annotate_compile_error("Data type DTL is unknown.", "S7-300/400")
        self.assertIn("S7-300/400", text)
        self.assertIn("DTL", text)

    def test_other_family_is_unchanged(self):
        message = "Data type DTL is unknown."
        self.assertEqual(annotate_compile_error(message, "S7-1500"), message)

    def test_unrelated_error_is_unchanged(self):
        message = "Block OB1: operand not found."
        self.assertEqual(annotate_compile_error(message, "S7-300/400"), message)

    def test_missing_family_is_unchanged(self):
        message = "Data type DTL is unknown."
        self.assertEqual(annotate_compile_error(message, None), message)


class DescribeForPromptTests(unittest.TestCase):
    def test_contains_facts(self):
        line = describe_for_prompt(normalize_profile(_payload()))
        self.assertIn("S7-300/400", line)
        self.assertIn("6ES7 314-6EH04-0AB0", line)
        self.assertIn("a1b2c3d4e5f6", line)

    def test_does_not_repeat_family_rules(self):
        """The rule text lives only in the C# context block; the task prompt stays facts-only."""
        line = describe_for_prompt(normalize_profile(_payload()))
        self.assertNotIn("S7_Optimized_Access", line)
        self.assertNotIn("R_TRIG", line)

    def test_empty_profile(self):
        self.assertEqual(describe_for_prompt({}), "")


class PlanEnvironmentSerializationTests(unittest.TestCase):
    def test_round_trip_preserves_environment(self):
        env = normalize_profile(_payload())
        plan = ProjectPlan(id="p1", project_key=env["project_key"], environment=env)
        restored = ProjectPlan.from_dict(plan.to_dict())
        self.assertEqual(restored.environment["fingerprint"], "a1b2c3d4e5f6")
        self.assertEqual(restored.environment["plc_family"], "S7-300/400")

    def test_missing_environment_defaults_to_empty(self):
        data = ProjectPlan(id="p2").to_dict()
        data.pop("environment", None)
        self.assertEqual(ProjectPlan.from_dict(data).environment, {})


class TiaClientPayloadTests(unittest.TestCase):
    """``tools/tia_client._post`` must accept both response shapes the host uses."""

    def _post_with(self, payload: dict, text: str) -> str:
        response = MagicMock()
        response.json.return_value = payload
        response.text = text
        with patch("tools.tia_client.httpx.Client") as client_cls:
            client_cls.return_value.__enter__.return_value.post.return_value = response
            return tia_client.get_environment_profile()

    def test_raw_payload_is_returned_unchanged(self):
        """get_environment_profile answers the profile itself, with no {ok, result} wrapper."""
        text = '{"PlcFamily": "S7-300/400"}'
        self.assertEqual(self._post_with({"PlcFamily": "S7-300/400"}, text), text)

    def test_envelope_result_is_unwrapped(self):
        self.assertEqual(self._post_with({"ok": True, "result": "recorded"}, ""), "recorded")

    def test_envelope_error_is_reported(self):
        self.assertEqual(self._post_with({"ok": False, "error": "nope"}, ""), "Error: nope")


if __name__ == "__main__":
    unittest.main()
