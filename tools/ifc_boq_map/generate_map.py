#!/usr/bin/env python3
"""
Generates the IFC element -> BOQ item mapping for the demo model.

WHY THIS EXISTS
---------------
A real IFC export carries no cost codes. `school_str.ifc` is a genuine Autodesk Revit structural
export and not one of its 1,526 elements names a BOQ item, so nothing in the model can be bound to
money without someone declaring the binding. This script declares it, once, into a reviewable file.

The binding is worth so little effort because of what sits behind it. `9_HISTORICAL_DATA.WBS_Code`
IS the BOQ `Item Ref` -- 173 vs 173, exact, bijective, no orphans either side. So one authored hop
buys the whole chain:

    IFC element --(this script)--> BOQ Item Ref --(= WBS_Code, real)--> BCC_ID --> 12 periods of EVM

Everything after the first arrow is genuine workbook data. Only the first arrow is a judgement, and
that judgement ships to the UI in the `Basis` column so a QS can disagree with it.

WHAT THIS IS NOT
----------------
The loaded model is a school. The bill is a tower's. Binding one to the other is a demo fixture, and
the mapping is deliberately silent about elements the bill never priced rather than reaching for the
nearest plausible item. See docs/17-ifc-boq-element-map.md.

Parsing is plain regex over the STEP file. No ifcopenshell: the three things needed here (GlobalId,
entity class, storey containment) are trivially addressable, and a build-time native dependency to
read four fields would not pay for itself.

Usage:  python3 tools/ifc_boq_map/generate_map.py
Output: data/ifc_boq_map.csv  (deterministic -- run twice, diff is empty)
"""

from __future__ import annotations

import csv
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
IFC = REPO / "QsEarlyWarning/frontend/qs-early-warning/public/models/school_str.ifc"
OUT = REPO / "data/ifc_boq_map.csv"

# The storey whose name marks substructure. Everything else is treated as above ground.
SUB_LEVEL = "Sub Level"

# Classes we consider for mapping -- the same structural/architectural bulk `ifcMeasure.ts`
# measures, so the coverage percentages the UI reports are over a comparable denominator.
CONSIDERED = [
    "IFCSLAB", "IFCCOLUMN", "IFCBEAM", "IFCWALL", "IFCWALLSTANDARDCASE", "IFCMEMBER",
    "IFCPLATE", "IFCFOOTING", "IFCPILE", "IFCCOVERING", "IFCCURTAINWALL", "IFCREINFORCINGBAR",
    "IFCSTAIR", "IFCRAMP", "IFCROOF",
]

DIRECT = 0.9      # the class itself is the evidence
INFERRED = 0.6    # the storey stands in for a relationship the file does not carry


def rules(ifc_class: str, storey: str | None) -> list[tuple[str, str, str, str, float]]:
    """
    (BoqSec, BoqItemRef, Role, Basis, Confidence) rows for one element.

    A physical element consumes more than one bill item -- a slab is concrete AND its soffit
    formwork AND its rebar -- so this returns a list, and the CSV carries one row per pair. Anything
    the bill does not price returns nothing at all and lands in the scope gap.
    """
    if ifc_class == "IFCCOLUMN":
        return [
            ("2", "2.04", "Concrete", "Column concrete, priced per m3 of column.", DIRECT),
            ("2", "2.09", "Formwork", "Column formwork is implied by the column it forms.", DIRECT),
        ]

    if ifc_class in ("IFCWALL", "IFCWALLSTANDARDCASE"):
        return [
            ("2", "2.05", "Concrete", "Structural wall concrete, priced per m3 of wall.", DIRECT),
            ("2", "2.10", "Formwork", "Wall formwork is implied by the wall it forms.", DIRECT),
        ]

    if ifc_class == "IFCSLAB":
        return [
            ("2", "2.06", "Concrete", "Suspended slab concrete, priced per m3 of slab.", DIRECT),
            ("2", "2.11", "Formwork", "Slab soffit formwork is implied by the slab it forms.", DIRECT),
        ]

    if ifc_class == "IFCREINFORCINGBAR":
        # The bar-to-host relationship does not exist in this file: IfcRelAssignsToProduct and
        # IfcRelNests are both absent, so there is no way to know which column or slab a bar
        # reinforces. Storey is the only signal left, and it is a weaker one -- hence 0.6.
        if storey == SUB_LEVEL:
            return [("2", "2.12", "Rebar",
                     "Bar sits on the substructure level, so it is taken as raft reinforcement. "
                     "The model carries no bar-to-host relationship to confirm it.", INFERRED)]
        return [("2", "2.14", "Rebar",
                 "Bar sits above ground, so it is taken as suspended-slab reinforcement. "
                 "The model carries no bar-to-host relationship to confirm it.", INFERRED)]

    return []


def why_unmapped(ifc_class: str) -> str:
    """Why a class the model contains reaches no bill item. Shown to the QS verbatim."""
    if ifc_class == "IFCBEAM":
        return ("The bill prices no beam concrete -- there is no beam item in any section. "
                "This is scope the model contains and the estimate never priced.")
    if ifc_class in ("IFCMEMBER", "IFCPLATE"):
        return "No item in this bill covers this element class."
    return "No mapping rule declared for this class."


