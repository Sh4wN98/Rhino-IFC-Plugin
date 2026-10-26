using Rhino;
using RhinoIfc.Core;

namespace RhinoIfc.Import
{
    /// <summary>The remembered import options and the converter path, in Rhino's plugin settings.</summary>
    public sealed class ImportSettings
    {
        public ImportOptions Options { get; set; } = new ImportOptions();

        /// <summary>ifc-convert.exe or python.exe chosen with IfcConverter; empty: search.</summary>
        public string ConverterPath { get; set; } = "";

        public static ImportSettings Load(PersistentSettings settings)
        {
            var options = new ImportOptions
            {
                Geometry = settings.GetBool("Meshes", false) ? GeometryMode.Meshes : GeometryMode.Polysurfaces,
                Blocks = settings.GetBool("Blocks", false),
                EnvelopeOnly = settings.GetBool("EnvelopeOnly", false),
                Kernel = settings.GetString("Kernel", ""),
                Language = RhinoLanguage(),
            };
            return new ImportSettings { Options = options, ConverterPath = settings.GetString("ConverterPath", "") };
        }

        public void Save(PersistentSettings settings)
        {
            settings.SetBool("Meshes", Options.Geometry == GeometryMode.Meshes);
            settings.SetBool("Blocks", Options.Blocks);
            settings.SetBool("EnvelopeOnly", Options.EnvelopeOnly);
            settings.SetString("Kernel", Options.Kernel ?? "");
            settings.SetString("ConverterPath", ConverterPath ?? "");
        }

        /// <summary>The converter's language matching Rhino's (it knows en, de, fr, es, it, pt, nl, pl, tr).</summary>
        public static string RhinoLanguage()
        {
            try
            {
                var culture = new System.Globalization.CultureInfo(Rhino.ApplicationSettings.AppearanceSettings.LanguageIdentifier);
                var language = culture.TwoLetterISOLanguageName;
                return "en de fr es it pt nl pl tr".Contains(language) ? language : "en";
            }
            catch (System.Exception)
            {
                return "en";
            }
        }
    }
}
