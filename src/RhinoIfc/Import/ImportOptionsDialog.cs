using Eto.Drawing;
using Eto.Forms;
using RhinoIfc.Core;

namespace RhinoIfc.Import
{
    /// <summary>The options of File › Open / Import and IfcImport.</summary>
    public sealed class ImportOptionsDialog : Dialog<bool>
    {
        private static readonly string[] Kernels = { "", "hybrid", "opencascade" };

        private readonly RadioButtonList _geometry;
        private readonly CheckBox _blocks;
        private readonly CheckBox _envelope;
        private readonly DropDown _kernel;
        private readonly ImportOptions _options;

        public ImportOptionsDialog(ImportOptions options, string fileName)
        {
            _options = options.Clone();
            Title = "IFC importieren";
            Resizable = false;
            Padding = new Padding(12);

            _geometry = new RadioButtonList { Orientation = Orientation.Vertical, Spacing = new Size(0, 4) };
            _geometry.Items.Add("Polyflächen (bearbeitbar, mit echten Öffnungen; sehr detaillierte Bauteile bleiben Meshes)");
            _geometry.Items.Add("Nur Meshes (schneller, kleinere Datei)");
            _geometry.SelectedIndex = options.Geometry == GeometryMode.Meshes ? 1 : 0;

            _blocks = new CheckBox
            {
                Text = "Mehrfach platzierte Typen als Blöcke (kleiner; zum Bearbeiten Block auflösen)",
                Checked = options.Blocks,
            };
            _envelope = new CheckBox
            {
                Text = "Nur Gebäudehülle (Innenwände, Möbel, Haustechnik weglassen)",
                Checked = options.EnvelopeOnly,
            };
            _kernel = new DropDown();
            _kernel.Items.Add("Standard (genau: OpenCASCADE, hybrid nur für große Netze)");
            _kernel.Items.Add("hybrid (schneller, kann Flächen verlieren)");
            _kernel.Items.Add("opencascade (alles mit OpenCASCADE)");
            _kernel.SelectedIndex = System.Math.Max(0, System.Array.IndexOf(Kernels, options.Kernel ?? ""));

            DefaultButton = new Button { Text = "Importieren" };
            DefaultButton.Click += (s, e) => Close(true);
            AbortButton = new Button { Text = "Abbrechen" };
            AbortButton.Click += (s, e) => Close(false);

            var layout = new DynamicLayout { Spacing = new Size(8, 8) };
            if (!string.IsNullOrEmpty(fileName))
                layout.AddRow(new Label { Text = fileName, Font = SystemFonts.Bold() });
            layout.AddRow(new Label { Text = "Geometrie" });
            layout.AddRow(_geometry);
            layout.AddRow(_blocks);
            layout.AddRow(_envelope);
            layout.AddRow(new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = { new Label { Text = "Geometriekern" }, _kernel },
            });
            layout.AddRow(new Label
            {
                Text = "Die Umwandlung macht der IFC Converter im Hintergrund. Esc bricht ab.",
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
                options.Blocks = _blocks.Checked == true;
                options.EnvelopeOnly = _envelope.Checked == true;
                options.Kernel = Kernels[System.Math.Max(0, _kernel.SelectedIndex)];
                return options;
            }
        }
    }
}
