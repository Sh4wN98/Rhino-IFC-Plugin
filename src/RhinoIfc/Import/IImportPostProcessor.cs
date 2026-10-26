using System;
using System.Collections.Generic;
using Rhino;
using RhinoIfc.Core;

namespace RhinoIfc.Import
{
    /// <summary>A step that works on the objects of an IFC import after they are in the document, e.g. turning
    /// remaining meshes into polysurfaces. Runs in order of <see cref="ImportPostProcessors.All"/>; the objects
    /// carry the IFC user text (IfcGlobalId, IfcClass, property sets), which a step keeps when it replaces one
    /// (doc.Objects.Replace keeps the attributes).</summary>
    public interface IImportPostProcessor
    {
        /// <summary>For the command line ("Meshes werden umgewandelt …").</summary>
        string Name { get; }

        /// <param name="objects">The ids of the objects the import added (block definitions' objects not included).</param>
        /// <param name="cancelRequested">True once the user pressed Esc.</param>
        void Process(RhinoDoc doc, IReadOnlyList<Guid> objects, ImportOptions options, Func<bool> cancelRequested);
    }

    public static class ImportPostProcessors
    {
        /// <summary>None yet: the converter already delivers polysurfaces. A mesh-to-polysurface step registers here.</summary>
        public static List<IImportPostProcessor> All { get; } = new List<IImportPostProcessor>();
    }
}
