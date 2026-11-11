using System;
using System.Collections.Generic;
using RhinoIfc.Core.Model;
using RhinoIfc.Core.Step;

namespace RhinoIfc.Core.Geometry
{
    /// <summary>Faceted geometry (IfcFacetedBrep, shells, face sets, planar advanced breps) as triangles, keeping
    /// which original face each triangle belongs to.</summary>
    internal static class MeshBuilder
    {
        private sealed class Accumulator
        {
            public readonly List<Vec3> Vertices = new List<Vec3>();
            public readonly List<int> Triangles = new List<int>();
            public readonly List<int> FaceIds = new List<int>();
            public readonly Dictionary<int, int> PointIndex = new Dictionary<int, int>(); // IfcCartesianPoint id → vertex
            public int Face;

            /// <summary>Adds a planar face: outer loop and holes as vertex indices.</summary>
            public void AddFace(List<int> outer, List<List<int>> holes)
            {
                if (outer.Count < 3)
                    return;
                var outerPoints = new List<Vec3>(outer.Count);
                foreach (var i in outer)
                    outerPoints.Add(Vertices[i]);
                var all = new List<int>(outer);
                List<IList<Vec3>> holePoints = null;
                if (holes != null && holes.Count > 0)
                {
                    holePoints = new List<IList<Vec3>>();
                    foreach (var h in holes)
                    {
                        if (h.Count < 3)
                            continue;
                        var pts = new List<Vec3>(h.Count);
                        foreach (var i in h)
                            pts.Add(Vertices[i]);
                        holePoints.Add(pts);
                        all.AddRange(h);
                    }
                }
                var local = new List<int>();
                if (outer.Count == 3 && holePoints == null)
                {
                    local.Add(0);
                    local.Add(1);
                    local.Add(2);
                }
                else if (outer.Count == 4 && holePoints == null && Convex4(outerPoints))
                {
                    local.AddRange(new[] { 0, 1, 2, 0, 2, 3 });
                }
                else
                    Triangulator.Triangulate(outerPoints, holePoints, local);
                foreach (var i in local)
                    Triangles.Add(all[i]);
                for (int t = 0; t < local.Count / 3; t++)
                    FaceIds.Add(Face);
                Face++;
            }

            private static bool Convex4(List<Vec3> q)
            {
                var n = Triangulator.Normal(q);
                for (int i = 0; i < 4; i++)
                {
                    var a = q[i];
                    var b = q[(i + 1) % 4];
                    var c = q[(i + 2) % 4];
                    if ((b - a).Cross(c - b).Dot(n) <= 0)
                        return false;
                }
                return true;
            }

            public MeshShape ToShape(bool closed) =>
                Triangles.Count > 0 ? new MeshShape(Vertices, Triangles, FaceIds, closed) : null;
        }

        public static MeshShape FromShells(ShapeBuilder builder, List<StepEntity> shells, bool closed, List<StepEntity> voids)
        {
            var model = builder.Model;
            var acc = new Accumulator();
            bool allClosed = closed;
            var list = new List<StepEntity>(shells);
            if (voids != null)
                list.AddRange(voids);
            foreach (var shell in list)
            {
                if (shell == null)
                    continue;
                if (shell.Type == "IFCOPENSHELL" || shell.Type == "IFCCONNECTEDFACESET")
                    allClosed = false;
                foreach (var face in shell.Entities("CfsFaces"))
                    AddFace(model, builder, face, acc);
            }
            return acc.ToShape(allClosed);
        }

        private static void AddFace(IfcModel model, ShapeBuilder builder, StepEntity face, Accumulator acc)
        {
            List<int> outer = null;
            var holes = new List<List<int>>();
            var bounds = face.Entities("Bounds");
            foreach (var bound in bounds)
            {
                var loop = bound.Entity("Bound");
                if (loop == null)
                    continue;
                var indices = LoopIndices(model, builder, loop, acc);
                if (indices == null || indices.Count < 3)
                    continue;
                if (bound.Bool("Orientation") == false)
                    indices.Reverse();
                if (bound.IsA("IFCFACEOUTERBOUND") && outer == null)
                    outer = indices;
                else
                    holes.Add(indices);
            }
            if (outer == null)
            {
                if (holes.Count == 0)
                    return;
                // No IfcFaceOuterBound: the largest loop is the outer one.
                int largest = 0;
                double largestArea = -1;
                for (int i = 0; i < holes.Count; i++)
                {
                    var pts = new List<Vec3>();
                    foreach (var k in holes[i])
                        pts.Add(acc.Vertices[k]);
                    double area = Triangulator.Normal(pts).Length;
                    if (area > largestArea)
                    {
                        largestArea = area;
                        largest = i;
                    }
                }
                outer = holes[largest];
                holes.RemoveAt(largest);
            }
            acc.AddFace(outer, holes);
        }

