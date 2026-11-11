using System;
using System.Collections.Generic;

namespace RhinoIfc.Core.Geometry
{
    // The geometry of an IFC element as the import understands it, independent of Rhino. The Rhino side turns
    // these into Extrusions, Breps and Meshes; the tests turn them into triangles to check sizes and volumes.
    // Coordinates are in the file's length unit.

    /// <summary>A piece of a curve: line, arc through three points, full circle/ellipse, or NURBS.</summary>
    public abstract class Segment
    {
        public abstract Vec3 Start { get; }

        public abstract Vec3 End { get; }

        public abstract Segment Transformed(Mat4 m);

        public abstract Segment Reversed();

        /// <summary>Points along the segment from start to end (end included, start included).</summary>
        public abstract List<Vec3> Tessellate(double maxAngle = Math.PI / 24);
    }

    public sealed class LineSegment : Segment
    {
        public LineSegment(Vec3 a, Vec3 b)
        {
            A = a;
            B = b;
        }

        public Vec3 A { get; }

        public Vec3 B { get; }

        public override Vec3 Start => A;

        public override Vec3 End => B;

        public override Segment Transformed(Mat4 m) => new LineSegment(m.Point(A), m.Point(B));

        public override Segment Reversed() => new LineSegment(B, A);

        public override List<Vec3> Tessellate(double maxAngle = Math.PI / 24) => new List<Vec3> { A, B };
    }

    /// <summary>A circular arc through start, a point on the arc and end.</summary>
    public sealed class ArcSegment : Segment
    {
        public ArcSegment(Vec3 a, Vec3 mid, Vec3 b)
        {
            A = a;
            Mid = mid;
            B = b;
        }

        public Vec3 A { get; }

        public Vec3 Mid { get; }

        public Vec3 B { get; }

        public override Vec3 Start => A;

        public override Vec3 End => B;

        public override Segment Transformed(Mat4 m) => new ArcSegment(m.Point(A), m.Point(Mid), m.Point(B));

        public override Segment Reversed() => new ArcSegment(B, Mid, A);

        /// <summary>Centre, radius, normal and sweep angle of the arc; false if the three points are collinear.</summary>
        public bool TryGetCircle(out Vec3 center, out double radius, out Vec3 normal, out double sweep)
        {
            center = Vec3.Zero;
            radius = 0;
            sweep = 0;
            var ab = Mid - A;
            var ac = B - A;
            normal = ab.Cross(ac);
            double n2 = normal.LengthSquared;
            if (n2 < 1e-24 * Math.Max(1e-300, ab.LengthSquared * ac.LengthSquared))
                return false;
            // Circumcentre of the triangle A, Mid, B.
            var toCenter = (normal.Cross(ab) * ac.LengthSquared + ac.Cross(normal) * ab.LengthSquared) / (2 * n2);
            center = A + toCenter;
            radius = toCenter.Length;
            normal = normal.Unit();
            var u = (A - center).Unit();
            var v = normal.Cross(u);
            var e = B - center;
            sweep = Math.Atan2(e.Dot(v), e.Dot(u));
            if (sweep <= 0)
                sweep += 2 * Math.PI;
            return true;
        }

        public override List<Vec3> Tessellate(double maxAngle = Math.PI / 24)
        {
            if (!TryGetCircle(out var center, out var radius, out var normal, out var sweep))
                return new List<Vec3> { A, B };
            var u = (A - center).Unit();
            var v = normal.Cross(u);
            int n = Math.Max(2, (int)Math.Ceiling(sweep / maxAngle));
            var points = new List<Vec3>(n + 1) { A };
            for (int i = 1; i < n; i++)
            {
                double t = sweep * i / n;
                points.Add(center + u * (radius * Math.Cos(t)) + v * (radius * Math.Sin(t)));
            }
            points.Add(B);
            return points;
        }
    }

