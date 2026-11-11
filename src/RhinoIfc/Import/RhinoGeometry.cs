using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;
using RhinoIfc.Core;
using RhinoIfc.Core.Geometry;

namespace RhinoIfc.Import
{
    /// <summary>One piece of Rhino geometry of an element, with its IFC colour.</summary>
    public sealed class BuiltGeometry
    {
        public BuiltGeometry(GeometryBase geometry, IfcColor? color)
        {
            Geometry = geometry;
            Color = color;
        }

        public GeometryBase Geometry { get; set; }

        public IfcColor? Color { get; }
    }

    /// <summary>Turns the import's <see cref="Shape"/>s into RhinoCommon geometry: Extrusions where IFC has a straight
    /// extrusion, Breps for everything else (joined, coplanar faces merged), Meshes for very detailed faceted elements or
    /// when meshes are asked for. Thread-safe: one instance per import, used from several threads.</summary>
    public sealed class RhinoGeometry
    {
        private readonly ImportOptions _options;

        /// <param name="tolerance">Absolute tolerance in the file's length unit.</param>
        public RhinoGeometry(ImportOptions options, double tolerance)
        {
            _options = options;
            Tolerance = tolerance;
        }

        public double Tolerance { get; }

        public const double AngleTolerance = Math.PI / 180;

        // ------------------------------------------------------------------ conversions

        public static Point3d P(Vec3 v) => new Point3d(v.X, v.Y, v.Z);

        public static Vector3d V(Vec3 v) => new Vector3d(v.X, v.Y, v.Z);

        public static Transform T(Mat4 m)
        {
            var t = Transform.Identity;
            t.M00 = m.M00; t.M01 = m.M01; t.M02 = m.M02; t.M03 = m.M03;
            t.M10 = m.M10; t.M11 = m.M11; t.M12 = m.M12; t.M13 = m.M13;
            t.M20 = m.M20; t.M21 = m.M21; t.M22 = m.M22; t.M23 = m.M23;
            t.M30 = 0; t.M31 = 0; t.M32 = 0; t.M33 = 1;
            return t;
        }

        public Curve Curve(Segment segment)
        {
            switch (segment)
            {
                case LineSegment line:
                    return new LineCurve(P(line.A), P(line.B));
                case ArcSegment arc:
                {
                    var a = new Arc(P(arc.A), P(arc.Mid), P(arc.B));
                    if (a.IsValid)
                        return new ArcCurve(a);
                    return new LineCurve(P(arc.A), P(arc.B));
                }
                case EllipseSegment ellipse:
                {
                    var plane = new Plane(P(ellipse.Center), V(ellipse.XAxis), V(ellipse.YAxis));
                    if (ellipse.IsCircle)
                        return new ArcCurve(new Circle(plane, ellipse.XAxis.Length));
                    // Plane(origin, x, y) orthonormalises; take the semi-axes' lengths.
                    return new Ellipse(plane, ellipse.XAxis.Length, ellipse.YAxis.Length).ToNurbsCurve();
                }
                case NurbsSegment nurbs:
                {
                    bool rational = nurbs.Weights != null;
                    var curve = new NurbsCurve(3, rational, nurbs.Degree + 1, nurbs.Points.Length);
                    for (int i = 0; i < nurbs.Points.Length; i++)
                    {
                        var p = P(nurbs.Points[i]);
                        if (rational)
                            curve.Points.SetPoint(i, p, nurbs.Weights[i]);
                        else
                            curve.Points.SetPoint(i, p);
                    }
                    // Rhino's knot vector leaves out the first and last of the full one.
                    for (int i = 0; i < curve.Knots.Count; i++)
                        curve.Knots[i] = nurbs.Knots[i + 1];
                    return curve.IsValid ? curve : null;
                }
            }
            return null;
        }