        private static List<int> LoopIndices(IfcModel model, ShapeBuilder builder, StepEntity loop, Accumulator acc)
        {
            var result = new List<int>();
            if (loop.Type == "IFCPOLYLOOP")
            {
                foreach (var value in loop.List("Polygon"))
                {
                    int id = StepEntity.AsRef(value);
                    if (id <= 0)
                        continue;
                    if (!acc.PointIndex.TryGetValue(id, out var index))
                    {
                        index = acc.Vertices.Count;
                        acc.Vertices.Add(model.Point(model.File.Get(id)));
                        acc.PointIndex[id] = index;
                    }
                    if (result.Count == 0 || result[result.Count - 1] != index)
                        result.Add(index);
                }
                if (result.Count > 1 && result[0] == result[result.Count - 1])
                    result.RemoveAt(result.Count - 1);
                return result;
            }
            if (loop.Type == "IFCEDGELOOP")
            {
                // Advanced faces: edges with curves; tessellate each oriented edge.
                var points = new List<Vec3>();
                foreach (var oriented in loop.Entities("EdgeList"))
                {
                    var edge = oriented.Entity("EdgeElement");
                    if (edge == null)
                        continue;
                    bool orientation = oriented.Bool("Orientation") ?? true;
                    var part = EdgePoints(model, builder, edge);
                    if (part == null)
                        continue;
                    if (!orientation)
                        part.Reverse();
                    foreach (var p in part)
                        if (points.Count == 0 || (points[points.Count - 1] - p).LengthSquared > 1e-18)
                            points.Add(p);
                }
                if (points.Count > 1 && (points[0] - points[points.Count - 1]).LengthSquared < 1e-18)
                    points.RemoveAt(points.Count - 1);
                foreach (var p in points)
                {
                    result.Add(acc.Vertices.Count);
                    acc.Vertices.Add(p);
                }
                return result;
            }
            if (loop.Type == "IFCVERTEXLOOP")
                return null;
            return null;
        }

        private static List<Vec3> EdgePoints(IfcModel model, ShapeBuilder builder, StepEntity edge)
        {
            var start = VertexPoint(model, edge.Entity("EdgeStart"));
            var end = VertexPoint(model, edge.Entity("EdgeEnd"));
            if (edge.IsA("IFCEDGECURVE"))
            {
                var geometry = edge.Entity("EdgeGeometry");
                bool sameSense = edge.Bool("SameSense") ?? true;
                if (geometry != null && !geometry.IsA("IFCLINE") && !geometry.IsA("IFCPOLYLINE"))
                {
                    var curve = builder.Curves.Curve(geometry);
                    if (curve != null)
                    {
                        var polygon = TrimToVertices(curve, sameSense ? start : end, sameSense ? end : start);
                        if (polygon != null)
                        {
                            if (!sameSense)
                                polygon.Reverse();
                            return polygon;
                        }
                    }
                }
                else if (geometry != null && geometry.IsA("IFCPOLYLINE"))
                {
                    var curve = builder.Curves.Curve(geometry);
                    if (curve != null)
                    {
                        var polygon = curve.ToPolygon();
                        if (polygon.Count >= 2)
                        {
                            if (!sameSense)
                                polygon.Reverse();
                            return polygon;
                        }
                    }
                }
            }
            return new List<Vec3> { start, end };
        }

