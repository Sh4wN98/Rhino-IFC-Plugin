using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RhinoIfc.Core.Geometry;
using RhinoIfc.Core.Step;

namespace RhinoIfc.Core.Model
{
    /// <summary>A storey (or another spatial container that holds elements) with its height.</summary>
    public sealed class SpatialLevel
    {
        public int Id { get; set; }

        public string Name { get; set; }

        public string ClassName { get; set; }

        /// <summary>IfcBuildingStorey.Elevation in file length units; NaN if not given.</summary>
        public double Elevation { get; set; } = double.NaN;

        /// <summary>Order for the layer list: by elevation, then file order.</summary>
        public int Order { get; set; }
    }

    /// <summary>The IFC file's semantic side: units, spatial structure, relationships (properties, types, materials,
    /// openings), styles and placements. Built once after indexing; thread-safe to query.</summary>
    public sealed class IfcModel
    {
        public const string NoStorey = "Ohne Geschoss";

        private readonly Dictionary<int, int> _container = new Dictionary<int, int>();      // element → spatial element
        private readonly Dictionary<int, int> _parent = new Dictionary<int, int>();         // part → whole (aggregates)
        private readonly Dictionary<int, List<int>> _propertySets = new Dictionary<int, List<int>>();
        private readonly Dictionary<int, int> _type = new Dictionary<int, int>();           // object → type object
        private readonly Dictionary<int, int> _material = new Dictionary<int, int>();       // object/type → material select
        private readonly Dictionary<int, List<int>> _openings = new Dictionary<int, List<int>>();
        private readonly HashSet<int> _fillings = new HashSet<int>();
        private readonly Dictionary<int, IfcColor> _itemColors = new Dictionary<int, IfcColor>();
        private readonly Dictionary<int, IfcColor> _materialColors = new Dictionary<int, IfcColor>();
        private readonly Dictionary<int, SpatialLevel> _levels = new Dictionary<int, SpatialLevel>();
        private readonly ConcurrentDictionary<int, Mat4> _placements = new ConcurrentDictionary<int, Mat4>();
        private readonly ConcurrentDictionary<int, IfcColor?> _styleColors = new ConcurrentDictionary<int, IfcColor?>();
        private readonly ConcurrentDictionary<int, List<KeyValuePair<string, string>>> _typeProperties =
            new ConcurrentDictionary<int, List<KeyValuePair<string, string>>>();

        public IfcModel(StepFile file)
        {
            File = file;
        }

        public StepFile File { get; }

        /// <summary>File length unit → metres (0.001 for millimetres).</summary>
        public double LengthToMetres { get; private set; } = 1.0;

        /// <summary>The file's length unit as text: mm, cm, m, in, ft …</summary>
        public string LengthUnitName { get; private set; } = "m";

        /// <summary>File plane angle unit → radians.</summary>
        public double AngleToRadians { get; private set; } = 1.0;

        public string AreaUnitName { get; private set; } = "m²";

        public string VolumeUnitName { get; private set; } = "m³";

        public string ProjectName { get; private set; } = "";

        /// <summary>Products with geometry that the import brings in, in file order.</summary>
        public List<int> Products { get; } = new List<int>();

        /// <summary>Storeys etc., by Order.</summary>
        public List<SpatialLevel> Levels => _levels.Values.OrderBy(l => l.Order).ToList();

