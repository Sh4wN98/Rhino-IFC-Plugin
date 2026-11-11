using System;
using System.Collections.Generic;
using RhinoIfc.Core.Model;
using RhinoIfc.Core.Step;

namespace RhinoIfc.Core.Geometry
{
    /// <summary>IFC curves (IfcPolyline, IfcCompositeCurve, IfcTrimmedCurve, IfcIndexedPolyCurve, IfcCircle,
    /// IfcEllipse, B-splines) as segment chains, and IFC profiles (parameterised and arbitrary) as regions.</summary>
    public sealed class CurveBuilder
    {
        private readonly IfcModel _model;

        public CurveBuilder(IfcModel model)
        {
            _model = model;
        }

        private StepFile File => _model.File;

        // ------------------------------------------------------------------ curves

        /// <summary>The curve as a chain of segments; null if the curve type is not supported.</summary>
        public CurveLoop Curve(StepEntity curve, int depth = 0)
        {
            if (curve == null || depth > 20)
                return null;
            var segments = new List<Segment>();
            if (!AddCurve(curve, segments, depth))
                return null;
            return segments.Count > 0 ? new CurveLoop(segments) : null;
        }

        private bool AddCurve(StepEntity curve, List<Segment> segments, int depth)
        {
            switch (curve.Type)
            {
                case "IFCPOLYLINE":
                {
                    var points = new List<Vec3>();
                    foreach (var p in curve.Entities("Points"))
                        points.Add(_model.Point(p));
                    AddPolyline(points, segments);
                    return true;
                }
                case "IFCINDEXEDPOLYCURVE":
                    return AddIndexedPolyCurve(curve, segments);
                case "IFCCOMPOSITECURVE":
                case "IFC2DCOMPOSITECURVE":
                case "IFCCOMPOSITECURVEONSURFACE":
                {
                    foreach (var segment in curve.Entities("Segments"))
                    {
                        var parent = segment.Entity("ParentCurve");
                        var part = Curve(parent, depth + 1);
                        if (part == null)
                            continue;
                        bool sameSense = segment.Bool("SameSense") ?? true;
                        if (!sameSense)
                            part = part.Reversed();
                        segments.AddRange(part.Segments);
                    }
                    return true;
                }
                case "IFCTRIMMEDCURVE":
                    return AddTrimmedCurve(curve, segments, depth);
                case "IFCCIRCLE":
                {
                    var frame = _model.Axis2Placement(curve.Entity("Position"));
                    double r = curve.Double("Radius");
                    if (!(r > 0))
                        return false;
                    segments.Add(new EllipseSegment(frame.Origin, frame.XColumn.Unit() * r, frame.YColumn.Unit() * r));
                    return true;
                }
                case "IFCELLIPSE":
                {
                    var frame = _model.Axis2Placement(curve.Entity("Position"));
                    double a = curve.Double("SemiAxis1"), b = curve.Double("SemiAxis2");
                    if (!(a > 0) || !(b > 0))
                        return false;
                    segments.Add(new EllipseSegment(frame.Origin, frame.XColumn.Unit() * a, frame.YColumn.Unit() * b));
                    return true;
                }
                case "IFCBSPLINECURVEWITHKNOTS":
                case "IFCRATIONALBSPLINECURVEWITHKNOTS":
                {
                    var nurbs = BSpline(curve);
                    if (nurbs == null)
                        return false;
                    segments.Add(nurbs);
                    return true;
                }
                case "IFCOFFSETCURVE2D":
                case "IFCOFFSETCURVE3D":
                    return false;
                default:
                    return false;
            }
        }

        private static void AddPolyline(List<Vec3> points, List<Segment> segments)
        {
            for (int i = 0; i + 1 < points.Count; i++)
                if ((points[i + 1] - points[i]).LengthSquared > 0)
                    segments.Add(new LineSegment(points[i], points[i + 1]));
        }

