using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using RhinoIfc.Core.Model;
using RhinoIfc.Core.Step;

namespace RhinoIfc.Core.Geometry
{
    /// <summary>Turns an IFC product's representation into <see cref="ProductGeometry"/>: the body items with their
    /// placement and colour, type geometry (IfcMappedItem) marked for blocks, and the openings to cut.</summary>
    public sealed class ShapeBuilder
    {
        private static readonly string[] BodyIdentifiers = { "Body", "Facetation", "Body-Fallback", "Mesh" };
        private static readonly HashSet<string> SkippedIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Axis", "FootPrint", "Box", "Annotation", "Profile", "Reference", "Lighting", "Survey", "Clearance",
            "CoG", "Plan", "Outline", "Footprint",
        };

        private readonly IfcModel _model;
        private readonly CurveBuilder _curves;
        private readonly ConcurrentDictionary<int, Profile> _profiles = new ConcurrentDictionary<int, Profile>();
        private readonly ConcurrentDictionary<int, Shape> _shapes = new ConcurrentDictionary<int, Shape>();

        public ShapeBuilder(IfcModel model)
        {
            _model = model;
            _curves = new CurveBuilder(model);
        }

        private StepFile File => _model.File;

        public CurveBuilder Curves => _curves;

        /// <summary>The geometry of a product; empty if it has no body representation.</summary>
        public ProductGeometry Build(int productId, bool withOpenings)
        {
            var result = new ProductGeometry();
            var product = File.Get(productId);
            if (product == null)
                return result;
            var placement = _model.Placement(product.Ref("ObjectPlacement"));
            result.Placement = placement;
            IfcColor? fallback = null;
            AddRepresentation(product, placement, result.Items, result, ref fallback);

            if (withOpenings)
            {
                var openings = _model.OpeningsOf(productId);
                if (openings != null)
                {
                    foreach (var openingId in openings)
                    {
                        var opening = File.Get(openingId);
                        if (opening == null)
                            continue;
                        var openingPlacement = _model.Placement(opening.Ref("ObjectPlacement"));
                        IfcColor? none = null;
                        AddRepresentation(opening, openingPlacement, result.Openings, result, ref none);
                    }
                }
            }
            return result;
        }

        private void AddRepresentation(StepEntity product, Mat4 placement, List<ShapeItem> items, ProductGeometry result, ref IfcColor? fallback)
        {
            var definition = product.Entity("Representation");
            if (definition == null)
                return;
            var representations = definition.Entities("Representations");
            foreach (var representation in ChooseBody(representations))
                foreach (var itemId in representation.Refs("Items"))
                    AddItem(itemId, placement, null, items, result, 0, 0, Mat4.Identity);
        }

        /// <summary>The body representations: "Body" (and its fallbacks) first; otherwise any 3D one that is not an
        /// axis, footprint, box or annotation.</summary>
        private static List<StepEntity> ChooseBody(List<StepEntity> representations)
        {
            var result = new List<StepEntity>();
            foreach (var identifier in BodyIdentifiers)
            {
                foreach (var r in representations)
                    if (string.Equals(r.String("RepresentationIdentifier"), identifier, StringComparison.OrdinalIgnoreCase))
                        result.Add(r);
                if (result.Count > 0)
                    return result;
            }
            foreach (var r in representations)
            {
                var identifier = r.String("RepresentationIdentifier") ?? "";
                var type = r.String("RepresentationType") ?? "";
                if (SkippedIdentifiers.Contains(identifier) || type == "Curve2D" || type == "Curve" || type == "BoundingBox"
                    || type == "Annotation2D" || type == "Point" || type == "PointCloud" || type == "GeometricCurveSet"
                    || type == "GeometricSet")
                    continue;
                result.Add(r);
            }
            return result;
        }

