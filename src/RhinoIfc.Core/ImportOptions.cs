namespace RhinoIfc.Core
{
    /// <summary>How the IFC Converter builds the Rhino geometry.</summary>
    public enum GeometryMode
    {
        /// <summary>Joined polysurfaces; very detailed elements stay meshes (converter: <c>-g brep</c>).</summary>
        Polysurfaces,

        /// <summary>Meshes only (converter: <c>-g mesh</c>).</summary>
        Meshes,
    }

    /// <summary>The options of one import, as the commands and the import dialog set them.</summary>
    public sealed class ImportOptions
    {
        public GeometryMode Geometry { get; set; } = GeometryMode.Polysurfaces;

        /// <summary>Type geometry placed several times becomes Rhino blocks (<c>--blocks</c>).</summary>
        public bool Blocks { get; set; }

        /// <summary>Only the building hull (<c>--envelope</c>).</summary>
        public bool EnvelopeOnly { get; set; }

        /// <summary>accurate, hybrid or opencascade; null or empty: the converter's default.</summary>
        public string Kernel { get; set; }

        /// <summary>Language of the converter's messages (en, de, …); null: English.</summary>
        public string Language { get; set; }

        public ImportOptions Clone() => (ImportOptions)MemberwiseClone();
    }
}
