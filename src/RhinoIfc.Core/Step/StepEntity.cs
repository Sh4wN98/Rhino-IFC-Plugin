using System;
using System.Collections.Generic;
using System.Globalization;
using RhinoIfc.Core.Schema;

namespace RhinoIfc.Core.Step
{
    /// <summary>One parsed entity instance. Attributes are addressed by their IFC name (works across IFC2X3/IFC4/IFC4X3,
    /// where positions differ) or by position.</summary>
    public sealed class StepEntity
    {
        private readonly StepFile _file;
        private readonly ushort _code;
        private readonly string[] _complexParts;

        internal StepEntity(StepFile file, int id, ushort code, string[] complexParts, object[] args)
        {
            _file = file;
            Id = id;
            _code = code;
            _complexParts = complexParts;
            Args = args;
        }

        public int Id { get; }

        public object[] Args { get; }

        public StepFile File => _file;

        /// <summary>Upper-case STEP name: IFCWALL; for a complex instance the last part.</summary>
        public string Type => _complexParts != null ? _complexParts[_complexParts.Length - 1] : _file.TypeNameOfCode(_code);

        public EntityDef Def => _complexParts != null ? null : _file.TypeDefOfCode(_code);

        /// <summary>IFC case: IfcWall.</summary>
        public string ClassName => Def?.Name ?? Type;

        public bool IsA(string upperName)
        {
            if (_complexParts != null)
            {
                foreach (var part in _complexParts)
                {
                    var def = _file.Schema.Find(part);
                    if (part == upperName || (def != null && def.IsA(upperName)))
                        return true;
                }
                return false;
            }
            var d = Def;
            return d != null ? d.IsA(upperName) : Type == upperName;
        }

        /// <summary>The raw value of an attribute by name; null if missing, unset or unknown.</summary>
        public object this[string attribute]
        {
            get
            {
                var def = Def;
                if (def == null)
                    return null;
                int index = def.IndexOf(attribute);
                return index >= 0 && index < Args.Length ? Args[index] : null;
            }
        }

        public object At(int index) => index >= 0 && index < Args.Length ? Args[index] : null;

        public bool Has(string attribute) => Def != null && Def.IndexOf(attribute) >= 0;

        // ------------------------------------------------------------ typed access

        public int Ref(string attribute) => AsRef(this[attribute]);

        public StepEntity Entity(string attribute)
        {
            int id = Ref(attribute);
            return id > 0 ? _file.Get(id) : null;
        }

        public double Double(string attribute, double fallback = double.NaN) => AsDouble(this[attribute], fallback);

        public string String(string attribute) => AsString(this[attribute]);

        public object[] List(string attribute) => this[attribute] as object[] ?? Array.Empty<object>();

        public List<int> Refs(string attribute)
        {
            var result = new List<int>();
            foreach (var item in List(attribute))
            {
                int id = AsRef(item);
                if (id > 0)
                    result.Add(id);
            }
            return result;
        }

        public List<StepEntity> Entities(string attribute)
        {
            var result = new List<StepEntity>();
            foreach (var id in Refs(attribute))
            {
                var e = _file.Get(id);
                if (e != null)
                    result.Add(e);
            }
            return result;
        }

        /// <summary>.T. → true, .F. → false, otherwise null.</summary>
        public bool? Bool(string attribute) => AsBool(this[attribute]);

        public string Enum(string attribute) => (this[attribute] as StepEnum)?.Value;

        // ------------------------------------------------------------ value helpers

        public static int AsRef(object value)
        {
            if (value is StepRef r)
                return r.Id;
            return 0;
        }

        public static double AsDouble(object value, double fallback = double.NaN)
        {
            switch (value)
            {
                case double d:
                    return d;
                case long l:
                    return l;
                case StepTyped t:
                    return AsDouble(t.Value, fallback);
                default:
                    return fallback;
            }
        }

        public static bool? AsBool(object value)
        {
            if (value is StepTyped t)
                value = t.Value;
            if (value is StepEnum e)
            {
                if (e.Value == "T" || e.Value == "TRUE")
                    return true;
                if (e.Value == "F" || e.Value == "FALSE")
                    return false;
            }
            return null;
        }

        public static string AsString(object value)
        {
            switch (value)
            {
                case null:
                    return null;
                case string s:
                    return s;
                case StepTyped t:
                    return AsString(t.Value);
                case StepEnum e:
                    return e.Value;
                default:
                    return null;
            }
        }

        /// <summary>A property value as text: numbers invariant, booleans TRUE/FALSE, lists comma-separated.</summary>
        public static string FormatValue(object value)
        {
            switch (value)
            {
                case null:
                    return null;
                case StepTyped t:
                    return FormatValue(t.Value);
                case string s:
                    return s;
                case double d:
                    return FormatNumber(d);
                case long l:
                    return l.ToString(CultureInfo.InvariantCulture);
                case StepEnum e:
                    if (e.Value == "T")
                        return "TRUE";
                    if (e.Value == "F")
                        return "FALSE";
                    if (e.Value == "U")
                        return "UNKNOWN";
                    return e.Value;
                case object[] list:
                    var parts = new List<string>();
                    foreach (var item in list)
                    {
                        var text = FormatValue(item);
                        if (text != null)
                            parts.Add(text);
                    }
                    return string.Join(", ", parts);
                case StepRef r:
                    return r.ToString();
                default:
                    return value.ToString();
            }
        }

        /// <summary>Shortest round-trip-ish text without float noise: 0.30000000000000004 → 0.3.</summary>
        public static string FormatNumber(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
                return d.ToString(CultureInfo.InvariantCulture);
            var text = d.ToString("G15", CultureInfo.InvariantCulture);
            return text;
        }

        public override string ToString() => "#" + Id + "=" + Type;
    }
}