    /// <summary>A closed ellipse (circle if both radii are equal): centre and the two semi-axis vectors.
    /// It runs from centre+XAxis counter-clockwise around XAxis×YAxis.</summary>
    public sealed class EllipseSegment : Segment
    {
        public EllipseSegment(Vec3 center, Vec3 xAxis, Vec3 yAxis)
        {
            Center = center;
            XAxis = xAxis;
            YAxis = yAxis;
        }

        public Vec3 Center { get; }

        public Vec3 XAxis { get; }

        public Vec3 YAxis { get; }

        public bool IsCircle => Math.Abs(XAxis.Length - YAxis.Length) < 1e-9 * Math.Max(1, XAxis.Length) && Math.Abs(XAxis.Dot(YAxis)) < 1e-9 * Math.Max(1, XAxis.LengthSquared);

        public override Vec3 Start => Center + XAxis;

        public override Vec3 End => Center + XAxis;

        public override Segment Transformed(Mat4 m) => new EllipseSegment(m.Point(Center), m.Vector(XAxis), m.Vector(YAxis));

        public override Segment Reversed() => new EllipseSegment(Center, XAxis, -YAxis);

        public override List<Vec3> Tessellate(double maxAngle = Math.PI / 24)
        {
            int n = Math.Max(8, (int)Math.Ceiling(2 * Math.PI / maxAngle));
            var points = new List<Vec3>(n + 1);
            for (int i = 0; i <= n; i++)
            {
                double t = 2 * Math.PI * i / n;
                points.Add(Center + XAxis * Math.Cos(t) + YAxis * Math.Sin(t));
            }
            return points;
        }
    }

    /// <summary>A NURBS curve: control points, weights (null: non-rational) and the full knot vector
    /// (count = points + degree + 1).</summary>
    public sealed class NurbsSegment : Segment
    {
        public NurbsSegment(int degree, Vec3[] points, double[] weights, double[] knots)
        {
            Degree = degree;
            Points = points;
            Weights = weights;
            Knots = knots;
        }

        public int Degree { get; }

        public Vec3[] Points { get; }

        public double[] Weights { get; }

        public double[] Knots { get; }

        public override Vec3 Start => Evaluate(Knots[Degree]);

        public override Vec3 End => Evaluate(Knots[Knots.Length - Degree - 1]);

        public override Segment Transformed(Mat4 m)
        {
            var points = new Vec3[Points.Length];
            for (int i = 0; i < points.Length; i++)
                points[i] = m.Point(Points[i]);
            return new NurbsSegment(Degree, points, Weights, Knots);
        }

        public override Segment Reversed()
        {
            var points = (Vec3[])Points.Clone();
            Array.Reverse(points);
            double[] weights = null;
            if (Weights != null)
            {
                weights = (double[])Weights.Clone();
                Array.Reverse(weights);
            }
            var knots = new double[Knots.Length];
            double a = Knots[0], b = Knots[Knots.Length - 1];
            for (int i = 0; i < knots.Length; i++)
                knots[i] = a + b - Knots[Knots.Length - 1 - i];
            return new NurbsSegment(Degree, points, weights, knots);
        }

        public Vec3 Evaluate(double t)
        {
            // de Boor on homogeneous coordinates.
            int p = Degree;
            int n = Points.Length - 1;
            int k = p;
            while (k < n && t >= Knots[k + 1])
                k++;
            var d = new double[p + 1, 4];
            for (int j = 0; j <= p; j++)
            {
                int index = j + k - p;
                if (index < 0)
                    index = 0;
                if (index > n)
                    index = n;
                double w = Weights != null ? Weights[index] : 1.0;
                d[j, 0] = Points[index].X * w;
                d[j, 1] = Points[index].Y * w;
                d[j, 2] = Points[index].Z * w;
                d[j, 3] = w;
            }
            for (int r = 1; r <= p; r++)
            {
                for (int j = p; j >= r; j--)
                {
                    int i = j + k - p;
                    double denom = Knots[i + p - r + 1] - Knots[i];
                    double alpha = denom == 0 ? 0 : (t - Knots[i]) / denom;
                    for (int c = 0; c < 4; c++)
                        d[j, c] = (1 - alpha) * d[j - 1, c] + alpha * d[j, c];
                }
            }
            double ww = d[p, 3] == 0 ? 1 : d[p, 3];
            return new Vec3(d[p, 0] / ww, d[p, 1] / ww, d[p, 2] / ww);
        }

