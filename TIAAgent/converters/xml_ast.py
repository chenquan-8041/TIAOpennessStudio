"""XML payload / fragment helpers for the backend.

XML -> IR conversion lives in ONE place: the C# snapshot projection
(``BlockSnapshotService``, served through ``get_block_content``). Python must not
re-parse block XML into AST/IR, so only the two payload helpers the HTTP surface
needs remain here:

* ``extract_xml_payload``  - pull the ``<Document>``/``<?xml ...>`` body out of a
  pasted XML blob.
* ``extract_xml_fragment`` - recover one CompileUnit / FlgNet fragment by
  compileUnitId (+ optional UId) for UI fragment lookups.
"""

from __future__ import annotations

import re
import xml.etree.ElementTree as ET
from typing import Optional


def _local_name(tag: str) -> str:
    if not tag:
        return ""
    if "}" in tag:
        return tag.rsplit("}", 1)[-1]
    return tag


def extract_xml_fragment(xml_text: str, compile_unit_id: str = "", uid: str = "") -> Optional[str]:
    if not xml_text:
        return None
    root = ET.fromstring(xml_text)
    compile_units = [node for node in root.iter() if _local_name(node.tag) == "CompileUnit"]
    compile_unit = None
    if compile_unit_id:
        compile_unit = next((node for node in compile_units if (node.attrib.get("ID", "") or "") == compile_unit_id), None)
    if compile_unit is None and compile_units:
        compile_unit = compile_units[0]
    if compile_unit is None:
        return None
    if uid:
        node = next((n for n in compile_unit.iter() if (n.attrib.get("UId", "") or "") == uid), None)
        if node is not None:
            return ET.tostring(node, encoding="unicode", method="xml")
    flgnet = next((n for n in compile_unit.iter() if _local_name(n.tag) == "FlgNet"), None)
    target = flgnet if flgnet is not None else compile_unit
    return ET.tostring(target, encoding="unicode", method="xml")


def extract_xml_payload(raw_text: str) -> Optional[str]:
    text = (raw_text or "").strip()
    if not text:
        return None
    if text.startswith("<?xml") or text.startswith("<Document"):
        return text
    m = re.search(r"(<\?xml[\s\S]*$|<Document[\s\S]*$)", text)
    if m:
        return m.group(1).strip()
    return None
