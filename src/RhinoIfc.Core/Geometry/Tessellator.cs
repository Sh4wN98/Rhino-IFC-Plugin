using System;
using System.Collections.Generic;

namespace RhinoIfc.Core.Geometry
{
    /// <summary>A plain triangle mesh.</summary>
    public sealed class TriangleMesh
    {
        public List<Vec3> Vertices { get; } = new List<Vec3>();

        public List<int> Triangles { get; } = new List<int>();

        public int TriangleCount => Triangles.Count / 3;

        public void Append(TriangleMesh other, Mat4 transform)
        {
            int offset = Vertices.Count;
            foreach (var v in other.Vertices)
                Vertices.Add(transform.Point(v));
            bool flip = transform.Determinant < 0;
            for (int i = 0; i < other.Triangles.Count; i += 3)
            {
                Triangles.Add(other.Triangles[i] + offset);
                Triangles.Add(other.Triangles[i + (flip ? 2 : 1)] + offset);
                Triangles.Add(other.Triangles[i + (flip ? 1 : 2)] + offset);
            }
        }

        public BBox BoundingBox()
        {
            var box = BBox.Empty;
            // Only vertices that are used.
            foreach (var i in Triangles)
                box.Add(Vertices[i]);
            return box;
        }

        /// <summary>Signed volume (positive for outward-facing closed meshes).</summary>
        public double Volume()
        {
            double volume = 0;
            for (int i = 0; i < Triangles.Count; i += 3)
            {
                var a = Vertices[Triangles[i]];
                var b = Vertices[Triangles[i + 1]];
                var c = Vertices[Triangles[i + 2]];
                volume += a.Dot(b.Cross(c));
            }
            return volume / 6;
        }

        public double Area()
        {
            double area = 0;
            for (int i = 0; i < Triangles.Count; i += 3)
            {
                var a = Vertices[Triangles[i]];
                var b = Vertices[Triangles[i + 1]];
                var c = Vertices[Triangles[i + 2]];
                area += (b - a).Cross(c - a).Length / 2;
            }
            return area;
        }
    }

    /// <summary>Turns <see cref="Shape"/>s into triangles, without Rhino: for tests and checks against other IFC
    /// readers. Booleans with half-spaces clip (without capping), other Booleans keep the first operand.</summary>
    public static class Tessellator
    {
        public static TriangleMesh Mesh(Shape shape, double maxAngle = Math.PI / 24)
        {
            var mesh = new TriangleMesh();
            Add(shape, Mat4.Identity, mesh, maxAngle);
            return mesh;
        }

        /// <summary>All items of a product in world coordinates; openings are not cut.</summary>
        public static TriangleMesh Mesh(ProductGeometry product, double maxAngle = Math.PI / 24)
        {
            var mesh = new TriangleMesh();
            foreach (var item in product.Items)
                Add(item.Shape, item.ToWorld, mesh, maxAngle);
            return mesh;
        }

        /// <summary>True if the shape contains a Boolean (its volume is then not exact here).</summary>
        public static bool HasBoolean(Shape shape)
        {
            switch (shape)
            {
                case BooleanShape _:
                    return true;
                case TransformedShape t:
                    return HasBoolean(t.Shape);
                default:
                    return false;
            }
        }

        private static void Add(Shape shape, Mat4 transform, TriangleMesh mesh, double maxAngle)
        {
            switch (shape)
            {
                case MeshShape m:
                {
                    var local = new TriangleMesh();
                    local.Vertices.AddRange(m.Vertices);
                    local.Triangles.AddRange(m.Triangles);
                    mesh.Append(local, transform);
                    break;
                }
                case ExtrusionShape e:
                    mesh.Append(Extrusion(e, maxAngle), transform);
                    break;
                case RevolutionShape r:
                    mesh.Append(Revolution(r, maxAngle), transform);
                    break;
                case PipeShape p:
                    mesh.Append(Pipe(p, maxAngle), transform);
                    break;
                case SphereShape s:
                    mesh.Append(Sphere(s, maxAngle), transform);
                    break;
                case TransformedShape t:
                    Add(t.Shape, transform * t.Transform, mesh, maxAngle);
                    break;
                case BooleanShape b:
                {
                    var first = new TriangleMesh();
                    Add(b.First, Mat4.Identity, first, maxAngle);
                    if (b.Operator == BooleanOperator.Difference)
                        first = Subtract(first, b.Second);
                    else if (b.Operator == BooleanOperator.Union)
                        Add(b.Second, Mat4.Identity, first, maxAngle);
                    mesh.Append(first, transform);
                    break;
                }
                case HalfSpaceShape _:
                    break;
            }
        }