        private bool AddIndexedPolyCurve(StepEntity curve, List<Segment> segments)
        {
            var pointList = curve.Entity("Points");
            if (pointList == null)
                return false;
            var coordinates = new List<Vec3>();
            foreach (var item in pointList.List("CoordList"))
                if (item is object[] c)
                    coordinates.Add(IfcModel.ToVec(c));
            var segmentList = curve["Segments"] as object[];
            if (segmentList == null || segmentList.Length == 0)
            {
                AddPolyline(coordinates, segments);
                return true;
            }
            Vec3 At(object index)
            {
                long i = index is long l ? l : (long)StepEntity.AsDouble(index, 0);
                return i >= 1 && i <= coordinates.Count ? coordinates[(int)i - 1] : Vec3.Zero;
            }
            foreach (var value in segmentList)
            {
                if (!(value is StepTyped typed) || !(typed.Value is object[] indices))
                    continue;
                if (typed.Type == "IFCARCINDEX" && indices.Length == 3)
                    segments.Add(new ArcSegment(At(indices[0]), At(indices[1]), At(indices[2])));
                else
                {
                    var points = new List<Vec3>();
                    foreach (var index in indices)
                        points.Add(At(index));
                    AddPolyline(points, segments);
                }
            }
            return true;
        }

        private bool AddTrimmedCurve(StepEntity curve, List<Segment> segments, int depth)
        {
            var basis = curve.Entity("BasisCurve");
            if (basis == null)
                return false;
            bool sense = curve.Bool("SenseAgreement") ?? true;
            string master = curve.Enum("MasterRepresentation") ?? "UNSPECIFIED";
            var trim1 = curve.List("Trim1");
            var trim2 = curve.List("Trim2");

            if (basis.IsA("IFCLINE"))
            {
                var origin = _model.Point(basis.Entity("Pnt"));
                var dir = _model.Direction(basis.Entity("Dir"), Vec3.XAxis);
                Vec3 Eval(object[] trim)
                {
                    TrimValues(trim, out var point, out var parameter);
                    if (point != null && (master != "PARAMETER" || parameter == null))
                        return point.Value;
                    if (parameter != null)
                        return origin + dir * parameter.Value;
                    return point ?? origin;
                }
                var a = Eval(trim1);
                var b = Eval(trim2);
                segments.Add(new LineSegment(a, b));
                return true;
            }

            if (basis.IsA("IFCCIRCLE") || basis.IsA("IFCELLIPSE"))
            {
                var frame = _model.Axis2Placement(basis.Entity("Position"));
                double ra, rb;
                if (basis.IsA("IFCCIRCLE"))
                    ra = rb = basis.Double("Radius");
                else
                {
                    ra = basis.Double("SemiAxis1");
                    rb = basis.Double("SemiAxis2");
                }
                if (!(ra > 0) || !(rb > 0))
                    return false;
                var center = frame.Origin;
                var u = frame.XColumn.Unit();
                var v = frame.YColumn.Unit();

                double Angle(object[] trim)
                {
                    TrimValues(trim, out var point, out var parameter);
                    if (parameter != null && (master == "PARAMETER" || point == null))
                        return parameter.Value * _model.AngleToRadians;
                    if (point != null)
                    {
                        var d = point.Value - center;
                        return Math.Atan2(d.Dot(v) / rb, d.Dot(u) / ra);
                    }
                    return 0;
                }

                double t1 = Angle(trim1);
                double t2 = Angle(trim2);
                Vec3 At(double t) => center + u * (ra * Math.Cos(t)) + v * (rb * Math.Sin(t));
                // Sweep from t1 to t2: counter-clockwise if SenseAgreement, else clockwise.
                double sweep = sense ? t2 - t1 : t1 - t2;
                while (sweep <= 1e-12)
                    sweep += 2 * Math.PI;
                while (sweep > 2 * Math.PI + 1e-12)
                    sweep -= 2 * Math.PI;
                double direction = sense ? 1 : -1;
                if (Math.Abs(ra - rb) < 1e-12 * ra)
                {
                    // Arcs over 180° are split so each piece is a well-defined three-point arc.
                    int pieces = sweep > Math.PI * 0.99 ? (sweep > Math.PI * 1.98 ? 3 : 2) : 1;
                    for (int i = 0; i < pieces; i++)
                    {
                        double s = t1 + direction * sweep * i / pieces;
                        double e = t1 + direction * sweep * (i + 1) / pieces;
                        segments.Add(new ArcSegment(At(s), At((s + e) / 2), At(e)));
                    }
                }
                else
                {
                    // Elliptic arc: polyline (rare in building models).
                    int n = Math.Max(4, (int)Math.Ceiling(sweep / (Math.PI / 36)));
                    var points = new List<Vec3>();
                    for (int i = 0; i <= n; i++)
                        points.Add(At(t1 + direction * sweep * i / n));
                    AddPolyline(points, segments);
                }
                return true;
            }

            // Trimmed polyline or other: the part between the trims, measured as length along the curve
            // (as IfcOpenShell does for polylines), or between the projections of the trim points.
            var whole = Curve(basis, depth + 1);
            if (whole == null)
                return false;
            var polygon = whole.ToPolygon();
            if (whole.Segments.Count > 0)
            {
                var last = whole.Segments[whole.Segments.Count - 1].End;
                if ((polygon[polygon.Count - 1] - last).LengthSquared > 1e-18)
                    polygon.Add(last);
            }
            double Station(object[] trim, double fallback)
            {
                TrimValues(trim, out var point, out var parameter);
                if (point != null && (master != "PARAMETER" || parameter == null))
                    return Project(polygon, point.Value);
                if (parameter != null)
                    return parameter.Value;
                return fallback;
            }
            double total = 0;
            for (int i = 0; i + 1 < polygon.Count; i++)
                total += (polygon[i + 1] - polygon[i]).Length;
            double s1 = Station(trim1, 0), s2 = Station(trim2, total);
            var part = SubPolyline(polygon, sense ? s1 : s2, sense ? s2 : s1);
            if (!sense)
                part.Reverse();
            AddPolyline(part, segments);
            return true;
        }

