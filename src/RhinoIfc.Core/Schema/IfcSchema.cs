using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace RhinoIfc.Core.Schema
{
    /// <summary>One IFC schema (IFC2X3, IFC4, IFC4X3_ADD2): entity names in their proper case, supertypes and the
    /// attribute order, so the reader can address attributes by name and works the same in every schema version.
    /// The data comes from ifc-schemas.txt (see tools/generate_schema.py).</summary>
    public sealed class IfcSchema
    {
        private static readonly Dictionary<string, IfcSchema> Schemas = Load();

        private readonly Dictionary<string, EntityDef> _byUpper = new Dictionary<string, EntityDef>(StringComparer.Ordinal);

        private IfcSchema(string name)
        {
            Name = name;
        }

        public string Name { get; }

        /// <summary>The schema for a FILE_SCHEMA identifier: IFC2X3 for IFC2X3/IFC2X2, IFC4X3_ADD2 for IFC4X1–IFC4X3*,
        /// otherwise IFC4.</summary>
        public static IfcSchema ForIdentifier(string identifier)
        {
            var id = (identifier ?? "").Trim().ToUpperInvariant();
            if (id.StartsWith("IFC2X", StringComparison.Ordinal))
                return Schemas["IFC2X3"];
            if (id.StartsWith("IFC4X", StringComparison.Ordinal))
                return Schemas["IFC4X3_ADD2"];
            return Schemas["IFC4"];
        }

        /// <summary>The entity definition for an upper-case STEP type name (IFCWALLSTANDARDCASE); entities that the
        /// file's schema does not know are looked up in the others, so a slightly off file still reads.</summary>
        public EntityDef Find(string upperName)
        {
            if (upperName == null)
                return null;
            if (_byUpper.TryGetValue(upperName, out var def))
                return def;
            foreach (var schema in Schemas.Values)
                if (schema != this && schema._byUpper.TryGetValue(upperName, out def))
                    return def;
            return null;
        }

        private static Dictionary<string, IfcSchema> Load()
        {
            var result = new Dictionary<string, IfcSchema>(StringComparer.Ordinal);
            var assembly = typeof(IfcSchema).GetTypeInfo().Assembly;
            string resource = null;
            foreach (var name in assembly.GetManifestResourceNames())
                if (name.EndsWith("ifc-schemas.txt", StringComparison.Ordinal))
                    resource = name;
            if (resource == null)
                throw new InvalidOperationException("ifc-schemas.txt is not embedded in RhinoIfc.Core.");

            IfcSchema current = null;
            var parents = new List<KeyValuePair<EntityDef, string>>();
            using (var stream = assembly.GetManifestResourceStream(resource))
            using (var reader = new StreamReader(stream))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] == '#')
                        continue;
                    if (line.StartsWith("S ", StringComparison.Ordinal))
                    {
                        current = new IfcSchema(line.Substring(2).Trim());
                        result[current.Name] = current;
                        continue;
                    }
                    var parts = line.Split(';');
                    var attributes = parts.Length > 2 && parts[2].Length > 0 ? parts[2].Split(',') : new string[0];
                    var def = new EntityDef(parts[0], attributes, current);
                    current._byUpper[parts[0].ToUpperInvariant()] = def;
                    if (parts.Length > 1 && parts[1].Length > 0)
                        parents.Add(new KeyValuePair<EntityDef, string>(def, parts[1].ToUpperInvariant()));
                }
            }
            foreach (var pair in parents)
                pair.Key.Parent = pair.Key.Schema._byUpper.TryGetValue(pair.Value, out var parent) ? parent : null;
            return result;
        }
    }

    /// <summary>An IFC entity type: name, supertype, attribute positions.</summary>
    public sealed class EntityDef
    {
        private readonly Dictionary<string, int> _attributeIndex;

        internal EntityDef(string name, string[] attributes, IfcSchema schema)
        {
            Name = name;
            UpperName = name.ToUpperInvariant();
            Attributes = attributes;
            Schema = schema;
            _attributeIndex = new Dictionary<string, int>(attributes.Length, StringComparer.Ordinal);
            for (int i = 0; i < attributes.Length; i++)
                _attributeIndex[attributes[i]] = i;
        }

        /// <summary>The name in IFC case: IfcWallStandardCase.</summary>
        public string Name { get; }

        public string UpperName { get; }

        public string[] Attributes { get; }

        public EntityDef Parent { get; internal set; }

        public IfcSchema Schema { get; }

        /// <summary>The position of an attribute (with the inherited ones), -1 if this entity does not have it.</summary>
        public int IndexOf(string attribute) => _attributeIndex.TryGetValue(attribute, out var i) ? i : -1;

        /// <summary>True if this entity is <paramref name="upperName"/> or a subtype of it.</summary>
        public bool IsA(string upperName)
        {
            for (var def = this; def != null; def = def.Parent)
                if (string.Equals(def.UpperName, upperName, StringComparison.Ordinal))
                    return true;
            return false;
        }

        public override string ToString() => Name;
    }
}
