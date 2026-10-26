using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoIfc.Core;
using RhinoIfc.Import;

namespace RhinoIfc.Commands
{
    /// <summary>IfcConverter: shows which IFC Converter the import uses, and sets or resets its path.</summary>
    public class IfcConverterCommand : Command
    {
        public override string EnglishName => "IfcConverter";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var plugIn = IfcPlugIn.Instance;
            var settings = ImportSettings.Load(plugIn.Settings);
            Report(settings.ConverterPath);

            var get = new GetOption();
            get.SetCommandPrompt("IFC Converter");
            get.AcceptNothing(true);
            int browse = get.AddOption("Browse");
            int reset = get.AddOption("Search");
            var answer = get.Get();
            if (answer != GetResult.Option)
                return answer == GetResult.Nothing ? Result.Success : Result.Cancel;

            if (get.OptionIndex() == reset)
                settings.ConverterPath = "";
            else if (get.OptionIndex() == browse)
            {
                string path;
                if (mode == RunMode.Interactive)
                {
                    var open = new Rhino.UI.OpenFileDialog
                    {
                        Title = "ifc-convert.exe oder python.exe (mit ifc_converter) wählen",
                        Filter = "Programme (*.exe)|*.exe",
                    };
                    if (!open.ShowOpenDialog())
                        return Result.Cancel;
                    path = open.FileName;
                }
                else
                {
                    var get2 = new GetString();
                    get2.SetCommandPrompt("Pfad zu ifc-convert.exe oder python.exe");
                    if (get2.GetLiteralString() != GetResult.String)
                        return Result.Cancel;
                    path = get2.StringResult().Trim().Trim('"');
                }
                settings.ConverterPath = path;
            }
            settings.Save(plugIn.Settings);
            Report(settings.ConverterPath);
            return Result.Success;
        }

        private static void Report(string configured)
        {
            var backend = IfcImporter.FindConverter(configured, out var notFound);
            if (backend == null)
            {
                RhinoApp.WriteLine(notFound);
                return;
            }
            var version = ConverterProcess.Version(backend);
            RhinoApp.WriteLine($"IFC Converter {version ?? "(Version unbekannt)"}: {backend.Description}");
        }
    }
}
