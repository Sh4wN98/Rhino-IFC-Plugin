namespace RhinoIfc.Core
{
    /// <summary>How the import builds the Rhino geometry.</summary>
    public enum GeometryMode
    {
        /// <summary>Extrusions and joined polysurfaces with real openings; very detailed or curved faceted elements
        /// stay meshes.</summary>
        Polysurfaces,

        /// <summary>Meshes only: fastest, smallest, for viewing and reference.</summary>
        Meshes,
    }

    /// <summary>The options of one import, as the commands and the import dialog set them.</summary>
    public sealed class ImportOptions
    {
        public GeometryMode Geometry { get; set; } = GeometryMode.Polysurfaces;

        /// <summary>Type geometry placed several times (IfcMappedItem) becomes Rhino block instances.</summary>
        public bool Blocks { get; set; }

        /// <summary>Openings (IfcOpeningElement) are cut out of walls, slabs … with a Boolean difference.</summary>
        public bool CutOpenings { get; set; } = true;

        /// <summary>Rooms (IfcSpace) are imported, on layers that start hidden.</summary>
        public bool Spaces { get; set; } = true;

        /// <summary>Build the geometry on several processor cores.</summary>
        public bool Parallel { get; set; } = true;

        /// <summary>Faceted elements with more triangles than this stay meshes in polysurface mode (joining them would be
        /// slow and gives no editable result).</summary>
        public int MaxPolysurfaceTriangles { get; set; } = 2000;

        public ImportOptions Clone() => (ImportOptions)MemberwiseClone();
    }
}
