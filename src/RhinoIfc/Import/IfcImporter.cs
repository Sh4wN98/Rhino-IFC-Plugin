using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Environment = System.Environment;
using RhinoIfc.Core;

namespace RhinoIfc.Import
{
    public sealed class ImportResult
    {
        public bool Success { get; set; }
        public bool Cancelled { get; set; }
        public int Objects { get; set; }
        public string Message { get; set; } = "";
    }

    /// <summary>Converts an IFC file with the IFC Converter into a temporary .3dm and brings that into the document:
    /// layers by storey and IFC class, colours and materials, property sets as user text, blocks; the document
    /// user text (source file, schema, georeferencing) separately, because Import does not take it.</summary>
    public static class IfcImporter
    {
        public static ImportResult Import(
            RhinoDoc doc, string ifcPath, ImportOptions options, string configuredConverter, bool openMode, bool interactive)
        {
            var result = new ImportResult();
            if (!File.Exists(ifcPath))
                return Fail(result, $"Datei nicht gefunden: {ifcPath}", interactive);

            var backend = FindConverter(configuredConverter, out var notFound);
            if (backend == null)
                return Fail(result, notFound, interactive);

            var folder = Path.Combine(Path.GetTempPath(), "RhinoIfc", Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(folder);
            var output = Path.Combine(folder, Path.GetFileNameWithoutExtension(ifcPath) + ".3dm");
            try
            {
                RhinoApp.WriteLine($"IFC: {Path.GetFileName(ifcPath)} wird umgewandelt ({backend.Description}). Esc bricht ab.");
                var run = Convert(doc, backend, ifcPath, output, options);
                foreach (var warning in run.Warnings)
                    RhinoApp.WriteLine($"IFC: ⚠ {warning.Text}");
                if (run.Cancelled)
                {
                    result.Cancelled = true;
                    result.Message = "IFC-Import abgebrochen.";
                    RhinoApp.WriteLine(result.Message);
                    return result;
                }
                if (run.ExitCode != 0 || run.Success == null || !File.Exists(output))
                {
                    var error = run.ErrorText();
                    return Fail(result, "Der IFC Converter konnte die Datei nicht umwandeln"
                        + (string.IsNullOrWhiteSpace(error) ? $" (Exitcode {run.ExitCode})." : ":" + Environment.NewLine + error), interactive);
                }

                var added = Merge(doc, output, ifcPath, openMode);
                if (added == null)
                    return Fail(result, "Die umgewandelte Datei konnte nicht in Rhino geladen werden.", interactive);

                bool cancelled = false;
                EventHandler onEscape = (s, e) => cancelled = true;
                RhinoApp.EscapeKeyPressed += onEscape;
                try
                {
                    foreach (var step in ImportPostProcessors.All)
                    {
                        if (cancelled)
                            break;
                        RhinoApp.SetCommandPrompt($"IFC: {step.Name}");
                        step.Process(doc, added, options, () => cancelled);
                    }
                }
                finally
                {
                    RhinoApp.EscapeKeyPressed -= onEscape;
                }

                if (openMode)
                    foreach (var view in doc.Views)
                        view.ActiveViewport.ZoomExtents();
                doc.Views.Redraw();

                result.Success = true;
                result.Objects = added.Count;
                int elements = run.Success.Elements;
                result.Message = $"IFC: {Path.GetFileName(ifcPath)} importiert, "
                    + (elements >= 0 ? $"{elements} Bauteile als {added.Count} Objekte." : $"{added.Count} Objekte.");
                RhinoApp.WriteLine(result.Message);
                return result;
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        public static ConverterBackend FindConverter(string configuredPath, out string notFoundMessage)
        {
            var locator = new ConverterLocator(File.Exists, Environment.GetEnvironmentVariable, InstallLocations,
                ConverterProcess.PythonHasConverter);
            var backend = locator.Find(configuredPath);
            notFoundMessage = backend != null ? "" :
                "Der IFC Converter wurde nicht gefunden. Er wandelt die IFC-Datei für Rhino um und wird einmal installiert:"
                + Environment.NewLine + ConverterLocator.DownloadUrl + Environment.NewLine
                + "(IFC-Converter-Setup.exe). Steht er woanders, mit dem Befehl IfcConverter den Pfad zu ifc-convert.exe wählen."
                + Environment.NewLine + "Gesucht in: " + string.Join("; ", locator.Searched.Distinct());
            return backend;
        }

        private static IEnumerable<string> InstallLocations()
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    string location = null;
                    try
                    {
                        using (var root = RegistryKey.OpenBaseKey(hive, view))
                        using (var key = root.OpenSubKey(ConverterLocator.UninstallKey))
                            location = key?.GetValue("InstallLocation") as string;
                    }
                    catch (Exception exception) when (exception is System.Security.SecurityException
                        || exception is UnauthorizedAccessException || exception is IOException || exception is PlatformNotSupportedException)
                    {
                    }
                    if (!string.IsNullOrWhiteSpace(location))
                        yield return location;
                }
            }
        }

