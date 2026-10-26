using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoIfc.Core;
using RhinoIfc.Import;

namespace RhinoIfc.Commands
{
    /// <summary>IfcImport: an IFC file into the current document. -IfcImport takes the path and the options on
    /// the command line (for scripts and macros).</summary>
    public class IfcImportCommand : Command
    {
        public override string EnglishName => "IfcImport";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var plugIn = IfcPlugIn.Instance;
            var settings = ImportSettings.Load(plugIn.Settings);
            var options = settings.Options;
            string path;

            if (mode == RunMode.Interactive)
            {
                var open = new Rhino.UI.OpenFileDialog
                {
                    Title = "IFC-Datei importieren",
                    Filter = "IFC-Dateien (*.ifc, *.ifczip)|*.ifc;*.ifczip|Alle Dateien (*.*)|*.*",
                };
                if (!open.ShowOpenDialog())
                    return Result.Cancel;
                path = open.FileName;
                var dialog = new ImportOptionsDialog(options, System.IO.Path.GetFileName(path));
                if (!dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow))
                    return Result.Cancel;
                options = dialog.Options;
            }
            else
            {
                path = AskPath(options);
                if (path == null)
                    return Result.Cancel;
            }

            settings.Options = options;
            settings.Save(plugIn.Settings);
            var result = IfcImporter.Import(doc, path, options, settings.ConverterPath, false, mode == RunMode.Interactive);
            if (result.Cancelled)
                return Result.Cancel;
            return result.Success ? Result.Success : Result.Failure;
        }

        /// <summary>The path with the options Geometry, Blocks, EnvelopeOnly on the command line.</summary>
        private static string AskPath(ImportOptions options)
        {
            var geometryValues = new[] { "Polysurfaces", "Meshes" };
            var blocks = new OptionToggle(options.Blocks, "No", "Yes");
            var envelope = new OptionToggle(options.EnvelopeOnly, "No", "Yes");
            while (true)
            {
                var get = new GetString();
                get.SetCommandPrompt("IFC-Datei (Pfad)");
                int geometry = get.AddOptionList("Geometry", geometryValues, options.Geometry == GeometryMode.Meshes ? 1 : 0);
                get.AddOptionToggle("Blocks", ref blocks);
                get.AddOptionToggle("EnvelopeOnly", ref envelope);
                var answer = get.GetLiteralString();
                if (answer == GetResult.Option)
                {
                    if (get.OptionIndex() == geometry)
                        options.Geometry = get.Option().CurrentListOptionIndex == 1 ? GeometryMode.Meshes : GeometryMode.Polysurfaces;
                    options.Blocks = blocks.CurrentValue;
                    options.EnvelopeOnly = envelope.CurrentValue;
                    continue;
                }
                if (answer != GetResult.String)
                    return null;
                options.Blocks = blocks.CurrentValue;
                options.EnvelopeOnly = envelope.CurrentValue;
                var path = get.StringResult().Trim().Trim('"');
                return path.Length > 0 ? path : null;
            }
        }
    }
}