        /// <summary>The part of a (possibly closed) curve between two points on it.</summary>
        private static List<Vec3> TrimToVertices(CurveLoop curve, Vec3 from, Vec3 to)
        {
            List<Vec3> polygon;
            bool closed = curve.IsSingleClosedCurve;
            if (curve.Segments.Count == 1 && curve.Segments[0] is EllipseSegment ellipse)
            {
                // Circle edges: walk counter-clockwise from 'from' to 'to'.
                var u = ellipse.XAxis;
                var v = ellipse.YAxis;
                double a0 = Math.Atan2((from - ellipse.Center).Dot(v) / v.LengthSquared, (from - ellipse.Center).Dot(u) / u.LengthSquared);
                double a1 = Math.Atan2((to - ellipse.Center).Dot(v) / v.LengthSquared, (to - ellipse.Center).Dot(u) / u.LengthSquared);
                double sweep = a1 - a0;
                while (sweep <= 1e-9)
                    sweep += 2 * Math.PI;
                int n = Math.Max(2, (int)Math.Ceiling(sweep / (Math.PI / 24)));
                polygon = new List<Vec3>(n + 1);
                for (int i = 0; i <= n; i++)
                {
                    double t = a0 + sweep * i / n;
                    polygon.Add(ellipse.Center + u * Math.Cos(t) + v * Math.Sin(t));
                }
                polygon[0] = from;
                polygon[polygon.Count - 1] = to;
                return polygon;
            }
            polygon = curve.ToPolygon();
            if (polygon.Count < 2)
                return null;
            int i0 = 0, i1 = polygon.Count - 1;
            double d0 = double.MaxValue, d1 = double.MaxValue;
            for (int i = 0; i < polygon.Count; i++)
            {
                double a = (polygon[i] - from).LengthSquared;
                double b = (polygon[i] - to).LengthSquared;
                if (a < d0)
                {
                    d0 = a;
                    i0 = i;
                }
                if (b < d1)
                {
                    d1 = b;
                    i1 = i;
                }
            }
            if (i1 < i0 && !closed)
                return new List<Vec3> { from, to };
            var result = new List<Vec3> { from };
            for (int i = i0 + 1; i < i1; i++)
                result.Add(polygon[i]);
            result.Add(to);
            return result;
        }

        private static Vec3 VertexPoint(IfcModel model, StepEntity vertex)
        {
            if (vertex == null)
                return Vec3.Zero;
            var point = vertex.Entity("VertexGeometry");
            return model.Point(point);
        }

        /// <summary>IfcAdvancedBrep: planar faces become polygons; faces on curved surfaces are approximated by their
        /// boundary only if it is nearly planar, otherwise reported (null).</summary>
        public static MeshShape FromAdvancedBrep(ShapeBuilder builder, StepEntity brep)
        {
            var model = builder.Model;
            var acc = new Accumulator();
            var shells = new List<StepEntity> { brep.Entity("Outer") };
            if (brep.Type == "IFCADVANCEDBREPWITHVOIDS")
                shells.AddRange(brep.Entities("Voids"));
            bool complete = true;
            foreach (var shell in shells)
            {
                if (shell == null)
                    continue;
                foreach (var face in shell.Entities("CfsFaces"))
                {
                    var surface = face.Entity("FaceSurface");
                    if (surface != null && !surface.IsA("IFCPLANE"))
                    {
                        if (!SurfaceFace(model, builder, face, surface, acc))
                            complete = false;
                        continue;
                    }
                    AddFace(model, builder, face, acc);
                }
            }
            var shape = acc.ToShape(complete);
            return shape;
        }

        /// <summary>A face on a curved surface (cylinder, B-spline): tessellated from its surface over its boundary's
        /// parameter range when the boundary is simple; returns false if it could not be represented.</summary>
        private static bool SurfaceFace(IfcModel model, ShapeBuilder builder, StepEntity face, StepEntity surface, Accumulator acc)
        {
            if (surface.IsA("IFCBSPLINESURFACEWITHKNOTS"))
            {
                var grid = BSplineSurface.Evaluate(model, surface, 12);
                if (grid == null)
                    return false;
                int rows = grid.GetLength(0), cols = grid.GetLength(1);
                int start = acc.Vertices.Count;
                for (int i = 0; i < rows; i++)
                    for (int j = 0; j < cols; j++)
                        acc.Vertices.Add(grid[i, j]);
                bool flip = (face.Bool("SameSense") ?? true) == false;
                for (int i = 0; i + 1 < rows; i++)
                {
                    for (int j = 0; j + 1 < cols; j++)
                    {
                        int a = start + i * cols + j, b = a + 1, c = a + cols + 1, d = a + cols;
                        if (flip)
                            acc.Triangles.AddRange(new[] { a, c, b, a, d, c });
                        else
                            acc.Triangles.AddRange(new[] { a, b, c, a, c, d });
                        acc.FaceIds.Add(acc.Face);
                        acc.FaceIds.Add(acc.Face);
                    }
                }
                acc.Face++;
                return true;
            }
            // Cylindrical, conical … surfaces: triangulate the tessellated boundary (good for faces that are
            // bounded by their full curved edges, e.g. pipe segments split into halves).
            int before = acc.Triangles.Count;
            AddFace(model, builder, face, acc);
            return acc.Triangles.Count > before;
        }