        /// <summary>A loop as one curve (PolyCurve for several segments), closed if it should be.</summary>
        public Curve Curve(CurveLoop loop, bool close)
        {
            if (loop == null || loop.Segments.Count == 0)
                return null;
            if (loop.Segments.Count == 1)
            {
                var single = Curve(loop.Segments[0]);
                if (single != null && close && !single.IsClosed && single is PolylineCurve == false)
                {
                    var poly = new PolyCurve();
                    poly.Append(single);
                    poly.MakeClosed(Tolerance);
                    return poly;
                }
                return single;
            }
            var polyCurve = new PolyCurve();
            foreach (var segment in loop.Segments)
            {
                var c = Curve(segment);
                if (c == null || !c.IsValid)
                    continue;
                if (polyCurve.SegmentCount > 0 && polyCurve.PointAtEnd.DistanceTo(c.PointAtStart) > Tolerance)
                    polyCurve.Append(new LineCurve(polyCurve.PointAtEnd, c.PointAtStart));
                polyCurve.Append(c);
            }
            if (polyCurve.SegmentCount == 0)
                return null;
            if (close && !polyCurve.IsClosed)
                polyCurve.MakeClosed(Tolerance);
            polyCurve.RemoveNesting();
            return polyCurve;
        }

        // ------------------------------------------------------------------ shapes

        /// <summary>The shape's geometry in its own coordinates (transform already applied if given).</summary>
        public List<GeometryBase> Build(Shape shape, Mat4 transform, List<string> problems)
        {
            var result = new List<GeometryBase>();
            switch (shape)
            {
                case ExtrusionShape e:
                    result.AddRange(Extrusion(e, transform));
                    break;
                case RevolutionShape r:
                    result.AddRange(Transformed(Revolution(r), transform));
                    break;
                case PipeShape p:
                    result.AddRange(Transformed(Pipe(p), transform));
                    break;
                case SphereShape s:
                    result.AddRange(Transformed(new[] { new Sphere(P(s.Center), s.Radius).ToBrep() }, transform));
                    break;
                case MeshShape m:
                    result.AddRange(Transformed(new[] { Faceted(m) }, transform));
                    break;
                case TransformedShape t:
                    result.AddRange(Build(t.Shape, transform * t.Transform, problems));
                    break;
                case BooleanShape b:
                    result.AddRange(Transformed(Boolean(b, problems), transform));
                    break;
                case HalfSpaceShape _:
                    break;
            }
            return result;
        }

        private static IEnumerable<GeometryBase> Transformed(IEnumerable<GeometryBase> geometry, Mat4 transform)
        {
            bool identity = transform.IsIdentity;
            var t = T(transform);
            foreach (var g in geometry)
            {
                if (g == null)
                    continue;
                if (!identity)
                {
                    if (g is Extrusion && !transform.IsSimilarity())
                    {
                        // Extrusions take only similarity transformations; anything else as a Brep.
                        var brep = ((Extrusion)g).ToBrep(true);
                        brep.Transform(t);
                        yield return brep;
                        continue;
                    }
                    if (!g.Transform(t) && g is Extrusion ex)
                    {
                        var brep = ex.ToBrep(true);
                        brep.Transform(t);
                        yield return brep;
                        continue;
                    }
                }
                yield return g;
            }
        }

        // ------------------------------------------------------------------ extrusions

        private IEnumerable<GeometryBase> Extrusion(ExtrusionShape e, Mat4 transform)
        {
            var position = e.Position;
            var normal = position.ZColumn.Unit();
            double along = normal.Dot(e.Direction);
            bool straight = Math.Abs(Math.Abs(along) - 1) < 1e-9 && position.IsSimilarity(1e-9) && Math.Abs(position.XColumn.Length - 1) < 1e-9;
            var output = new List<GeometryBase>();
            foreach (var region in e.Profile.Regions)
            {
                GeometryBase g = null;
                if (straight)
                    g = RhinoExtrusion(region, position, e.Direction, e.Depth, along < 0);
                if (g == null)
                    g = ExtrusionBrep(region, position, e.Direction, e.Depth);
                if (g != null)
                    output.Add(g);
            }
            return Transformed(output, transform);
        }

