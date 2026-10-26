using Rhino;
using Rhino.FileIO;
using Rhino.PlugIns;
using RhinoIfc.Import;

namespace RhinoIfc
{
    /// <summary>Adds IFC (.ifc, .ifczip) to File › Open and File › Import, and the Ifc* commands.</summary>
    public class IfcPlugIn : FileImportPlugIn
    {
        public IfcPlugIn()
        {
            Instance = this;
        }

        public static IfcPlugIn Instance { get; private set; }

        protected override FileTypeList AddFileTypes(FileReadOptions options)
        {
            var types = new FileTypeList();
            types.AddFileType("IFC (*.ifc, *.ifczip)", new[] { "ifc", "ifczip" });
            return types;
        }

        protected override bool ReadFile(string filename, int index, RhinoDoc doc, FileReadOptions options)
        {
            var settings = ImportSettings.Load(Settings);
            var importOptions = settings.Options;
            if (!options.BatchMode)
            {
                // File › Open and File › Import ask like other importers; the choice is remembered.
                var dialog = new ImportOptionsDialog(importOptions, System.IO.Path.GetFileName(filename));
                if (!dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow))
                    return false;
                importOptions = dialog.Options;
                settings.Options = importOptions;
                settings.Save(Settings);
            }
            var result = IfcImporter.Import(doc, filename, importOptions, settings.ConverterPath, options.OpenMode, !options.BatchMode);
            return result.Success;
        }
    }
}