        /// <summary>Document-level text: schema, project, units, IfcMapConversion.</summary>
        public Dictionary<string, string> DocumentText { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public static IfcModel Load(StepFile file, ImportOptions options, CancellationToken cancel = default)
        {
            var model = new IfcModel(file);
            model.ReadUnits();
            model.ReadRelationships(cancel);
            model.ReadStyles(cancel);
            model.ReadSpatialLevels();
            model.CollectProducts(options);
            model.ReadDocumentText();
            return model;
        }

        // ------------------------------------------------------------------ units

        private void ReadUnits()
        {
            var projects = File.IdsOf(File.CodesOf("IFCPROJECT"));
            if (projects.Count == 0)
                return;
            var project = File.Get(projects[0]);
            ProjectName = project.String("Name") ?? project.String("LongName") ?? "";
            var assignment = project.Entity("UnitsInContext");
            if (assignment == null)
                return;
            foreach (var unit in assignment.Entities("Units"))
            {
                string unitType = unit.Enum("UnitType");
                if (unitType == "LENGTHUNIT")
                {
                    LengthToMetres = UnitScale(unit, out var name);
                    LengthUnitName = name;
                }
                else if (unitType == "PLANEANGLEUNIT")
                    AngleToRadians = UnitScale(unit, out _);
                else if (unitType == "AREAUNIT")
                {
                    UnitScale(unit, out var name);
                    AreaUnitName = name;
                }
                else if (unitType == "VOLUMEUNIT")
                {
                    UnitScale(unit, out var name);
                    VolumeUnitName = name;
                }
            }
            if (!(LengthToMetres > 0) || double.IsInfinity(LengthToMetres))
                LengthToMetres = 1;
            if (!(AngleToRadians > 0) || double.IsInfinity(AngleToRadians))
                AngleToRadians = 1;
        }

        /// <summary>The factor of a unit to its SI base unit (metre, radian, m², m³) and a short name.</summary>
        private double UnitScale(StepEntity unit, out string name, int depth = 0)
        {
            name = "";
            if (unit == null || depth > 8)
                return 1;
            if (unit.IsA("IFCSIUNIT"))
            {
                string prefix = unit.Enum("Prefix");
                string si = unit.Enum("Name");
                double factor = PrefixFactor(prefix);
                string symbol = SiSymbol(si);
                if (si == "SQUARE_METRE")
                    factor *= factor;
                else if (si == "CUBIC_METRE")
                    factor *= factor * factor;
                name = PrefixSymbol(prefix) + symbol;
                if (si == "SQUARE_METRE")
                    name = PrefixSymbol(prefix) + "m²";
                else if (si == "CUBIC_METRE")
                    name = PrefixSymbol(prefix) + "m³";
                return factor;
            }
            if (unit.IsA("IFCCONVERSIONBASEDUNIT"))
            {
                name = unit.String("Name") ?? "";
                var measure = unit.Entity("ConversionFactor");
                if (measure == null)
                    return 1;
                double value = StepEntity.AsDouble(measure["ValueComponent"], 1);
                double baseFactor = UnitScale(measure.Entity("UnitComponent"), out _, depth + 1);
                string lower = name.ToLowerInvariant();
                if (lower.Contains("inch"))
                    name = "in";
                else if (lower.Contains("foot") || lower.Contains("feet"))
                    name = "ft";
                else if (lower.Contains("degree"))
                    name = "°";
                return value * baseFactor;
            }
            return 1;
        }

        private static double PrefixFactor(string prefix)
        {
            switch (prefix)
            {
                case "EXA": return 1e18;
                case "PETA": return 1e15;
                case "TERA": return 1e12;
                case "GIGA": return 1e9;
                case "MEGA": return 1e6;
                case "KILO": return 1e3;
                case "HECTO": return 1e2;
                case "DECA": return 1e1;
                case "DECI": return 1e-1;
                case "CENTI": return 1e-2;
                case "MILLI": return 1e-3;
                case "MICRO": return 1e-6;
                case "NANO": return 1e-9;
                case "PICO": return 1e-12;
                default: return 1;
            }
        }

        private static string PrefixSymbol(string prefix)
        {
            switch (prefix)
            {
                case "KILO": return "k";
                case "DECI": return "d";
                case "CENTI": return "c";
                case "MILLI": return "m";
                case "MICRO": return "µ";
                default: return "";
            }
        }

        private static string SiSymbol(string name)
        {
            switch (name)
            {
                case "METRE": return "m";
                case "RADIAN": return "rad";
                default: return name?.ToLowerInvariant() ?? "";
            }
        }

        // ------------------------------------------------------------------ relationships

        private void ReadRelationships(CancellationToken cancel)
        {
            var codes = File.CodesOf("IFCRELCONTAINEDINSPATIALSTRUCTURE", "IFCRELAGGREGATES", "IFCRELNESTS",
                "IFCRELDEFINESBYPROPERTIES", "IFCRELDEFINESBYTYPE", "IFCRELASSOCIATESMATERIAL", "IFCRELVOIDSELEMENT",
                "IFCRELFILLSELEMENT");
            foreach (var id in File.IdsOf(codes))
            {
                cancel.ThrowIfCancellationRequested();
                StepEntity rel;
                try
                {
                    rel = File.Get(id);
                }
                catch (Exception)
                {
                    continue; // a broken record does not stop the import
                }
                var type = rel.Type;
                switch (type)
                {
                    case "IFCRELCONTAINEDINSPATIALSTRUCTURE":
                    {
                        int structure = rel.Ref("RelatingStructure");
                        foreach (var element in rel.Refs("RelatedElements"))
                            if (!_container.ContainsKey(element))
                                _container[element] = structure;
                        break;
                    }
                    case "IFCRELAGGREGATES":
                    case "IFCRELNESTS":
                    {
                        int whole = rel.Ref("RelatingObject");
                        foreach (var part in rel.Refs("RelatedObjects"))
                            if (!_parent.ContainsKey(part))
                                _parent[part] = whole;
                        break;
                    }
                    case "IFCRELDEFINESBYPROPERTIES":
                    {
                        // IFC4: RelatingPropertyDefinition may be an IfcPropertySetDefinitionSet (list).
                        var definition = rel["RelatingPropertyDefinition"];
                        var sets = new List<int>();
                        if (definition is StepRef r)
                            sets.Add(r.Id);
                        else if (definition is StepTyped t && t.Value is object[] list)
                            sets.AddRange(list.Select(StepEntity.AsRef).Where(i => i > 0));
                        foreach (var obj in rel.Refs("RelatedObjects"))
                        {
                            if (!_propertySets.TryGetValue(obj, out var existing))
                                _propertySets[obj] = existing = new List<int>(2);
                            existing.AddRange(sets);
                        }
                        break;
                    }
                    case "IFCRELDEFINESBYTYPE":
                    {
                        int typeObject = rel.Ref("RelatingType");
                        foreach (var obj in rel.Refs("RelatedObjects"))
                            _type[obj] = typeObject;
                        break;
                    }
                    case "IFCRELASSOCIATESMATERIAL":
                    {
                        int material = rel.Ref("RelatingMaterial");
                        foreach (var obj in rel.Refs("RelatedObjects"))
                            _material[obj] = material;
                        break;
                    }
                    case "IFCRELVOIDSELEMENT":
                    {
                        int element = rel.Ref("RelatingBuildingElement");
                        int opening = rel.Ref("RelatedOpeningElement");
                        if (!_openings.TryGetValue(element, out var list))
                            _openings[element] = list = new List<int>(2);
                        list.Add(opening);
                        break;
                    }
                    case "IFCRELFILLSELEMENT":
                        _fillings.Add(rel.Ref("RelatedBuildingElement"));
                        break;
                }
            }
        }

        // ------------------------------------------------------------------ styles

        private void ReadStyles(CancellationToken cancel)
        {
            var styledItems = File.IdsOf(File.CodesOf("IFCSTYLEDITEM"));
            var materialStyleItems = new Dictionary<int, IfcColor>(); // styled items without Item (material definitions)
            foreach (var id in styledItems)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    var styled = File.Get(id);
                    var color = ColorOfStyles(styled["Styles"]);
                    if (color == null)
                        continue;
                    int item = styled.Ref("Item");
                    if (item > 0)
                        _itemColors[item] = color.Value;
                    else
                        materialStyleItems[id] = color.Value;
                }
                catch (Exception)
                {
                }
            }

