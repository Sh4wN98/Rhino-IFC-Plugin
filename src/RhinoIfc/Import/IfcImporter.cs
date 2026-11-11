using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoIfc.Core;
using RhinoIfc.Core.Geometry;
using RhinoIfc.Core.Model;
using RhinoIfc.Core.Step;

namespace RhinoIfc.Import
{
    public sealed class ImportResult
    {
        public bool Success { get; set; }
        public bool Cancelled { get; set; }
        public int Elements { get; set; }
        public int Objects { get; set; }
        public string Message { get; set; } = "";
    }

    /// <summary>Reads an IFC file directly (no conversion, no external program) and adds its elements to the document:
    /// layers "storey::IFC class", Extrusions/polysurfaces/meshes with IFC colours, openings cut out, property sets as
    /// user text, type geometry as blocks if wanted. The geometry is built on several cores; the objects are added on
    /// Rhino's main thread. Esc cancels.</summary>
    public static class IfcImporter
    {
        private const long MaxIfcZipBytes = 64L << 30;

        public static ImportResult Import(RhinoDoc doc, string ifcPath, ImportOptions options, bool openMode, bool interactive)
        {
            var result = new ImportResult();
            if (!File.Exists(ifcPath))
                return Fail(result, $"Datei nicht gefunden: {ifcPath}", interactive);

            var clock = Stopwatch.StartNew();
            var cancel = new CancellationTokenSource();
            EventHandler onEscape = (s, e) => cancel.Cancel();
            RhinoApp.EscapeKeyPressed += onEscape;
            string tempFolder = null;
            StepFile file = null;
            bool redraw = doc.Views.RedrawEnabled;
            try
            {
                var name = Path.GetFileName(ifcPath);
                RhinoApp.WriteLine($"IFC: {name} wird gelesen. Esc bricht ab.");
                string readPath = ifcPath;
                if (IfcZip.IsZip(ifcPath))
                {
                    tempFolder = Path.Combine(Path.GetTempPath(), "RhinoIfc", Guid.NewGuid().ToString("N").Substring(0, 12));
                    var progress = new Progress();
                    readPath = Run("IFC entpacken", progress,
                        token => IfcZip.Extract(ifcPath, tempFolder, MaxIfcZipBytes, progress.Set, token), cancel.Token);
                }

                var indexProgress = new Progress();
                file = Run("IFC lesen", indexProgress, token => StepFile.Open(readPath, indexProgress.Set, token), cancel.Token);
                var model = Run("IFC-Struktur", new Progress(), token => IfcModel.Load(file, options, token), cancel.Token);
                RhinoApp.WriteLine($"IFC: {file.SchemaIdentifier}, {file.EntityCount:N0} Einträge, {model.Products.Count:N0} Bauteile mit Geometrie, "
                    + $"Längeneinheit {model.LengthUnitName} ({clock.Elapsed.TotalSeconds:0.0} s).");

                // Units: opening takes the IFC's unit; importing converts into the document's.
                double scale;
                if (openMode)
                {
                    var unit = UnitOf(model.LengthToMetres, out var exact);
                    doc.AdjustModelUnitSystem(unit, false);
                    scale = exact ? 1.0 : model.LengthToMetres / RhinoMath.UnitScale(unit, UnitSystem.Meters);
                    doc.ModelAbsoluteTolerance = 1e-4 * RhinoMath.UnitScale(UnitSystem.Meters, unit);
                    doc.ModelAngleToleranceRadians = RhinoMath.ToRadians(1.0);
                }
                else
                    scale = model.LengthToMetres * RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
                double tolerance = doc.ModelAbsoluteTolerance / scale; // in file units

                doc.Views.RedrawEnabled = false;
                var writer = new DocumentWriter(doc, model, scale);
                writer.CreateStoreyLayers();
                writer.WriteDocumentText(ifcPath, openMode);

                var stats = Build(doc, model, options, tolerance, writer, cancel.Token);
                result.Elements = stats.Elements;
                result.Objects = stats.Objects;

                if (openMode)
                    foreach (var view in doc.Views)
                        view.ActiveViewport.ZoomExtents();
                doc.Views.RedrawEnabled = redraw;
                doc.Views.Redraw();

                result.Success = true;
                result.Message = $"IFC: {name} importiert: {stats.Elements:N0} Bauteile als {stats.Objects:N0} Objekte in {clock.Elapsed.TotalSeconds:0.0} s.";
                RhinoApp.WriteLine(result.Message);
                stats.Report();
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                result.Message = "IFC-Import abgebrochen; was schon importiert war, bleibt im Dokument.";
                RhinoApp.WriteLine(result.Message);
                return result;
            }
            catch (Exception exception) when (exception is IOException || exception is InvalidDataException || exception is UnauthorizedAccessException)
            {
                return Fail(result, "Die IFC-Datei konnte nicht gelesen werden: " + exception.Message, interactive);
            }
            finally
            {
                RhinoApp.EscapeKeyPressed -= onEscape;
                doc.Views.RedrawEnabled = redraw;
                RhinoApp.SetCommandPrompt("Befehl");
                file?.Dispose();
                if (tempFolder != null)
                {
                    try
                    {
                        Directory.Delete(tempFolder, true);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        private static UnitSystem UnitOf(double metres, out bool exact)
        {
            exact = true;
            var units = new[]
            {
                UnitSystem.Millimeters, UnitSystem.Centimeters, UnitSystem.Decimeters, UnitSystem.Meters, UnitSystem.Kilometers,
                UnitSystem.Inches, UnitSystem.Feet, UnitSystem.Yards, UnitSystem.Microns,
            };
            foreach (var unit in units)
                if (Math.Abs(RhinoMath.UnitScale(unit, UnitSystem.Meters) / metres - 1) < 1e-6)
                    return unit;
            exact = false;
            return UnitSystem.Meters;
        }

        // ------------------------------------------------------------------ geometry, in parallel

        private sealed class ElementResult
        {
            public int Id;
            public List<BuiltGeometry> Geometry = new List<BuiltGeometry>();
            public List<BlockInstance> Instances = new List<BlockInstance>();
            public List<KeyValuePair<string, string>> UserText;
            public string Layer;
            public bool Hidden;
            public string Name;
            public List<string> Problems = new List<string>();
            public string Error;
        }

        private sealed class BlockInstance
        {
            public int MapId;
            public Lazy<BlockGeometry> Definition;
            public Transform Xform;
        }

        /// <summary>The geometry of a block definition (type geometry), built once per IfcRepresentationMap.</summary>
        public sealed class BlockGeometry
        {
            public string Name;
            public List<BuiltGeometry> Geometry = new List<BuiltGeometry>();
        }

        private sealed class Stats
        {
            public int Elements;
            public int Objects;
            public int Failed;
            public int Empty;
            public readonly ConcurrentDictionary<string, int> Unsupported = new ConcurrentDictionary<string, int>();
            public readonly ConcurrentDictionary<string, int> Problems = new ConcurrentDictionary<string, int>();
            public readonly List<string> Errors = new List<string>();

            public void Report()
            {
                if (Empty > 0)
                    RhinoApp.WriteLine($"IFC: {Empty:N0} Bauteile ohne lesbare Körpergeometrie.");
                if (Unsupported.Count > 0)
                    RhinoApp.WriteLine("IFC: Nicht unterstützte Geometrie (übersprungen): "
                        + string.Join(", ", Unsupported.OrderByDescending(p => p.Value).Select(p => $"{p.Key} ×{p.Value}")));
                if (Problems.Count > 0)
                    RhinoApp.WriteLine("IFC: Hinweise (Benutzertext IfcImportNote an den Objekten): "
                        + string.Join(", ", Problems.OrderByDescending(p => p.Value).Select(p => $"{p.Key} ×{p.Value}")));
                if (Failed > 0)
                {
                    RhinoApp.WriteLine($"IFC: {Failed:N0} Bauteile konnten nicht gebaut werden.");
                    foreach (var error in Errors.Take(5))
                        RhinoApp.WriteLine("  " + error);
                }
            }
        }

        private static Stats Build(RhinoDoc doc, IfcModel model, ImportOptions options, double tolerance, DocumentWriter writer, CancellationToken cancel)
        {
            var stats = new Stats();
            var builder = new ShapeBuilder(model);
            var rhino = new RhinoGeometry(options, tolerance);
            var blocks = new ConcurrentDictionary<int, Lazy<BlockGeometry>>();
            var queue = new BlockingCollection<ElementResult>(boundedCapacity: 4096);
            var products = model.Products;
            int total = products.Count;
            int done = 0;

            var producer = Task.Run(() =>
            {
                try
                {
                    var parallel = new ParallelOptions
                    {
                        CancellationToken = cancel,
                        MaxDegreeOfParallelism = options.Parallel ? Math.Max(1, System.Environment.ProcessorCount - 1) : 1,
                    };
                    Parallel.ForEach(Partitioner.Create(0, total, Math.Max(16, Math.Min(256, total / 256 + 1))), parallel, range =>
                    {
                        for (int i = range.Item1; i < range.Item2; i++)
                        {
                            cancel.ThrowIfCancellationRequested();
                            var element = BuildElement(products[i], model, builder, rhino, options, blocks, writer.Scale, stats);
                            queue.Add(element, cancel);
                        }
                    });
                }
                finally
                {
                    queue.CompleteAdding();
                }
            }, cancel);

            Rhino.UI.StatusBar.ShowProgressMeter(0, Math.Max(1, total), "IFC Geometrie", true, true);
            var lastUpdate = Stopwatch.StartNew();
            try
            {
                while (!queue.IsCompleted)
                {
                    if (queue.TryTake(out var element, 15))
                    {
                        writer.Add(element.Layer, element.Hidden, element.Name, element.UserText, element.Problems,
                            element.Geometry, element.Instances.Select(x => Tuple.Create(x.MapId, x.Definition, x.Xform)).ToList());
                        stats.Objects = writer.Objects;
                        done++;
                        if (element.Error != null)
                        {
                            stats.Failed++;
                            lock (stats.Errors)
                                stats.Errors.Add(element.Error);
                        }
                        else if (element.Geometry.Count == 0 && element.Instances.Count == 0)
                            stats.Empty++;
                        else
                            stats.Elements++;
                    }
                    if (lastUpdate.ElapsedMilliseconds > 150)
                    {
                        lastUpdate.Restart();
                        Rhino.UI.StatusBar.UpdateProgressMeter(done, true);
                        RhinoApp.SetCommandPrompt($"IFC: Bauteil {done:N0} von {total:N0} (Esc bricht ab)");
                        RhinoApp.Wait();
                    }
                    if (cancel.IsCancellationRequested)
                        break;
                }
                try
                {
                    producer.Wait();
                }
                catch (AggregateException exception) when (exception.InnerExceptions.All(e => e is OperationCanceledException))
                {
                }
                cancel.ThrowIfCancellationRequested();
            }
            finally
            {
                Rhino.UI.StatusBar.HideProgressMeter();
            }
            return stats;
        }

        private static ElementResult BuildElement(int id, IfcModel model, ShapeBuilder builder, RhinoGeometry rhino, ImportOptions options,
            ConcurrentDictionary<int, Lazy<BlockGeometry>> blocks, double scale, Stats stats)
        {
            var element = new ElementResult { Id = id };
            try
            {
                var entity = model.File.Get(id);
                string className = entity.ClassName;
                element.Name = entity.String("Name");
                element.Layer = (model.StoreyOf(id)?.Name ?? IfcModel.NoStorey) + "::" + className;
                element.Hidden = model.IsSpace(id);
                element.UserText = model.UserText(id);

                var geometry = builder.Build(id, options.CutOpenings);
                foreach (var pair in geometry.Unsupported)
                    stats.Unsupported.AddOrUpdate(pair.Key, pair.Value, (k, n) => n + pair.Value);
                if (geometry.Items.Count == 0)
                    return element;

                var fallbackColor = model.MaterialColor(id) ?? DefaultColors.Of(entity);
                var placement = geometry.Placement;
                var toLocal = placement.Inverse();
                var final = RhinoGeometry.T(Mat4.Scale(scale) * placement);
                bool hasOpenings = geometry.Openings.Count > 0;
                bool useBlocks = options.Blocks && !hasOpenings;

                var local = new List<BuiltGeometry>();
                foreach (var item in geometry.Items)
                {
                    var color = item.Color ?? fallbackColor;
                    if (useBlocks && item.MapId != 0)
                    {
                        if (element.Instances.Any(x => x.MapId == item.MapId && x.Xform.Equals(BlockXform(item, scale))))
                            continue; // other items of the same map: already part of the block
                        var mapItems = geometry.Items.Where(x => x.MapId == item.MapId && x.MapToWorld.Equals(item.MapToWorld)).ToList();
                        var definition = blocks.GetOrAdd(item.MapId, mapId => new Lazy<BlockGeometry>(
                            () => BuildBlock(mapId, mapItems, rhino, fallbackColor, scale, model), LazyThreadSafetyMode.ExecutionAndPublication));
                        element.Instances.Add(new BlockInstance { MapId = item.MapId, Definition = definition, Xform = BlockXform(item, scale) });
                        continue;
                    }
                    var built = rhino.Build(item.Shape, toLocal * item.ToWorld, element.Problems);
                    foreach (var g in built)
                        local.Add(new BuiltGeometry(g, color));
                }

                if (hasOpenings && local.Count > 0)
                {
                    var cutters = new List<Brep>();
                    foreach (var opening in geometry.Openings)
                        cutters.AddRange(rhino.ToBreps(rhino.Build(opening.Shape, toLocal * opening.ToWorld, element.Problems)));
                    if (cutters.Count > 0)
                    {
                        // Each piece keeps its colour; openings are cut from every piece they touch.
                        var cut = new List<BuiltGeometry>();
                        foreach (var piece in local)
                        {
                            var pieces = rhino.Difference(new List<GeometryBase> { piece.Geometry }, cutters, element.Problems, "Öffnungen");
                            foreach (var g in pieces)
                                cut.Add(new BuiltGeometry(g, piece.Color));
                        }
                        local = cut;
                    }
                }

                foreach (var piece in local)
                {
                    var g = rhino.Final(piece.Geometry);
                    if (g == null)
                        continue;
                    if (!g.Transform(final) && g is Extrusion extrusion)
                    {
                        g = extrusion.ToBrep(true);
                        g.Transform(final);
                    }
                    element.Geometry.Add(new BuiltGeometry(g, piece.Color));
                }
                foreach (var problem in element.Problems)
                    stats.Problems.AddOrUpdate(problem.Split(':')[0], 1, (k, n) => n + 1);
            }
            catch (Exception exception) when (!(exception is OperationCanceledException))
            {
                element.Error = $"#{id}: {exception.GetType().Name}: {exception.Message}";
            }
            return element;
        }

        /// <summary>Instance transformation of a block: map → world, with the unit scale applied so that the definition
        /// (also scaled) lands at the right size.</summary>
        private static Transform BlockXform(ShapeItem item, double scale) =>
            RhinoGeometry.T(Mat4.Scale(scale) * item.MapToWorld * Mat4.Scale(1 / scale));

        private static BlockGeometry BuildBlock(int mapId, List<ShapeItem> items, RhinoGeometry rhino, IfcColor fallbackColor, double scale, IfcModel model)
        {
            var block = new BlockGeometry { Name = BlockName(mapId, model) };
            var s = RhinoGeometry.T(Mat4.Scale(scale));
            foreach (var item in items)
            {
                foreach (var g in rhino.Build(item.Shape, item.ItemToMap, null))
                {
                    var f = rhino.Final(g);
                    if (f == null)
                        continue;
                    if (!f.Transform(s) && f is Extrusion extrusion)
                    {
                        f = extrusion.ToBrep(true);
                        f.Transform(s);
                    }
                    block.Geometry.Add(new BuiltGeometry(f, item.Color ?? fallbackColor));
                }
            }
            return block;
        }

        private static string BlockName(int mapId, IfcModel model)
        {
            // The type's name if the map belongs to one (IfcTypeProduct.RepresentationMaps), otherwise the map id.
            var name = model.MapTypeName(mapId);
            return string.IsNullOrWhiteSpace(name) ? "IFC Typ #" + mapId : name.Trim();
        }

        // ------------------------------------------------------------------ running work off the main thread

        private sealed class Progress
        {
            private double _value;

            public void Set(double value) => Volatile.Write(ref _value, value);

            public double Value => Volatile.Read(ref _value);
        }

        private static T Run<T>(string label, Progress progress, Func<CancellationToken, T> work, CancellationToken cancel)
        {
            var task = Task.Run(() => work(cancel), cancel);
            Rhino.UI.StatusBar.ShowProgressMeter(0, 1000, label, true, true);
            try
            {
                while (!task.Wait(50))
                {
                    Rhino.UI.StatusBar.UpdateProgressMeter((int)(progress.Value * 1000), true);
                    RhinoApp.SetCommandPrompt($"{label} … {progress.Value * 100:0} % (Esc bricht ab)");
                    RhinoApp.Wait();
                }
            }
            catch (AggregateException exception)
            {
                var inner = exception.InnerExceptions.Count == 1 ? exception.InnerException : exception;
                if (inner is OperationCanceledException)
                    throw inner;
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
            }
            finally
            {
                Rhino.UI.StatusBar.HideProgressMeter();
            }
            return task.Result;
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