        /// <summary>Runs the converter with progress in the status bar and on the command line; Esc cancels.</summary>
        private static ConverterRun Convert(RhinoDoc doc, ConverterBackend backend, string input, string output, ImportOptions options)
        {
            bool cancelled = false;
            EventHandler onEscape = (s, e) => cancelled = true;
            RhinoApp.EscapeKeyPressed += onEscape;
            bool meter = Rhino.UI.StatusBar.ShowProgressMeter(doc.RuntimeSerialNumber, 0, 100, "IFC", true, true) == 1;
            string status = "IFC wird gelesen …";
            string detail = "";
            int percent = -1;
            var started = DateTime.Now;
            var lastPrompt = DateTime.MinValue;

            void Prompt(bool force)
            {
                // The prompt is rewritten at most a few times a second; the elapsed time shows it is alive.
                if (!force && (DateTime.Now - lastPrompt).TotalMilliseconds < 250)
                    return;
                lastPrompt = DateTime.Now;
                var elapsed = DateTime.Now - started;
                var text = percent >= 0 ? $"{percent} %" + (detail.Length > 0 ? $" – {detail}" : "") : status;
                RhinoApp.SetCommandPrompt($"IFC: {text} ({(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}, Esc bricht ab)");
            }

            try
            {
                Prompt(true);
                return ConverterProcess.Run(
                    backend,
                    ConverterBackend.ConvertArguments(input, output, options),
                    ConverterProcess.Environment(options),
                    line =>
                    {
                        switch (line.Kind)
                        {
                            case OutputKind.Status:
                                status = line.Text;
                                percent = -1;
                                break;
                            case OutputKind.Percent:
                                percent = line.Percent;
                                if (meter)
                                    Rhino.UI.StatusBar.UpdateProgressMeter(doc.RuntimeSerialNumber, percent, true);
                                break;
                            case OutputKind.Phase:
                                detail = IsDetailPhase(line.Phase) ? line.Text : "";
                                break;
                            case OutputKind.Warning:
                                RhinoApp.WriteLine($"IFC: ⚠ {line.Text}");
                                break;
                        }
                        Prompt(false);
                    },
                    () => cancelled,
                    () =>
                    {
                        RhinoApp.Wait();
                        Prompt(false);
                    });
            }
            finally
            {
                RhinoApp.EscapeKeyPressed -= onEscape;
                if (meter)
                    Rhino.UI.StatusBar.HideProgressMeter(doc.RuntimeSerialNumber);
            }
        }

        /// <summary>The converter's DETAIL_PHASES: long phases in which the percentage says little.</summary>
        private static bool IsDetailPhase(string phase) =>
            phase == "envelope" || phase == "fill" || phase == "deep" || phase == "write" || phase == "copy";

        /// <summary>Imports the converted file; returns the ids of the objects added, null if it failed.</summary>
        private static List<Guid> Merge(RhinoDoc doc, string path, string ifcPath, bool openMode)
        {
            RhinoApp.SetCommandPrompt("IFC: Modell wird geladen …");
            // Only settings and document user text; objects and the large tables are left to Import.
            File3dm head = null;
            try
            {
                head = File3dm.Read(path,
                    File3dm.TableTypeFilter.Properties | File3dm.TableTypeFilter.Settings | File3dm.TableTypeFilter.UserTable,
                    File3dm.ObjectTypeFilter.None);
            }
            catch (Exception exception)
            {
                RhinoApp.WriteLine($"IFC: Einstellungen der umgewandelten Datei nicht lesbar ({exception.Message}).");
            }

            using (head)
            {
                if (openMode && head != null)
                {
                    // Opening an IFC is like opening the .3dm the converter writes: its units (metres) and tolerances.
                    doc.AdjustModelUnitSystem(head.Settings.ModelUnitSystem, false);
                    doc.ModelAbsoluteTolerance = head.Settings.ModelAbsoluteTolerance;
                    doc.ModelAngleToleranceDegrees = head.Settings.ModelAngleToleranceDegrees;
                }

                var added = new List<Guid>();
                EventHandler<RhinoObjectEventArgs> onAdd = (s, e) =>
                {
                    if (e.TheObject?.Document?.RuntimeSerialNumber == doc.RuntimeSerialNumber)
                        added.Add(e.ObjectId);
                };
                RhinoDoc.AddRhinoObject += onAdd;
                bool imported;
                try
                {
                    imported = doc.Import(path);
                }
                finally
                {
                    RhinoDoc.AddRhinoObject -= onAdd;
                }
                if (!imported)
                    return null;

                if (head != null)
                    CopyDocumentText(doc, head, openMode, ifcPath);
                // Block definition geometry fires AddRhinoObject too; post-processing works on the model objects.
                return added.Where(id => doc.Objects.FindId(id) is RhinoObject o && !o.IsInstanceDefinitionGeometry).ToList();
            }
        }

        /// <summary>Source file, schema, project and IfcMapConversion as document user text (Rhino: DocumentUserText).
        /// An import into a document that already has IFC text keeps the existing values.</summary>
        private static void CopyDocumentText(RhinoDoc doc, File3dm head, bool openMode, string ifcPath)
        {
            bool keep = !openMode && !string.IsNullOrEmpty(doc.Strings.GetValue("IfcSourceFile"));
            if (keep)
            {
                RhinoApp.WriteLine($"IFC: Das Dokument hat schon IFC-Dokumenttext (IfcSourceFile = {doc.Strings.GetValue("IfcSourceFile")}); "
                    + $"der von {Path.GetFileName(ifcPath)} wird nicht übernommen.");
                return;
            }
            for (int i = 0; i < head.Strings.Count; i++)
            {
                var key = head.Strings.GetKey(i);
                if (!string.IsNullOrEmpty(key))
                    doc.Strings.SetString(key, head.Strings.GetValue(i));
            }
        }

        private static ImportResult Fail(ImportResult result, string message, bool interactive)
        {
            result.Message = message;
            RhinoApp.WriteLine("IFC: " + message);
            if (interactive)
                Rhino.UI.Dialogs.ShowMessage(message, "IFC-Import");
            return result;
        }
    }
}