        /// <summary>A Rhino Extrusion object (light, editable like Rhino's own): the profile in the XY plane of the
        /// path's frame, whose Z is the extrusion direction and Y the profile's Y.</summary>
        private Extrusion RhinoExtrusion(ProfileRegion region, Mat4 position, Vec3 direction, double depth, bool reversed)
        {
            // Reversed (direction = -Z): Y stays, Z flips, so X must flip: mirror the profile's x.
            var mirror = reversed ? new Mat4(-1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0) : Mat4.Identity;
            var outer = Curve(reversed ? region.Outer.Transformed(mirror) : region.Outer, true);
            if (outer == null || !outer.IsClosed)
                return null;
            var extrusion = new Extrusion();
            var start = P(position.Origin);
            var end = start + V(direction) * depth;
            var up = V(position.YColumn.Unit());
            if (!extrusion.SetPathAndUp(start, end, up))
                return null;
            if (!extrusion.SetOuterProfile(outer, true))
                return null;
            foreach (var hole in region.Holes)
            {
                var inner = Curve(reversed ? hole.Transformed(mirror) : hole, true);
                if (inner == null || !inner.IsClosed || !extrusion.AddInnerProfile(inner))
                    return null;
            }
            if (!extrusion.IsValid)
                return null;
            // Self-check: the profile must land where the IFC puts it (else the Brep route is used).
            var xform = extrusion.GetProfileTransformation(0);
            foreach (var t in new[] { 0.0, 0.37, 0.71 })
            {
                var local = outer.PointAt(outer.Domain.ParameterAt(t));
                var expected = P(position.Point(new Vec3(reversed ? -local.X : local.X, local.Y, 0)));
                local.Transform(xform);
                if (local.DistanceTo(expected) > Tolerance * 10)
                    return null;
            }
            return extrusion;
        }

        /// <summary>Any extrusion (also oblique) as a closed Brep: planar caps and extruded sides, joined.</summary>
        private Brep ExtrusionBrep(ProfileRegion region, Mat4 position, Vec3 direction, double depth)
        {
            var vector = V(direction) * depth;
            var loops = new List<Curve>();
            var outer = Curve(region.Outer.Transformed(position), true);
            if (outer == null)
                return null;
            loops.Add(outer);
            foreach (var hole in region.Holes)
            {
                var c = Curve(hole.Transformed(position), true);
                if (c != null)
                    loops.Add(c);
            }
            var pieces = new List<Brep>();
            var bottom = Brep.CreatePlanarBreps(loops, Tolerance);
            if (bottom == null || bottom.Length == 0)
                return null;
            pieces.AddRange(bottom);
            foreach (var cap in bottom)
            {
                var top = cap.DuplicateBrep();
                top.Translate(vector);
                pieces.Add(top);
            }
            foreach (var loop in loops)
            {
                var side = Surface.CreateExtrusion(loop, vector);
                if (side != null)
                    pieces.Add(side.ToBrep());
            }
            return JoinSolid(pieces);
        }

        private Brep JoinSolid(List<Brep> pieces)
        {
            var joined = Brep.JoinBreps(pieces, Tolerance);
            if (joined == null || joined.Length == 0)
                return null;
            var brep = joined.OrderByDescending(b => b.Faces.Count).First();
            if (joined.Length > 1)
            {
                // Several pieces: keep them together in one Brep.
                brep = joined[0].DuplicateBrep();
                for (int i = 1; i < joined.Length; i++)
                    brep.Append(joined[i]);
            }
            if (brep.IsSolid && brep.SolidOrientation == BrepSolidOrientation.Inward)
                brep.Flip();
            return brep;
        }

        // ------------------------------------------------------------------ revolutions, pipes

        private IEnumerable<GeometryBase> Revolution(RevolutionShape r)
        {
            var axis = new Line(P(r.AxisPoint), P(r.AxisPoint + r.AxisDirection));
            bool full = Math.Abs(Math.Abs(r.Angle) - 2 * Math.PI) < 1e-6;
            double start = r.Angle >= 0 ? 0 : r.Angle;
            double end = r.Angle >= 0 ? r.Angle : 0;
            foreach (var region in r.Profile.Regions)
            {
                var pieces = new List<Brep>();
                var loops = new List<Curve>();
                var outer = Curve(region.Outer.Transformed(r.Position), true);
                if (outer == null)
                    continue;
                loops.Add(outer);
                foreach (var hole in region.Holes)
                {
                    var c = Curve(hole.Transformed(r.Position), true);
                    if (c != null)
                        loops.Add(c);
                }
                foreach (var loop in loops)
                {
                    var surface = RevSurface.Create(loop, axis, start, end);
                    if (surface != null)
                        pieces.Add(surface.ToBrep());
                }
                if (!full)
                {
                    var caps = Brep.CreatePlanarBreps(loops, Tolerance);
                    if (caps != null)
                    {
                        foreach (var cap in caps)
                        {
                            var a = cap.DuplicateBrep();
                            a.Rotate(start, axis.Direction, axis.From);
                            pieces.Add(a);
                            var b = cap.DuplicateBrep();
                            b.Rotate(end, axis.Direction, axis.From);
                            pieces.Add(b);
                        }
                    }
                }
                var brep = JoinSolid(pieces);
                if (brep != null)
                    yield return brep;
            }
        }

