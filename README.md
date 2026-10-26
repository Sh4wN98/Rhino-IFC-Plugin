# Rhino IFC Plugin

Ein Plugin für **Rhino 8 (Windows)**, das IFC-Dateien (`.ifc`, `.ifczip`) direkt über **Datei › Öffnen**
und **Datei › Importieren** einliest, damit man sie in Rhino weiterbearbeiten und als `.3dm` speichern kann.
Ein Rückweg nach IFC ist nicht vorgesehen.

Was in Rhino ankommt:

- **Polyflächen** (bearbeitbar, mit echten Öffnungen) oder auf Wunsch nur **Meshes**; sehr detaillierte
  Bauteile und facettierte Rundkörper bleiben auch im Polyflächen-Modus Meshes.
- **Layer nach Geschoss und IFC-Klasse** (`EG::IfcWall`, `1.OG::IfcWindow` …), Elemente ohne Geschoss
  unter `Ohne Geschoss`, Achsen unter `Achsen`; `IfcSpace`-Layer starten ausgeblendet.
- **Farben und Materialien** aus den IFC-Styles.
- **Alle Property-Sets und Standardmengen als Benutzertext** (Attribute User Text) jedes Objekts:
  `IfcGlobalId`, `IfcClass`, `IfcName`, `IfcType`, `IfcStorey`, `Pset_WallCommon.IsExternal` usw.
- **Dokument-Benutzertext**: Quelldatei, Schema, Projekt und Georeferenzierung (`IfcMapConversion.*`).
- Optional **Blöcke** für mehrfach platzierte Typen und **nur die Gebäudehülle**.

## Wie es funktioniert

