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
   [Grenzen](#grenzen)
10. [Neue Version veröffentlichen (für Entwickler)](#10-neue-version-veröffentlichen-für-entwickler)

## 1. Voraussetzungen

- **Rhino 8 für Windows** (jede Service-Release-Version).

Sonst nichts: Das Plugin liest IFC-Dateien selbst. Der IFC Converter oder Python werden **nicht** gebraucht.

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

### Weg C: Entwicklungsstand testen

1. Auf GitHub unter **Actions** › **build** den Lauf des gewünschten Branches öffnen.
2. Unten das Artefakt **RhinoIfc-plugin** herunterladen und entpacken.
3. Weiter wie bei Weg B (beide DLLs in einen Ordner, `.rhp` auf Rhino ziehen). Eine vorher installierte
   Version zuerst deinstallieren.

### Prüfen, ob es geklappt hat

Im Öffnen- und Importieren-Dialog steht jetzt der Dateityp **IFC (\*.ifc, \*.ifczip)**, und der Befehl
`IfcImport` ist bekannt.

**Das Plugin muss nicht von Hand gestartet werden.** Rhino lädt es automatisch, sobald eine IFC geöffnet
oder einer der `Ifc…`-Befehle benutzt wird.

## 3. Eine IFC öffnen oder importieren

| Weg | Was passiert |
|---|---|
| **Datei › Öffnen** (oder Doppelklick auf eine `.ifc`, wenn sie mit Rhino verknüpft ist) | Die IFC wird ein **neues Dokument**. Es übernimmt die Längeneinheit der IFC (meist Millimeter oder Meter), Toleranz 0,1 mm. |
| **Datei › Importieren** oder Befehl `IfcImport` | Die IFC kommt ins **aktuelle Dokument**. Rhino rechnet in dessen Einheiten um, z. B. in Millimeter. |
| **IFC-Datei auf das Rhino-Fenster ziehen** | Wie bei anderen Dateitypen: Rhino öffnet oder importiert sie, je nach Auswahl bzw. Einstellung. |

Danach erscheint der Optionsdialog (siehe unten). Das Lesen läuft in drei Schritten: Index der Datei,
Modellstruktur, Geometrie. Die Geometrie wird auf allen Prozessorkernen gebaut. Fortschritt steht in der
Statusleiste, Meldungen in der Befehlszeile. **Esc bricht ab**; was bis dahin gebaut ist, bleibt im Dokument.

Am Ende meldet die Befehlszeile, wie viele Bauteile als wie viele Objekte angekommen sind, und listet
Auffälligkeiten: Bauteile ohne lesbare Geometrie, nicht unterstützte Geometrietypen und Hinweise wie
„Öffnung nicht ausgeschnitten“. Die betroffenen Objekte tragen den Hinweis im Benutzertext `IfcImportNote`,
man findet sie mit `-_IfcSelect _Property IfcImportNote *`.

## 4. Die Importoptionen

Der Dialog **IFC importieren** merkt sich die letzte Wahl.

| Option | Bedeutung |
|---|---|
| **Polyflächen** (Standard) | Bearbeitbare Körper: Extrusionen, Polyflächen, Rotationskörper, Rohre. Facettierte Körper werden zu Polyflächen mit zusammengefassten ebenen Flächen. Sehr detaillierte Netze (über 2000 Dreiecke) bleiben Meshes. |
| **Nur Meshes** | Alles als Mesh. Schneller und kleinere Datei, aber nicht als Volumen bearbeitbar. Gut zum Anschauen und für sehr große Modelle. |
| **Öffnungen ausschneiden** (Standard an) | Wände und Decken bekommen echte Löcher für Fenster und Türen (`IfcOpeningElement`). Aus: schneller, die Bauteile bleiben geschlossen. |
| **Mehrfach platzierte Typen als Blöcke** | Bauteile, deren Geometrie aus einem Typ kommt (`IfcRepresentationMap`, z. B. Fenster, Türen, Möbel) und die keine Öffnungen haben, werden Blockinstanzen. Der Block heißt wie der Typ. Spart Speicher. Zum Bearbeiten `_ExplodeBlock`. |
| **Räume importieren** (Standard an) | `IfcSpace` als Körper auf ausgeblendeten Layern. |

## 5. Was in Rhino ankommt

- **Layer nach Geschoss und IFC-Klasse**, z. B. `EG::IfcWall` oder `1.OG::IfcWindow`.
  - Geschosse sind nach Höhe sortiert. Elemente ohne Geschoss liegen unter `Ohne Geschoss`.
  - `IfcSpace`-Layer (Räume) sind beim Start ausgeblendet.
  - Blockdefinitionen liegen auf `IFC Blockdefinitionen`.
- **Farben und Materialien** aus den IFC-Styles (mit Transparenz für Glas), sonst aus dem Material, sonst eine
  Standardfarbe je Klasse.
- **Gruppen**: Besteht ein Bauteil aus mehreren Rhino-Objekten, sind sie zu einer Gruppe mit der GlobalId
  zusammengefasst.
- **Benutzertext je Objekt** (Eigenschaften › Attribut-Benutzertext):
  - `IfcGlobalId`, `IfcClass`, `IfcName`, `IfcDescription`, `IfcType`, `IfcTypeClass`, `IfcPredefinedType`,
    `IfcObjectType`, `IfcTag`, `IfcStorey`, `IfcMaterial`
  - alle Property-Sets und Mengen als `Pset.Eigenschaft`, z. B. `Pset_WallCommon.IsExternal = True` oder
    `BaseQuantities.GrossVolume = 0.8975`
- **Dokument-Benutzertext** (`_DocumentProperties` › Benutzertext): `IfcSourceFile`, `IfcSchema`,
  `IfcProject`, `IfcOriginatingSystem`, `IfcLengthUnit` und, wenn vorhanden, die Georeferenzierung
  `IfcMapConversion.*` und `IfcSite.Ref*`.
- **Koordinaten**: wie in der IFC, ohne Verschiebung. Modelle in Landeskoordinaten liegen also weit vom
  Ursprung; die Geometrie wird trotzdem genau gebaut (erst lokal, dann verschoben).

Das Ergebnis ist eine normale Rhino-Geometrie. Speichern als `.3dm` wie gewohnt. **Ein Export zurück nach
IFC ist nicht vorgesehen.**

## 6. Befehle

### `IfcImport`

Importiert eine IFC ins aktuelle Dokument: erst der Dateidialog, dann die Optionen.

Mit Bindestrich (`-IfcImport`) läuft alles über die Befehlszeile, ohne Dialog. Das ist für Skripte,
Makros und Werkzeugleisten-Knöpfe gedacht. Optionen: `Geometry=Polysurfaces|Meshes`, `Openings=Yes|No`,
`Blocks=Yes|No`, `Spaces=Yes|No`.

```
-_IfcImport "C:\Projekte\Haus 1.ifc"
-_IfcImport _Geometry=_Meshes _Openings=_No _Spaces=_No "C:\Projekte\Haus 1.ifc"
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

**Sehr große Modelle schnell ansehen**

Import mit **Nur Meshes**, ohne Öffnungen und ohne Räume:

```
-_IfcImport _Geometry=_Meshes _Openings=_No _Spaces=_No "C:\Projekte\Gesamtmodell.ifc"
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
| **IFC fehlt im Dateityp-Filter** | `_PlugInManager` öffnen, nach „IFC Import“ suchen und prüfen, ob es aktiviert ist. Sonst die `.rhp` neu installieren und Rhino neu starten. `RhinoIfc.Core.dll` muss neben der `.rhp` liegen. |
| **Import dauert sehr lange** | Meist sind es die Öffnungen (Boolesche Operationen) im Polyflächen-Modus. Schneller: **Nur Meshes** oder **Öffnungen ausschneiden** aus. Esc bricht jederzeit ab. |
| **Öffnung nicht ausgeschnitten** | Rhinos Boolean ist an dem Bauteil gescheitert; das Bauteil kommt geschlossen an und trägt `IfcImportNote`. Auswahl: `-_IfcSelect _Property IfcImportNote *`. |
| **Bauteile fehlen** | Die Befehlszeile listet „Nicht unterstützte Geometrie“ mit Typnamen (siehe Grenzen) und Bauteile ohne lesbare Körpergeometrie. |
| **Hinweis „Das Dokument hat schon IFC-Dokumenttext“** | Es wurde bereits eine IFC in dieses Dokument importiert. Die Geometrie kommt trotzdem an, der Dokumenttext der ersten IFC bleibt erhalten. |
| **Modell liegt sehr weit vom Ursprung** | Die IFC nutzt Landeskoordinaten. In Rhino `_Zoom _Extents`. Die Georeferenz steht im Dokument-Benutzertext. |
| **Import bricht mit Fehlermeldung ab** | Die Meldung aus der Befehlszeile (`F2`) kopieren und zusammen mit der Information, welche Datei es war, melden. |

## Grenzen

Nicht oder nur teilweise unterstützt (die Befehlszeile nennt sie beim Import):

- `IfcSurfaceCurveSweptAreaSolid`, `IfcFixedReferenceSweptAreaSolid`, `IfcSectionedSolidHorizontal` und
  andere Infrastruktur-Körper aus IFC4X3 (Trassen) sowie Achsen und `IfcLine` als Leitkurve.
- `IfcAdvancedBrep` mit gekrümmten Flächen außer B-Spline-Flächen (andere gekrümmte Flächen werden über ihre
  Randkurven angenähert).
- `IfcArbitraryOpenProfileDef` (offene Profile, z. B. für Bleche) und `IfcGeometricSet` (Kurven/Punkte).
- Bei `IfcSweptDiskSolid` werden `StartParam`/`EndParam` nicht ausgewertet (das ganze Leitkurvenstück wird
  benutzt).
- Kein Export zurück nach IFC.

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