        private static TriangleMesh Subtract(TriangleMesh mesh, Shape second)
        {
            if (second is HalfSpaceShape h)
                return Clip(mesh, h);
            if (second is BooleanShape b && b.Operator == BooleanOperator.Union)
                return Subtract(Subtract(mesh, b.First), b.Second);
            return mesh;
        }

        /// <summary>Removes the part of the mesh inside the half-space (for bounded ones: inside the prism).</summary>
        private static TriangleMesh Clip(TriangleMesh mesh, HalfSpaceShape h)
        {
            var result = new TriangleMesh();
            List<Vec3> boundary = null;
            Mat4 inverse = Mat4.Identity;
            bool convex = false;
            if (h.Boundary != null)
            {
                inverse = h.BoundaryPosition.Inverse();
                boundary = h.Boundary.ToPolygon();
                if (AreaXY(boundary) < 0)
                    boundary.Reverse();
                convex = IsConvexXY(boundary);
            }
            double Side(Vec3 p) => (p - h.Point).Dot(h.Normal); // > 0: inside the half-space (removed)
            for (int i = 0; i < mesh.Triangles.Count; i += 3)
            {
                var tri = new List<Vec3> { mesh.Vertices[mesh.Triangles[i]], mesh.Vertices[mesh.Triangles[i + 1]], mesh.Vertices[mesh.Triangles[i + 2]] };
                AddPolygon(result, ClipPolygon(tri, Side));
                if (boundary == null)
                    continue;
                // The part on the removed side survives where it lies outside the bounded half-space's prism.
                var removed = ClipPolygon(tri, p => -Side(p));
                if (removed.Count < 3)
                    continue;
                if (convex)
                {
                    var remaining = removed;
                    for (int k = 0; k < boundary.Count && remaining.Count >= 3; k++)
                    {
                        var a = boundary[k];
                        var b = boundary[(k + 1) % boundary.Count];
                        // > 0: outside this edge (polygon counter-clockwise).
                        Func<Vec3, double> outside = p =>
                        {
                            var q = inverse.Point(p);
                            return -((b.X - a.X) * (q.Y - a.Y) - (b.Y - a.Y) * (q.X - a.X));
                        };
                        // Faces lying on the prism's side (outside == 0) count as inside.
                        double eps = 1e-9 * Math.Max(1, (b - a).LengthSquared);
                        AddPolygon(result, ClipPolygon(remaining, p => eps - outside(p)));
                        remaining = ClipPolygon(remaining, outside);
                    }
                }
                else
                {
                    var c = (removed[0] + removed[1] + removed[2]) / 3;
                    var q = inverse.Point(c);
                    if (!PointInPolygon(boundary, q.X, q.Y))
                        AddPolygon(result, removed);
                }
            }
            return result;
        }

        private static bool IsConvexXY(List<Vec3> polygon)
        {
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                var c = polygon[(i + 2) % polygon.Count];
                if ((b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X) < -1e-12)
                    return false;
            }
            return true;
        }