        public override List<Vec3> Tessellate(double maxAngle = Math.PI / 24)
        {
            double a = Knots[Degree], b = Knots[Knots.Length - Degree - 1];
            int n = Math.Max(8, Points.Length * 4);
            var points = new List<Vec3>(n + 1);
            for (int i = 0; i <= n; i++)
                points.Add(Evaluate(a + (b - a) * i / n));
            return points;
        }
    }

    /// <summary>A chain of segments; closed if the last ends where the first starts.</summary>
    public sealed class CurveLoop
    {
        public CurveLoop(List<Segment> segments)
        {
            Segments = segments;
        }

        public List<Segment> Segments { get; }

        public CurveLoop Transformed(Mat4 m)
        {
            var result = new List<Segment>(Segments.Count);
            foreach (var s in Segments)
                result.Add(s.Transformed(m));
            return new CurveLoop(result);
        }

        public CurveLoop Reversed()
        {
            var result = new List<Segment>(Segments.Count);
            for (int i = Segments.Count - 1; i >= 0; i--)
                result.Add(Segments[i].Reversed());
            return new CurveLoop(result);
        }

        /// <summary>The loop as a polygon (no repeated closing point).</summary>
        public List<Vec3> ToPolygon(double maxAngle = Math.PI / 24)
        {
            var points = new List<Vec3>();
            foreach (var segment in Segments)
            {
                var part = segment.Tessellate(maxAngle);
                for (int i = 0; i < part.Count; i++)
                {
                    if (points.Count > 0 && (points[points.Count - 1] - part[i]).LengthSquared < 1e-20)
                        continue;
                    points.Add(part[i]);
                }
            }
            if (points.Count > 1 && (points[0] - points[points.Count - 1]).LengthSquared < 1e-20)
                points.RemoveAt(points.Count - 1);
            return points;
        }

        /// <summary>Signed area of the loop projected on the XY plane (profiles live there).</summary>
        public double SignedAreaXY()
        {
            var polygon = ToPolygon();
            double area = 0;
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            return area / 2;
        }

        public bool IsSingleClosedCurve => Segments.Count == 1 && Segments[0] is EllipseSegment;
    }

    /// <summary>A planar region in the XY plane of its profile: one outer loop and holes.</summary>
    public sealed class ProfileRegion
    {
        public ProfileRegion(CurveLoop outer, List<CurveLoop> holes = null)
        {
            Outer = outer;
            Holes = holes ?? new List<CurveLoop>();
        }

        public CurveLoop Outer { get; }

        public List<CurveLoop> Holes { get; }

        public ProfileRegion Transformed(Mat4 m)
        {
            var holes = new List<CurveLoop>(Holes.Count);
            foreach (var h in Holes)
                holes.Add(h.Transformed(m));
            return new ProfileRegion(Outer.Transformed(m), holes);
        }
    }

    /// <summary>A profile (IfcProfileDef): one or more regions in its XY plane.</summary>
    public sealed class Profile
    {
        public Profile(List<ProfileRegion> regions)
        {
            Regions = regions;
        }

        public List<ProfileRegion> Regions { get; }

        public Profile Transformed(Mat4 m)
        {
            var regions = new List<ProfileRegion>(Regions.Count);
            foreach (var r in Regions)
                regions.Add(r.Transformed(m));
            return new Profile(regions);
        }
    }

