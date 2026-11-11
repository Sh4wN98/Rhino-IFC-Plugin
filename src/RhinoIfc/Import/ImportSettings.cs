using Rhino;
using RhinoIfc.Core;

namespace RhinoIfc.Import
{
    /// <summary>The remembered import options, in Rhino's plugin settings.</summary>
    public sealed class ImportSettings
    {
        public ImportOptions Options { get; set; } = new ImportOptions();

        public static ImportSettings Load(PersistentSettings settings)
        {
            var options = new ImportOptions
            {
                Geometry = settings.GetBool("Meshes", false) ? GeometryMode.Meshes : GeometryMode.Polysurfaces,
                Blocks = settings.GetBool("Blocks", false),
                CutOpenings = settings.GetBool("CutOpenings", true),
                Spaces = settings.GetBool("Spaces", true),
                Parallel = settings.GetBool("Parallel", true),
            };
            return new ImportSettings { Options = options };
        }

        public void Save(PersistentSettings settings)
        {
            settings.SetBool("Meshes", Options.Geometry == GeometryMode.Meshes);
            settings.SetBool("Blocks", Options.Blocks);
            settings.SetBool("CutOpenings", Options.CutOpenings);
            settings.SetBool("Spaces", Options.Spaces);
            settings.SetBool("Parallel", Options.Parallel);
        }
    }
}