        /// <summary>Length along the polyline of the point's projection.</summary>
        private static double Project(List<Vec3> polygon, Vec3 p)
        {
            double best = double.MaxValue, station = 0, run = 0;
            for (int i = 0; i + 1 < polygon.Count; i++)
            {
                var a = polygon[i];
                var d = polygon[i + 1] - a;
                double l2 = d.LengthSquared;
                double t = l2 > 0 ? Math.Max(0, Math.Min(1, (p - a).Dot(d) / l2)) : 0;
                double dist = (a + d * t - p).LengthSquared;
                if (dist < best)
                {
                    best = dist;
                    station = run + t * Math.Sqrt(l2);
                }
                run += Math.Sqrt(l2);
            }
            return station;
        }

        /// <summary>The polyline between two lengths along it (from &lt;= to).</summary>
        private static List<Vec3> SubPolyline(List<Vec3> polygon, double from, double to)
        {
            if (to < from)
            {
                var t = to;
                to = from;
                from = t;
            }
            var result = new List<Vec3>();
            double run = 0;
            for (int i = 0; i + 1 < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[i + 1];
                double l = (b - a).Length;
                double s0 = run, s1 = run + l;
                if (s1 >= from && s0 <= to && l > 0)
                {
                    double ta = Math.Max(0, (from - s0) / l), tb = Math.Min(1, (to - s0) / l);
                    var pa = a + (b - a) * ta;
                    var pb = a + (b - a) * tb;
                    if (result.Count == 0 || (result[result.Count - 1] - pa).LengthSquared > 1e-18)
                        result.Add(pa);
                    result.Add(pb);
                }
                run = s1;
            }
            return result;
        }

        private void TrimValues(object[] trim, out Vec3? point, out double? parameter)
        {
            point = null;
            parameter = null;
            foreach (var value in trim)
            {
                if (value is StepRef r)
                {
                    var entity = File.Get(r.Id);
                    if (entity != null && entity.IsA("IFCCARTESIANPOINT"))
                        point = _model.Point(entity);
                }
                else if (value is StepTyped t)
                    parameter = StepEntity.AsDouble(t.Value);
                else if (value is double || value is long)
                    parameter = StepEntity.AsDouble(value);
            }
        }

