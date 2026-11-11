using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RhinoIfc.Core;
using RhinoIfc.Core.Geometry;
using RhinoIfc.Core.Model;
using RhinoIfc.Core.Step;

// Usage: RhinoIfc.Verify <file.ifc> [out.jsonl] [--limit N]
var args2 = args.ToList();
int limit = int.MaxValue;
int li = args2.IndexOf("--limit");
if (li >= 0)
{
    limit = int.Parse(args2[li + 1]);
    args2.RemoveRange(li, 2);
}
int dump = 0;
int di = args2.IndexOf("--dump");
if (di >= 0)
{
    dump = int.Parse(args2[di + 1]);
    args2.RemoveRange(di, 2);
}
var path = args2[0];
var output = args2.Count > 1 ? args2[1] : null;
var clock = Stopwatch.StartNew();
var options = new ImportOptions();
string temp = null;
if (IfcZip.IsZip(path))
{
    temp = Path.Combine(Path.GetTempPath(), "rhinoifc-verify-" + Guid.NewGuid().ToString("N"));
    path = IfcZip.Extract(path, temp, 64L << 30);
}
using var file = StepFile.Open(path);
double tIndex = clock.Elapsed.TotalSeconds;
var model = IfcModel.Load(file, options);
double tModel = clock.Elapsed.TotalSeconds;
var builder = new ShapeBuilder(model);
double scale = model.LengthToMetres;
var products = model.Products.Take(limit).ToList();
if (dump > 0)
{
    var g = builder.Build(dump, true);
    foreach (var item in g.Items)
    {
        Console.WriteLine($"item {item.Shape.GetType().Name} map={item.MapId} toWorld={item.ToWorld}");
        Dump(item.Shape, "  ");
    }
    return;
}
var lines = new string[products.Count];
var unsupported = new ConcurrentDictionary<string, int>();
int failures = 0;
Parallel.For(0, products.Count, i =>
{
    int id = products[i];
    var sb = new StringBuilder();
    var product = file.Get(id);
    try
    {
        var geometry = builder.Build(id, true);
        foreach (var pair in geometry.Unsupported)
            unsupported.AddOrUpdate(pair.Key, pair.Value, (_, n) => n + pair.Value);
        var mesh = Tessellator.Mesh(geometry);
        var box = mesh.BoundingBox();
        bool booleans = geometry.Items.Any(it => Tessellator.HasBoolean(it.Shape));
        double volume = 0;
        foreach (var item in geometry.Items)
        {
            var m = new TriangleMesh();
            m.Append(Tessellator.Mesh(item.Shape), item.ToWorld);
            volume += Math.Abs(m.Volume());
        }
        volume *= scale * scale * scale;
        sb.Append("{\"guid\":").Append(Json(product.String("GlobalId")));
        sb.Append(",\"id\":").Append(id);
        sb.Append(",\"cls\":").Append(Json(product.ClassName));
        sb.Append(",\"storey\":").Append(Json(model.StoreyOf(id)?.Name));
        if (!box.IsEmpty)
            sb.Append(",\"bbox\":[[").Append(N(box.Min.X * scale)).Append(',').Append(N(box.Min.Y * scale)).Append(',').Append(N(box.Min.Z * scale))
              .Append("],[").Append(N(box.Max.X * scale)).Append(',').Append(N(box.Max.Y * scale)).Append(',').Append(N(box.Max.Z * scale)).Append("]]");
        sb.Append(",\"volume\":").Append(N(volume));
        sb.Append(",\"booleans\":").Append(booleans ? "true" : "false");
        sb.Append(",\"items\":").Append(geometry.Items.Count);
        sb.Append(",\"openings\":").Append(geometry.Openings.Count);
        sb.Append(",\"tris\":").Append(mesh.TriangleCount);
        if (geometry.Unsupported.Count > 0)
            sb.Append(",\"unsupported\":").Append(Json(string.Join(" ", geometry.Unsupported.Keys)));
        sb.Append('}');
    }
    catch (Exception e)
    {
        System.Threading.Interlocked.Increment(ref failures);
        sb.Clear();
        sb.Append("{\"guid\":").Append(Json(product?.String("GlobalId"))).Append(",\"id\":").Append(id)
          .Append(",\"error\":").Append(Json(e.GetType().Name + ": " + e.Message)).Append('}');
    }
    lines[i] = sb.ToString();
});
double tGeometry = clock.Elapsed.TotalSeconds;
if (output != null)
    File.WriteAllLines(output, lines);
var process = Process.GetCurrentProcess();
if (temp != null) { file.Dispose(); Directory.Delete(temp, true); }
Console.WriteLine($"{Path.GetFileName(args2[0])}: {new FileInfo(args2[0]).Length / 1e6:0.0} MB, {file.EntityCount} entities, schema {file.SchemaIdentifier}, unit {model.LengthUnitName} ({scale}), angle {model.AngleToRadians}");
Console.WriteLine($"  index {tIndex:0.00}s, model {tModel - tIndex:0.00}s, geometry {tGeometry - tModel:0.00}s for {products.Count} products, {failures} failures, peak memory {process.PeakWorkingSet64 / 1e6:0} MB");
if (unsupported.Count > 0)
    Console.WriteLine("  unsupported: " + string.Join(", ", unsupported.OrderByDescending(p => p.Value).Select(p => $"{p.Key}×{p.Value}")));

static void Dump(Shape shape, string indent)
{
    switch (shape)
    {
        case PipeShape p:
            Console.WriteLine($"{indent}pipe r={p.Radius}");
            foreach (var s in p.Path.Segments) Console.WriteLine($"{indent}  {s.GetType().Name} {s.Start} -> {s.End}");
            break;
        case ExtrusionShape e:
            Console.WriteLine($"{indent}extrusion depth={e.Depth} dir={e.Direction} pos={e.Position}");
            foreach (var r in e.Profile.Regions) { Console.WriteLine($"{indent}  region holes={r.Holes.Count}"); foreach (var s in r.Outer.Segments) Console.WriteLine($"{indent}    {s.GetType().Name} {s.Start} -> {s.End}"); }
            break;
        case BooleanShape b:
            Console.WriteLine($"{indent}boolean {b.Operator}"); Dump(b.First, indent + "  "); Dump(b.Second, indent + "  ");
            break;
        case HalfSpaceShape h:
            Console.WriteLine($"{indent}halfspace p={h.Point} n={h.Normal} bounded={h.Boundary != null} pos={h.BoundaryPosition}");
            if (h.Boundary != null) foreach (var s in h.Boundary.Segments) Console.WriteLine($"{indent}  {s.Start} -> {s.End}");
            break;
        case MeshShape m:
            Console.WriteLine($"{indent}mesh v={m.Vertices.Count} t={m.TriangleCount} closed={m.Closed}");
            break;
        default:
            Console.WriteLine($"{indent}{shape?.GetType().Name}");
            break;
    }
}

static string N(double v) => double.IsFinite(v) ? v.ToString("R", CultureInfo.InvariantCulture) : "null";
static string Json(string s)
{
    if (s == null) return "null";
    var sb = new StringBuilder("\"");
    foreach (var c in s)
    {
        if (c == '"' || c == '\\') sb.Append('\\').Append(c);
        else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
        else sb.Append(c);
    }
    return sb.Append('"').ToString();
}
