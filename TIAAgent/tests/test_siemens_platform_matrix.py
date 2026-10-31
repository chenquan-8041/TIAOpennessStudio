"""Siemens CPU incompatibility matrix: one document, queryable by feature or error text."""

import os
import sys
import unittest

_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ROOT not in sys.path:
    sys.path.insert(0, _ROOT)
_pydeps = os.path.join(_ROOT, "_pydeps")
if os.path.isdir(_pydeps) and _pydeps not in sys.path:
    sys.path.insert(0, _pydeps)

from tools.siemens_platform import annotate, query_siemens_platform_matrix  # noqa: E402


class PlatformMatrixQueryTest(unittest.TestCase):
    def test_empty_query_returns_the_single_document(self):
        text = query_siemens_platform_matrix.invoke({})
        for family in ("S7-300", "S7-400", "S7-1200", "S7-1500"):
            self.assertIn(family, text)

    def test_dtl_query_reports_classic_replacement(self):
        text = query_siemens_platform_matrix.invoke(
            {"query": "DTL", "cpu_family": "S7-300/400"})
        self.assertIn("## DTL", text)
        self.assertIn("S7-300: ✗", text)
        self.assertIn("DATE_AND_TIME", text)

    def test_dtl_query_on_1500_has_no_replacement(self):
        text = query_siemens_platform_matrix.invoke(
            {"query": "DTL", "cpu_family": "S7-1500"})
        self.assertIn("S7-1500: ✓", text)
        self.assertNotIn("use instead", text)

    def test_error_text_maps_to_the_feature(self):
        text = query_siemens_platform_matrix.invoke(
            {"query": "Data type DTL is unknown.", "cpu_family": "S7-300/400"})
        self.assertIn("## matched error", text)
        self.assertIn("DATE_AND_TIME", text)

    def test_unknown_query_returns_the_full_matrix(self):
        text = query_siemens_platform_matrix.invoke({"query": "zzzz_not_a_term"})
        self.assertIn("No matrix row matched", text)
        self.assertIn("## 兼容性矩阵", text)


class AnnotateFromMatrixTest(unittest.TestCase):
    def test_classic_family_gets_hint(self):
        text = annotate("Data type DTL is unknown.", "S7-300/400")
        self.assertIn("not supported", text)
        self.assertIn("DATE_AND_TIME", text)

    def test_supporting_family_is_unchanged(self):
        message = "Data type DTL is unknown."
        self.assertEqual(annotate(message, "S7-1500"), message)

    def test_unrelated_error_is_unchanged(self):
        message = "Block OB1: operand not found."
        self.assertEqual(annotate(message, "S7-300/400"), message)


if __name__ == "__main__":
    unittest.main()