        private void AddItem(int itemId, Mat4 toWorld, IfcColor? inherited, List<ShapeItem> items, ProductGeometry result,
            int depth, int mapId, Mat4 mapToWorld)
        {
            if (depth > 10)
                return;
            var color = _model.ItemColor(itemId) ?? inherited;
            var item = File.Get(itemId);
            if (item == null)
                return;
            if (item.IsA("IFCMAPPEDITEM"))
            {
                var source = item.Entity("MappingSource");
                if (source == null)
                    return;
                var origin = _model.Axis2Placement(source.Entity("MappingOrigin"));
                var target = _curves.TransformationOperator(item.Entity("MappingTarget"));
                var mapped = source.Entity("MappedRepresentation");
                if (mapped == null)
                    return;
                var transform = toWorld * target * origin;
                // The outermost mapped item decides the block: its map coordinates are the block's.
                int map = mapId != 0 ? mapId : source.Id;
                var instance = mapId != 0 ? mapToWorld : toWorld * target;
                foreach (var inner in mapped.Refs("Items"))
                    AddItem(inner, transform, color, items, result, depth + 1, map, instance);
                return;
            }
            Shape shape;
            try
            {
                shape = ItemShape(item, 0);
            }
            catch (Exception)
            {
                shape = null;
            }
            if (shape == null)
            {
                if (!(item.IsA("IFCGEOMETRICCURVESET") || item.IsA("IFCCURVE") || item.IsA("IFCPOINT") || item.IsA("IFCTEXTLITERAL")
                    || item.IsA("IFCANNOTATIONFILLAREA") || item.IsA("IFCBOUNDINGBOX")))
                    result.NoteUnsupported(item.ClassName);
                return;
            }
            var shapeItem = new ShapeItem(shape, toWorld, color) { MapId = mapId };
            if (mapId != 0)
            {
                shapeItem.MapToWorld = mapToWorld;
                shapeItem.ItemToMap = mapToWorld.Inverse() * toWorld;
            }
            items.Add(shapeItem);
        }

        // ------------------------------------------------------------------ representation items

        /// <summary>The shape of a geometric representation item in its own coordinates; null if not supported.
        /// Shapes of items used by several products (type geometry) are built once.</summary>
        public Shape ItemShape(StepEntity item, int depth)
        {
            if (item == null || depth > 30)
                return null;
            if (_shapes.TryGetValue(item.Id, out var cached))
                return cached;
            var shape = BuildItemShape(item, depth);
            // Only items that can be shared through a map are worth keeping; caching everything would keep the whole
            // model's geometry in memory.
            if (shape != null && depth == 0 && IsShareable(shape))
                _shapes.TryAdd(item.Id, shape);
            return shape;
        }

        private static bool IsShareable(Shape shape) => shape is MeshShape || shape is BooleanShape;

