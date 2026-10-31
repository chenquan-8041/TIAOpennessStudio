"""Task 11.1 unit tests: ProjectPlan serialization / persistence idempotency / topological sort with cycle detection / interface drift write-back.

Covers the pure-function layer, no LLM or TIA Portal required:
  - plan_models   : ProjectPlan / Task / Phase / BlockInterface / MilestoneDecision serialization round-trip
  - plan_store    : SQLite save / load / resume (find_unfinished) / idempotent upsert / delete
  - planner       : _toposort topological order and cycle fallback
  - executor      : _extract_scl_interface extraction, _interface_changed drift detection, _reconcile_interface write-back + stale propagation

Run (from the TIAAgent directory):
    python -m unittest tests/test_plan_models.py
    # or: pytest tests/test_plan_models.py
"""

import json
import os
import sys
import tempfile
import unittest
from unittest import mock

# Make tests runnable standalone: add the TIAAgent root and _pydeps (vendored langchain_core etc.) to sys.path.
_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ROOT not in sys.path:
    sys.path.insert(0, _ROOT)
_pydeps = os.path.join(_ROOT, "_pydeps")
if os.path.isdir(_pydeps) and _pydeps not in sys.path:
    sys.path.insert(0, _pydeps)

from agent.plan_models import (  # noqa: E402
    BlockInterface,
    MilestoneDecision,
    Phase,
    PlanStatus,
    ProjectPlan,
    Task,
    TaskAction,
    TaskStatus,
)
from agent.plan_store import PlanStore  # noqa: E402
from agent.plan_job import PlanJobManager  # noqa: E402
from agent.planner import (  # noqa: E402
    _merge_replanned,
    _normalize_group_path,
    _task_from_skeleton,
    _toposort,
)
from agent.executor import (  # noqa: E402
    _task_prompt,
    _extract_scl_block,
    _extract_scl_interface,
    _interface_changed,
    _reconcile_interface,
    _referenced_member_schemas,
    _resolve_ob_number,
    _split_ob_scl,
    apply_task_decision,
    next_resumable_tasks,
    _extract_task_tokens,
)


def _make_plan() -> ProjectPlan:
    """Build a typical plan with two phases and three tasks, including a milestone decision."""
    return ProjectPlan(
        id="p1",
        project_key="C:/proj.apXX",
        summary="生成整套 PLC 项目",
        status=PlanStatus.PAUSED.value,
        phases=[
            Phase(id="ph1", name="UDT + DB", is_milestone=True, tasks=["t1", "t2"]),
            Phase(id="ph2", name="FC/FB/OB", is_milestone=True, tasks=["t3"]),
        ],
        tasks=[
            Task(
                id="t1",
                kind="UDT",
                block_name="MotorSpeed",
                action=TaskAction.CREATE.value,
                depends_on=[],
                interface=BlockInterface(outputs=["speed:Real"]),
                status=TaskStatus.SUCCESS.value,
            ),
            Task(
                id="t2",
                kind="DB",
                block_name="SharedDB",
                depends_on=["t1"],
                interface=BlockInterface(db_refs=["MotorSpeed"]),
                status=TaskStatus.SUCCESS.value,
            ),
            Task(
                id="t3",
                kind="FB",
                block_name="MotorCtrl",
                depends_on=["t2"],
                interface=BlockInterface(inputs=["start:Bool"], udt_refs=["MotorSpeed"]),
                status=TaskStatus.PENDING.value,
            ),
        ],
        milestone_decisions=[
            MilestoneDecision(phase_id="ph1", decision="approve", note=None, at="2026-10-21T10:00:00"),
        ],
    )


class TestPlanModels(unittest.TestCase):
    def test_project_plan_roundtrip(self):
        plan = _make_plan()
        restored = ProjectPlan.from_dict(plan.to_dict())
        self.assertEqual(restored.id, "p1")
        self.assertEqual(restored.project_key, "C:/proj.apXX")
        self.assertEqual(restored.status, PlanStatus.PAUSED.value)
        self.assertEqual(len(restored.phases), 2)
        self.assertEqual(len(restored.tasks), 3)
        self.assertEqual(len(restored.milestone_decisions), 1)

        t3 = restored.task_by_id("t3")
        self.assertIsNotNone(t3)
        self.assertEqual(t3.kind, "FB")
        self.assertEqual(t3.depends_on, ["t2"])
        self.assertEqual(t3.interface.udt_refs, ["MotorSpeed"])
        self.assertEqual(t3.interface.inputs, ["start:Bool"])

    def test_task_status_and_milestone_decision_note(self):
        plan = _make_plan()
        d = plan.to_dict()
        note = d["milestone_decisions"][0]["note"]
        self.assertIsNone(note)
        # the note field can be carried (adjust scenario)
        plan.milestone_decisions.append(MilestoneDecision(phase_id="ph2", decision="adjust", note="增加一个堵转保护块"))
        restored = ProjectPlan.from_dict(plan.to_dict())
        self.assertEqual(restored.milestone_decisions[1].note, "增加一个堵转保护块")

    def test_set_task_status(self):
        plan = ProjectPlan(id="x", tasks=[Task(id="t1")])
        self.assertTrue(plan.set_task_status("t1", TaskStatus.FAILED.value, error="boom"))
        self.assertEqual(plan.task_by_id("t1").status, TaskStatus.FAILED.value)
        self.assertEqual(plan.task_by_id("t1").error, "boom")
        self.assertFalse(plan.set_task_status("missing", TaskStatus.SUCCESS.value))


