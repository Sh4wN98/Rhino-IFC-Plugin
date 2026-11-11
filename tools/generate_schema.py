"""Writes src/RhinoIfc.Core/Schema/ifc-schemas.txt: entity names, supertypes and attribute names of the IFC schemas.

Developer tool only (the plugin does not use Python): it reads the schema definitions that ship with IfcOpenShell
(pip install ifcopenshell) and writes them as plain text. The entity and attribute names are those of the published
buildingSMART IFC schemas.

    python tools/generate_schema.py
"""
import pathlib

import ifcopenshell.ifcopenshell_wrapper as wrapper

SCHEMAS = ["IFC2X3", "IFC4", "IFC4X3_ADD2"]
OUT = pathlib.Path(__file__).resolve().parent.parent / "src" / "RhinoIfc.Core" / "Schema" / "ifc-schemas.txt"

lines = ["# IFC schemas: 'S <name>' starts a schema, then 'entity;supertype;attribute,attribute,...' (all attributes incl. inherited, in order)."]
for name in SCHEMAS:
    schema = wrapper.schema_by_name(name)
    lines.append(f"S {name}")
    for entity in sorted(schema.entities(), key=lambda e: e.name()):
        parent = entity.supertype().name() if entity.supertype() else ""
        attributes = ",".join(a.name() for a in entity.all_attributes())
        lines.append(f"{entity.name()};{parent};{attributes}")
OUT.write_text("\n".join(lines) + "\n", encoding="utf-8")
print(OUT, OUT.stat().st_size)
