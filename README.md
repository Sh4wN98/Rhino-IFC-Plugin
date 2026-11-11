# Rhino IFC Plugin

Ein Plugin für **Rhino 8 (Windows)**, das IFC-Dateien (`.ifc`, `.ifczip`) **direkt** über **Datei › Öffnen**
und **Datei › Importieren** einliest, ohne Umwandlung und ohne zusätzliche Programme. Das Plugin hat einen
eigenen IFC-Leser und baut die Geometrie selbst mit RhinoCommon.

Was in Rhino ankommt:

- **Polyflächen** (Extrusionen bleiben Rhino-Extrusionen, Öffnungen werden echt ausgeschnitten) oder auf
  Wunsch nur **Meshes**. Facettierte Körper werden zu Polyflächen mit zusammengefassten Flächen; sehr
  detaillierte Netze (über 2000 Dreiecke) bleiben Meshes.
- **Layer nach Geschoss und IFC-Klasse** (`EG::IfcWall`, `1.OG::IfcWindow` …), sortiert nach Geschosshöhe,
  Elemente ohne Geschoss unter `Ohne Geschoss`. `IfcSpace`-Layer starten ausgeblendet.
- **Farben und Materialien** aus den IFC-Styles (Oberflächenfarbe, Transparenz), sonst aus dem IFC-Material,
  sonst eine Standardfarbe je Klasse.
- **Alle Property-Sets und Mengen als Benutzertext** (Attribute User Text) jedes Objekts:
  `IfcGlobalId`, `IfcClass`, `IfcName`, `IfcType`, `IfcStorey`, `IfcMaterial`, `Pset_WallCommon.IsExternal` usw.
  Bauteile aus mehreren Objekten werden gruppiert.
- **Dokument-Benutzertext**: Quelldatei, Schema, Projekt, Einheit und Georeferenzierung (`IfcMapConversion.*`).
- Optional **Blöcke** für Typen, die über `IfcRepresentationMap` mehrfach platziert sind (Blockname = Typname).
- IFC2X3, IFC4 und IFC4X3, auch `.ifczip`.

**Ausführliche Anleitung** (Installation, Optionen, Befehle, Grenzen, Fehlerbehebung):
[docs/ANLEITUNG.md](docs/ANLEITUNG.md)

## Wie es funktioniert

1. **Index** (`StepFile`): Die Datei wird einmal in großen Blöcken gelesen und nur die Lage jedes Eintrags
   (`#id`, Typ) gemerkt. Die Datei selbst bleibt per Memory-Mapping auf der Platte; Einträge werden erst beim
   Zugriff geparst. So bleiben auch GB-große Dateien im Speicher klein.
2. **Modell** (`IfcModel`): Einheiten, Geschossstruktur, Beziehungen (enthalten in, Aggregation, Typ,
   Property-Sets, Material, Öffnungen) und Styles.
3. **Geometrie** (`ShapeBuilder`): Platzierungen und Darstellungen werden in eine neutrale Beschreibung
   übersetzt (Profile, Extrusionen, Rotationen, Rohre, Netze, Boolesche Operationen, Halbräume, Mapped Items).
   Dieser Teil kennt Rhino nicht und ist mit Tests und einem Vergleichswerkzeug abgesichert.
4. **Rhino** (`RhinoGeometry`, `DocumentWriter`): Aus der Beschreibung entstehen Rhino-Objekte. Die Bauteile
   werden parallel gebaut, ins Dokument geschrieben wird auf dem Hauptthread. Gebaut wird in lokalen
   Koordinaten des Bauteils und erst danach verschoben, damit Modelle in Landeskoordinaten genau bleiben.

Fortschritt steht in der Statusleiste, **Esc bricht ab**.

## Installation