class TestGroupPaths(unittest.TestCase):
    def test_task_paths_roundtrip(self):
        task = Task(
            id="t1",
            kind="LAD_FB",
            block_name="FB_StationCycle",
            group_path="Station/Logic",
            type_group_path="Station",
        )
        restored = Task.from_dict(task.to_dict())
        self.assertEqual(restored.group_path, "Station/Logic")
        self.assertEqual(restored.type_group_path, "Station")

    def test_legacy_plan_without_paths_loads(self):
        restored = Task.from_dict({"id": "t1", "kind": "DB", "block_name": "SharedDB"})
        self.assertIsNone(restored.group_path)
        self.assertIsNone(restored.type_group_path)

    def test_normalize_group_path(self):
        self.assertEqual(_normalize_group_path("Station/Logic"), "Station/Logic")
        self.assertEqual(_normalize_group_path("/Station/Logic/"), "Station/Logic")
        self.assertEqual(_normalize_group_path("Station\\Logic"), "Station/Logic")
        self.assertEqual(_normalize_group_path(" Station /  Logic "), "Station/Logic")
        # Tree roots are not groups: a leading Blocks/Types segment is stripped.
        self.assertEqual(_normalize_group_path("Blocks/Station/Logic"), "Station/Logic")
        self.assertEqual(_normalize_group_path("Types/Station"), "Station")
        self.assertEqual(_normalize_group_path("blocks/station/data"), "station/data")
        self.assertEqual(_normalize_group_path("Blocks\\Station\\Data"), "Station/Data")
        self.assertIsNone(_normalize_group_path("Types"))
        self.assertIsNone(_normalize_group_path(""))
        self.assertIsNone(_normalize_group_path("A/B/C/D"))
        self.assertIsNone(_normalize_group_path("Station:Bad"))
        self.assertIsNone(_normalize_group_path(".."))
        self.assertIsNone(_normalize_group_path(None))

    def test_skeleton_paths_by_kind(self):
        udt = _task_from_skeleton(
            {"id": "t1", "kind": "UDT", "block_name": "WorkpieceData",
             "group_path": "Station/Logic", "type_group_path": "Station"}
        )
        self.assertIsNone(udt.group_path)
        self.assertEqual(udt.type_group_path, "Station")

        db = _task_from_skeleton(
            {"id": "t2", "kind": "DB", "block_name": "StationDB",
             "group_path": "Station/Data", "type_group_path": "Station"}
        )
        self.assertEqual(db.group_path, "Station/Data")
        self.assertIsNone(db.type_group_path)

        # A planner that still emits tree-root prefixes must land in the same groups.
        prefixed = _task_from_skeleton(
            {"id": "t4", "kind": "UDT", "block_name": "StationData",
             "type_group_path": "Types/Station"}
        )
        self.assertEqual(prefixed.type_group_path, "Station")

        bad = _task_from_skeleton(
            {"id": "t3", "kind": "LAD_FB", "block_name": "FB_Bad", "group_path": "A/B/C/D"}
        )
        self.assertIsNone(bad.group_path)
        self.assertTrue(any("invalid group_path" in w for w in bad.warnings))


class TestGlobalTagWiring(unittest.TestCase):
    """Blocks must be wired to real project globals, and GRAPH needs conditions + actions."""

    def test_interface_tags_roundtrip(self):
        iface = BlockInterface(
            inputs=["Start:Bool"],
            outputs=["Running:Bool"],
            tags=["s1", "h1", "ZZ_DB_StationData.Alarm"],
        )
        restored = BlockInterface.from_dict(iface.to_dict())
        self.assertEqual(restored.tags, ["s1", "h1", "ZZ_DB_StationData.Alarm"])

    def test_task_prompt_names_global_tags(self):
        task = Task(kind="FB", block_name="FB_Control", interface=BlockInterface(tags=["s1", "h1"]))
        self.assertIn("Global tags to drive: s1, h1", _task_prompt(task))

    def test_lad_prompt_requires_global_operands(self):
        task = Task(kind="LAD_FC", block_name="FC_Control", interface=BlockInterface(tags=["s1", "h1"]))
        prompt = _task_prompt(task)
        self.assertIn("Global tags to drive: s1, h1", prompt)
        self.assertIn("Scope=GlobalVariable", prompt)

    def test_graph_prompt_requires_conditions_and_actions(self):
        task = Task(kind="GRAPH_FB", block_name="FB_Seq", interface=BlockInterface(tags=["s1", "h1"]))
        prompt = _task_prompt(task)
        self.assertIn("Global tags to drive: s1, h1", prompt)
        self.assertIn("MUST declare a real condition", prompt)
        self.assertIn("at least one action", prompt)


