# Anleitung: IFC-Dateien in Rhino 8 öffnen

Diese Anleitung erklärt Installation, Bedienung und Fehlerbehebung des Plugins **IFC Import** für
Rhino 8 unter Windows. Einen Überblick über Funktionen und Aufbau gibt das [README](../README.md).

## Inhalt

1. [Voraussetzungen](#1-voraussetzungen)
2. [Installation](#2-installation)
3. [Eine IFC öffnen oder importieren](#3-eine-ifc-öffnen-oder-importieren)
4. [Die Importoptionen](#4-die-importoptionen)
5. [Was in Rhino ankommt](#5-was-in-rhino-ankommt)
6. [Befehle](#6-befehle)
7. [Typische Arbeitsabläufe](#7-typische-arbeitsabläufe)
8. [Update und Deinstallation](#8-update-und-deinstallation)
9. [Fehlerbehebung](#9-fehlerbehebung)
10. [Neue Version veröffentlichen (für Entwickler)](#10-neue-version-veröffentlichen-für-entwickler)

## 1. Voraussetzungen

- **Rhino 8 für Windows** (jede Service-Release-Version).
- **IFC Converter**: Er macht die eigentliche Umwandlung im Hintergrund. Einmal
  `IFC-Converter-Setup.exe` aus den [Converter-Releases](https://github.com/Sh4wN98/IFC-Converter/releases/latest)
  installieren. Python wird dafür nicht gebraucht.

Das Plugin findet den Converter von selbst: über den Registry-Eintrag des Installers,
`%LOCALAPPDATA%\Programs\IFC Converter`, `C:\Programme\IFC Converter` oder den `PATH`.
Alternativ geht ein Python ab 3.10 mit dem Paket `ifc_converter`.

## 2. Installation

Es gibt zwei Wege. **Nur einen davon benutzen**: Beide registrieren dasselbe Plugin, und Rhino kann
nur eine Kopie laden.

### Weg A: Paketmanager (empfohlen)

1. Aus den [Releases](https://github.com/Sh4wN98/Rhino-IFC-Plugin/releases/latest) die Datei
   `rhino-ifc-plugin-<version>-rh8_0-win.yak` herunterladen.
2. Die `.yak`-Datei auf das geöffnete Rhino-Fenster ziehen. Alternativ in Rhino `_PackageManager`
   öffnen und die Datei dort installieren.
3. Rhino neu starten.

Updates und die Deinstallation laufen dann ebenfalls über den Paketmanager.

### Weg B: Von Hand

1. Aus den Releases `RhinoIfc-<version>.zip` herunterladen und entpacken.
2. `RhinoIfc.rhp` und `RhinoIfc.Core.dll` zusammen in einen festen Ordner legen, zum Beispiel
   `%APPDATA%\McNeel\Rhinoceros\8.0\Plug-ins\IFC Import\`. Beide Dateien müssen im selben Ordner liegen.
3. `RhinoIfc.rhp` auf das Rhino-Fenster ziehen. Alternativ `_PlugInManager` › Installieren.
4. Rhino neu starten.

Den Ordner danach nicht verschieben oder löschen: Rhino merkt sich den Pfad zur `.rhp`.

### Prüfen, ob es geklappt hat

In Rhino `IfcConverter` eintippen. Die Befehlszeile zeigt dann zum Beispiel:

```
IFC Converter 0.9.8: IFC Converter (installiert): C:\Users\…\AppData\Local\Programs\IFC Converter\ifc-convert.exe
```

Außerdem steht im Öffnen- und Importieren-Dialog jetzt der Dateityp **IFC (\*.ifc, \*.ifczip)**.

**Das Plugin muss nicht von Hand gestartet werden.** Rhino lädt es automatisch, sobald eine IFC geöffnet
oder einer der `Ifc…`-Befehle benutzt wird.

## 3. Eine IFC öffnen oder importieren

| Weg | Was passiert |
|---|---|
| **Datei › Öffnen** (oder Doppelklick auf eine `.ifc`, wenn sie mit Rhino verknüpft ist) | Die IFC wird ein **neues Dokument**. Es übernimmt die Einheiten der Umwandlung: **Meter**, Toleranz 1 mm. |
| **Datei › Importieren** oder Befehl `IfcImport` | Die IFC kommt ins **aktuelle Dokument**. Rhino rechnet in dessen Einheiten um, z. B. in Millimeter. |
| **IFC-Datei auf das Rhino-Fenster ziehen** | Wie bei anderen Dateitypen: Rhino öffnet oder importiert sie, je nach Auswahl bzw. Einstellung. |

Danach erscheint der Optionsdialog (siehe unten). Die Umwandlung läuft im Hintergrund. Fortschritt und
Meldungen stehen in der Befehlszeile und in der Statusleiste. **Esc bricht ab.**

Am Ende meldet die Befehlszeile zum Beispiel `IFC: Haus.ifc importiert, 412 Bauteile als 415 Objekte.`

Weil die Umwandlung in einem eigenen Prozess läuft, kann ein Absturz in der IFC-Verarbeitung Rhino nicht
mitreißen. Im schlimmsten Fall bricht nur der Import ab.

## 4. Die Importoptionen

Der Dialog **IFC importieren** merkt sich die letzte Wahl.

| Option | Bedeutung |
|---|---|
| **Polyflächen** (Standard) | Bearbeitbare Polyflächen mit echten Öffnungen. Sehr detaillierte Bauteile (über 1000 Dreiecke) und facettierte Rundkörper bleiben Meshes. |
| **Nur Meshes** | Alles als Mesh. Schneller und kleinere Datei, aber nicht als Volumen bearbeitbar. Gut zum Anschauen und für Renderings. |
| **Mehrfach platzierte Typen als Blöcke** | Gleiche Bauteile (Fenster, Türen, Stützen …) werden Blockinstanzen. Spart Speicher. Zum Bearbeiten den Block mit `_ExplodeBlock` auflösen. |
| **Nur Gebäudehülle** | Lässt Innenwände, Möbel und Haustechnik weg. Gut für Umgebungs- und Volumenmodelle. |
| **Geometriekern** | *Standard*: genau (OpenCASCADE, hybrid nur bei großen Netzen). *hybrid*: schneller, kann aber Flächen verlieren. *opencascade*: alles mit OpenCASCADE. Normalerweise auf Standard lassen. |

## 5. Was in Rhino ankommt

- **Layer nach Geschoss und IFC-Klasse**, z. B. `EG::IfcWall` oder `1.OG::IfcWindow`.
  - Elemente ohne Geschoss liegen unter `Ohne Geschoss`.
  - Achsen liegen unter `Achsen`.
  - `IfcSpace`-Layer (Räume) sind beim Start ausgeblendet.
- **Farben und Materialien** aus den IFC-Styles.
- **Benutzertext je Objekt** (Eigenschaften › Attribut-Benutzertext):
  - `IfcGlobalId`, `IfcClass`, `IfcName`, `IfcType`, `IfcObjectType`, `IfcTag`, `IfcStorey`
  - alle Property-Sets und Mengen als `Pset.Eigenschaft`, z. B. `Pset_WallCommon.IsExternal = True` oder
    `BaseQuantities.GrossVolume = 0.8975`
- **Dokument-Benutzertext** (`_DocumentProperties` › Benutzertext): `IfcSourceFile`, `IfcSchema`,
  `IfcProject` und, wenn vorhanden, die Georeferenzierung `IfcMapConversion.*`.

Das Ergebnis ist eine normale Rhino-Geometrie. Speichern als `.3dm` wie gewohnt. **Ein Export zurück nach
IFC ist nicht vorgesehen.**

## 6. Befehle

### `IfcImport`

Importiert eine IFC ins aktuelle Dokument: erst der Dateidialog, dann die Optionen.

Mit Bindestrich (`-IfcImport`) läuft alles über die Befehlszeile, ohne Dialog. Das ist für Skripte,
Makros und Werkzeugleisten-Knöpfe gedacht. Optionen: `Geometry=Polysurfaces|Meshes`, `Blocks=Yes|No`,
`EnvelopeOnly=Yes|No`.

```
-_IfcImport "C:\Projekte\Haus 1.ifc"
-_IfcImport _Geometry=_Meshes _EnvelopeOnly=_Yes "C:\Projekte\Haus 1.ifc"
```

### `IfcSelect`

Wählt Objekte über ihren IFC-Benutzertext aus. Es berücksichtigt nur sichtbare, nicht gesperrte Objekte.

| Option | Auswahl nach | Hinweis |
|---|---|---|
| `Class` | IFC-Klasse | `IfcWall` nimmt `IfcWallStandardCase` und `IfcWallElementedCase` mit. Mit `IfcWall*` gelten Platzhalter. |
| `Storey` | Geschoss | z. B. `EG` oder `1.OG` |
| `Property` | beliebiger Benutzertext-Schlüssel und Wert | z. B. `Pset_WallCommon.IsExternal` mit `True` |

`*` steht für beliebig viele Zeichen, `?` für ein Zeichen. Groß- und Kleinschreibung spielt keine Rolle.
Mehrere Werte trennt man mit `;`.

Interaktiv zeigt der Befehl eine Liste der vorhandenen Werte zum Anklicken. Über die Befehlszeile
(`-IfcSelect`) tippt man die Werte ein:

```
-_IfcSelect _Class IfcWall;IfcSlab
-_IfcSelect _Storey "1.OG"
-_IfcSelect _Property Pset_WallCommon.IsExternal True
```

### `IfcProperties`

Zeigt den IFC-Benutzertext eines Objekts in der Befehlszeile, gruppiert nach Property-Set (`[IFC]`,
`[Pset_WallCommon]`, `[BaseQuantities]` …). Enter ohne Objekt zeigt den IFC-Dokumenttext
(Quelldatei, Schema, Georeferenz). Mit `F2` lässt sich die Befehlszeile vergrößert ansehen.

### `IfcConverter`

Zeigt, welcher IFC Converter benutzt wird, mit Version.

- `Browse`: `ifc-convert.exe` oder ein `python.exe` mit `ifc_converter` von Hand wählen.
- `Search`: wieder automatisch suchen.

## 7. Typische Arbeitsabläufe

**Ein Geschoss allein anzeigen**

```
-_IfcSelect _Storey "EG"
_Isolate
```

Mit `_Unisolate` kommt alles zurück. Alternativ in der Layer-Palette die anderen Geschoss-Layer ausblenden.

**Alle Außenwände auswählen**

```
-_IfcSelect _Property Pset_WallCommon.IsExternal True
```

**Nur ein Volumenmodell der Hülle**

Import mit **Nur Gebäudehülle** und **Nur Meshes**, z. B. für Umgebungsmodelle:

```
-_IfcImport _Geometry=_Meshes _EnvelopeOnly=_Yes "C:\Projekte\Nachbargebäude.ifc"
```

**IFC-Modell in ein bestehendes Projekt einfügen**

Das Projekt öffnen und dann `IfcImport` aufrufen. Die Geometrie wird in die Einheiten des Projekts
umgerechnet. Liegt das IFC-Modell weit vom Ursprung (Landeskoordinaten), steht die Verschiebung im
Dokument-Benutzertext `IfcMapConversion.*`.

**Eigenschaften eines Bauteils nachsehen**

Bauteil anklicken, dann `IfcProperties`. Oder im Eigenschaften-Panel die Seite **Attribut-Benutzertext**.

## 8. Update und Deinstallation

| Installiert über | Update | Deinstallation |
|---|---|---|
| Paketmanager (Weg A) | `_PackageManager` › Installiert › Aktualisieren, oder die neue `.yak` auf Rhino ziehen | `_PackageManager` › Installiert › Deinstallieren |
| Von Hand (Weg B) | Rhino schließen, die beiden Dateien im Ordner durch die neuen ersetzen, Rhino starten | `_PlugInManager` › IFC Import › Deinstallieren, dann den Ordner löschen |

Wer von Weg B auf Weg A wechselt, deinstalliert zuerst die Version von Hand.

## 9. Fehlerbehebung

| Problem | Lösung |
|---|---|
| **„Der IFC Converter wurde nicht gefunden.“** | Converter installieren (siehe Voraussetzungen) oder mit `IfcConverter` › `Browse` den Pfad zu `ifc-convert.exe` angeben. |
| **IFC fehlt im Dateityp-Filter** | `_PlugInManager` öffnen, nach „IFC Import“ suchen und prüfen, ob es aktiviert ist. Sonst die `.rhp` neu installieren und Rhino neu starten. |
| **Umwandlung dauert sehr lange** | Große Modelle brauchen Minuten. Schneller geht es mit **Nur Meshes**, **Nur Gebäudehülle** oder dem Kern *hybrid*. Esc bricht jederzeit ab. |
| **Kein Fortschritt in Prozent** | Converter 0.9.8 und älter melden nur die verstrichene Zeit. Neuere Versionen zeigen Prozent. |
| **Bauteile fehlen oder sind Meshes statt Polyflächen** | Sehr detaillierte und gerundete Bauteile bleiben absichtlich Meshes. Fehlende Flächen: Kern auf *Standard* oder *opencascade* stellen. Warnungen des Converters stehen in der Befehlszeile mit ⚠. |
| **Hinweis „Das Dokument hat schon IFC-Dokumenttext“** | Es wurde bereits eine IFC in dieses Dokument importiert. Die Geometrie kommt trotzdem an, der Dokumenttext der ersten IFC bleibt erhalten. |
| **Modell liegt sehr weit vom Ursprung** | Die IFC nutzt Landeskoordinaten. In Rhino `_Zoom _Extents`. Die Verschiebung steht im Dokument-Benutzertext. |
| **Import bricht mit Fehlermeldung ab** | Die Meldung aus der Befehlszeile (`F2`) kopieren. Zum Gegentest die Datei im IFC Converter selbst umwandeln. Klappt es dort auch nicht, liegt es an der IFC-Datei. |

## 10. Neue Version veröffentlichen (für Entwickler)

Ein Git-Tag `v<version>` startet den Workflow `.github/workflows/release.yml`. Er:

- führt die Tests aus,
- baut das Plugin,
- erzeugt das Yak-Paket und eine zip-Datei,
- legt beides als GitHub-Release an.

Die Version kommt aus dem Tag.

```bash
git tag v0.2.0
git push origin v0.2.0
```

Lokal bauen geht mit `.\build.ps1` (braucht das .NET SDK 8), siehe [README](../README.md#bauen).
