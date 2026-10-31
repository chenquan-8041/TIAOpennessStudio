"""Unit tests for the T4-light advisory plan contract checks (pure functions, no LLM/TIA).

The checks must never reject a plan: they only annotate tasks with warnings that travel with
the plan into the executor and into the coder prompt.

Run (from the TIAAgent directory):
    python -m unittest tests/test_plan_contract.py
    # or: pytest tests/test_plan_contract.py
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

from agent.plan_checks import validate_plan  # noqa: E402
from agent.plan_models import BlockInterface, ProjectPlan, Task  # noqa: E402


def _task(tid, kind, name, depends_on=None, **iface):
    return Task(
        id=tid,
        kind=kind,
        block_name=name,
        interface=BlockInterface(**iface),
        depends_on=list(depends_on or []),
    )


class PlanContractTests(unittest.TestCase):
    def test_udt_without_members_warns(self):
        plan = ProjectPlan(tasks=[_task("t1", "UDT", "TypeA")])
        findings = validate_plan(plan)
        self.assertTrue(any("UDT has no members" in f for f in findings))
        self.assertTrue(plan.tasks[0].warnings)

    def test_db_without_members_warns(self):
        plan = ProjectPlan(tasks=[_task("t1", "DB", "DataA")])
        findings = validate_plan(plan)
        self.assertTrue(any("DB has no members" in f for f in findings))

    def test_fc_without_io_warns_but_with_input_is_clean(self):
        empty = ProjectPlan(tasks=[_task("t1", "FC", "FcEmpty")])
        self.assertTrue(any("has no inputs or outputs" in f for f in validate_plan(empty)))

        with_io = ProjectPlan(tasks=[_task("t1", "FC", "FcOk", inputs=["Start : Bool"])])
        self.assertEqual(validate_plan(with_io), [])

    def test_resolvable_refs_are_clean(self):
        plan = ProjectPlan(tasks=[
            _task("t1", "UDT", "TypeA", members=["Speed : Real"]),
            _task("t2", "DB", "DataA", members=["Motor : TypeA"], udt_refs=["TypeA"]),
            _task("t3", "FB", "FbA", inputs=["Enable : Bool"], db_refs=["DataA"]),
        ])
        self.assertEqual(validate_plan(plan), [])

    def test_unresolved_refs_warn(self):
        plan = ProjectPlan(tasks=[
            _task("t1", "FB", "FbA", inputs=["Enable : Bool"],
                  db_refs=["MissingDb"], udt_refs=["MissingType"]),
        ])
        findings = validate_plan(plan)
        self.assertTrue(any("db_refs 'MissingDb'" in f for f in findings))
        self.assertTrue(any("udt_refs 'MissingType'" in f for f in findings))

    def test_flattened_udt_member_warns(self):
        plan = ProjectPlan(tasks=[
            _task("t1", "UDT", "TypeA", members=["Speed : Real"]),
            _task("t2", "DB", "DataA", members=["Motor.Speed : Real"], udt_refs=["TypeA"]),
        ])
        findings = validate_plan(plan)
        self.assertTrue(any("flatten" in f for f in findings))

    def test_unknown_dependency_warns(self):
        plan = ProjectPlan(tasks=[_task("t1", "OB", "ObMain", depends_on=["t999"])])
        findings = validate_plan(plan)
        self.assertTrue(any("depends_on 't999'" in f for f in findings))

    def test_ob_without_io_is_allowed(self):
        plan = ProjectPlan(tasks=[_task("t1", "OB", "ObMain")])
        self.assertEqual(validate_plan(plan), [])

    def test_validation_is_idempotent_and_never_rejects(self):
        plan = ProjectPlan(tasks=[_task("t1", "UDT", "TypeA")])
        first = validate_plan(plan)
        second = validate_plan(plan)
        self.assertEqual(first, second)
        self.assertEqual(len(plan.tasks[0].warnings), 1)   # not stacked
        self.assertEqual(plan.status, "planning")          # validation never changes plan status

    def test_warnings_survive_serialization(self):
        plan = ProjectPlan(id="p1", tasks=[_task("t1", "UDT", "TypeA")])
        validate_plan(plan)
        restored = ProjectPlan.from_dict(plan.to_dict())
        self.assertTrue(restored.tasks[0].warnings)


if __name__ == "__main__":
    unittest.main()