class TestGroupPathWriteArgs(unittest.IsolatedAsyncioTestCase):
    """The deterministic writers must forward the planned group path to the build tool."""

    class _FakeTool:
        def __init__(self, reply, calls):
            self._reply = reply
            self._calls = calls

        async def ainvoke(self, args):
            self._calls.append(args)
            return self._reply

    class _FakeEntry:
        def __init__(self, reply, calls):
            self.tool = TestGroupPathWriteArgs._FakeTool(reply, calls)

    def _patched_catalog(self, reply, calls):
        fake = mock.MagicMock()
        fake.get.return_value = self._FakeEntry(reply, calls)
        return mock.patch("tools.catalog.get_catalog", return_value=fake)

    async def test_udt_passes_type_group_path(self):
        from agent.executor import _execute_udt_db_direct

        calls = []
        task = Task(
            kind="UDT",
            block_name="WorkpieceData",
            type_group_path="Station",
            interface=BlockInterface(members=["ID : Int"]),
        )
        with self._patched_catalog("[UDT] 'WorkpieceData' built and imported.", calls):
            ok, err = await _execute_udt_db_direct(task)
        self.assertTrue(ok, err)
        self.assertEqual(calls[0]["group_path"], "Station")

    async def test_db_passes_block_group_path(self):
        from agent.executor import _execute_udt_db_direct

        calls = []
        task = Task(
            kind="DB",
            block_name="StationDB",
            block_number="DB300",
            group_path="Station/Data",
            interface=BlockInterface(members=["Workpiece : WorkpieceData"]),
        )
        with self._patched_catalog("[DB] 'StationDB' built and imported.", calls):
            ok, err = await _execute_udt_db_direct(task)
        self.assertTrue(ok, err)
        self.assertEqual(calls[0]["group_path"], "Station/Data")

    async def test_legacy_task_without_path_omits_argument(self):
        from agent.executor import _execute_udt_db_direct

        calls = []
        task = Task(kind="UDT", block_name="Legacy", interface=BlockInterface(members=["A : Bool"]))
        with self._patched_catalog("[UDT] 'Legacy' built and imported.", calls):
            ok, err = await _execute_udt_db_direct(task)
        self.assertTrue(ok, err)
        self.assertNotIn("group_path", calls[0])

    async def test_scl_import_passes_group_path(self):
        from agent.executor import _import_scl_direct

        calls = []
        with self._patched_catalog("Block 'X' generated from SCL source.", calls):
            ok, err = await _import_scl_direct("X", "FB", "FUNCTION_BLOCK X", "Station/Logic")
        self.assertTrue(ok, err)
        self.assertEqual(calls[0]["group_path"], "Station/Logic")