            // Colours of materials: IfcMaterialDefinitionRepresentation → IfcStyledRepresentation → IfcStyledItem.
            foreach (var id in File.IdsOf(File.CodesOf("IFCMATERIALDEFINITIONREPRESENTATION")))
            {
                try
                {
                    var definition = File.Get(id);
                    int material = definition.Ref("RepresentedMaterial");
                    foreach (var representation in definition.Entities("Representations"))
                    {
                        foreach (var itemId in representation.Refs("Items"))
                        {
                            IfcColor? color = null;
                            if (materialStyleItems.TryGetValue(itemId, out var c))
                                color = c;
                            else if (File.TypeName(itemId) == "IFCSTYLEDITEM")
                                color = ColorOfStyles(File.Get(itemId)["Styles"]);
                            if (color != null && material > 0 && !_materialColors.ContainsKey(material))
                                _materialColors[material] = color.Value;
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>The surface colour in a list of IfcPresentationStyleAssignment / IfcSurfaceStyle.</summary>
        private IfcColor? ColorOfStyles(object styles, int depth = 0)
        {
            if (depth > 4)
                return null;
            if (!(styles is object[] list))
            {
                int single = StepEntity.AsRef(styles);
                if (single <= 0)
                    return null;
                list = new object[] { styles };
            }
            foreach (var value in list)
            {
                int id = StepEntity.AsRef(value);
                if (id <= 0)
                    continue;
                var color = _styleColors.GetOrAdd(id, key => ColorOfStyle(key, depth));
                if (color != null)
                    return color;
            }
            return null;
        }

        private IfcColor? ColorOfStyle(int id, int depth)
        {
            var style = File.Get(id);
            if (style == null)
                return null;
            if (style.IsA("IFCPRESENTATIONSTYLEASSIGNMENT"))
                return ColorOfStyles(style["Styles"], depth + 1);
            if (!style.IsA("IFCSURFACESTYLE"))
                return null;
            foreach (var element in style.Entities("Styles"))
            {
                if (element.IsA("IFCSURFACESTYLESHADING"))
                {
                    var rgb = element.Entity("SurfaceColour");
                    if (rgb == null)
                        continue;
                    double transparency = element.Has("Transparency") ? element.Double("Transparency", 0) : 0;
                    if (double.IsNaN(transparency))
                        transparency = 0;
                    return new IfcColor(rgb.Double("Red", 0.8), rgb.Double("Green", 0.8), rgb.Double("Blue", 0.8), transparency);
                }
            }
            return null;
        }

        /// <summary>The colour of a representation item from IfcStyledItem, if any.</summary>
        public IfcColor? ItemColor(int itemId) => _itemColors.TryGetValue(itemId, out var c) ? c : (IfcColor?)null;

        /// <summary>The colour of an element from its material (or its type's material).</summary>
        public IfcColor? MaterialColor(int productId)
        {
            foreach (var material in MaterialIds(productId))
                if (_materialColors.TryGetValue(material, out var c))
                    return c;
            return null;
        }

        // ------------------------------------------------------------------ spatial structure

        private void ReadSpatialLevels()
        {
            var storeys = File.IdsOf(File.CodesOf("IFCBUILDINGSTOREY"));
            var list = new List<SpatialLevel>();
            foreach (var id in storeys)
            {
                var storey = File.Get(id);
                var level = new SpatialLevel
                {
                    Id = id,
                    Name = (storey.String("Name") ?? "").Trim(),
                    ClassName = storey.ClassName,
                    Elevation = storey.Double("Elevation"),
                };
                if (level.Name.Length == 0)
                    level.Name = (storey.String("LongName") ?? "").Trim();
                if (level.Name.Length == 0)
                    level.Name = "Geschoss #" + id;
                list.Add(level);
            }
            // Same name twice (two buildings): the second gets its id.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int order = 0;
            foreach (var level in list.OrderBy(l => double.IsNaN(l.Elevation) ? double.MaxValue : l.Elevation).ThenBy(l => l.Id))
            {
                if (!seen.Add(level.Name))
                    level.Name = level.Name + " (#" + level.Id + ")";
                level.Order = order++;
                _levels[level.Id] = level;
            }
        }

        /// <summary>The storey an element belongs to: its container, the container of what it is part of, or the storey
        /// that holds a space it is in. Null: none.</summary>
        public SpatialLevel StoreyOf(int productId)
        {
            int current = productId;
            for (int guard = 0; guard < 64 && current > 0; guard++)
            {
                if (_levels.TryGetValue(current, out var level) && current != productId)
                    return level;
                if (_container.TryGetValue(current, out var container))
                {
                    if (_levels.TryGetValue(container, out level))
                        return level;
                    current = container; // a space or another spatial element: go up from there
                    continue;
                }
                if (_parent.TryGetValue(current, out var whole))
                {
                    current = whole;
                    continue;
                }
                break;
            }
            return null;
        }

        // ------------------------------------------------------------------ products

        private static readonly string[] ExcludedProducts =
        {
            "IFCOPENINGELEMENT", "IFCVIRTUALELEMENT", "IFCANNOTATION", "IFCSTRUCTURALITEM", "IFCSTRUCTURALACTIVITY",
            "IFCALIGNMENT", "IFCLINEARPOSITIONINGELEMENT", "IFCREFERENT", "IFCGRID", "IFCPORT", "IFCSPATIALZONE",
            "IFCBUILDINGSTOREY", "IFCBUILDING", "IFCPROJECT",
        };

        private void CollectProducts(ImportOptions options)
        {
            var codes = File.CodesOf("IFCPRODUCT");
            foreach (var code in codes.ToList())
            {
                var def = File.TypeDefOfCode(code);
                if (ExcludedProducts.Any(def.IsA) || (!options.Spaces && def.IsA("IFCSPACE")))
                    codes.Remove(code);
            }
            foreach (var id in File.IdsOf(codes))
            {
                var product = File.Get(id);
                if (product.Ref("Representation") > 0)
                    Products.Add(id);
            }
        }

        public bool IsSpace(int productId) => File.TypeDef(productId)?.IsA("IFCSPACE") == true;

        public List<int> OpeningsOf(int productId) => _openings.TryGetValue(productId, out var list) ? list : null;

        /// <summary>True if the element fills an opening (doors, windows).</summary>
        public bool FillsOpening(int productId) => _fillings.Contains(productId);

        public int TypeOf(int productId) => _type.TryGetValue(productId, out var t) ? t : 0;

        private Dictionary<int, string> _mapNames;
        private readonly object _mapNamesLock = new object();

        /// <summary>The name of the type (IfcTypeProduct) that owns a representation map, or null.</summary>
        public string MapTypeName(int mapId)
        {
            lock (_mapNamesLock)
            {
                if (_mapNames == null)
                {
                    _mapNames = new Dictionary<int, string>();
                    foreach (var id in File.IdsOf(File.CodesOf("IFCTYPEPRODUCT")))
                    {
                        var type = File.Get(id);
                        var name = type.String("Name");
                        if (string.IsNullOrWhiteSpace(name))
                            name = File.TypeName(id);
                        foreach (var map in type.Refs("RepresentationMaps"))
                            if (!_mapNames.ContainsKey(map))
                                _mapNames[map] = name;
                    }
                }
                return _mapNames.TryGetValue(mapId, out var result) ? result : null;
            }
        }

        // ------------------------------------------------------------------ placements

        /// <summary>Object placement → world (file units).</summary>
        public Mat4 Placement(int placementId) => Placement(placementId, 0);

        private Mat4 Placement(int placementId, int depth)
        {
            if (placementId <= 0 || depth > 100)
                return Mat4.Identity;
            if (_placements.TryGetValue(placementId, out var cached))
                return cached;
            var placement = File.Get(placementId);
            Mat4 result = Mat4.Identity;
            if (placement != null && placement.IsA("IFCLOCALPLACEMENT"))
            {
                var relative = Axis2Placement(placement.Entity("RelativePlacement"));
                int parent = placement.Ref("PlacementRelTo");
                result = parent > 0 ? Placement(parent, depth + 1) * relative : relative;
            }
            // Only placements that others are relative to are kept: the leaves (one per element) would fill the
            // memory on large models without being asked for again.
            if (depth > 0)
                _placements[placementId] = result;
            return result;
        }

        /// <summary>IfcAxis2Placement3D/2D (or IfcAxis1Placement) as a frame.</summary>
        public Mat4 Axis2Placement(StepEntity placement)
        {
            if (placement == null)
                return Mat4.Identity;
            var location = Point(placement.Entity("Location"));
            if (placement.IsA("IFCAXIS2PLACEMENT2D"))
            {
                var x2 = Direction(placement.Entity("RefDirection"), Vec3.XAxis);
                x2 = new Vec3(x2.X, x2.Y, 0).Unit();
                if (x2.LengthSquared < 0.5)
                    x2 = Vec3.XAxis;
                var y2 = new Vec3(-x2.Y, x2.X, 0);
                return Mat4.FromAxes(location, x2, y2, Vec3.ZAxis);
            }
            var z = Direction(placement.Entity("Axis"), Vec3.ZAxis).Unit();
            if (z.LengthSquared < 0.5)
                z = Vec3.ZAxis;
            var refDirection = Direction(placement.Entity("RefDirection"), Vec3.XAxis);
            var x = (refDirection - z * refDirection.Dot(z));
            if (x.Length < 1e-9)
            {
                // RefDirection parallel to Axis: take any perpendicular, as other readers do.
                x = Vec3.XAxis - z * z.X;
                if (x.Length < 1e-9)
                    x = Vec3.YAxis - z * z.Y;
            }
            x = x.Unit();
            var y = z.Cross(x);
            return Mat4.FromAxes(location, x, y, z);
        }

        public Vec3 Point(StepEntity point)
        {
            if (point == null)
                return Vec3.Zero;
            var coordinates = point.List("Coordinates");
            return ToVec(coordinates);
        }

        public Vec3 Direction(StepEntity direction, Vec3 fallback)
        {
            if (direction == null)
                return fallback;
            if (direction.IsA("IFCVECTOR"))
                return Direction(direction.Entity("Orientation"), fallback) * direction.Double("Magnitude", 1);
            var v = ToVec(direction.List("DirectionRatios"));
            return v.LengthSquared > 0 && v.IsValid ? v : fallback;
        }

        public static Vec3 ToVec(object[] values)
        {
            double x = values.Length > 0 ? StepEntity.AsDouble(values[0], 0) : 0;
            double y = values.Length > 1 ? StepEntity.AsDouble(values[1], 0) : 0;
            double z = values.Length > 2 ? StepEntity.AsDouble(values[2], 0) : 0;
            return new Vec3(x, y, z);
        }

        // ------------------------------------------------------------------ properties

        /// <summary>The user text of a product: IFC attributes first (IfcGlobalId, IfcClass …), then property sets and
        /// quantities as "Set.Property" (the occurrence's own values win over its type's).</summary>
        public List<KeyValuePair<string, string>> UserText(int productId)
        {
            var product = File.Get(productId);
            var result = new List<KeyValuePair<string, string>>();
            var keys = new HashSet<string>(StringComparer.Ordinal);

            void Add(string key, string value)
            {
                if (string.IsNullOrEmpty(value) || !keys.Add(key))
                    return;
                result.Add(new KeyValuePair<string, string>(key, value));
            }

            Add("IfcGlobalId", product.String("GlobalId"));
            Add("IfcClass", product.ClassName);
            Add("IfcName", product.String("Name"));
            Add("IfcDescription", product.String("Description"));
            int typeId = TypeOf(productId);
            var typeEntity = typeId > 0 ? File.Get(typeId) : null;
            if (typeEntity != null)
            {
                Add("IfcType", typeEntity.String("Name"));
                Add("IfcTypeClass", typeEntity.ClassName);
            }
            string predefined = product.Has("PredefinedType") ? product.Enum("PredefinedType") : null;
            if (predefined == null && typeEntity != null && typeEntity.Has("PredefinedType"))
                predefined = typeEntity.Enum("PredefinedType");
            if (predefined != null && predefined != "NOTDEFINED")
                Add("IfcPredefinedType", predefined);
            var objectType = product.String("ObjectType");
            if (objectType != predefined)
                Add("IfcObjectType", objectType);
            if (product.Has("Tag"))
                Add("IfcTag", product.String("Tag"));
            if (product.Has("LongName"))
                Add("IfcLongName", product.String("LongName"));
            Add("IfcStorey", StoreyOf(productId)?.Name);
            var materials = MaterialNames(productId);
            if (materials.Count > 0)
                Add("IfcMaterial", string.Join(" | ", materials));

            if (_propertySets.TryGetValue(productId, out var sets))
                foreach (var set in sets)
                    foreach (var pair in PropertySetValues(set))
                        Add(pair.Key, pair.Value);
            if (typeEntity != null)
            {
                var typeValues = _typeProperties.GetOrAdd(typeId, key =>
                {
                    var values = new List<KeyValuePair<string, string>>();
                    var type = File.Get(key);
                    foreach (var set in type.Refs("HasPropertySets"))
                        values.AddRange(PropertySetValues(set));
                    if (_propertySets.TryGetValue(key, out var typeSets)) // IFC4: types via IfcRelDefinesByProperties
                        foreach (var set in typeSets)
                            values.AddRange(PropertySetValues(set));
                    return values;
                });
                foreach (var pair in typeValues)
                    Add(pair.Key, pair.Value);
            }
            return result;
        }

        private IEnumerable<KeyValuePair<string, string>> PropertySetValues(int setId)
        {
            StepEntity set;
            try
            {
                set = File.Get(setId);
            }
            catch (Exception)
            {
                yield break;
            }
            if (set == null)
                yield break;
            string setName = (set.String("Name") ?? set.ClassName).Trim();
            if (set.IsA("IFCPROPERTYSET"))
            {
                foreach (var property in set.Entities("HasProperties"))
                {
                    var name = property.String("Name");
                    var value = PropertyValue(property);
                    if (!string.IsNullOrEmpty(name) && value != null)
                        yield return new KeyValuePair<string, string>(setName + "." + name.Trim(), value);
                }
            }
            else if (set.IsA("IFCELEMENTQUANTITY"))
            {
                foreach (var quantity in set.Entities("Quantities"))
                {
                    var name = quantity.String("Name");
                    if (string.IsNullOrEmpty(name))
                        continue;
                    if (quantity.IsA("IFCPHYSICALCOMPLEXQUANTITY"))
                    {
                        foreach (var part in quantity.Entities("HasQuantities"))
                        {
                            var value = QuantityValue(part);
                            if (value != null)
                                yield return new KeyValuePair<string, string>(setName + "." + name.Trim() + "." + (part.String("Name") ?? "").Trim(), value);
                        }
                        continue;
                    }
                    var text = QuantityValue(quantity);
                    if (text != null)
                        yield return new KeyValuePair<string, string>(setName + "." + name.Trim(), text);
                }
            }
            else
            {
                // IfcPreDefinedPropertySet (IFC4: IfcDoorLiningProperties …): its attributes after the root ones.
                var def = set.Def;
                if (def == null)
                    yield break;
                for (int i = 4; i < set.Args.Length && i < def.Attributes.Length; i++)
                {
                    var value = set.Args[i];
                    if (value == null || value is StepRef)
                        continue;
                    var text = StepEntity.FormatValue(value);
                    if (!string.IsNullOrEmpty(text))
                        yield return new KeyValuePair<string, string>(set.ClassName + "." + def.Attributes[i], text);
                }
            }
        }

        private static string PropertyValue(StepEntity property)
        {
            if (property.IsA("IFCPROPERTYSINGLEVALUE"))
                return StepEntity.FormatValue(property["NominalValue"]);
            if (property.IsA("IFCPROPERTYENUMERATEDVALUE"))
                return StepEntity.FormatValue(property["EnumerationValues"]);
            if (property.IsA("IFCPROPERTYLISTVALUE"))
                return StepEntity.FormatValue(property["ListValues"]);
            if (property.IsA("IFCPROPERTYBOUNDEDVALUE"))
            {
                var lower = StepEntity.FormatValue(property["LowerBoundValue"]);
                var upper = StepEntity.FormatValue(property["UpperBoundValue"]);
                if (lower == null && upper == null)
                    return null;
                return (lower ?? "") + " – " + (upper ?? "");
            }
            if (property.IsA("IFCPROPERTYREFERENCEVALUE"))
            {
                var reference = property.Entity("PropertyReference");
                return reference?.String("Name") ?? StepEntity.FormatValue(property["UsageName"]);
            }
            return null;
        }

        private static string QuantityValue(StepEntity quantity)
        {
            foreach (var attribute in new[] { "LengthValue", "AreaValue", "VolumeValue", "CountValue", "WeightValue", "TimeValue" })
                if (quantity.Has(attribute))
                    return StepEntity.FormatValue(quantity[attribute]);
            return null;
        }

        // ------------------------------------------------------------------ materials

        private IEnumerable<int> MaterialIds(int productId)
        {
            int select = _material.TryGetValue(productId, out var m) ? m : 0;
            if (select == 0)
            {
                int type = TypeOf(productId);
                if (type > 0 && _material.TryGetValue(type, out var tm))
                    select = tm;
            }
            if (select == 0)
                return Enumerable.Empty<int>();
            var result = new List<int>();
            CollectMaterials(select, result, 0);
            return result;
        }

        private void CollectMaterials(int id, List<int> result, int depth)
        {
            if (depth > 6 || id <= 0)
                return;
            var entity = File.Get(id);
            if (entity == null)
                return;
            switch (entity.Type)
            {
                case "IFCMATERIAL":
                    result.Add(id);
                    break;
                case "IFCMATERIALLAYERSETUSAGE":
                    CollectMaterials(entity.Ref("ForLayerSet"), result, depth + 1);
                    break;
                case "IFCMATERIALLAYERSET":
                    foreach (var layer in entity.Entities("MaterialLayers"))
                        CollectMaterials(layer.Ref("Material"), result, depth + 1);
                    break;
                case "IFCMATERIALLAYER":
                    CollectMaterials(entity.Ref("Material"), result, depth + 1);
                    break;
                case "IFCMATERIALLIST":
                    foreach (var material in entity.Refs("Materials"))
                        CollectMaterials(material, result, depth + 1);
                    break;
                case "IFCMATERIALPROFILESETUSAGE":
                case "IFCMATERIALPROFILESETUSAGETAPERING":
                    CollectMaterials(entity.Ref("ForProfileSet"), result, depth + 1);
                    break;
                case "IFCMATERIALPROFILESET":
                    foreach (var profile in entity.Entities("MaterialProfiles"))
                        CollectMaterials(profile.Ref("Material"), result, depth + 1);
                    break;
                case "IFCMATERIALPROFILE":
                    CollectMaterials(entity.Ref("Material"), result, depth + 1);
                    break;
                case "IFCMATERIALCONSTITUENTSET":
                    foreach (var constituent in entity.Entities("MaterialConstituents"))
                        CollectMaterials(constituent.Ref("Material"), result, depth + 1);
                    break;
                case "IFCMATERIALCONSTITUENT":
                    CollectMaterials(entity.Ref("Material"), result, depth + 1);
                    break;
            }
        }

        public List<string> MaterialNames(int productId)
        {
            var names = new List<string>();
            foreach (var id in MaterialIds(productId))
            {
                var name = File.Get(id)?.String("Name");
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name.Trim()))
                    names.Add(name.Trim());
            }
            return names;
        }

        // ------------------------------------------------------------------ document

        private void ReadDocumentText()
        {
            DocumentText["IfcSchema"] = File.SchemaIdentifier;
            if (!string.IsNullOrEmpty(ProjectName))
                DocumentText["IfcProject"] = ProjectName;
            if (!string.IsNullOrEmpty(File.Originator))
                DocumentText["IfcOriginatingSystem"] = File.Originator;
            DocumentText["IfcLengthUnit"] = LengthUnitName;

            var conversions = File.IdsOf(File.CodesOf("IFCMAPCONVERSION"));
            if (conversions.Count > 0)
            {
                var conversion = File.Get(conversions[0]);
                foreach (var name in new[] { "Eastings", "Northings", "OrthogonalHeight", "XAxisAbscissa", "XAxisOrdinate", "Scale" })
                {
                    double value = conversion.Double(name);
                    if (!double.IsNaN(value))
                        DocumentText["IfcMapConversion." + name] = StepEntity.FormatNumber(value);
                }
                double abscissa = conversion.Double("XAxisAbscissa"), ordinate = conversion.Double("XAxisOrdinate");
                if (!double.IsNaN(abscissa) && !double.IsNaN(ordinate))
                    DocumentText["IfcMapConversion.Rotation"] = StepEntity.FormatNumber(Math.Atan2(ordinate, abscissa) * 180 / Math.PI);
                var crs = conversion.Entity("TargetCRS");
                if (crs != null)
                    DocumentText["IfcMapConversion.TargetCRS"] = crs.String("Name") ?? "";
            }
            var sites = File.IdsOf(File.CodesOf("IFCSITE"));
            if (sites.Count > 0)
            {
                var site = File.Get(sites[0]);
                var latitude = site["RefLatitude"] as object[];
                var longitude = site["RefLongitude"] as object[];
                if (latitude != null && latitude.Length >= 3)
                    DocumentText["IfcSite.RefLatitude"] = CompoundAngle(latitude);
                if (longitude != null && longitude.Length >= 3)
                    DocumentText["IfcSite.RefLongitude"] = CompoundAngle(longitude);
                double elevation = site.Double("RefElevation");
                if (!double.IsNaN(elevation))
                    DocumentText["IfcSite.RefElevation"] = StepEntity.FormatNumber(elevation);
            }
        }

        private static string CompoundAngle(object[] parts)
        {
            double degrees = StepEntity.AsDouble(parts[0], 0);
            double minutes = StepEntity.AsDouble(parts[1], 0);
            double seconds = StepEntity.AsDouble(parts[2], 0);
            double micro = parts.Length > 3 ? StepEntity.AsDouble(parts[3], 0) : 0;
            double value = Math.Abs(degrees) + Math.Abs(minutes) / 60 + (Math.Abs(seconds) + Math.Abs(micro) / 1e6) / 3600;
            bool negative = degrees < 0 || minutes < 0 || seconds < 0;
            return StepEntity.FormatNumber(negative ? -value : value);
        }
    }
}
