using System.Linq;
using System.Text;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoIfc.Core;

namespace RhinoIfc.Commands
{
    /// <summary>IfcProperties: the IFC user text of an object on the command line, grouped by property set;
    /// Enter without an object shows the document's (source file, schema, georeferencing).</summary>
    public class IfcPropertiesCommand : Command
    {
        public override string EnglishName => "IfcProperties";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var get = new GetObject();
            get.SetCommandPrompt("Objekt wählen (Enter: IFC-Angaben des Dokuments)");
            get.AcceptNothing(true);
            get.SubObjectSelect = false;
            var answer = get.Get();
            if (answer == GetResult.Nothing)
            {
                var text = new StringBuilder("IFC-Dokumenttext:");
                for (int i = 0; i < doc.Strings.Count; i++)
                {
                    var key = doc.Strings.GetKey(i);
                    if (key != null && key.StartsWith("Ifc"))
                        text.AppendLine().Append($"  {key} = {doc.Strings.GetValue(i)}");
                }
                RhinoApp.WriteLine(text.ToString());
                return Result.Success;
            }
            if (answer != GetResult.Object)
                return get.CommandResult();

            var obj = get.Object(0).Object();
            var strings = obj.Attributes.GetUserStrings();
            if (strings.Count == 0)
            {
                RhinoApp.WriteLine("Das Objekt hat keinen Benutzertext.");
                return Result.Success;
            }
            var output = new StringBuilder();
            output.Append($"{obj.Attributes.GetUserString("IfcClass") ?? "Objekt"} „{obj.Attributes.Name}“");
            var keys = strings.AllKeys.ToList();
            foreach (var group in keys.GroupBy(UserTextMatch.Group).OrderBy(g => g.Key == "IFC" ? "" : g.Key))
            {
                output.AppendLine().Append($"[{group.Key}]");
                foreach (var key in group)
                {
                    var name = group.Key == "IFC" ? key : key.Substring(group.Key.Length + 1);
                    output.AppendLine().Append($"  {name} = {strings[key]}");
                }
            }
            RhinoApp.WriteLine(output.ToString());
            return Result.Success;
        }
    }
}