Das Plugin baut die Geometrie nicht selbst, sondern nutzt den
[IFC Converter](https://github.com/Sh4wN98/IFC-Converter) als Backend: Es startet `ifc-convert.exe`
im Hintergrund, der die IFC in eine temporäre `.3dm` umwandelt, und importiert diese ins aktive Dokument.
So steckt die ganze ausgereifte Logik des Converters (Polyflächen verschweißen, koplanare Flächen mit Löchern
zusammenfassen, Innen-nach-außen-Korrektur, Benutzertext mit Einheiten) auch im Plugin, und ein Absturz oder
Hänger von IfcOpenShell trifft nur den Hilfsprozess, nie Rhino.

Fortschritt steht in der Befehlszeile und in der Statusleiste, **Esc bricht ab**.

## Installation

1. **IFC Converter installieren** (einmalig): `IFC-Converter-Setup.exe` aus den
   [Releases](https://github.com/Sh4wN98/IFC-Converter/releases/latest). Das Plugin findet ihn von selbst
   (Registry-Eintrag des Installers, `%LOCALAPPDATA%\Programs\IFC Converter`, `Programme\IFC Converter`, `PATH`).
   Alternativ geht ein Python ≥ 3.10 mit dem Paket `ifc_converter` (`pip install` aus dem Converter-Repo).
   Für den Fortschritt in Prozent braucht es einen Converter mit `IFC_CONVERTER_PROGRESS`-Unterstützung
   (ab der Version nach 0.9.8); ältere Versionen funktionieren auch, zeigen aber nur die verstrichene Zeit.
2. **Plugin installieren**, eine der beiden Arten:
   - **Yak-Paket**: `rhino-ifc-plugin-….yak` auf das Rhino-Fenster ziehen, oder in Rhino
     `_PackageManager` › Datei installieren.
   - **Direkt**: `RhinoIfc.rhp` auf das Rhino-Fenster ziehen (oder `_PluginManager` › Installieren).
     `RhinoIfc.Core.dll` muss im selben Ordner wie die `.rhp` liegen.
3. Rhino neu starten. Danach steht **IFC (\*.ifc, \*.ifczip)** in den Dateitypen von Öffnen und Importieren.

## Benutzung

| Befehl | Was er macht |
|---|---|
| **Öffnen / Importieren** | IFC wie jede andere Datei öffnen. Ein Dialog fragt Geometrie (Polyflächen/Meshes), Blöcke, Nur Gebäudehülle und den Geometriekern; die Wahl wird gemerkt. Beim **Öffnen** übernimmt das Dokument die Einheiten der IFC-Umwandlung (Meter, Toleranz 1 mm), beim **Importieren** rechnet Rhino in die Einheiten des Dokuments um. |
| `IfcImport` | IFC in das aktuelle Dokument importieren (Dateidialog, dann Optionen). `-IfcImport` nimmt Pfad und Optionen `Geometry=Polysurfaces/Meshes`, `Blocks`, `EnvelopeOnly` auf der Befehlszeile, für Skripte und Makros. |
| `IfcSelect` | Objekte auswählen nach `Class` (IFC-Klasse), `Storey` (Geschoss) oder `Property` (beliebiger Benutzertext-Schlüssel, Wert mit `*` und `?` als Platzhalter). Zum Isolieren danach Rhinos `Isolate` oder `Hide`. |
| `IfcProperties` | Benutzertext eines Objekts nach Property-Set gruppiert in der Befehlszeile; Enter ohne Objekt zeigt den IFC-Dokumenttext (Quelle, Schema, Georeferenz). |
| `IfcConverter` | Zeigt, welcher IFC Converter benutzt wird (mit Version); `Browse` wählt `ifc-convert.exe` oder `python.exe` von Hand, `Search` sucht wieder automatisch. |

Ein Skript-Beispiel:

```
-_IfcImport Geometry=Meshes EnvelopeOnly=Yes "C:\Projekte\Haus 1.ifc"
```

## Bauen

Voraussetzung: .NET SDK 8 (baut auf Windows und Linux; RhinoCommon kommt aus NuGet).

```powershell
.\build.ps1                              # Tests, Release-Build, dist\ und – wenn Yak.exe gefunden wird – das .yak
.\build.ps1 -Yak "E:\Rhino\System\Yak.exe"
```

oder von Hand:

```
dotnet test tests/RhinoIfc.Core.Tests
dotnet build src/RhinoIfc/RhinoIfc.csproj -c Release
```

Ergebnis: `src/RhinoIfc/bin/Release/net48/RhinoIfc.rhp` und `RhinoIfc.Core.dll`.
Das Plugin ist für .NET Framework 4.8 gebaut und läuft damit in beiden Laufzeiten von Rhino 8
(.NET Core, Standard, und .NET Framework).

## Aufbau

| Pfad | Inhalt |
|---|---|
| `src/RhinoIfc.Core/` | Alles ohne Rhino, mit Tests: Converter finden (`ConverterLocator`), Befehlszeile (`ConverterBackend`), Hilfsprozess mit Abbruch (`ConverterProcess`), Ausgabe lesen (`OutputLine`). |
| `src/RhinoIfc/IfcPlugIn.cs` | Das `FileImportPlugIn`: Dateitypen für Öffnen/Importieren. |
| `src/RhinoIfc/Import/IfcImporter.cs` | Umwandeln, `.3dm` importieren, Einheiten beim Öffnen, Dokument-Benutzertext, Nachbearbeitung. |
| `src/RhinoIfc/Import/IImportPostProcessor.cs` | Erweiterungspunkt für Schritte nach dem Import, z. B. **Meshes in Polyflächen umwandeln** (kommt später; bekommt die Ids der importierten Objekte samt IFC-Benutzertext). |
| `src/RhinoIfc/Commands/` | `IfcImport`, `IfcSelect`, `IfcProperties`, `IfcConverter`. |
| `yak/manifest.yml`, `build.ps1` | Yak-Paket. |

### Fortschritt aus dem Converter

Der Converter zeigt seinen Fortschritt nur in einem Terminal. Das Plugin setzt `IFC_CONVERTER_PROGRESS=lines`;
ein Converter, der das kennt, schreibt dann Zeilen wie `@status IFC wird gelesen …`, `@percent 42` und
`@phase write Rhino-Datei wird geschrieben …`. Ältere Converter ignorieren die Variable.

## Lizenz

GPL-3.0-or-later, wie der IFC Converter (`LICENSE`).