        public static MeshShape FromTriangulatedFaceSet(StepEntity set)
        {
            var coordinates = ReadPointList(set.Entity("Coordinates"));
            if (coordinates == null)
                return null;
            var pn = PnIndex(set);
            var triangles = new List<int>();
            var faces = new List<int>();
            int face = 0;
            foreach (var value in set.List("CoordIndex"))
            {
                if (!(value is object[] triple) || triple.Length < 3)
                    continue;
                int a = Index(triple[0], pn, coordinates.Count), b = Index(triple[1], pn, coordinates.Count), c = Index(triple[2], pn, coordinates.Count);
                if (a < 0 || b < 0 || c < 0 || a == b || b == c || a == c)
                    continue;
                triangles.Add(a);
                triangles.Add(b);
                triangles.Add(c);
                faces.Add(face++);
            }
            bool closed = set.Has("Closed") && set.Bool("Closed") == true;
            return triangles.Count > 0 ? new MeshShape(coordinates, triangles, faces, closed) : null;
        }

        public static MeshShape FromPolygonalFaceSet(StepEntity set)
        {
            var coordinates = ReadPointList(set.Entity("Coordinates"));
            if (coordinates == null)
                return null;
            var pn = PnIndex(set);
            var acc = new Accumulator();
            acc.Vertices.AddRange(coordinates);
            foreach (var face in set.Entities("Faces"))
            {
                var outer = Indices(face.List("CoordIndex"), pn, coordinates.Count);
                var holes = new List<List<int>>();
                if (face.Has("InnerCoordIndices"))
                    foreach (var inner in face.List("InnerCoordIndices"))
                        if (inner is object[] list)
                            holes.Add(Indices(list, pn, coordinates.Count));
                acc.AddFace(outer, holes);
            }
            bool closed = set.Bool("Closed") == true;
            return acc.ToShape(closed);
        }

        private static List<int> Indices(object[] values, int[] pn, int count)
        {
            var result = new List<int>(values.Length);
            foreach (var v in values)
            {
                int i = Index(v, pn, count);
                if (i >= 0 && (result.Count == 0 || result[result.Count - 1] != i))
                    result.Add(i);
            }
            if (result.Count > 1 && result[0] == result[result.Count - 1])
                result.RemoveAt(result.Count - 1);
            return result;
        }

        private static int[] PnIndex(StepEntity set)
        {
            if (!set.Has("PnIndex"))
                return null;
            var values = set["PnIndex"] as object[];
            if (values == null || values.Length == 0)
                return null;
            var result = new int[values.Length];
            for (int i = 0; i < values.Length; i++)
                result[i] = (int)StepEntity.AsDouble(values[i], 0);
            return result;
        }

        private static int Index(object value, int[] pn, int count)
        {
            long i = value is long l ? l : (long)StepEntity.AsDouble(value, 0);
            if (pn != null)
            {
                if (i < 1 || i > pn.Length)
                    return -1;
                i = pn[i - 1];
            }
            return i >= 1 && i <= count ? (int)i - 1 : -1;
        }

        private static List<Vec3> ReadPointList(StepEntity list)
        {
            if (list == null)
                return null;
            var result = new List<Vec3>();
            foreach (var item in list.List("CoordList"))
                if (item is object[] c)
                    result.Add(IfcModel.ToVec(c));
            return result;
        }
    }