        private IEnumerable<GeometryBase> Pipe(PipeShape pipe)
        {
            var rail = Curve(pipe.Path, false);
            if (rail == null)
                yield break;
            Brep[] breps;
            if (pipe.InnerRadius > 0 && pipe.InnerRadius < pipe.Radius)
                breps = Brep.CreateThickPipe(rail, pipe.Radius, pipe.InnerRadius, false, PipeCapMode.Flat, true, Tolerance, AngleTolerance);
            else
                breps = Brep.CreatePipe(rail, pipe.Radius, false, PipeCapMode.Flat, true, Tolerance, AngleTolerance);
            if (breps == null)
                yield break;
            foreach (var b in breps)
                yield return b;
        }

        // ------------------------------------------------------------------ faceted geometry

        public static Mesh ToMesh(MeshShape shape)
        {
            var mesh = new Mesh();
            foreach (var v in shape.Vertices)
                mesh.Vertices.Add(v.X, v.Y, v.Z);
            for (int i = 0; i + 2 < shape.Triangles.Count; i += 3)
                mesh.Faces.AddFace(shape.Triangles[i], shape.Triangles[i + 1], shape.Triangles[i + 2]);
            mesh.Vertices.CombineIdentical(true, true);
            mesh.Faces.CullDegenerateFaces();
            mesh.Compact();
            mesh.Normals.ComputeNormals();
            return mesh;
        }

        /// <summary>Faceted geometry as a polysurface with coplanar faces merged; stays a mesh when meshes are asked for,
        /// when it has too many triangles, or when it is a curved shape made of facets (many faces after merging).</summary>
        private GeometryBase Faceted(MeshShape shape)
        {
            var mesh = ToMesh(shape);
            if (mesh.Faces.Count == 0)
                return null;
            if (_options.Geometry == GeometryMode.Meshes || shape.TriangleCount > _options.MaxPolysurfaceTriangles)
                return mesh;
            var brep = FacetedBrep(mesh);
            return brep ?? (GeometryBase)mesh;
        }

        private Brep FacetedBrep(Mesh mesh)
        {
            Brep brep;
            try
            {
                brep = Brep.CreateFromMesh(mesh, true);
            }
            catch (Exception)
            {
                return null;
            }
            if (brep == null)
                return null;
            int before = brep.Faces.Count;
            brep.MergeCoplanarFaces(Tolerance, AngleTolerance);
            // Facets of a round shape do not merge: such elements read better as meshes.
            if (brep.Faces.Count > 250 && brep.Faces.Count > before / 3)
                return null;
            if (brep.IsSolid && brep.SolidOrientation == BrepSolidOrientation.Inward)
                brep.Flip();
            return brep;
        }

        // ------------------------------------------------------------------ Booleans

        /// <summary>The geometry as Breps (for Booleans); meshes become Breps if they are not too large.</summary>
        public List<Brep> ToBreps(IEnumerable<GeometryBase> geometry)
        {
            var result = new List<Brep>();
            foreach (var g in geometry)
            {
                switch (g)
                {
                    case Brep b:
                        result.Add(b);
                        break;
                    case Extrusion e:
                        result.Add(e.ToBrep(true));
                        break;
                    case Mesh m:
                        var brep = m.Faces.Count <= 20000 ? Brep.CreateFromMesh(m, true) : null;
                        if (brep != null)
                            result.Add(brep);
                        break;
                    case Surface s:
                        result.Add(s.ToBrep());
                        break;
                }
            }
            return result;
        }