def parse(text: str):
    """(elements, storey_of) from the STEP data section."""
    # #123=IFCCOLUMN('guid',#4,'Name',...)  -- GlobalId is always the first argument.
    element_re = re.compile(r"#(\d+)\s*=\s*(IFC[A-Z0-9]+)\s*\(\s*'([^']*)'", re.I)

    entity_class: dict[str, str] = {}
    guid_of: dict[str, str] = {}
    for m in element_re.finditer(text):
        eid, cls, guid = m.group(1), m.group(2).upper(), m.group(3)
        entity_class[eid] = cls
        guid_of[eid] = guid

    # #9=IFCBUILDINGSTOREY('guid',#4,'01 - Entry Level',...)  -- Name is the third argument.
    storey_name: dict[str, str] = {}
    for m in re.finditer(
        r"#(\d+)\s*=\s*IFCBUILDINGSTOREY\s*\(\s*'[^']*'\s*,\s*[^,]*,\s*'([^']*)'", text, re.I
    ):
        storey_name[m.group(1)] = m.group(2)

    # IFCRELCONTAINEDINSPATIALSTRUCTURE(guid, owner, name, desc, (elements...), storey)
    storey_of: dict[str, str] = {}
    for m in re.finditer(r"IFCRELCONTAINEDINSPATIALSTRUCTURE\s*\((.*?)\)\s*;", text, re.S | re.I):
        body = m.group(1)
        tail = re.search(r"\(([^()]*)\)\s*,\s*#(\d+)\s*$", body.strip())
        if not tail:
            continue
        name = storey_name.get(tail.group(2))
        if not name:
            continue
        for eid in re.findall(r"#(\d+)", tail.group(1)):
            storey_of[eid] = name

    considered = {c for c in CONSIDERED}
    elements = [
        (eid, entity_class[eid], guid_of[eid], storey_of.get(eid))
        for eid in entity_class
        if entity_class[eid] in considered
    ]
    # Sort by GlobalId so the output is stable regardless of dict ordering.
    elements.sort(key=lambda e: (e[2], e[1]))
    return elements


def main() -> int:
    if not IFC.exists():
        print(f"error: model not found at {IFC}", file=sys.stderr)
        return 1

    elements = parse(IFC.read_text(encoding="utf-8", errors="replace"))

    rows = []
    mapped_guids: set[str] = set()
    unmapped = Counter()
    by_item = Counter()
    per_class = defaultdict(Counter)

    for _eid, cls, guid, storey in elements:
        produced = rules(cls, storey)
        if not produced:
            # Unmapped elements are written too. The file is the complete register of what the
            # model contains, so the scope gap travels with the mapping instead of having to be
            # recomputed — and an element the bill cannot price is a finding, not an absence.
            unmapped[cls] += 1
            rows.append({
                "IfcGlobalId": guid,
                "IfcClass": cls,
                "Storey": storey or "",
                "BoqSec": "",
                "BoqItemRef": "",
                "Role": "Unmapped",
                "Basis": why_unmapped(cls),
                "Confidence": "0.0",
            })
            continue
        mapped_guids.add(guid)
        for sec, item, role, basis, conf in produced:
            rows.append({
                "IfcGlobalId": guid,
                "IfcClass": cls,
                "Storey": storey or "",
                "BoqSec": sec,
                "BoqItemRef": item,
                "Role": role,
                "Basis": basis,
                "Confidence": f"{conf:.1f}",
            })
            by_item[item] += 1
            per_class[cls][item] += 1

    OUT.parent.mkdir(parents=True, exist_ok=True)
    with OUT.open("w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=[
            "IfcGlobalId", "IfcClass", "Storey", "BoqSec", "BoqItemRef", "Role", "Basis", "Confidence",
        ])
        w.writeheader()
        w.writerows(rows)

    total = len(elements)
    print(f"model      : {IFC.relative_to(REPO)}")
    print(f"output     : {OUT.relative_to(REPO)}")
    print(f"considered : {total} elements")
    mapped_rows = sum(1 for r in rows if r["BoqItemRef"])
    print(f"mapped     : {len(mapped_guids)} elements ({100*len(mapped_guids)/total:.0f}%) -> {mapped_rows} rows")
    print(f"unmapped   : {sum(unmapped.values())} elements ({100*sum(unmapped.values())/total:.0f}%)")
    print(f"total rows : {len(rows)}")
    print()
    print("rows per BOQ item:")
    for item, n in sorted(by_item.items()):
        print(f"  {item:6} {n:>5}")
    print()
    print("mapped by class:")
    for cls in sorted(per_class):
        items = ", ".join(f"{i}({n})" for i, n in sorted(per_class[cls].items()))
        print(f"  {cls:20} {items}")
    print()
    print("scope gap -- in the model, not in the bill:")
    for cls, n in unmapped.most_common():
        print(f"  {cls:20} {n:>5}  {why_unmapped(cls)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