        private Shape BuildItemShape(StepEntity item, int depth)
        {
            switch (item.Type)
            {
                case "IFCEXTRUDEDAREASOLID":
                case "IFCEXTRUDEDAREASOLIDTAPERED":
                    return Extrusion(item);
                case "IFCREVOLVEDAREASOLID":
                case "IFCREVOLVEDAREASOLIDTAPERED":
                    return Revolution(item);
                case "IFCSWEPTDISKSOLID":
                case "IFCSWEPTDISKSOLIDPOLYGONAL":
                {
                    var path = _curves.Curve(item.Entity("Directrix"));
                    double radius = item.Double("Radius");
                    if (path == null || !(radius > 0))
                        return null;
                    double inner = item.Double("InnerRadius", 0);
                    return new PipeShape(path, radius, double.IsNaN(inner) ? 0 : inner);
                }
                case "IFCFACETEDBREP":
                case "IFCFACETEDBREPWITHVOIDS":
                    return MeshBuilder.FromShells(this, new List<StepEntity> { item.Entity("Outer") }, true, item.Type == "IFCFACETEDBREPWITHVOIDS" ? item.Entities("Voids") : null);
                case "IFCADVANCEDBREP":
                case "IFCADVANCEDBREPWITHVOIDS":
                    return MeshBuilder.FromAdvancedBrep(this, item);
                case "IFCSHELLBASEDSURFACEMODEL":
                    return MeshBuilder.FromShells(this, item.Entities("SbsmBoundary"), false, null);
                case "IFCFACEBASEDSURFACEMODEL":
                    return MeshBuilder.FromShells(this, item.Entities("FbsmFaces"), false, null);
                case "IFCOPENSHELL":
                case "IFCCLOSEDSHELL":
                case "IFCCONNECTEDFACESET":
                    return MeshBuilder.FromShells(this, new List<StepEntity> { item }, item.Type == "IFCCLOSEDSHELL", null);
                case "IFCTRIANGULATEDFACESET":
                case "IFCTRIANGULATEDIRREGULARNETWORK":
                    return MeshBuilder.FromTriangulatedFaceSet(item);
                case "IFCPOLYGONALFACESET":
                    return MeshBuilder.FromPolygonalFaceSet(item);
                case "IFCBOOLEANRESULT":
                case "IFCBOOLEANCLIPPINGRESULT":
                {
                    string op = item.Enum("Operator");
                    var first = Operand(item.Entity("FirstOperand"), depth);
                    var second = Operand(item.Entity("SecondOperand"), depth);
                    if (first == null)
                        return null;
                    if (second == null)
                        return op == "DIFFERENCE" ? first : null;
                    var kind = op == "UNION" ? BooleanOperator.Union : op == "INTERSECTION" ? BooleanOperator.Intersection : BooleanOperator.Difference;
                    return new BooleanShape(kind, first, second);
                }
                case "IFCCSGSOLID":
                    return Operand(item.Entity("TreeRootExpression"), depth);
                case "IFCBLOCK":
                case "IFCRIGHTCIRCULARCYLINDER":
                case "IFCSPHERE":
                case "IFCRIGHTCIRCULARCONE":
                case "IFCRECTANGULARPYRAMID":
                    return Primitive(item);
                case "IFCHALFSPACESOLID":
                case "IFCPOLYGONALBOUNDEDHALFSPACE":
                case "IFCBOXEDHALFSPACE":
                    return HalfSpace(item);
                default:
                    return null;
            }
        }

        private Shape Operand(StepEntity operand, int depth)
        {
            if (operand == null)
                return null;
            return ItemShape(operand, depth + 1);
        }

        public Profile ProfileOf(StepEntity profile)
        {
            if (profile == null)
                return null;
            return _profiles.GetOrAdd(profile.Id, id => _curves.Profile(profile));
        }

        private Shape Extrusion(StepEntity item)
        {
            var profile = ProfileOf(item.Entity("SweptArea"));
            double depth = item.Double("Depth");
            if (profile == null || !(depth > 0))
                return null;
            var position = _model.Axis2Placement(item.Entity("Position"));
            var direction = _model.Direction(item.Entity("ExtrudedDirection"), Vec3.ZAxis).Unit();
            // ExtrudedDirection is given in the Position's coordinates.
            var worldDirection = position.Vector(direction).Unit();
            if (item.Type == "IFCEXTRUDEDAREASOLIDTAPERED")
            {
                // Tapered: the end profile differs; the import uses the start profile (noted for the report).
            }
            return new ExtrusionShape(profile, position, worldDirection, depth);
        }

        private Shape Revolution(StepEntity item)
        {
            var profile = ProfileOf(item.Entity("SweptArea"));
            var axis = item.Entity("Axis");
            double angle = item.Double("Angle") * _model.AngleToRadians;
            if (profile == null || axis == null || !(Math.Abs(angle) > 0))
                return null;
            var position = _model.Axis2Placement(item.Entity("Position"));
            // The axis (IfcAxis1Placement) is in the Position's coordinates.
            var axisPoint = position.Point(_model.Point(axis.Entity("Location")));
            var axisDirection = position.Vector(_model.Direction(axis.Entity("Axis"), Vec3.ZAxis)).Unit();
            return new RevolutionShape(profile, position, axisPoint, axisDirection, angle);
        }