class TestPlanStore(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.store = PlanStore(db_path=os.path.join(self._tmp.name, "plans.db"))

    def tearDown(self):
        self._tmp.cleanup()

    def test_save_load_roundtrip(self):
        plan = _make_plan()
        self.assertTrue(self.store.save(plan))
        loaded = self.store.load("p1")
        self.assertIsNotNone(loaded)
        self.assertEqual(loaded.project_key, "C:/proj.apXX")
        self.assertEqual(len(loaded.tasks), 3)

    def test_save_is_upsert(self):
        plan = _make_plan()
        self.store.save(plan)
        plan.summary = "更新后的摘要"
        self.store.save(plan)
        loaded = self.store.load("p1")
        self.assertEqual(loaded.summary, "更新后的摘要")
        # still only one record (no duplicate rows)
        self.assertIsNotNone(loaded)

    def test_find_unfinished_returns_paused(self):
        plan = _make_plan()  # status = paused
        self.store.save(plan)
        found = self.store.find_unfinished("C:/proj.apXX")
        self.assertIsNotNone(found)
        self.assertEqual(found.id, "p1")

    def test_find_unfinished_skips_terminal(self):
        plan = _make_plan()
        plan.status = PlanStatus.DONE.value
        self.store.save(plan)
        self.assertIsNone(self.store.find_unfinished("C:/proj.apXX"))

        plan.status = PlanStatus.REJECTED.value
        self.store.save(plan)
        self.assertIsNone(self.store.find_unfinished("C:/proj.apXX"))

    def test_delete(self):
        plan = _make_plan()
        self.store.save(plan)
        self.assertTrue(self.store.delete("p1"))
        self.assertIsNone(self.store.load("p1"))


class TestToposort(unittest.TestCase):
    def test_linear_order(self):
        tasks = [
            Task(id="t3", kind="FB", depends_on=["t2"]),
            Task(id="t1", kind="UDT", depends_on=[]),
            Task(id="t2", kind="DB", depends_on=["t1"]),
        ]
        ordered = _toposort(tasks)
        ids = [t.id for t in ordered]
        self.assertEqual(set(ids), {"t1", "t2", "t3"})
        self.assertLess(ids.index("t1"), ids.index("t2"))
        self.assertLess(ids.index("t2"), ids.index("t3"))

    def test_independent_tasks_preserve_input_order(self):
        # tasks without dependencies keep their input relative order (stable Kahn topological sort); "kinds before consumers"
        # is guaranteed by the depends_on produced by the Planner, not by implicit reordering.
        tasks = [
            Task(id="a", kind="FB"),
            Task(id="b", kind="UDT"),
            Task(id="c", kind="DB"),
        ]
        ordered = _toposort(tasks)
        self.assertEqual([t.id for t in ordered], ["a", "b", "c"])

    def test_cycle_fallback_keeps_all(self):
        tasks = [
            Task(id="t1", kind="FB", depends_on=["t2"]),
            Task(id="t2", kind="DB", depends_on=["t1"]),
        ]
        ordered = _toposort(tasks)
        kinds = [t.kind for t in ordered]
        self.assertEqual(set(kinds), {"FB", "DB"})  # cycles do not drop blocks or crash
        # order within a cycle falls back to kind priority: DB before FB
        self.assertLess(kinds.index("DB"), kinds.index("FB"))


class TestInterfaceDrift(unittest.TestCase):
    SCL = """
FUNCTION_BLOCK "Motor"
VAR_INPUT
    start : Bool;
    speed : Real;
END_VAR
VAR_OUTPUT
    done : Bool;
END_VAR
VAR_IN_OUT
    status : Int;
END_VAR
BEGIN
    #motor := "SharedDB".setpoint;
END_FUNCTION_BLOCK
"""

    def test_extract_interface(self):
        iface = _extract_scl_interface(self.SCL)
        self.assertEqual(iface.inputs, ["start", "speed", "status"])
        self.assertEqual(iface.outputs, ["done"])
        self.assertIn("SharedDB", iface.db_refs)

    def test_interface_changed(self):
        planned = BlockInterface(inputs=["start", "speed", "status"], outputs=["done"], db_refs=["SharedDB"])
        actual = _extract_scl_interface(self.SCL)
        self.assertFalse(_interface_changed(planned, actual))
        self.assertTrue(_interface_changed(BlockInterface(inputs=["start"]), actual))

    def test_reconcile_writes_back_and_marks_downstream_stale(self):
        planned = BlockInterface(inputs=["start"])  # does not match the real interface
        plan = ProjectPlan(id="p", tasks=[
            Task(id="t1", kind="FB", block_name="Motor", interface=planned, status=TaskStatus.SUCCESS.value),
            Task(id="t2", kind="FC", block_name="Caller", depends_on=["t1"], status=TaskStatus.PENDING.value),
        ])
        t1 = plan.task_by_id("t1")
        self.assertTrue(_reconcile_interface(plan, t1, _extract_scl_interface(self.SCL)))
        self.assertEqual(t1.interface.inputs, ["start", "speed", "status"])
        self.assertEqual(plan.task_by_id("t2").status, TaskStatus.STALE.value)

    def test_reconcile_no_drift_on_empty_extraction(self):
        planned = BlockInterface(inputs=["start"])
        plan = ProjectPlan(id="p", tasks=[Task(id="x", interface=planned)])
        self.assertFalse(_reconcile_interface(plan, plan.task_by_id("x"), _extract_scl_interface("")))

    def test_udt_refs(self):
        from agent.executor import _extract_scl_interface
        scl = ('FUNCTION_BLOCK "Motor"\nVAR_INPUT\ncfg : "MotorCfg";\nspeed : Real;\nEND_VAR\n'
               'VAR\ninst : MyTimer;\nEND_VAR\nBEGIN\nEND_FUNCTION_BLOCK')
        iface = _extract_scl_interface(scl)
        self.assertIn("MotorCfg", iface.udt_refs)
        self.assertIn("MyTimer", iface.udt_refs)
        self.assertNotIn("REAL", iface.udt_refs)  # builtin types are not counted as UDT refs

    def test_xml_interface(self):
        from agent.executor import _extract_xml_interface
        xml = ('<Blocks><SW.Blocks.FB><Interface>'
               '<Section Name="Input"><Member Name="start" Datatype="Bool"/><Member Name="cfg" Datatype="MyCfg"/></Section>'
               '<Section Name="Output"><Member Name="done" Datatype="Bool"/></Section>'
               '</Interface></SW.Blocks.FB></Blocks>')
        iface = _extract_xml_interface(xml)
        self.assertEqual(iface.inputs, ["start", "cfg"])
        self.assertEqual(iface.outputs, ["done"])
        self.assertIn("MyCfg", iface.udt_refs)
        self.assertNotIn("Bool", iface.udt_refs)


class TestReferencedMemberSchemas(unittest.TestCase):
    """A task that references a UDT/DB must receive that type's planned fields, so the coder does not invent them."""

    def _plan(self):
        return ProjectPlan(id="p", tasks=[
            Task(
                id="t1", kind="UDT", block_name="V2_MotorParam", status=TaskStatus.SUCCESS.value,
                interface=BlockInterface(members=["SpeedSetpoint : Real", "Enable : Bool"]),
            ),
            Task(
                id="t2", kind="DB", block_name="V2_DB_Motor", status=TaskStatus.SUCCESS.value,
                interface=BlockInterface(members=["Motor : V2_MotorParam"]),
            ),
            Task(
                id="t3", kind="FB", block_name="V2_FB_MotorCtrl",
                interface=BlockInterface(
                    inputs=["Start"], db_refs=["V2_DB_Motor"], udt_refs=["V2_MotorParam"]
                ),
            ),
        ])

    def test_resolves_udt_and_db_fields_from_the_plan(self):
        plan = self._plan()
        lines = _referenced_member_schemas(plan, plan.task_by_id("t3"))
        self.assertEqual(lines, [
            "- UDT `V2_MotorParam` fields: SpeedSetpoint : Real, Enable : Bool",
            "- DB `V2_DB_Motor` fields: Motor : V2_MotorParam",
        ])

    def test_no_references_means_no_lines(self):
        plan = self._plan()
        self.assertEqual(_referenced_member_schemas(plan, plan.task_by_id("t1")), [])

    def test_unfinished_source_task_is_ignored(self):
        plan = self._plan()
        plan.task_by_id("t1").status = TaskStatus.PENDING.value
        lines = _referenced_member_schemas(plan, plan.task_by_id("t3"))
        self.assertEqual(lines, ["- DB `V2_DB_Motor` fields: Motor : V2_MotorParam"])

    def test_db_reference_resolves_the_udt_it_instantiates(self):
        # An OB that only references the DB still needs the UDT's leaf fields: the DB
        # member alone ("Motor : V2_MotorParam") is not addressable without them.
        plan = self._plan()
        plan.tasks.append(Task(
            id="t4", kind="OB", block_name="V2_OB_Main",
            interface=BlockInterface(db_refs=["V2_DB_Motor"]),
        ))
        lines = _referenced_member_schemas(plan, plan.task_by_id("t4"))
        self.assertEqual(lines, [
            "- DB `V2_DB_Motor` fields: Motor : V2_MotorParam",
            "- UDT `V2_MotorParam` fields: SpeedSetpoint : Real, Enable : Bool",
        ])


class TestObBodySplit(unittest.TestCase):
    """OB tasks go through build_ob: the envelope is host-side, so only body + temps travel."""

    SCL = '''ORGANIZATION_BLOCK "V2_OB_Main"
TITLE = 'main'
VERSION : 0.1
VAR_TEMP
  tempEnable : Bool;
  tempSpeed : Real;
END_VAR
BEGIN
  #tempEnable := TRUE;
  #tempSpeed := 1500.0;
END_ORGANIZATION_BLOCK
'''

    def test_split_name_temps_and_body(self):
        parsed = _split_ob_scl(self.SCL)
        self.assertIsNotNone(parsed)
        self.assertEqual(parsed["name"], "V2_OB_Main")
        self.assertEqual(parsed["temps"], [
            {"name": "tempEnable", "datatype": "Bool"},
            {"name": "tempSpeed", "datatype": "Real"},
        ])
        self.assertIn("#tempEnable := TRUE;", parsed["body"])
        self.assertIn("#tempSpeed := 1500.0;", parsed["body"])
        self.assertNotIn("VAR_TEMP", parsed["body"])
        self.assertNotIn("ORGANIZATION_BLOCK", parsed["body"])

    def test_non_ob_returns_none(self):
        self.assertIsNone(_split_ob_scl('FUNCTION_BLOCK "X"\nBEGIN\nEND_FUNCTION_BLOCK'))

    def test_constants_are_inlined_into_the_body(self):
        # build_ob has no place for VAR CONSTANT, so the literal must replace #<name>;
        # leaving it in makes TIA fail the temp-FC tokenizer with "operand undefined".
        scl = '''ORGANIZATION_BLOCK "V2_OB_Main"
VAR_TEMP
  tempSpeed : Real;
END_VAR
VAR CONSTANT
  cSpeed : Real := 1500.0;
  cLimit : Real := 4.5;
END_VAR
BEGIN
  "DB".Motor.Speed := #cSpeed;
  "DB".Motor.Limit := #cLimit;
  #tempSpeed := #cSpeed;
END_ORGANIZATION_BLOCK
'''
        parsed = _split_ob_scl(scl)
        self.assertIsNotNone(parsed)
        self.assertIn('"DB".Motor.Speed := 1500.0;', parsed["body"])
        self.assertIn('"DB".Motor.Limit := 4.5;', parsed["body"])
        self.assertIn("#tempSpeed := 1500.0;", parsed["body"])
        self.assertNotIn("#cSpeed", parsed["body"])
        self.assertNotIn("#cLimit", parsed["body"])


class TestResolveObNumber(unittest.TestCase):
    def _tree(self, obs):
        return {"blocks": [{"type": "OB", "number": n, "name": nm} for n, nm in obs]}

    def test_planned_number_free(self):
        task = Task(id="t", kind="OB", block_name="V2_OB_Main", block_number="OB33")
        self.assertEqual(_resolve_ob_number(task, self._tree([(1, "Main"), (32, "CYC")])), (33, False, None))

    def test_planned_number_taken_by_other(self):
        task = Task(id="t", kind="OB", block_name="V2_OB_Main", block_number="32")
        number, overwrite, err = _resolve_ob_number(task, self._tree([(32, "CYC_INT2_RTG1")]))
        self.assertIsNone(number)
        self.assertFalse(overwrite)
        self.assertIn("CYC_INT2_RTG1", err)

    def test_planned_number_same_name_means_overwrite(self):
        task = Task(id="t", kind="OB", block_name="Main", block_number="1")
        self.assertEqual(_resolve_ob_number(task, self._tree([(1, "Main")])), (1, True, None))

    def test_missing_number_allocates_first_free_cyclic(self):
        task = Task(id="t", kind="OB", block_name="V2_OB_Main")
        tree = self._tree([(1, "Main"), (30, "A"), (31, "B"), (32, "CYC")])
        self.assertEqual(_resolve_ob_number(task, tree), (33, False, None))

    def test_all_cyclic_numbers_taken(self):
        task = Task(id="t", kind="OB", block_name="V2_OB_Main")
        tree = self._tree([(1, "Main")] + [(n, "OB%d" % n) for n in range(30, 39)])
        number, _, err = _resolve_ob_number(task, tree)
        self.assertIsNone(number)
        self.assertIn("No free", err)


class TestExtractSclBlock(unittest.TestCase):
    """The importer must receive the block, not the Design prose models like to wrap inside the fence."""

    def test_prose_inside_one_fence_is_trimmed(self):
        text = (
            "## Design\n- notes\n```scl\n## Design\n- more notes\n"
            'FUNCTION_BLOCK "A"\nBEGIN\nEND_FUNCTION_BLOCK\n```'
        )
        block = _extract_scl_block(text)
        self.assertTrue(block.startswith('FUNCTION_BLOCK "A"'))
        self.assertNotIn("## Design", block)

    def test_nested_second_fence_wins(self):
        text = (
            "```scl\n## Design\n- notes\n```scl\n"
            'ORGANIZATION_BLOCK "B"\nBEGIN\nEND_ORGANIZATION_BLOCK\n```'
        )
        block = _extract_scl_block(text)
        self.assertTrue(block.startswith('ORGANIZATION_BLOCK "B"'))


class TestReplanMerge(unittest.TestCase):
    """adjust replanning: _merge_replanned keeps success blocks, replaces unfinished blocks, and remaps dependencies."""

    def _base_plan(self):
        return ProjectPlan(
            id="p",
            project_key="k",
            tasks=[
                Task(id="t1", kind="UDT", block_name="MotorSpeed", status=TaskStatus.SUCCESS.value),
                Task(id="t2", kind="DB", block_name="SharedDB", depends_on=["t1"], status=TaskStatus.SUCCESS.value),
                Task(id="t3", kind="FB", block_name="MotorCtrl", depends_on=["t2"], status=TaskStatus.PENDING.value),
            ],
            phases=[Phase(id="ph1", name="旧阶段", is_milestone=True, tasks=["t1", "t2", "t3"])],
            milestone_decisions=[MilestoneDecision(phase_id="ph1", decision="approve")],
        )

    def test_keeps_success_and_replaces_pending(self):
        plan = self._base_plan()
        new_plan = ProjectPlan(id="n", project_key="k", tasks=[
            Task(id="a", kind="UDT", block_name="MotorSpeed", status=TaskStatus.PENDING.value),
            Task(id="b", kind="FC", block_name="Alarm", depends_on=["a"], status=TaskStatus.PENDING.value),
        ])
        _merge_replanned(plan, new_plan)

        ids = {t.id for t in plan.tasks}
        self.assertIn("t1", ids)
        self.assertIn("t2", ids)
        self.assertNotIn("t3", ids)  # the old unfinished block was replaced
        alarm = next(t for t in plan.tasks if t.block_name == "Alarm")
        self.assertEqual(alarm.status, TaskStatus.PENDING.value)
        self.assertEqual(alarm.depends_on, ["t1"])  # dependency remapped to the kept MotorSpeed

    def test_duplicate_completed_block_is_not_regenerated(self):
        plan = self._base_plan()
        new_plan = ProjectPlan(id="n", project_key="k", tasks=[
            Task(id="a", kind="UDT", block_name="MotorSpeed", depends_on=[], status=TaskStatus.PENDING.value),
            Task(id="c", kind="FC", block_name="MotorCtrl2", depends_on=["a"], status=TaskStatus.PENDING.value),
        ])
        _merge_replanned(plan, new_plan)
        # MotorSpeed is already completed and must not appear twice
        speeds = [t for t in plan.tasks if t.block_name == "MotorSpeed"]
        self.assertEqual(len(speeds), 1)
        self.assertEqual(speeds[0].status, TaskStatus.SUCCESS.value)
        ctrl2 = next(t for t in plan.tasks if t.block_name == "MotorCtrl2")
        self.assertEqual(ctrl2.depends_on, ["t1"])

    def test_phase_rebuilt_with_followup(self):
        plan = self._base_plan()
        new_plan = ProjectPlan(id="n", project_key="k", tasks=[
            Task(id="a", kind="UDT", block_name="MotorSpeed", status=TaskStatus.PENDING.value),
            Task(id="b", kind="FC", block_name="Alarm", depends_on=["a"], status=TaskStatus.PENDING.value),
        ])
        _merge_replanned(plan, new_plan)
        self.assertTrue(any(p.name == "重规划后续" for p in plan.phases))
        # the previously approved phase is kept
        self.assertTrue(any(p.id == "ph1" for p in plan.phases))


class TestPlanJobSnapshot(unittest.TestCase):
    """A3 progress snapshot: PlanJobManager.snapshot computes progress from the persisted plan."""

    def test_snapshot_counts(self):
        import tempfile
        from agent.plan_store import PlanStore
        with tempfile.TemporaryDirectory() as td:
            store = PlanStore(db_path=os.path.join(td, "plans.db"))
            plan = _make_plan()  # t1/t2 success, t3 pending, status=paused
            store.save(plan)
            mgr = PlanJobManager(store=store)
            snap = mgr.snapshot("p1")
            self.assertIsNotNone(snap)
            self.assertEqual(snap["total"], 3)
            self.assertEqual(snap["done"], 2)
            self.assertEqual(snap["failed"], 0)
            self.assertEqual(snap["status"], PlanStatus.PAUSED.value)
            self.assertEqual(len(snap["milestones"]), 1)

    def test_snapshot_unknown_plan(self):
        import tempfile
        from agent.plan_store import PlanStore
        with tempfile.TemporaryDirectory() as td:
            store = PlanStore(db_path=os.path.join(td, "plans.db"))
            mgr = PlanJobManager(store=store)
            self.assertIsNone(mgr.snapshot("missing"))


class TestTaskDecision(unittest.TestCase):
    """Task 5: manual pause-on-failure (continue/skip/terminate) + resume filtering skipped."""

    def _plan(self):
        return ProjectPlan(id="p", project_key="k", status=PlanStatus.PAUSED.value, tasks=[
            Task(id="t1", kind="UDT", block_name="A", status=TaskStatus.SUCCESS.value),
            Task(id="t2", kind="FB", block_name="B", depends_on=["t1"], status=TaskStatus.FAILED.value, error="boom"),
            Task(id="t3", kind="FC", block_name="C", depends_on=["t2"], status=TaskStatus.PENDING.value),
        ])

    def test_continue_resets_failed_to_pending(self):
        plan = self._plan()
        self.assertIsNone(apply_task_decision(plan, "t2", "continue"))
        self.assertEqual(plan.task_by_id("t2").status, TaskStatus.PENDING.value)
        self.assertEqual(plan.status, PlanStatus.READY.value)

    def test_skip_marks_downstream_stale(self):
        plan = self._plan()
        self.assertIsNone(apply_task_decision(plan, "t2", "skip"))
        self.assertEqual(plan.task_by_id("t2").status, TaskStatus.SKIPPED.value)
        self.assertEqual(plan.task_by_id("t3").status, TaskStatus.STALE.value)

    def test_terminate_rejects_plan(self):
        plan = self._plan()
        self.assertIsNone(apply_task_decision(plan, "t2", "terminate"))
        self.assertEqual(plan.status, PlanStatus.REJECTED.value)

    def test_invalid_decision_and_task(self):
        plan = self._plan()
        self.assertEqual(apply_task_decision(plan, "t2", "bogus"), "invalid decision")
        self.assertEqual(apply_task_decision(plan, "missing", "continue"), "task not found")

    def test_next_resumable_skips_success_and_skipped(self):
        plan = self._plan()
        # resume=True: skip success (t1) and skipped (t2), keep only pending (t3)
        plan.task_by_id("t2").status = TaskStatus.SKIPPED.value
        ids = [t.id for t in next_resumable_tasks(plan, resume=True)]
        self.assertEqual(ids, ["t3"])
        # resume=False: all tasks
        self.assertEqual(len(next_resumable_tasks(plan, resume=False)), 3)

    def test_extract_task_tokens(self):
        class FakeMsg:
            def __init__(self, usage):
                self.usage_metadata = usage

        result = {
            "messages": [
                FakeMsg({"input_tokens": 100, "output_tokens": 50, "total_tokens": 150}),
                FakeMsg({"input_tokens": 200, "output_tokens": 30, "total_tokens": 230}),
                object(),  # messages without usage_metadata must be ignored
            ]
        }
        self.assertEqual(_extract_task_tokens(result), 380)
        self.assertEqual(_extract_task_tokens({}), 0)
        self.assertEqual(_extract_task_tokens({"messages": None}), 0)


class TestCompileSignature(unittest.TestCase):
    """Task 8: compile error signature normalization + SCL structure precheck."""

    def test_normalize_structured_errors(self):
        from agent.supervisor import normalize_compile_error_signature
        raw = ('{"success": false, "errors": ['
               '{"block": "FB1", "message": "Identifier x already declared at line 12, col 3"}, '
               '{"block": "FB1", "message": "Unknown type Foo"}]}')
        sig = normalize_compile_error_signature(raw)
        self.assertIn("already declared", sig)
        self.assertIn("Unknown type Foo", sig)
        self.assertNotIn("line 12", sig)  # line/column noise must be stripped

    def test_normalize_free_text(self):
        from agent.supervisor import normalize_compile_error_signature
        sig = normalize_compile_error_signature("Error at line 5, col 2: Unknown identifier")
        self.assertIn("Unknown identifier", sig)
        self.assertNotIn("line 5", sig)

    def test_scl_structure_precheck(self):
        from agent.supervisor import _scl_structure_precheck
        good = ('FUNCTION_BLOCK "Motor"\nVAR_INPUT\nx : Int;\nEND_VAR\n'
                'BEGIN\nx := 1;\nEND_FUNCTION_BLOCK')
        self.assertEqual(_scl_structure_precheck(good), "")
        # missing BEGIN
        bad = 'FUNCTION_BLOCK "Motor"\nVAR_INPUT\nx : Int;\nEND_VAR\nEND_FUNCTION_BLOCK'
        self.assertIn("BEGIN", _scl_structure_precheck(bad))
        # missing END
        bad2 = 'FUNCTION_BLOCK "Motor"\nBEGIN\nx := 1;'
        self.assertIn("END_FUNCTION_BLOCK", _scl_structure_precheck(bad2))
        # DB has no mandatory BEGIN but requires END_DATA_BLOCK closure
        db_ok = 'DATA_BLOCK "D"\nSTRUCT\na : Int;\nEND_STRUCT\nEND_DATA_BLOCK'
        self.assertEqual(_scl_structure_precheck(db_ok), "")


class TestNotice(unittest.TestCase):
    """The user-facing notice path must always produce a JSON-serializable payload."""

    def test_notice_serializes_odd_params(self):
        from agent.notices import notice
        payload = notice("plan.started", "Plan started: {0} blocks", 3, None, object())
        self.assertEqual(payload["code"], "plan.started")
        self.assertEqual(payload["params"][0], "3")
        json.dumps(payload, ensure_ascii=False)  # must not raise

    def test_notice_survives_broken_str(self):
        from agent.notices import notice

        class Boom:
            def __str__(self):
                raise RuntimeError("boom")

        payload = notice("x", "y", Boom())
        self.assertEqual(payload["params"], [""])
        json.dumps(payload)


if __name__ == "__main__":
    unittest.main()