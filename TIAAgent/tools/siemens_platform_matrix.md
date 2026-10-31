# Siemens CPU incompatibility matrix — S7-300 / S7-400 / S7-1200 / S7-1500

Keywords: s7-300, s7-400, s7-1200, s7-1500, 西门子, 平台, 差异, 兼容, dtl, ldt, wstring, r_trig, f_trig, graph, optimized, remanence, firmware

This is the single source for CPU-family differences. The agent reads it through
`query_siemens_platform_matrix(query, cpu_family)`; edit this file only.

## 兼容性矩阵

| feature | S7-300 | S7-400 | S7-1200 | S7-1500 | replacement on S7-300/400 | auto retype |
|---|---|---|---|---|---|---|
| DTL | ✗ | ✗ | ✓ FW>=2.0 | ✓ | DATE_AND_TIME (8-byte BCD), or DATE + TIME_OF_DAY | ✓ |
| LDT | ✗ | ✗ | ✓ newer FW | ✓ | DATE_AND_TIME | ✓ |
| WSTRING | ✗ | ✗ | ✓ newer FW | ✓ | STRING, or ARRAY[..] OF CHAR | ✓ |
| R_TRIG / F_TRIG | ✗ | ✗ | ✓ | ✓ | BOOL edge bit: rising = x AND NOT x_prev | ✗ |
| S7-GRAPH (build_graph_fb) | ✓ | ✓ | ✗ | ✓ | (none — pick a CPU that supports GRAPH) | ✗ |
| optimized block access | ✗ | ✗ | default | default | `{ S7_Optimized_Access := 'FALSE' }` | ✗ |
| DB static member Remanence | must be declared | must be declared | per member | per member | `Remanence="Retain"` | ✗ |

Legend: ✓ supported · ✗ not supported (the spec imports and then fails the
project compile) · "default" the CPU expects optimized access unless stated.
"auto retype" marks the rows an automated repair may rewrite: a pure datatype
swap. Rows where the replacement changes behaviour (instructions, attributes)
are reported but never written automatically.

## 报错原文对照

| symptom | feature | what to do |
|---|---|---|
| Data type DTL is unknown. | DTL | replace DTL with DATE_AND_TIME or split DATE + TIME_OF_DAY |
| 数据类型 DTL 未知。 | DTL | replace DTL with DATE_AND_TIME or split DATE + TIME_OF_DAY |
| Data type WSTRING is unknown. | WSTRING | replace WSTRING with STRING or ARRAY OF CHAR |
| 无法设置属性"Remanence" | DB static member Remanence | declare Remanence="Retain" on the new member |
| R_TRIG / F_TRIG unknown | R_TRIG / F_TRIG | use a BOOL edge bit instead of the instruction block |

## 用法

1. `get_environment_profile` → `plc_family` (e.g. `S7-300/400`, `S7-1200`, `S7-1500`).
2. `query_siemens_platform_matrix("<datatype or instruction or symptom>", cpu_family)`.
3. A `✗` cell means: do not generate it; use the `replacement` column instead.
4. After writing, `compile_block(<block>)` to confirm on the real CPU.

## 验证状态

- S7-300 / S7-400 rows: verified on a real machine (2026-10-30, TIA V15.1,
  CPU 6ES7 318-3FL01-0AB0) — DTL/WSTRING/LDT/R_TRIG/F_TRIG rejected, Remanence
  required on DB static members.
- S7-1200 / S7-1500 rows: platform baselines. Firmware gates (e.g. DTL on S7-1200
  needs FW >= 2.0) must be confirmed on the live CPU; V18–V21 sample verification
  remains parked by project decision.