        private NurbsSegment BSpline(StepEntity curve)
        {
            int degree = (int)StepEntity.AsDouble(curve["Degree"], 0);
            var points = new List<Vec3>();
            foreach (var p in curve.Entities("ControlPointsList"))
                points.Add(_model.Point(p));
            var multiplicities = curve.List("KnotMultiplicities");
            var knotValues = curve.List("Knots");
            if (degree < 1 || points.Count <= degree || multiplicities.Length != knotValues.Length)
                return null;
            var knots = new List<double>();
            for (int i = 0; i < knotValues.Length; i++)
            {
                int m = (int)StepEntity.AsDouble(multiplicities[i], 1);
                double k = StepEntity.AsDouble(knotValues[i], 0);
                for (int j = 0; j < m; j++)
                    knots.Add(k);
            }
            if (knots.Count != points.Count + degree + 1)
                return null;
            double[] weights = null;
            if (curve.IsA("IFCRATIONALBSPLINECURVEWITHKNOTS"))
            {
                var w = curve.List("WeightsData");
                if (w.Length == points.Count)
                {
                    weights = new double[w.Length];
                    for (int i = 0; i < w.Length; i++)
                        weights[i] = StepEntity.AsDouble(w[i], 1);
                }
            }
            return new NurbsSegment(degree, points.ToArray(), weights, knots.ToArray());
        }

        /// <summary>A closed loop: closes a small gap with a line; null if it is not closed and has no area.</summary>
        public static CurveLoop Closed(CurveLoop loop)
        {
            if (loop == null || loop.Segments.Count == 0)
                return null;
            if (loop.IsSingleClosedCurve)
                return loop;
            var start = loop.Segments[0].Start;
            var end = loop.Segments[loop.Segments.Count - 1].End;
            if ((start - end).Length > 1e-9 * Math.Max(1, start.Length))
                loop.Segments.Add(new LineSegment(end, start));
            return loop;
        }

        // ------------------------------------------------------------------ profiles

        /// <summary>The profile in its own XY plane (position applied); null if not supported.</summary>
        public Profile Profile(StepEntity profile, int depth = 0)
        {
            if (profile == null || depth > 10)
                return null;
            string type = profile.Type;
            if (type == "IFCARBITRARYCLOSEDPROFILEDEF" || type == "IFCARBITRARYPROFILEDEFWITHVOIDS")
            {
                var outer = Closed(Flat(Curve(profile.Entity("OuterCurve"))));
                if (outer == null)
                    return null;
                var holes = new List<CurveLoop>();
                if (type == "IFCARBITRARYPROFILEDEFWITHVOIDS")
                    foreach (var inner in profile.Entities("InnerCurves"))
                    {
                        var hole = Closed(Flat(Curve(inner)));
                        if (hole != null)
                            holes.Add(hole);
                    }
                return new Profile(new List<ProfileRegion> { new ProfileRegion(outer, holes) });
            }
            if (type == "IFCDERIVEDPROFILEDEF")
            {
                var parent = Profile(profile.Entity("ParentProfile"), depth + 1);
                if (parent == null)
                    return null;
                var op = TransformationOperator(profile.Entity("Operator"));
                return parent.Transformed(op);
            }
            if (type == "IFCCOMPOSITEPROFILEDEF")
            {
                var regions = new List<ProfileRegion>();
                foreach (var part in profile.Entities("Profiles"))
                {
                    var p = Profile(part, depth + 1);
                    if (p != null)
                        regions.AddRange(p.Regions);
                }
                return regions.Count > 0 ? new Profile(regions) : null;
            }
            if (type == "IFCMIRROREDPROFILEDEF")
            {
                var parent = Profile(profile.Entity("ParentProfile"), depth + 1);
                if (parent == null)
                    return null;
                return parent.Transformed(Mat4.FromAxes(Vec3.Zero, -Vec3.XAxis, Vec3.YAxis, Vec3.ZAxis));
            }

            var regionList = ParameterizedProfile(profile);
            if (regionList == null)
                return null;
            var position = profile.Has("Position") ? _model.Axis2Placement(profile.Entity("Position")) : Mat4.Identity;
            var result = new Profile(regionList);
            return position.IsIdentity ? result : result.Transformed(position);
        }

        private static CurveLoop Flat(CurveLoop loop)
        {
            if (loop == null)
                return null;
            // Profile curves are 2D; a 3D curve given anyway is projected to the profile plane.
            return loop.Transformed(new Mat4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0));
        }