        private Shape Primitive(StepEntity item)
        {
            var position = _model.Axis2Placement(item.Entity("Position"));
            switch (item.Type)
            {
                case "IFCBLOCK":
                {
                    double x = item.Double("XLength"), y = item.Double("YLength"), z = item.Double("ZLength");
                    if (!(x > 0) || !(y > 0) || !(z > 0))
                        return null;
                    // The block starts at the position's origin (corner), not centred.
                    var loop = CurveBuilder.Rounded(new[] { new Vec3(0, 0, 0), new Vec3(x, 0, 0), new Vec3(x, y, 0), new Vec3(0, y, 0) },
                        new double[4]);
                    var profile = new Profile(new List<ProfileRegion> { new ProfileRegion(loop) });
                    return new ExtrusionShape(profile, position, position.ZColumn.Unit(), z);
                }
                case "IFCRIGHTCIRCULARCYLINDER":
                {
                    double h = item.Double("Height"), r = item.Double("Radius");
                    if (!(h > 0) || !(r > 0))
                        return null;
                    var loop = new CurveLoop(new List<Segment> { new EllipseSegment(Vec3.Zero, Vec3.XAxis * r, Vec3.YAxis * r) });
                    var profile = new Profile(new List<ProfileRegion> { new ProfileRegion(loop) });
                    return new ExtrusionShape(profile, position, position.ZColumn.Unit(), h);
                }
                case "IFCSPHERE":
                {
                    double r = item.Double("Radius");
                    return r > 0 ? new SphereShape(position.Origin, r) : null;
                }
                case "IFCRIGHTCIRCULARCONE":
                {
                    double h = item.Double("Height"), r = item.Double("BottomRadius");
                    if (!(h > 0) || !(r > 0))
                        return null;
                    // Triangle in the XZ plane revolved about Z.
                    var triangle = CurveBuilder.Rounded(new[] { new Vec3(0, 0, 0), new Vec3(r, 0, 0), new Vec3(0, h, 0) }, new double[3]);
                    var profilePosition = position * Mat4.FromAxes(Vec3.Zero, Vec3.XAxis, Vec3.ZAxis, -Vec3.YAxis);
                    var profile = new Profile(new List<ProfileRegion> { new ProfileRegion(triangle) });
                    return new RevolutionShape(profile, profilePosition, position.Origin, position.ZColumn.Unit(), 2 * Math.PI);
                }
                case "IFCRECTANGULARPYRAMID":
                {
                    double x = item.Double("XLength"), y = item.Double("YLength"), h = item.Double("Height");
                    if (!(x > 0) || !(y > 0) || !(h > 0))
                        return null;
                    var vertices = new List<Vec3>
                    {
                        position.Point(new Vec3(0, 0, 0)), position.Point(new Vec3(x, 0, 0)), position.Point(new Vec3(x, y, 0)),
                        position.Point(new Vec3(0, y, 0)), position.Point(new Vec3(x / 2, y / 2, h)),
                    };
                    var triangles = new List<int> { 0, 2, 1, 0, 3, 2, 0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4 };
                    var faces = new List<int> { 0, 0, 1, 2, 3, 4 };
                    return new MeshShape(vertices, triangles, faces, true);
                }
            }
            return null;
        }

        private Shape HalfSpace(StepEntity item)
        {
            var surface = item.Entity("BaseSurface");
            if (surface == null || !surface.IsA("IFCPLANE"))
                return null;
            var plane = _model.Axis2Placement(surface.Entity("Position"));
            bool agreement = item.Bool("AgreementFlag") ?? true;
            // IFC: AgreementFlag TRUE → the plane normal points away from the half-space solid.
            var normal = plane.ZColumn.Unit();
            if (agreement)
                normal = -normal;
            CurveLoop boundary = null;
            var boundaryPosition = Mat4.Identity;
            if (item.Type == "IFCPOLYGONALBOUNDEDHALFSPACE")
            {
                boundaryPosition = _model.Axis2Placement(item.Entity("Position"));
                boundary = CurveBuilder.Closed(_curves.Curve(item.Entity("PolygonalBoundary")));
                if (boundary == null)
                    return null;
            }
            return new HalfSpaceShape(plane.Origin, normal, boundary, boundaryPosition);
        }

        // ------------------------------------------------------------------ helpers for MeshBuilder

        internal IfcModel Model => _model;
    }
}
