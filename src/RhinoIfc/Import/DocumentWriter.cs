using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoIfc.Core.Geometry;
using RhinoIfc.Core.Model;
using RhinoIfc.Core.Step;

namespace RhinoIfc.Import
{
    /// <summary>Puts the built geometry into the document on Rhino's main thread: layers "storey::class", materials for
    /// colours and transparency, user text, groups for elements with several objects, block definitions.</summary>
    internal sealed class DocumentWriter
    {
        public const string BlockLayer = "IFC Blockdefinitionen";

        private readonly RhinoDoc _doc;
        private readonly IfcModel _model;
        private readonly Dictionary<string, int> _layers = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<IfcColor, int> _materials = new Dictionary<IfcColor, int>();
        private readonly Dictionary<int, int> _definitions = new Dictionary<int, int>();

        public DocumentWriter(RhinoDoc doc, IfcModel model, double scale)
        {
            _doc = doc;
            _model = model;
            Scale = scale;
        }

        /// <summary>File length unit → document unit.</summary>
        public double Scale { get; }

        public int Objects { get; private set; }

        /// <summary>The storey layers first, in the order of their elevation.</summary>
        public void CreateStoreyLayers()
        {
            foreach (var level in _model.Levels)
                Layer(level.Name, false);
        }

        public void WriteDocumentText(string ifcPath, bool openMode)
        {
            if (!openMode && !string.IsNullOrEmpty(_doc.Strings.GetValue("IfcSourceFile")))
            {
                RhinoApp.WriteLine($"IFC: Das Dokument hat schon IFC-Dokumenttext (IfcSourceFile = {_doc.Strings.GetValue("IfcSourceFile")}); "
                    + $"der von {Path.GetFileName(ifcPath)} wird nicht übernommen.");
                return;
            }
            _doc.Strings.SetString("IfcSourceFile", Path.GetFileName(ifcPath));
            foreach (var pair in _model.DocumentText)
                _doc.Strings.SetString(pair.Key, pair.Value);
        }

        /// <summary>The layer for a full path ("EG::IfcWall"), created on first use; parents are created as needed.</summary>
        public int Layer(string fullPath, bool hidden)
        {
            if (_layers.TryGetValue(fullPath, out var index))
                return index;
            var parts = fullPath.Split(new[] { "::" }, StringSplitOptions.None);
            Guid parent = Guid.Empty;
            string path = "";
            for (int i = 0; i < parts.Length; i++)
            {
                var name = Clean(parts[i]);
                path = path.Length == 0 ? name : path + "::" + name;
                if (!_layers.TryGetValue(path, out index))
                {
                    index = _doc.Layers.FindByFullPath(path, -1);
                    if (index < 0)
                    {
                        var layer = new Layer { Name = name, ParentLayerId = parent };
                        if (i == parts.Length - 1 && hidden)
                            layer.IsVisible = false;
                        index = _doc.Layers.Add(layer);
                        if (index < 0)
                            index = _doc.Layers.CurrentLayerIndex;
                    }
                    _layers[path] = index;
                }
                parent = _doc.Layers[index].Id;
            }
            _layers[fullPath] = index;
            return index;
        }

        /// <summary>Rhino layer names may not contain some characters and no leading/trailing spaces.</summary>
        private static string Clean(string name)
        {
            var cleaned = new string((name ?? "").Select(c => c == ':' || c == ';' || c == '[' || c == ']' || char.IsControl(c) ? '_' : c).ToArray()).Trim();
            if (cleaned.StartsWith("(") || cleaned.StartsWith("{"))
                cleaned = "_" + cleaned;
            return cleaned.Length > 0 ? cleaned : "_";
        }

        private int Material(IfcColor color)
        {
            if (_materials.TryGetValue(color, out var index))
                return index;
            var material = new Material
            {
                Name = $"IFC {color.R8:000}-{color.G8:000}-{color.B8:000}" + (color.Transparency > 0 ? $" T{color.Transparency * 100:0}" : ""),
                DiffuseColor = ToColor(color),
                Transparency = color.Transparency,
            };
            index = _doc.Materials.Add(material);
            _materials[color] = index;
            return index;
        }

        private static Color ToColor(IfcColor color) => Color.FromArgb(255, color.R8, color.G8, color.B8);

        private ObjectAttributes Attributes(int layer, IfcColor? color, string name)
        {
            var attributes = new ObjectAttributes { LayerIndex = layer };
            if (!string.IsNullOrEmpty(name))
                attributes.Name = name;
            if (color != null)
            {
                attributes.ColorSource = ObjectColorSource.ColorFromObject;
                attributes.ObjectColor = ToColor(color.Value);
                attributes.MaterialSource = ObjectMaterialSource.MaterialFromObject;
                attributes.MaterialIndex = Material(color.Value);
            }
            return attributes;
        }