        /// <summary>IfcCartesianTransformationOperator 2D/3D (also non-uniform) as a matrix.</summary>
        public Mat4 TransformationOperator(StepEntity op)
        {
            if (op == null)
                return Mat4.Identity;
            var origin = _model.Point(op.Entity("LocalOrigin"));
            double scale = op.Double("Scale", 1);
            if (double.IsNaN(scale) || scale == 0)
                scale = 1;
            double scale2 = scale, scale3 = scale;
            if (op.Has("Scale2"))
            {
                scale2 = op.Double("Scale2", scale);
                if (double.IsNaN(scale2))
                    scale2 = scale;
            }
            if (op.Has("Scale3"))
            {
                scale3 = op.Double("Scale3", scale);
                if (double.IsNaN(scale3))
                    scale3 = scale;
            }
            bool is3D = op.IsA("IFCCARTESIANTRANSFORMATIONOPERATOR3D");
            var axis1 = op["Axis1"] is StepRef a1 ? _model.Direction(File.Get(a1.Id), Vec3.XAxis) : (Vec3?)null;
            var axis2 = op["Axis2"] is StepRef a2 ? _model.Direction(File.Get(a2.Id), Vec3.YAxis) : (Vec3?)null;
            Vec3 x, y, z;
            if (is3D)
            {
                // IfcBaseAxis: Z from Axis3, X from Axis1 projected, Y from Axis2 projected (may mirror).
                z = op["Axis3"] is StepRef a3 ? _model.Direction(File.Get(a3.Id), Vec3.ZAxis).Unit() : Vec3.ZAxis;
                var xs = axis1 ?? (Math.Abs(z.X) < 0.9 ? Vec3.XAxis : Vec3.YAxis);
                x = (xs - z * xs.Dot(z)).Unit();
                if (x.LengthSquared < 0.5)
                    x = z.Perpendicular();
                if (axis2 != null)
                {
                    var ys = axis2.Value - z * axis2.Value.Dot(z);
                    ys = ys - x * ys.Dot(x);
                    y = ys.LengthSquared > 1e-18 ? ys.Unit() : z.Cross(x);
                }
                else
                    y = z.Cross(x);
            }
            else
            {
                z = Vec3.ZAxis;
                var xs = axis1 ?? Vec3.XAxis;
                x = new Vec3(xs.X, xs.Y, 0).Unit();
                if (x.LengthSquared < 0.5)
                    x = Vec3.XAxis;
                if (axis2 != null)
                {
                    var ys = new Vec3(axis2.Value.X, axis2.Value.Y, 0);
                    ys = ys - x * ys.Dot(x);
                    y = ys.LengthSquared > 1e-18 ? ys.Unit() : new Vec3(-x.Y, x.X, 0);
                }
                else
                    y = new Vec3(-x.Y, x.X, 0);
            }
            return Mat4.FromAxes(origin, x * scale, y * scale2, z * scale3);
        }

        private static double D(StepEntity e, string attribute, double fallback = 0)
        {
            if (!e.Has(attribute))
                return fallback;
            double v = e.Double(attribute, fallback);
            return double.IsNaN(v) ? fallback : v;
        }