Aus einem Release ([Releases](https://github.com/Sh4wN98/Rhino-IFC-Plugin/releases/latest)), eine der beiden
Arten (nicht beide):

- **Yak-Paket** (empfohlen): `rhino-ifc-plugin-….yak` auf das Rhino-Fenster ziehen, oder in Rhino
  `_PackageManager` › Datei installieren.
- **Direkt**: `RhinoIfc-….zip` in einen festen Ordner entpacken und `RhinoIfc.rhp` auf das Rhino-Fenster
  ziehen (oder `_PlugInManager` › Installieren). `RhinoIfc.Core.dll` muss im selben Ordner wie die `.rhp` liegen.

Zum Testen eines Entwicklungsstands ohne Release: beim Workflow-Lauf **build** des Branches unter
*Actions* das Artefakt `RhinoIfc-plugin` herunterladen, entpacken und wie bei „Direkt“ installieren.

Danach Rhino neu starten. **IFC (\*.ifc, \*.ifczip)** steht dann in den Dateitypen von Öffnen und Importieren.

## Benutzung

| Befehl | Was er macht |
|---|---|
| **Öffnen / Importieren** | IFC wie jede andere Datei öffnen. Ein Dialog fragt Geometrie (Polyflächen/Meshes), Öffnungen ausschneiden, Blöcke und Räume; die Wahl wird gemerkt. Beim **Öffnen** übernimmt das Dokument die Längeneinheit der IFC (Toleranz 0,1 mm), beim **Importieren** wird in die Einheiten des Dokuments umgerechnet. |
| `IfcImport` | IFC in das aktuelle Dokument importieren. `-IfcImport` nimmt Pfad und Optionen `Geometry=Polysurfaces/Meshes`, `Openings`, `Blocks`, `Spaces` auf der Befehlszeile, für Skripte und Makros. |
| `IfcSelect` | Objekte auswählen nach `Class` (IFC-Klasse; `IfcWall` nimmt `IfcWallStandardCase` mit), `Storey` (Geschoss) oder `Property` (beliebiger Benutzertext-Schlüssel, Wert mit `*` und `?`). |
| `IfcProperties` | Benutzertext eines Objekts nach Property-Set gruppiert; Enter ohne Objekt zeigt den IFC-Dokumenttext. |

```
-_IfcImport _Geometry=_Meshes _Spaces=_No "C:\Projekte\Haus 1.ifc"
```

## Genauigkeit

`tools/RhinoIfc.Verify` liest eine IFC mit demselben Kern wie das Plugin, trianguliert jedes Bauteil und schreibt
Bounding-Box und Volumen je Bauteil (in Metern) als JSON-Zeilen. Abgeglichen gegen eine Referenz aus
IfcOpenShell über rund 550 öffentliche Testmodelle (knapp 37.000 Bauteile) stimmen 99,6 % der Bounding-Boxen
(Abweichung höchstens 2 mm bzw. 0,5 % der Bauteilgröße) und 98 % der Volumen (höchstens 1 %). Die Abweichungen betreffen vor allem nicht unterstützte Sonderfälle (siehe Anleitung,
Abschnitt Grenzen). Booleans (Öffnungen, Halbräume) prüft das Werkzeug ohne Rhino nur näherungsweise.

```
dotnet run --project tools/RhinoIfc.Verify -c Release -- Haus.ifc ergebnis.jsonl
```

Eine 1,1-GB-Datei (17 Mio. Einträge, 86.000 Bauteile) braucht im Kern rund 5 s für den Index, 2 s fürs Modell
und 13 s für die Geometrie bei etwa 2,4 GB Speicher. Die Rhino-Objekte zu erzeugen dauert länger, vor allem
im Polyflächen-Modus mit Öffnungen.

## Bauen

Voraussetzung: .NET SDK 8 (baut auf Windows und Linux; RhinoCommon kommt aus NuGet).

```powershell
.\build.ps1                              # Tests, Release-Build, dist\ und – wenn Yak.exe gefunden wird – das .yak
```

oder von Hand:

```
dotnet test tests/RhinoIfc.Core.Tests
dotnet build src/RhinoIfc/RhinoIfc.csproj -c Release
```

Ergebnis: `src/RhinoIfc/bin/Release/net48/RhinoIfc.rhp` und `RhinoIfc.Core.dll`. Das Plugin ist für
.NET Framework 4.8 gebaut und läuft damit in beiden Laufzeiten von Rhino 8.

**Release**: Ein Tag `v1.2.3` baut über `.github/workflows/release.yml` das Yak-Paket und eine zip-Datei
und legt beides als GitHub-Release an.

## Aufbau

| Pfad | Inhalt |
|---|---|
| `src/RhinoIfc.Core/Step/` | STEP-Datei: Index, Memory-Mapping, Parser, Zeichenketten (`\X2\` …), `.ifczip`. |
| `src/RhinoIfc.Core/Schema/` | Entitäten und Attributnamen von IFC2X3, IFC4 und IFC4X3 (`ifc-schemas.txt`). |
| `src/RhinoIfc.Core/Model/` | `IfcModel`: Einheiten, Geschosse, Beziehungen, Benutzertext, Farben, Platzierungen. |
| `src/RhinoIfc.Core/Geometry/` | Profile, Kurven, Körper (`ShapeBuilder`, `CurveBuilder`, `MeshBuilder`), Triangulierung und `Tessellator` für die Prüfung. |
| `src/RhinoIfc/Import/` | `IfcImporter` (Ablauf, parallel), `RhinoGeometry` (Rhino-Geometrie), `DocumentWriter` (Layer, Materialien, Blöcke). |
| `src/RhinoIfc/Commands/` | `IfcImport`, `IfcSelect`, `IfcProperties`. |
| `tests/RhinoIfc.Core.Tests/` | Tests des Kerns mit einer kleinen IFC-Datei. |
| `tools/RhinoIfc.Verify/` | Vergleichswerkzeug (siehe Genauigkeit). |
| `tools/generate_schema.py` | Erzeugt `ifc-schemas.txt` neu (nur für Entwickler, braucht `ifcopenshell`). |

## Lizenz

GPL-3.0-or-later (`LICENSE`). Das Plugin hat **keine fremden Abhängigkeiten** außer RhinoCommon (wird von Rhino
selbst bereitgestellt) und, nur für die Tests, xUnit. Die Schemadatei enthält nur Entitäts- und Attributnamen
der offenen IFC-Standards von buildingSMART. `ifcopenshell` (LGPL) wird nur vom Entwicklerwerkzeug
`generate_schema.py` benutzt und nicht mitgeliefert.