        public void Add(string layerPath, bool hidden, string name, List<KeyValuePair<string, string>> userText, List<string> problems,
            List<BuiltGeometry> geometry, List<Tuple<int, Lazy<IfcImporter.BlockGeometry>, Transform>> instances)
        {
            int count = geometry.Count + instances.Count;
            if (count == 0)
                return;
            int layer = Layer(layerPath, hidden);
            int group = -1;
            if (count > 1)
            {
                var globalId = userText?.FirstOrDefault(p => p.Key == "IfcGlobalId").Value;
                group = _doc.Groups.Add(globalId ?? name);
            }

            void Decorate(ObjectAttributes attributes)
            {
                if (userText != null)
                    foreach (var pair in userText)
                        attributes.SetUserString(pair.Key, pair.Value);
                if (problems != null && problems.Count > 0)
                    attributes.SetUserString("IfcImportNote", string.Join("; ", problems.Distinct()));
                if (group >= 0)
                    attributes.AddToGroup(group);
            }

            foreach (var piece in geometry)
            {
                if (piece.Geometry == null || !piece.Geometry.IsValid)
                    continue;
                var attributes = Attributes(layer, piece.Color, name);
                Decorate(attributes);
                if (_doc.Objects.Add(piece.Geometry, attributes) != Guid.Empty)
                    Objects++;
            }
            foreach (var instance in instances)
            {
                int definition = Definition(instance.Item1, instance.Item2.Value);
                if (definition < 0)
                    continue;
                var attributes = new ObjectAttributes { LayerIndex = layer };
                if (!string.IsNullOrEmpty(name))
                    attributes.Name = name;
                Decorate(attributes);
                if (_doc.Objects.AddInstanceObject(definition, instance.Item3, attributes) != Guid.Empty)
                    Objects++;
            }
        }

        private int Definition(int mapId, IfcImporter.BlockGeometry block)
        {
            if (_definitions.TryGetValue(mapId, out var index))
                return index;
            index = -1;
            var geometry = new List<GeometryBase>();
            var attributes = new List<ObjectAttributes>();
            int layer = Layer(BlockLayer, false);
            foreach (var piece in block.Geometry)
            {
                if (piece.Geometry == null || !piece.Geometry.IsValid)
                    continue;
                geometry.Add(piece.Geometry);
                attributes.Add(Attributes(layer, piece.Color, null));
            }
            if (geometry.Count > 0)
            {
                var name = block.Name;
                for (int i = 2; _doc.InstanceDefinitions.Find(name) != null; i++)
                    name = block.Name + " (" + i + ")";
                index = _doc.InstanceDefinitions.Add(name, "IFC-Typgeometrie (IfcRepresentationMap #" + mapId + ")", Point3d.Origin, geometry, attributes);
            }
            _definitions[mapId] = index;
            return index;
        }
    }

    /// <summary>Colours for elements without an IFC style or material colour, by class.</summary>
    internal static class DefaultColors
    {
        public static IfcColor Of(StepEntity entity)
        {
            if (entity.IsA("IFCWINDOW") || entity.IsA("IFCPLATE") && (entity.String("Name") ?? "").IndexOf("glas", StringComparison.OrdinalIgnoreCase) >= 0)
                return new IfcColor(0.6, 0.75, 0.85, 0.6);
            if (entity.IsA("IFCDOOR"))
                return new IfcColor(0.55, 0.40, 0.25);
            if (entity.IsA("IFCSPACE"))
                return new IfcColor(0.55, 0.75, 0.85, 0.85);
            if (entity.IsA("IFCSLAB") || entity.IsA("IFCROOF"))
                return new IfcColor(0.55, 0.55, 0.55);
            if (entity.IsA("IFCCOLUMN") || entity.IsA("IFCBEAM") || entity.IsA("IFCMEMBER"))
                return new IfcColor(0.60, 0.60, 0.65);
            if (entity.IsA("IFCSITE") || entity.IsA("IFCGEOGRAPHICELEMENT"))
                return new IfcColor(0.55, 0.65, 0.45);
            if (entity.IsA("IFCDISTRIBUTIONELEMENT") || entity.IsA("IFCFLOWSEGMENT") || entity.IsA("IFCFLOWFITTING"))
                return new IfcColor(0.45, 0.60, 0.75);
            if (entity.IsA("IFCFURNISHINGELEMENT") || entity.IsA("IFCFURNITURE"))
                return new IfcColor(0.75, 0.62, 0.45);
            if (entity.IsA("IFCRAILING") || entity.IsA("IFCSTAIR") || entity.IsA("IFCSTAIRFLIGHT") || entity.IsA("IFCRAMP"))
                return new IfcColor(0.65, 0.65, 0.65);
            return new IfcColor(0.85, 0.85, 0.82);
        }
    }
}