        /// <summary>The standard profiles, centred on their bounding box as IFC defines them.</summary>
        private List<ProfileRegion> ParameterizedProfile(StepEntity p)
        {
            switch (p.Type)
            {
                case "IFCRECTANGLEPROFILEDEF":
                {
                    double x = D(p, "XDim"), y = D(p, "YDim");
                    if (!(x > 0) || !(y > 0))
                        return null;
                    return Single(Rectangle(x, y, 0));
                }
                case "IFCROUNDEDRECTANGLEPROFILEDEF":
                {
                    double x = D(p, "XDim"), y = D(p, "YDim");
                    if (!(x > 0) || !(y > 0))
                        return null;
                    return Single(Rectangle(x, y, D(p, "RoundingRadius")));
                }
                case "IFCRECTANGLEHOLLOWPROFILEDEF":
                {
                    double x = D(p, "XDim"), y = D(p, "YDim"), t = D(p, "WallThickness");
                    if (!(x > 0) || !(y > 0))
                        return null;
                    var outer = Rectangle(x, y, D(p, "OuterFilletRadius"));
                    if (!(t > 0) || 2 * t >= Math.Min(x, y))
                        return Single(outer);
                    var inner = Rectangle(x - 2 * t, y - 2 * t, D(p, "InnerFilletRadius")).Reversed();
                    return new List<ProfileRegion> { new ProfileRegion(outer, new List<CurveLoop> { inner }) };
                }
                case "IFCCIRCLEPROFILEDEF":
                {
                    double r = D(p, "Radius");
                    if (!(r > 0))
                        return null;
                    return Single(Circle(r));
                }
                case "IFCCIRCLEHOLLOWPROFILEDEF":
                {
                    double r = D(p, "Radius"), t = D(p, "WallThickness");
                    if (!(r > 0))
                        return null;
                    if (!(t > 0) || t >= r)
                        return Single(Circle(r));
                    return new List<ProfileRegion> { new ProfileRegion(Circle(r), new List<CurveLoop> { Circle(r - t).Reversed() }) };
                }
                case "IFCELLIPSEPROFILEDEF":
                {
                    double a = D(p, "SemiAxis1"), b = D(p, "SemiAxis2");
                    if (!(a > 0) || !(b > 0))
                        return null;
                    return Single(new CurveLoop(new List<Segment> { new EllipseSegment(Vec3.Zero, Vec3.XAxis * a, Vec3.YAxis * b) }));
                }
                case "IFCISHAPEPROFILEDEF":
                case "IFCASYMMETRICISHAPEPROFILEDEF":
                {
                    double depth = D(p, "OverallDepth");
                    double bottomWidth = p.Has("BottomFlangeWidth") ? D(p, "BottomFlangeWidth") : D(p, "OverallWidth");
                    double bottomThickness = p.Has("BottomFlangeThickness") ? D(p, "BottomFlangeThickness") : D(p, "FlangeThickness");
                    double topWidth = D(p, "TopFlangeWidth", bottomWidth);
                    double topThickness = D(p, "TopFlangeThickness", bottomThickness);
                    double web = D(p, "WebThickness");
                    double fillet = p.Has("BottomFlangeFilletRadius") ? D(p, "BottomFlangeFilletRadius") : D(p, "FilletRadius");
                    double topFillet = D(p, "TopFlangeFilletRadius", fillet);
                    double edge = D(p, "FlangeEdgeRadius");
                    if (!(depth > 0) || !(bottomWidth > 0) || !(web > 0) || !(bottomThickness > 0))
                        return null;
                    double h = depth / 2, b = bottomWidth / 2, t = topWidth / 2, w = web / 2;
                    var points = new[]
                    {
                        new Vec3(-b, -h, 0), new Vec3(b, -h, 0), new Vec3(b, -h + bottomThickness, 0),
                        new Vec3(w, -h + bottomThickness, 0), new Vec3(w, h - topThickness, 0), new Vec3(t, h - topThickness, 0),
                        new Vec3(t, h, 0), new Vec3(-t, h, 0), new Vec3(-t, h - topThickness, 0),
                        new Vec3(-w, h - topThickness, 0), new Vec3(-w, -h + bottomThickness, 0), new Vec3(-b, -h + bottomThickness, 0),
                    };
                    var radii = new[] { 0, 0, edge, fillet, topFillet, edge, 0, 0, edge, topFillet, fillet, edge };
                    return Single(Rounded(points, radii));
                }
                case "IFCLSHAPEPROFILEDEF":
                {
                    double depth = D(p, "Depth");
                    double width = D(p, "Width", depth);
                    if (!(width > 0))
                        width = depth;
                    double t = D(p, "Thickness"), r = D(p, "FilletRadius"), e = D(p, "EdgeRadius");
                    if (!(depth > 0) || !(t > 0))
                        return null;
                    double h = depth / 2, b = width / 2;
                    var points = new[]
                    {
                        new Vec3(-b, -h, 0), new Vec3(b, -h, 0), new Vec3(b, -h + t, 0),
                        new Vec3(-b + t, -h + t, 0), new Vec3(-b + t, h, 0), new Vec3(-b, h, 0),
                    };
                    return Single(Rounded(points, new[] { 0, 0, e, r, e, 0 }));
                }
                case "IFCTSHAPEPROFILEDEF":
                {
                    double depth = D(p, "Depth"), flange = D(p, "FlangeWidth"), web = D(p, "WebThickness"), tf = D(p, "FlangeThickness");
                    double r = D(p, "FilletRadius"), fe = D(p, "FlangeEdgeRadius"), we = D(p, "WebEdgeRadius");
                    if (!(depth > 0) || !(flange > 0) || !(web > 0) || !(tf > 0))
                        return null;
                    double h = depth / 2, b = flange / 2, w = web / 2;
                    var points = new[]
                    {
                        new Vec3(-w, -h, 0), new Vec3(w, -h, 0), new Vec3(w, h - tf, 0), new Vec3(b, h - tf, 0),
                        new Vec3(b, h, 0), new Vec3(-b, h, 0), new Vec3(-b, h - tf, 0), new Vec3(-w, h - tf, 0),
                    };
                    return Single(Rounded(points, new[] { we, we, r, fe, 0, 0, fe, r }));
                }
                case "IFCUSHAPEPROFILEDEF":
                {
                    double depth = D(p, "Depth"), flange = D(p, "FlangeWidth"), web = D(p, "WebThickness"), tf = D(p, "FlangeThickness");
                    double r = D(p, "FilletRadius"), e = D(p, "EdgeRadius");
                    if (!(depth > 0) || !(flange > 0) || !(web > 0) || !(tf > 0))
                        return null;
                    double h = depth / 2, b = flange / 2;
                    var points = new[]
                    {
                        new Vec3(-b, -h, 0), new Vec3(b, -h, 0), new Vec3(b, -h + tf, 0), new Vec3(-b + web, -h + tf, 0),
                        new Vec3(-b + web, h - tf, 0), new Vec3(b, h - tf, 0), new Vec3(b, h, 0), new Vec3(-b, h, 0),
                    };
                    return Single(Rounded(points, new[] { 0, 0, e, r, r, e, 0, 0 }));
                }
                case "IFCCSHAPEPROFILEDEF":
                {
                    double depth = D(p, "Depth"), width = D(p, "Width"), t = D(p, "WallThickness"), girth = D(p, "Girth");
                    double r = D(p, "InternalFilletRadius");
                    if (!(depth > 0) || !(width > 0) || !(t > 0))
                        return null;
                    double h = depth / 2, b = width / 2;
                    double ro = r > 0 ? r + t : 0;
                    if (!(girth > t))
                    {
                        var plain = new[]
                        {
                            new Vec3(-b, -h, 0), new Vec3(b, -h, 0), new Vec3(b, -h + t, 0), new Vec3(-b + t, -h + t, 0),
                            new Vec3(-b + t, h - t, 0), new Vec3(b, h - t, 0), new Vec3(b, h, 0), new Vec3(-b, h, 0),
                        };
                        return Single(Rounded(plain, new[] { ro, 0, 0, r, r, 0, 0, ro }));
                    }
                    var points = new[]
                    {
                        new Vec3(-b, -h, 0), new Vec3(b, -h, 0), new Vec3(b, -h + girth, 0), new Vec3(b - t, -h + girth, 0),
                        new Vec3(b - t, -h + t, 0), new Vec3(-b + t, -h + t, 0), new Vec3(-b + t, h - t, 0), new Vec3(b - t, h - t, 0),
                        new Vec3(b - t, h - girth, 0), new Vec3(b, h - girth, 0), new Vec3(b, h, 0), new Vec3(-b, h, 0),
                    };
                    return Single(Rounded(points, new[] { ro, ro, 0, 0, r, r, r, r, 0, 0, ro, ro }));
                }
                case "IFCZSHAPEPROFILEDEF":
                {
                    double depth = D(p, "Depth"), flange = D(p, "FlangeWidth"), web = D(p, "WebThickness"), tf = D(p, "FlangeThickness");
                    double r = D(p, "FilletRadius"), e = D(p, "EdgeRadius");
                    if (!(depth > 0) || !(flange > 0) || !(web > 0) || !(tf > 0))
                        return null;
                    // The flanges reach FlangeWidth from the web's centre line on either side.
                    double h = depth / 2, w = web / 2;
                    var points = new[]
                    {
                        new Vec3(-flange, -h, 0), new Vec3(w, -h, 0), new Vec3(w, h - tf, 0), new Vec3(flange, h - tf, 0),
                        new Vec3(flange, h, 0), new Vec3(-w, h, 0), new Vec3(-w, -h + tf, 0), new Vec3(-flange, -h + tf, 0),
                    };
                    return Single(Rounded(points, new[] { 0, 0, r, e, 0, 0, r, e }));
                }
                case "IFCTRAPEZIUMPROFILEDEF":
                {
                    double bottom = D(p, "BottomXDim"), top = D(p, "TopXDim"), y = D(p, "YDim"), offset = D(p, "TopXOffset");
                    if (!(bottom > 0) || !(y > 0))
                        return null;
                    // Centred on the bounding box: the top edge may reach beyond the bottom one.
                    double h = y / 2;
                    double minX = Math.Min(0, offset), maxX = Math.Max(bottom, offset + top);
                    double c = (minX + maxX) / 2;
                    var points = new List<Vec3>
                    {
                        new Vec3(-c, -h, 0), new Vec3(bottom - c, -h, 0), new Vec3(offset + top - c, h, 0), new Vec3(offset - c, h, 0),
                    };
                    var segments = new List<Segment>();
                    points.Add(points[0]);
                    AddPolyline(points, segments);
                    return Single(new CurveLoop(segments));
                }
                default:
                    return null;
            }
        }

