using Eto.Drawing;
using Eto.Forms;
using RhinoIfc.Core;

namespace RhinoIfc.Import
{
    /// <summary>The options of File › Open / Import and IfcImport.</summary>
    public sealed class ImportOptionsDialog : Dialog<bool>
    {
        private readonly RadioButtonList _geometry;
        private readonly CheckBox _openings;
        private readonly CheckBox _blocks;
        private readonly CheckBox _spaces;
        private readonly ImportOptions _options;

        public ImportOptionsDialog(ImportOptions options, string fileName)
        {
            _options = options.Clone();
            Title = "IFC importieren";
            Resizable = false;
            Padding = new Padding(12);

            _geometry = new RadioButtonList { Orientation = Orientation.Vertical, Spacing = new Size(0, 4) };
            _geometry.Items.Add("Extrusionen und Polyflächen (bearbeitbar; sehr detaillierte Bauteile bleiben Meshes)");
            _geometry.Items.Add("Nur Meshes (schneller, kleinere Datei, zum Ansehen und als Referenz)");
            _geometry.SelectedIndex = options.Geometry == GeometryMode.Meshes ? 1 : 0;

            _openings = new CheckBox
            {
                Text = "Öffnungen ausschneiden (Wände, Decken mit echten Löchern für Fenster und Türen)",
                Checked = options.CutOpenings,
            };
            _blocks = new CheckBox
            {
                Text = "Mehrfach platzierte Typen als Blöcke (kleiner und schneller; zum Bearbeiten Block auflösen)",
                Checked = options.Blocks,
            };
            _spaces = new CheckBox
            {
                Text = "Räume (IfcSpace) importieren, auf ausgeblendeten Layern",
                Checked = options.Spaces,
            };

            DefaultButton = new Button { Text = "Importieren" };
            DefaultButton.Click += (s, e) => Close(true);
            AbortButton = new Button { Text = "Abbrechen" };
            AbortButton.Click += (s, e) => Close(false);

            var layout = new DynamicLayout { Spacing = new Size(8, 8) };
            if (!string.IsNullOrEmpty(fileName))
                layout.AddRow(new Label { Text = fileName, Font = SystemFonts.Bold() });
            layout.AddRow(new Label { Text = "Geometrie" });
            layout.AddRow(_geometry);
            layout.AddRow(_openings);
            layout.AddRow(_blocks);
            layout.AddRow(_spaces);
            layout.AddRow(new Label
            {
                Text = "Die IFC-Datei wird direkt gelesen, ohne Umwandlung. Fortschritt in der Statusleiste, Esc bricht ab.",
                TextColor = Colors.Gray,
            });
            layout.AddSeparateRow(null, DefaultButton, AbortButton);
            Content = layout;
        }

        public ImportOptions Options
        {
            get
            {
                var options = _options.Clone();
                options.Geometry = _geometry.SelectedIndex == 1 ? GeometryMode.Meshes : GeometryMode.Polysurfaces;
                options.CutOpenings = _openings.Checked == true;
                options.Blocks = _blocks.Checked == true;
                options.Spaces = _spaces.Checked == true;
                return options;
            }
        }
    }
}
