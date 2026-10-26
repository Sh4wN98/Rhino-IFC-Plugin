using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoIfc.Core;

namespace RhinoIfc.Commands
{
    /// <summary>IfcSelect: selects objects by IFC class, storey or any property in their user text.
    /// Together with Rhino's Isolate/Hide this isolates a storey or a class.</summary>
    public class IfcSelectCommand : Command
    {
        public override string EnglishName => "IfcSelect";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var get = new GetOption();
            get.SetCommandPrompt("Auswählen nach");
            int byClass = get.AddOption("Class");
            int byStorey = get.AddOption("Storey");
            int byProperty = get.AddOption("Property");
            if (get.Get() != GetResult.Option)
                return Result.Cancel;
            int chosen = get.OptionIndex();

            var objects = Candidates(doc).ToList();
            string key;
            string[] patterns;
            if (chosen == byClass || chosen == byStorey)
            {
                key = chosen == byClass ? "IfcClass" : "IfcStorey";
                patterns = AskValues(objects, key, chosen == byClass ? "IFC-Klassen" : "Geschosse", mode);
            }
            else if (chosen == byProperty)
            {
                key = AskKey(objects, mode);
                if (key == null)
                    return Result.Cancel;
                var value = "*";
                var result = RhinoGet.GetString($"Wert von {key} (* und ? als Platzhalter)", true, ref value);
                if (result != Result.Success)
                    return result;
                patterns = new[] { string.IsNullOrWhiteSpace(value) ? "*" : value };
            }
            else
                return Result.Cancel;
            if (patterns == null || patterns.Length == 0)
                return Result.Cancel;

            int count = 0;
            foreach (var obj in objects)
            {
                var value = obj.Attributes.GetUserString(key);
                if (value != null && patterns.Any(p => UserTextMatch.Matches(value, p)))
                    count += obj.Select(true) != 0 ? 1 : 0;
            }
            doc.Views.Redraw();
            RhinoApp.WriteLine(count == 1 ? "1 Objekt ausgewählt." : $"{count} Objekte ausgewählt.");
            return Result.Success;
        }

        private static IEnumerable<RhinoObject> Candidates(RhinoDoc doc) =>
            doc.Objects.GetObjectList(new ObjectEnumeratorSettings
            {
                NormalObjects = true,
                LockedObjects = false,
                HiddenObjects = false,
                IncludeLights = false,
                IncludeGrips = false,
                ActiveObjects = true,
                ReferenceObjects = true,
            }).Where(o => o.IsSelectable(true, false, false, false));

        private static string[] AskValues(List<RhinoObject> objects, string key, string title, RunMode mode)
        {
            var values = objects.Select(o => o.Attributes.GetUserString(key)).Where(v => !string.IsNullOrEmpty(v))
                .Distinct().OrderBy(v => v, System.StringComparer.CurrentCultureIgnoreCase).ToList();
            if (values.Count == 0)
            {
                RhinoApp.WriteLine($"Kein sichtbares Objekt hat den Benutzertext {key}.");
                return null;
            }
            if (mode == RunMode.Interactive)
                return Rhino.UI.Dialogs.ShowMultiListBox(title, $"Objekte auswählen mit {key} =", values, null);
            RhinoApp.WriteLine($"{key}: " + string.Join(", ", values));
            var value = "";
            var result = RhinoGet.GetString($"{key} (* und ? als Platzhalter, mehrere mit ; trennen)", false, ref value);
            return result == Result.Success
                ? value.Split(';').Select(v => v.Trim()).Where(v => v.Length > 0).ToArray()
                : null;
        }

        private static string AskKey(List<RhinoObject> objects, RunMode mode)
        {
            var keys = new SortedSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var obj in objects)
            {
                var strings = obj.Attributes.GetUserStrings();
                foreach (string name in strings.AllKeys)
                    keys.Add(name);
            }
            if (keys.Count == 0)
            {
                RhinoApp.WriteLine("Kein sichtbares Objekt hat Benutzertext.");
                return null;
            }
            if (mode == RunMode.Interactive)
                return Rhino.UI.Dialogs.ShowListBox("IFC-Eigenschaft", "Eigenschaft (Benutzertext-Schlüssel):", keys.ToList()) as string;
            var key = "";
            return RhinoGet.GetString("Schlüssel, z. B. Pset_WallCommon.IsExternal", false, ref key) == Result.Success
                && key.Trim().Length > 0 ? key.Trim() : null;
        }
    }
}