        private IEnumerable<GeometryBase> Boolean(BooleanShape shape, List<string> problems)
        {
            var first = Build(shape.First, Mat4.Identity, problems);
            if (first.Count == 0)
                return first;
            if (shape.Operator == BooleanOperator.Difference)
                return Subtract(first, shape.Second, problems);

            var second = Build(shape.Second, Mat4.Identity, problems);
            if (second.Count == 0)
                return shape.Operator == BooleanOperator.Union ? first : new List<GeometryBase>();
            var a = ToBreps(first);
            var b = ToBreps(second);
            Brep[] result = null;
            try
            {
                result = shape.Operator == BooleanOperator.Union
                    ? Brep.CreateBooleanUnion(a.Concat(b), Tolerance)
                    : Brep.CreateBooleanIntersection(a, b, Tolerance);
            }
            catch (Exception)
            {
            }
            if (result == null || result.Length == 0)
            {
                problems?.Add(shape.Operator == BooleanOperator.Union ? "Vereinigung fehlgeschlagen" : "Schnittmenge fehlgeschlagen");
                return shape.Operator == BooleanOperator.Union ? first.Concat(second).ToList() : first;
            }
            return result;
        }

        private List<GeometryBase> Subtract(List<GeometryBase> first, Shape second, List<string> problems)
        {
            if (second is HalfSpaceShape h)
                return ClipHalfSpace(first, h, problems);
            if (second is BooleanShape u && u.Operator == BooleanOperator.Union)
                return Subtract(Subtract(first, u.First, problems), u.Second, problems);
            var cutters = ToBreps(Build(second, Mat4.Identity, problems));
            if (cutters.Count == 0)
                return first;
            return Difference(first, cutters, problems, "Differenz");
        }

        /// <summary>Subtracts cutters from geometry; on failure the geometry stays as it was and a note is added.</summary>
        public List<GeometryBase> Difference(List<GeometryBase> geometry, List<Brep> cutters, List<string> problems, string what)
        {
            var targets = ToBreps(geometry);
            if (targets.Count == 0)
                return geometry;
            // Only cutters that touch the target matter.
            var box = BoundingBox.Empty;
            foreach (var t in targets)
                box.Union(t.GetBoundingBox(false));
            box.Inflate(Tolerance * 10);
            var relevant = cutters.Where(c => Overlaps(box, c.GetBoundingBox(false))).ToList();
            if (relevant.Count == 0)
                return geometry;
            Brep[] result = null;
            try
            {
                result = Brep.CreateBooleanDifference(targets, relevant, Tolerance, false);
            }
            catch (Exception)
            {
            }
            if (result != null && result.Length > 0)
                return result.Cast<GeometryBase>().ToList();

            // All at once failed: one by one, keeping what works.
            var current = targets;
            int failed = 0;
            foreach (var cutter in relevant)
            {
                Brep[] step = null;
                try
                {
                    step = Brep.CreateBooleanDifference(current, new[] { cutter }, Tolerance, false);
                }
                catch (Exception)
                {
                }
                if (step != null && step.Length > 0)
                    current = step.ToList();
                else
                    failed++;
            }
            if (failed > 0)
                problems?.Add($"{what}: {failed} von {relevant.Count} nicht ausgeschnitten");
            return current.Cast<GeometryBase>().ToList();
        }

        private static bool Overlaps(BoundingBox a, BoundingBox b) =>
            a.IsValid && b.IsValid && a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y
            && a.Min.Z <= b.Max.Z && b.Min.Z <= a.Max.Z;