    /// <summary>Evaluates an IfcBSplineSurfaceWithKnots (rational too) on a grid.</summary>
    internal static class BSplineSurface
    {
        public static Vec3[,] Evaluate(IfcModel model, StepEntity surface, int samplesPerSpan)
        {
            int uDegree = (int)StepEntity.AsDouble(surface["UDegree"], 0);
            int vDegree = (int)StepEntity.AsDouble(surface["VDegree"], 0);
            var rows = surface.List("ControlPointsList");
            if (uDegree < 1 || vDegree < 1 || rows.Length <= uDegree)
                return null;
            int nu = rows.Length;
            int nv = (rows[0] as object[])?.Length ?? 0;
            if (nv <= vDegree)
                return null;
            var control = new Vec3[nu, nv];
            for (int i = 0; i < nu; i++)
            {
                var row = rows[i] as object[];
                if (row == null || row.Length != nv)
                    return null;
                for (int j = 0; j < nv; j++)
                    control[i, j] = model.Point(model.File.Get(StepEntity.AsRef(row[j])));
            }
            var uKnots = Knots(surface.List("UMultiplicities"), surface.List("UKnots"));
            var vKnots = Knots(surface.List("VMultiplicities"), surface.List("VKnots"));
            if (uKnots == null || vKnots == null || uKnots.Length != nu + uDegree + 1 || vKnots.Length != nv + vDegree + 1)
                return null;
            double[,] weights = null;
            if (surface.IsA("IFCRATIONALBSPLINESURFACEWITHKNOTS"))
            {
                var w = surface.List("WeightsData");
                if (w.Length == nu)
                {
                    weights = new double[nu, nv];
                    for (int i = 0; i < nu; i++)
                    {
                        var row = w[i] as object[];
                        for (int j = 0; j < nv; j++)
                            weights[i, j] = row != null && j < row.Length ? StepEntity.AsDouble(row[j], 1) : 1;
                    }
                }
            }
            int su = Math.Min(64, Math.Max(2, (nu - uDegree) * samplesPerSpan / 4 + 1));
            int sv = Math.Min(64, Math.Max(2, (nv - vDegree) * samplesPerSpan / 4 + 1));
            var grid = new Vec3[su + 1, sv + 1];
            double u0 = uKnots[uDegree], u1 = uKnots[nu], v0 = vKnots[vDegree], v1 = vKnots[nv];
            for (int a = 0; a <= su; a++)
            {
                double u = u0 + (u1 - u0) * a / su;
                var bu = Basis(uKnots, uDegree, nu, u);
                for (int b = 0; b <= sv; b++)
                {
                    double v = v0 + (v1 - v0) * b / sv;
                    var bv = Basis(vKnots, vDegree, nv, v);
                    double x = 0, y = 0, z = 0, wsum = 0;
                    for (int i = 0; i < nu; i++)
                    {
                        if (bu[i] == 0)
                            continue;
                        for (int j = 0; j < nv; j++)
                        {
                            double f = bu[i] * bv[j];
                            if (f == 0)
                                continue;
                            double w = weights != null ? weights[i, j] : 1;
                            f *= w;
                            x += control[i, j].X * f;
                            y += control[i, j].Y * f;
                            z += control[i, j].Z * f;
                            wsum += f;
                        }
                    }
                    grid[a, b] = wsum != 0 ? new Vec3(x / wsum, y / wsum, z / wsum) : Vec3.Zero;
                }
            }
            return grid;
        }

        private static double[] Knots(object[] multiplicities, object[] values)
        {
            if (multiplicities.Length != values.Length)
                return null;
            var knots = new List<double>();
            for (int i = 0; i < values.Length; i++)
            {
                int m = (int)StepEntity.AsDouble(multiplicities[i], 1);
                for (int k = 0; k < m; k++)
                    knots.Add(StepEntity.AsDouble(values[i], 0));
            }
            return knots.ToArray();
        }

        /// <summary>All B-spline basis function values at t (Cox–de Boor).</summary>
        private static double[] Basis(double[] knots, int degree, int count, double t)
        {
            var n = new double[knots.Length - 1];
            double last = knots[count];
            for (int i = 0; i < n.Length; i++)
            {
                bool inSpan = t >= knots[i] && t < knots[i + 1];
                // The end of the range belongs to the last non-empty span.
                if (t >= last && knots[i] < last && knots[i + 1] >= last)
                    inSpan = true;
                n[i] = inSpan ? 1 : 0;
            }
            for (int p = 1; p <= degree; p++)
            {
                for (int i = 0; i + p < n.Length; i++)
                {
                    double left = knots[i + p] - knots[i];
                    double right = knots[i + p + 1] - knots[i + 1];
                    double a = left > 0 ? (t - knots[i]) / left * n[i] : 0;
                    double b = right > 0 ? (knots[i + p + 1] - t) / right * n[i + 1] : 0;
                    n[i] = a + b;
                }
            }
            var result = new double[count];
            Array.Copy(n, result, Math.Min(count, n.Length));
            return result;
        }
    }
}