        private static List<ProfileRegion> Single(CurveLoop loop) => new List<ProfileRegion> { new ProfileRegion(loop) };

        private static CurveLoop Circle(double r) =>
            new CurveLoop(new List<Segment> { new EllipseSegment(Vec3.Zero, Vec3.XAxis * r, Vec3.YAxis * r) });

        private static CurveLoop Rectangle(double x, double y, double radius)
        {
            double a = x / 2, b = y / 2;
            var points = new[] { new Vec3(-a, -b, 0), new Vec3(a, -b, 0), new Vec3(a, b, 0), new Vec3(-a, b, 0) };
            return Rounded(points, new[] { radius, radius, radius, radius });
        }

        /// <summary>A closed polygon whose corners are rounded with tangent arcs of the given radii (0: sharp).</summary>
        public static CurveLoop Rounded(Vec3[] points, double[] radii)
        {
            int n = points.Length;
            var starts = new Vec3[n];
            var ends = new Vec3[n];
            var mids = new Vec3?[n];
            for (int i = 0; i < n; i++)
            {
                var corner = points[i];
                double r = radii[i];
                starts[i] = ends[i] = corner;
                if (!(r > 0))
                    continue;
                var prev = points[(i + n - 1) % n];
                var next = points[(i + 1) % n];
                var d1 = (prev - corner);
                var d2 = (next - corner);
                double l1 = d1.Length, l2 = d2.Length;
                if (l1 < 1e-12 || l2 < 1e-12)
                    continue;
                d1 = d1 / l1;
                d2 = d2 / l2;
                double cos = Math.Max(-1, Math.Min(1, d1.Dot(d2)));
                double angle = Math.Acos(cos); // interior angle between the edges
                if (angle < 1e-6 || angle > Math.PI - 1e-6)
                    continue;
                double tangent = r / Math.Tan(angle / 2);
                if (tangent > l1 * 0.999 || tangent > l2 * 0.999)
                    continue; // radius does not fit: keep the corner sharp
                var a = corner + d1 * tangent;
                var b = corner + d2 * tangent;
                var bisector = (d1 + d2).Unit();
                var center = corner + bisector * (r / Math.Sin(angle / 2));
                var mid = center - bisector * r;
                starts[i] = a;
                ends[i] = b;
                mids[i] = mid;
            }
            var segments = new List<Segment>();
            for (int i = 0; i < n; i++)
            {
                if (mids[i] != null)
                    segments.Add(new ArcSegment(starts[i], mids[i].Value, ends[i]));
                var from = ends[i];
                var to = starts[(i + 1) % n];
                if ((to - from).LengthSquared > 1e-24)
                    segments.Add(new LineSegment(from, to));
            }
            return new CurveLoop(segments);
        }
    }
}