    // ------------------------------------------------------------------ solids and surfaces

    public abstract class Shape
    {
    }

    /// <summary>IfcExtrudedAreaSolid: the profile (in the XY plane of <see cref="Position"/>) swept along
    /// <see cref="Direction"/> (in item coordinates, unit length) by <see cref="Depth"/>.</summary>
    public sealed class ExtrusionShape : Shape
    {
        public ExtrusionShape(Profile profile, Mat4 position, Vec3 direction, double depth)
        {
            Profile = profile;
            Position = position;
            Direction = direction;
            Depth = depth;
        }

        public Profile Profile { get; }

        public Mat4 Position { get; }

        public Vec3 Direction { get; }

        public double Depth { get; }
    }

    /// <summary>IfcRevolvedAreaSolid: profile in the XY plane of <see cref="Position"/>, revolved about an axis through
    /// <see cref="AxisPoint"/> with <see cref="AxisDirection"/> (item coordinates) by <see cref="Angle"/> radians.</summary>
    public sealed class RevolutionShape : Shape
    {
        public RevolutionShape(Profile profile, Mat4 position, Vec3 axisPoint, Vec3 axisDirection, double angle)
        {
            Profile = profile;
            Position = position;
            AxisPoint = axisPoint;
            AxisDirection = axisDirection;
            Angle = angle;
        }

        public Profile Profile { get; }

        public Mat4 Position { get; }

        public Vec3 AxisPoint { get; }

        public Vec3 AxisDirection { get; }

        public double Angle { get; }
    }

    /// <summary>IfcSweptDiskSolid: a pipe of <see cref="Radius"/> along a 3D path.</summary>
    public sealed class PipeShape : Shape
    {
        public PipeShape(CurveLoop path, double radius, double innerRadius)
        {
            Path = path;
            Radius = radius;
            InnerRadius = innerRadius;
        }

        public CurveLoop Path { get; }

        public double Radius { get; }

        public double InnerRadius { get; }
    }

    /// <summary>A sphere (IfcSphere).</summary>
    public sealed class SphereShape : Shape
    {
        public SphereShape(Vec3 center, double radius)
        {
            Center = center;
            Radius = radius;
        }

        public Vec3 Center { get; }

        public double Radius { get; }
    }

    /// <summary>Triangles: faceted breps, face sets, surface models. <see cref="Closed"/> if it bounds a solid.
    /// <see cref="FaceIds"/> gives the original planar face of each triangle (for merging back into polygons).</summary>
    public sealed class MeshShape : Shape
    {
        public MeshShape(List<Vec3> vertices, List<int> triangles, List<int> faceIds, bool closed)
        {
            Vertices = vertices;
            Triangles = triangles;
            FaceIds = faceIds;
            Closed = closed;
        }

        public List<Vec3> Vertices { get; }

        /// <summary>Three vertex indices per triangle.</summary>
        public List<int> Triangles { get; }

        public List<int> FaceIds { get; }

        public bool Closed { get; set; }

        public int TriangleCount => Triangles.Count / 3;
    }

    public enum BooleanOperator
    {
        Difference,
        Union,
        Intersection,
    }

    /// <summary>IfcBooleanResult / IfcBooleanClippingResult.</summary>
    public sealed class BooleanShape : Shape
    {
        public BooleanShape(BooleanOperator op, Shape first, Shape second)
        {
            Operator = op;
            First = first;
            Second = second;
        }

        public BooleanOperator Operator { get; }

        public Shape First { get; }

        public Shape Second { get; }
    }

    /// <summary>IfcHalfSpaceSolid: everything on the side of the plane that <see cref="Normal"/> points to (the normal
    /// here always points into the half-space solid). With a boundary (IfcPolygonalBoundedHalfSpace) only the part
    /// inside the prism of <see cref="Boundary"/> (in the XY plane of <see cref="BoundaryPosition"/>, along its Z).</summary>
    public sealed class HalfSpaceShape : Shape
    {
        public HalfSpaceShape(Vec3 point, Vec3 normal, CurveLoop boundary, Mat4 boundaryPosition)
        {
            Point = point;
            Normal = normal;
            Boundary = boundary;
            BoundaryPosition = boundaryPosition;
        }