        private static List<Vec3> ClipPolygon(IList<Vec3> polygon, Func<Vec3, double> side)
        {
            var output = new List<Vec3>();
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                double da = side(a), db = side(b);
                if (da <= 0)
                    output.Add(a);
                if ((da < 0 && db > 0) || (da > 0 && db < 0))
                {
                    double t = da / (da - db);
                    output.Add(a + (b - a) * t);
                }
            }
            return output;
        }

        private static void AddPolygon(TriangleMesh mesh, List<Vec3> polygon)
        {
            if (polygon.Count < 3)
                return;
            // Slivers on a cutting plane (points exactly on it) have no area: leave them out.
            var normal = Triangulator.Normal(polygon);
            double extent = 0;
            foreach (var p in polygon)
                extent = Math.Max(extent, (p - polygon[0]).LengthSquared);
            if (normal.Length < 1e-9 * extent)
                return;
            int start = mesh.Vertices.Count;
            mesh.Vertices.AddRange(polygon);
            for (int i = 1; i + 1 < polygon.Count; i++)
            {
                mesh.Triangles.Add(start);
                mesh.Triangles.Add(start + i);
                mesh.Triangles.Add(start + i + 1);
            }
        }

        public static bool PointInPolygon(List<Vec3> polygon, double x, double y)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var a = polygon[i];
                var b = polygon[j];
                if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        // ------------------------------------------------------------------ solids

        /// <summary>A region's polygons, outer counter-clockwise and holes clockwise (in the profile's XY plane).</summary>
        private static void RegionPolygons(ProfileRegion region, double maxAngle, out List<Vec3> outer, out List<IList<Vec3>> holes)
        {
            outer = region.Outer.ToPolygon(maxAngle);
            if (AreaXY(outer) < 0)
                outer.Reverse();
            holes = new List<IList<Vec3>>();
            foreach (var hole in region.Holes)
            {
                var polygon = hole.ToPolygon(maxAngle);
                if (polygon.Count < 3)
                    continue;
                if (AreaXY(polygon) > 0)
                    polygon.Reverse();
                holes.Add(polygon);
            }
        }

        private static double AreaXY(List<Vec3> polygon)
        {
            double area = 0;
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            return area / 2;
        }

        public static TriangleMesh Extrusion(ExtrusionShape e, double maxAngle = Math.PI / 24)
        {
            var mesh = new TriangleMesh();
            var offset = e.Direction * e.Depth;
            // Whether the extrusion goes to the side of the profile's normal: decides the cap orientation.
            bool positive = e.Position.ZColumn.Dot(e.Direction) * Math.Sign(e.Position.Determinant) >= 0;
            foreach (var region in e.Profile.Regions)
            {
                RegionPolygons(region, maxAngle, out var outer, out var holes);
                if (outer.Count < 3)
                    continue;
                var triangles = new List<int>();
                Triangulator.Triangulate(outer, holes, triangles);
                var all = new List<Vec3>(outer);
                foreach (var h in holes)
                    all.AddRange(h);
                int bottom = mesh.Vertices.Count;
                foreach (var p in all)
                    mesh.Vertices.Add(e.Position.Point(p));
                int top = mesh.Vertices.Count;
                foreach (var p in all)
                    mesh.Vertices.Add(e.Position.Point(p) + offset);
                for (int i = 0; i < triangles.Count; i += 3)
                {
                    int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                    if (positive)
                    {
                        mesh.Triangles.AddRange(new[] { bottom + a, bottom + c, bottom + b, top + a, top + b, top + c });
                    }
                    else
                    {
                        mesh.Triangles.AddRange(new[] { bottom + a, bottom + b, bottom + c, top + a, top + c, top + b });
                    }
                }
                int index = 0;
                var loops = new List<IList<Vec3>> { outer };
                loops.AddRange(holes);
                foreach (var loop in loops)
                {
                    int n = loop.Count;
                    for (int i = 0; i < n; i++)
                    {
                        int a = index + i, b = index + (i + 1) % n;
                        if (positive)
                            mesh.Triangles.AddRange(new[] { bottom + a, bottom + b, top + b, bottom + a, top + b, top + a });
                        else
                            mesh.Triangles.AddRange(new[] { bottom + a, top + b, bottom + b, bottom + a, top + a, top + b });
                    }
                    index += n;
                }
            }
            if (mesh.Volume() < 0)
                Flip(mesh);
            return mesh;
        }

        public static TriangleMesh Revolution(RevolutionShape r, double maxAngle = Math.PI / 24)
        {
            var mesh = new TriangleMesh();
            int steps = Math.Max(4, (int)Math.Ceiling(Math.Abs(r.Angle) / maxAngle));
            bool full = Math.Abs(Math.Abs(r.Angle) - 2 * Math.PI) < 1e-6;
            foreach (var region in r.Profile.Regions)
            {
                RegionPolygons(region, maxAngle, out var outer, out var holes);
                var loops = new List<IList<Vec3>> { outer };
                loops.AddRange(holes);
                var rings = new List<List<Vec3>>();
                foreach (var loop in loops)
                {
                    var world = new List<Vec3>();
                    foreach (var p in loop)
                        world.Add(r.Position.Point(p));
                    rings.Add(world);
                }
                // Sides.
                foreach (var ring in rings)
                {
                    int n = ring.Count;
                    int baseIndex = mesh.Vertices.Count;
                    for (int s = 0; s <= steps; s++)
                    {
                        double angle = r.Angle * s / steps;
                        foreach (var p in ring)
                            mesh.Vertices.Add(Rotate(p, r.AxisPoint, r.AxisDirection, angle));
                    }
                    for (int s = 0; s < steps; s++)
                        for (int i = 0; i < n; i++)
                        {
                            int a = baseIndex + s * n + i, b = baseIndex + s * n + (i + 1) % n;
                            int c = a + n, d = b + n;
                            mesh.Triangles.AddRange(new[] { a, b, d, a, d, c });
                        }
                }
                if (!full)
                {
                    var triangles = new List<int>();
                    Triangulator.Triangulate(outer, holes, triangles);
                    var all = new List<Vec3>();
                    foreach (var ring in rings)
                        all.AddRange(ring);
                    int start = mesh.Vertices.Count;
                    mesh.Vertices.AddRange(all);
                    int end = mesh.Vertices.Count;
                    foreach (var p in all)
                        mesh.Vertices.Add(Rotate(p, r.AxisPoint, r.AxisDirection, r.Angle));
                    for (int i = 0; i < triangles.Count; i += 3)
                    {
                        mesh.Triangles.AddRange(new[] { start + triangles[i], start + triangles[i + 2], start + triangles[i + 1] });
                        mesh.Triangles.AddRange(new[] { end + triangles[i], end + triangles[i + 1], end + triangles[i + 2] });
                    }
                }
            }
            if (mesh.Volume() < 0)
                Flip(mesh);
            return mesh;
        }

        private static void Flip(TriangleMesh mesh)
        {
            for (int i = 0; i < mesh.Triangles.Count; i += 3)
            {
                int t = mesh.Triangles[i + 1];
                mesh.Triangles[i + 1] = mesh.Triangles[i + 2];
                mesh.Triangles[i + 2] = t;
            }
        }

        public static Vec3 Rotate(Vec3 p, Vec3 axisPoint, Vec3 axis, double angle)
        {
            var k = axis.Unit();
            var v = p - axisPoint;
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            var rotated = v * cos + k.Cross(v) * sin + k * (k.Dot(v) * (1 - cos));
            return axisPoint + rotated;
        }

        public static TriangleMesh Pipe(PipeShape pipe, double maxAngle = Math.PI / 24)
        {
            var mesh = new TriangleMesh();
            var path = pipe.Path.ToPolygon(maxAngle);
            // ToPolygon drops a repeated closing point; a pipe path is open.
            if (pipe.Path.Segments.Count > 0)
            {
                var last = pipe.Path.Segments[pipe.Path.Segments.Count - 1].End;
                if ((path[path.Count - 1] - last).LengthSquared > 1e-18)
                    path.Add(last);
            }
            if (path.Count < 2)
                return mesh;
            int n = Math.Max(8, (int)Math.Ceiling(2 * Math.PI / maxAngle));
            var dir0 = (path[1] - path[0]).Unit();
            var u = dir0.Perpendicular();
            for (int k = 0; k < path.Count; k++)
            {
                Vec3 tangent;
                if (k == 0)
                    tangent = (path[1] - path[0]).Unit();
                else if (k == path.Count - 1)
                    tangent = (path[k] - path[k - 1]).Unit();
                else
                    tangent = ((path[k] - path[k - 1]).Unit() + (path[k + 1] - path[k]).Unit()).Unit();
                u = (u - tangent * u.Dot(tangent)).Unit();
                var v = tangent.Cross(u);
                for (int i = 0; i < n; i++)
                {
                    double a = 2 * Math.PI * i / n;
                    mesh.Vertices.Add(path[k] + (u * Math.Cos(a) + v * Math.Sin(a)) * pipe.Radius);
                }
            }
            for (int k = 0; k + 1 < path.Count; k++)
                for (int i = 0; i < n; i++)
                {
                    int a = k * n + i, b = k * n + (i + 1) % n, c = a + n, d = b + n;
                    mesh.Triangles.AddRange(new[] { a, b, d, a, d, c });
                }
            // Caps.
            int capStart = mesh.Vertices.Count;
            mesh.Vertices.Add(path[0]);
            mesh.Vertices.Add(path[path.Count - 1]);
            int lastRing = (path.Count - 1) * n;
            for (int i = 0; i < n; i++)
            {
                mesh.Triangles.AddRange(new[] { capStart, (i + 1) % n, i });
                mesh.Triangles.AddRange(new[] { capStart + 1, lastRing + i, lastRing + (i + 1) % n });
            }
            if (mesh.Volume() < 0)
                Flip(mesh);
            return mesh;
        }

        public static TriangleMesh Sphere(SphereShape s, double maxAngle = Math.PI / 24)
        {
            var mesh = new TriangleMesh();
            int rings = Math.Max(6, (int)Math.Ceiling(Math.PI / maxAngle));
            int segments = rings * 2;
            for (int i = 0; i <= rings; i++)
            {
                double phi = Math.PI * i / rings;
                for (int j = 0; j < segments; j++)
                {
                    double theta = 2 * Math.PI * j / segments;
                    mesh.Vertices.Add(s.Center + new Vec3(Math.Sin(phi) * Math.Cos(theta), Math.Sin(phi) * Math.Sin(theta), Math.Cos(phi)) * s.Radius);
                }
            }
            for (int i = 0; i < rings; i++)
                for (int j = 0; j < segments; j++)
                {
                    int a = i * segments + j, b = i * segments + (j + 1) % segments, c = a + segments, d = b + segments;
                    mesh.Triangles.AddRange(new[] { a, c, d, a, d, b });
                }
            if (mesh.Volume() < 0)
                Flip(mesh);
            return mesh;
        }
    }
}