        /// <summary>IfcBooleanClippingResult with a half-space: the part on the half-space's side is removed. Unbounded:
        /// a planar cut and cap; bounded (IfcPolygonalBoundedHalfSpace): Boolean difference with the cut prism.</summary>
        private List<GeometryBase> ClipHalfSpace(List<GeometryBase> first, HalfSpaceShape h, List<string> problems)
        {
            var breps = ToBreps(first);
            if (breps.Count == 0)
                return first;
            var box = BoundingBox.Empty;
            foreach (var b in breps)
                box.Union(b.GetBoundingBox(false));
            var origin = P(h.Point);
            var normal = V(h.Normal);
            normal.Unitize();
            if (h.Boundary == null)
            {
                // Trim keeps what lies opposite the plane's normal; the normal points into the removed half-space.
                var plane = new Plane(origin, normal);
                if (Side(box, plane) < 0)
                    return first; // entirely kept
                if (Side(box, plane) > 0)
                    return new List<GeometryBase>(); // entirely removed
                var result = new List<GeometryBase>();
                foreach (var b in breps)
                {
                    var parts = TrimKeepBack(b, plane);
                    if (parts == null)
                    {
                        // No intersection: the Brep lies on one side.
                        var c = b.GetBoundingBox(false).Center;
                        if (plane.DistanceTo(c) < 0)
                            result.Add(b);
                        continue;
                    }
                    result.AddRange(parts);
                }
                return result;
            }

            // Bounded: prism of the boundary along its position's Z, long enough to pass the element, cut by the plane.
            double size = box.Diagonal.Length + box.Center.DistanceTo(origin) + 1;
            var boundaryPosition = h.BoundaryPosition;
            var profile = Curve(h.Boundary, true);
            if (profile == null)
                return first;
            profile.Transform(T(boundaryPosition));
            var axis = V(boundaryPosition.ZColumn.Unit());
            profile.Translate(-axis * size);
            var prism = Extrude(profile, axis * (2 * size));
            if (prism == null)
            {
                problems?.Add("Schnitt mit begrenztem Halbraum nicht möglich");
                return first;
            }
            // Keep the prism's part inside the half-space (in front of the plane).
            var cut = TrimKeepBack(prism, new Plane(origin, -normal));
            Brep cutter;
            if (cut == null)
            {
                var c = prism.GetBoundingBox(false).Center;
                if (new Plane(origin, normal).DistanceTo(c) < 0)
                    return first; // the prism lies outside the half-space: nothing to remove
                cutter = prism;
            }
            else
                cutter = cut[0];
            return Difference(breps.Cast<GeometryBase>().ToList(), new List<Brep> { cutter }, problems, "Schnitt");
        }

        /// <summary>The parts of a Brep behind the plane (opposite its normal), capped; null if the plane misses it.
        /// Checks the side of the result, so it does not depend on how Brep.Trim orients its cutter.</summary>
        private Brep[] TrimKeepBack(Brep brep, Plane plane)
        {
            Brep[] parts = null;
            try
            {
                parts = brep.Trim(plane, Tolerance);
                if (parts != null && parts.Length > 0 && parts.All(p => plane.DistanceTo(p.GetBoundingBox(false).Center) > 0))
                {
                    var flipped = plane;
                    flipped.Flip();
                    parts = brep.Trim(flipped, Tolerance);
                }
            }
            catch (Exception)
            {
                parts = null;
            }
            if (parts == null || parts.Length == 0)
                return null;
            var result = new List<Brep>();
            foreach (var part in parts)
            {
                var capped = part.CapPlanarHoles(Tolerance) ?? part;
                if (capped.IsSolid && capped.SolidOrientation == BrepSolidOrientation.Inward)
                    capped.Flip();
                result.Add(capped);
            }
            return result.ToArray();
        }

        /// <summary>-1: the box lies entirely on the back of the plane, +1: entirely in front, 0: crossing.</summary>
        private int Side(BoundingBox box, Plane plane)
        {
            double min = double.MaxValue, max = double.MinValue;
            foreach (var corner in box.GetCorners())
            {
                double d = plane.DistanceTo(corner);
                min = Math.Min(min, d);
                max = Math.Max(max, d);
            }
            if (max <= Tolerance)
                return -1;
            if (min >= -Tolerance)
                return 1;
            return 0;
        }

        private Brep Extrude(Curve profile, Vector3d vector)
        {
            var caps = Brep.CreatePlanarBreps(profile, Tolerance);
            if (caps == null || caps.Length == 0)
                return null;
            var pieces = new List<Brep>();
            pieces.AddRange(caps);
            foreach (var cap in caps)
            {
                var top = cap.DuplicateBrep();
                top.Translate(vector);
                pieces.Add(top);
            }
            var side = Surface.CreateExtrusion(profile, vector);
            if (side != null)
                pieces.Add(side.ToBrep());
            return JoinSolid(pieces);
        }

        // ------------------------------------------------------------------ final form

        /// <summary>In mesh mode everything becomes a mesh (render mesh quality); otherwise unchanged.</summary>
        public GeometryBase Final(GeometryBase g)
        {
            if (_options.Geometry != GeometryMode.Meshes || g is Mesh)
                return g;
            Brep brep = g as Brep ?? (g as Extrusion)?.ToBrep(true);
            if (brep == null)
                return g;
            var meshes = Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh);
            if (meshes == null || meshes.Length == 0)
                return g;
            var mesh = new Mesh();
            foreach (var m in meshes)
                mesh.Append(m);
            mesh.Normals.ComputeNormals();
            mesh.Compact();
            return mesh;
        }
    }
}