        public Vec3 Point { get; }

        public Vec3 Normal { get; }

        public CurveLoop Boundary { get; }

        public Mat4 BoundaryPosition { get; }
    }

    /// <summary>A shape in other coordinates (IfcMappedItem, CSG primitives' placement).</summary>
    public sealed class TransformedShape : Shape
    {
        public TransformedShape(Mat4 transform, Shape shape)
        {
            Transform = transform;
            Shape = shape;
        }

        public Mat4 Transform { get; }

        public Shape Shape { get; }
    }

    // ------------------------------------------------------------------ products

    /// <summary>An RGB colour with transparency 0 (opaque) .. 1.</summary>
    public readonly struct IfcColor : IEquatable<IfcColor>
    {
        public IfcColor(double r, double g, double b, double transparency = 0)
        {
            R = Clamp(r);
            G = Clamp(g);
            B = Clamp(b);
            Transparency = Clamp(transparency);
        }

        public double R { get; }

        public double G { get; }

        public double B { get; }

        public double Transparency { get; }

        public byte R8 => (byte)Math.Round(R * 255);

        public byte G8 => (byte)Math.Round(G * 255);

        public byte B8 => (byte)Math.Round(B * 255);

        private static double Clamp(double v) => double.IsNaN(v) ? 0 : Math.Max(0, Math.Min(1, v));

        public bool Equals(IfcColor other) => R8 == other.R8 && G8 == other.G8 && B8 == other.B8 && Math.Abs(Transparency - other.Transparency) < 0.004;

        public override bool Equals(object obj) => obj is IfcColor c && Equals(c);

        public override int GetHashCode() => (R8 << 16) | (G8 << 8) | B8;

        public override string ToString() => $"RGB({R8},{G8},{B8}) T={Transparency:0.##}";
    }

    /// <summary>One representation item of a product, ready to place: the shape in item coordinates, the
    /// transformation to world (file units), its colour; for type geometry (IfcMappedItem) the map it comes from.</summary>
    public sealed class ShapeItem
    {
        public ShapeItem(Shape shape, Mat4 toWorld, IfcColor? color)
        {
            Shape = shape;
            ToWorld = toWorld;
            Color = color;
        }

        public Shape Shape { get; }

        public Mat4 ToWorld { get; set; }

        public IfcColor? Color { get; set; }

        /// <summary>The IfcRepresentationMap id if this item is type geometry; 0 otherwise.</summary>
        public int MapId { get; set; }

        /// <summary>For type geometry: map coordinates → world (the instance transformation of a block).</summary>
        public Mat4 MapToWorld { get; set; } = Mat4.Identity;

        /// <summary>For type geometry: item → map coordinates (inside the block definition).</summary>
        public Mat4 ItemToMap { get; set; } = Mat4.Identity;
    }

    /// <summary>The geometry of one product: its items and the openings to cut (in world coordinates).</summary>
    public sealed class ProductGeometry
    {
        /// <summary>The product's own placement (object → world). Building the geometry relative to it keeps the numbers
        /// small for Booleans even when the model sits far from the origin.</summary>
        public Mat4 Placement { get; set; } = Mat4.Identity;

        public List<ShapeItem> Items { get; } = new List<ShapeItem>();

        public List<ShapeItem> Openings { get; } = new List<ShapeItem>();

        /// <summary>Representation items that could not be read (type name → count), for the import report.</summary>
        public Dictionary<string, int> Unsupported { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

        public void NoteUnsupported(string type)
        {
            Unsupported.TryGetValue(type, out var n);
            Unsupported[type] = n + 1;
        }
    }
}
