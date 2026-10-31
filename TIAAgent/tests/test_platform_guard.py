"""CPU-family write guard: unsupported datatypes never reach TIA (matrix is the source)."""

import os
import sys
import unittest

_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ROOT not in sys.path:
    sys.path.insert(0, _ROOT)
_pydeps = os.path.join(_ROOT, "_pydeps")
if os.path.isdir(_pydeps) and _pydeps not in sys.path:
    sys.path.insert(0, _pydeps)

from tools.platform_guard import validate_write_spec  # noqa: E402


class PlatformGuardTest(unittest.TestCase):
    def test_udt_dtl_is_refused_on_classic_cpu(self):
        message = validate_write_spec("build_udt", {"json": {
            "name": "AlarmRecord",
            "members": [{"name": "Code", "datatype": "Int"},
                        {"name": "TriggerTime", "datatype": "DTL"}]}}, "S7-300/400")
        self.assertIn("unsupported_datatype_on_cpu", message)
        self.assertIn("TriggerTime", message)
        self.assertIn("DATE_AND_TIME", message)

    def test_udt_dtl_is_allowed_on_1500(self):
        self.assertIsNone(validate_write_spec("build_udt", {"json": {
            "name": "AlarmRecord",
            "members": [{"name": "TriggerTime", "datatype": "DTL"}]}}, "S7-1500"))

    def test_global_db_wstring_is_refused_on_classic_cpu(self):
        message = validate_write_spec("build_global_db", {"json": {
            "dbName": "DB_Text",
            "staticMembers": [{"name": "Note", "datatype": "WSTRING"}]}}, "S7-400")
        self.assertIn("STRING", message)

    def test_supported_datatype_passes(self):
        self.assertIsNone(validate_write_spec("build_udt", {"json": {
            "name": "OK", "members": [{"name": "Time", "datatype": "DATE_AND_TIME"}]}},
            "S7-300/400"))

    def test_interface_patch_with_dtl_is_refused(self):
        message = validate_write_spec("patch_block_interface", {
            "block_name": "DB_Alarm",
            "interface": {"kind": "Interface", "children": [{"kind": "Sections", "children": [
                {"kind": "Section", "attrs": {"Name": "Static"}, "children": [
                    {"kind": "Member", "attrs": {"Name": "TriggerTime", "Datatype": "DTL"}}]}]}]},
        }, "S7-300/400")
        self.assertIn("unsupported_datatype_on_cpu", message)
        self.assertIn("TriggerTime", message)

    def test_other_tools_are_not_guarded(self):
        self.assertIsNone(validate_write_spec("import_block", {"code": "TYPE X DTL"}, "S7-300/400"))


if __name__ == "__main__":
    unittest.main()
