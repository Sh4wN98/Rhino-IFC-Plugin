using System;
using System.IO;
using System.Linq;
using RhinoIfc.Core;
using RhinoIfc.Core.Geometry;
using RhinoIfc.Core.Model;
using RhinoIfc.Core.Step;
using Xunit;

namespace RhinoIfc.Core.Tests
{
    /// <summary>A hand-written IFC4 file in millimetres: storey at +3 m, a wall with an opening, a column from a type map.</summary>
    public sealed class SmallModelTests : IDisposable
    {
        private const string Ifc = @"ISO-10303-21;
HEADER;
FILE_DESCRIPTION(('ViewDefinition [CoordinationView]'),'2;1');
FILE_NAME('test.ifc','2026-01-01T00:00:00',(''),(''),'','Test','');
FILE_SCHEMA(('IFC4'));
ENDSEC;
DATA;
#1=IFCPROJECT('0001',$,'Testprojekt',$,$,$,$,(#20),#10);
#10=IFCUNITASSIGNMENT((#11,#12));
#11=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);
#12=IFCSIUNIT(*,.PLANEANGLEUNIT.,$,.RADIAN.);
#20=IFCGEOMETRICREPRESENTATIONCONTEXT($,'Model',3,1.E-05,#21,$);
#21=IFCAXIS2PLACEMENT3D(#22,$,$);
#22=IFCCARTESIANPOINT((0.,0.,0.));
#23=IFCDIRECTION((0.,0.,1.));
#24=IFCDIRECTION((1.,0.,0.));
#30=IFCSITE('0002',$,'Gelaende',$,$,#31,$,$,.ELEMENT.,$,$,$,$,$);
#31=IFCLOCALPLACEMENT($,#21);
#40=IFCBUILDING('0003',$,'Haus',$,$,#41,$,$,.ELEMENT.,$,$,$);
#41=IFCLOCALPLACEMENT(#31,#21);
#50=IFCBUILDINGSTOREY('0004',$,'EG',$,$,#51,$,$,.ELEMENT.,3000.);
#51=IFCLOCALPLACEMENT(#41,#52);
#52=IFCAXIS2PLACEMENT3D(#53,$,$);
#53=IFCCARTESIANPOINT((0.,0.,3000.));
#60=IFCRELAGGREGATES('0005',$,$,$,#1,(#30));
#61=IFCRELAGGREGATES('0006',$,$,$,#30,(#40));
#62=IFCRELAGGREGATES('0007',$,$,$,#40,(#50));
#63=IFCRELCONTAINEDINSPATIALSTRUCTURE('0008',$,$,$,(#100,#200),#50);
/* wall 5000 x 200 x 3000 at x = 1000 */
#100=IFCWALL('0010',$,'Wand 1',$,$,#101,#110,$,.STANDARD.);
#101=IFCLOCALPLACEMENT(#51,#102);
#102=IFCAXIS2PLACEMENT3D(#103,#23,#24);
#103=IFCCARTESIANPOINT((1000.,0.,0.));
#110=IFCPRODUCTDEFINITIONSHAPE($,$,(#111));
#111=IFCSHAPEREPRESENTATION(#20,'Body','SweptSolid',(#112));
#112=IFCEXTRUDEDAREASOLID(#113,#116,#23,3000.);
#113=IFCRECTANGLEPROFILEDEF(.AREA.,$,#114,5000.,200.);
#114=IFCAXIS2PLACEMENT2D(#115,$);
#115=IFCCARTESIANPOINT((2500.,100.));
#116=IFCAXIS2PLACEMENT3D(#22,$,$);
/* opening 1000 x 2000 through the wall */
#120=IFCOPENINGELEMENT('0011',$,'Oeffnung',$,$,#121,#130,$,.OPENING.);
#121=IFCLOCALPLACEMENT(#101,#122);
#122=IFCAXIS2PLACEMENT3D(#123,$,$);
#123=IFCCARTESIANPOINT((2000.,-50.,0.));
#130=IFCPRODUCTDEFINITIONSHAPE($,$,(#131));
#131=IFCSHAPEREPRESENTATION(#20,'Body','SweptSolid',(#132));
#132=IFCEXTRUDEDAREASOLID(#133,#116,#23,2000.);
#133=IFCRECTANGLEPROFILEDEF(.AREA.,$,#134,1000.,300.);
#134=IFCAXIS2PLACEMENT2D(#135,$);
#135=IFCCARTESIANPOINT((500.,150.));
#140=IFCRELVOIDSELEMENT('0012',$,$,$,#100,#120);
#150=IFCPROPERTYSINGLEVALUE('IsExternal',$,IFCBOOLEAN(.T.),$);
#151=IFCPROPERTYSINGLEVALUE('Bemerkung',$,IFCLABEL('Gr\X2\00FC\X0\n'),$);
#152=IFCPROPERTYSET('0013',$,'Pset_WallCommon',$,(#150,#151));
#153=IFCRELDEFINESBYPROPERTIES('0014',$,$,$,(#100),#152);
/* round column from a type map, lifted 500 by the mapping */
#200=IFCCOLUMN('0020',$,'Stuetze',$,$,#201,#210,$,.COLUMN.);
#201=IFCLOCALPLACEMENT(#51,#202);
#202=IFCAXIS2PLACEMENT3D(#203,$,$);
#203=IFCCARTESIANPOINT((10000.,0.,0.));
#210=IFCPRODUCTDEFINITIONSHAPE($,$,(#211));
#211=IFCSHAPEREPRESENTATION(#20,'Body','MappedRepresentation',(#212));
#212=IFCMAPPEDITEM(#220,#213);
#213=IFCCARTESIANTRANSFORMATIONOPERATOR3D($,$,#214,$,$);
#214=IFCCARTESIANPOINT((0.,0.,500.));
#220=IFCREPRESENTATIONMAP(#116,#221);
#221=IFCSHAPEREPRESENTATION(#20,'Body','SweptSolid',(#222));
#222=IFCEXTRUDEDAREASOLID(#223,#116,#23,2500.);
#223=IFCCIRCLEPROFILEDEF(.AREA.,$,$,150.);
#230=IFCCOLUMNTYPE('0021',$,'Rundstuetze D300',$,$,$,(#220),$,$,.COLUMN.);
#231=IFCRELDEFINESBYTYPE('0022',$,$,$,(#200),#230);
ENDSEC;
END-ISO-10303-21;
";

        private readonly string _path;
        private readonly StepFile _file;
        private readonly IfcModel _model;

        public SmallModelTests()
        {
            _path = Path.Combine(Path.GetTempPath(), "rhinoifc-test-" + Guid.NewGuid().ToString("N") + ".ifc");
            File.WriteAllText(_path, Ifc);
            _file = StepFile.Open(_path);
            _model = IfcModel.Load(_file, new ImportOptions());
        }

        public void Dispose()
        {
            _file.Dispose();
            File.Delete(_path);
        }

        private static void Near(double expected, double actual, double tolerance) =>
            Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected}, got {actual}");

        [Fact]
        public void ReadsUnitsAndStructure()
        {
            Near(0.001, _model.LengthToMetres, 1e-12);
            Assert.Equal(new[] { 100, 200 }, _model.Products.OrderBy(p => p).ToArray());
            Assert.Equal("EG", _model.StoreyOf(100).Name);
            Assert.Equal("EG", _model.StoreyOf(200).Name);
        }

        [Fact]
        public void WallWithOpeningHasRightSizeAndVolume()
        {
            var geometry = new ShapeBuilder(_model).Build(100, true);
            var mesh = Tessellator.Mesh(geometry);
            var box = mesh.BoundingBox();
            double m = _model.LengthToMetres;
            Near(1.0, box.Min.X * m, 1e-6);
            Near(0.0, box.Min.Y * m, 1e-6);
            Near(3.0, box.Min.Z * m, 1e-6);
            Near(6.0, box.Max.X * m, 1e-6);
            Near(0.2, box.Max.Y * m, 1e-6);
            Near(6.0, box.Max.Z * m, 1e-6);
            Assert.Single(geometry.Openings);
            // The opening is cut in Rhino; here the solid without it is 3 m³, the opening 0.4 m³.
            Near(3.0, Math.Abs(mesh.Volume()) * m * m * m, 1e-6);
        }

        [Fact]
        public void MappedColumnIsPlacedAndNamedAfterItsType()
        {
            var geometry = new ShapeBuilder(_model).Build(200, false);
            var mesh = Tessellator.Mesh(geometry);
            var box = mesh.BoundingBox();
            double m = _model.LengthToMetres;
            Near(9.85, box.Min.X * m, 1e-3);
            Near(10.15, box.Max.X * m, 1e-3);
            Near(3.5, box.Min.Z * m, 1e-6);
            Near(6.0, box.Max.Z * m, 1e-6);
            Near(Math.PI * 0.15 * 0.15 * 2.5, Math.Abs(mesh.Volume()) * m * m * m, 0.01 * Math.PI * 0.15 * 0.15 * 2.5);
            Assert.Equal(220, geometry.Items.Single().MapId);
            Assert.Equal("Rundstuetze D300", _model.MapTypeName(220));
        }

        [Fact]
        public void UserTextHasPropertiesAndDecodedStrings()
        {
            var text = _model.UserText(100).ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal("0010", text["IfcGlobalId"]);
            Assert.Equal("Wand 1", text["IfcName"]);
            Assert.Contains(text, p => p.Key.EndsWith("IsExternal") && p.Key.Contains("Pset_WallCommon"));
            Assert.Contains(text, p => p.Key.EndsWith("Bemerkung") && p.Value == "Grün");
        }

        [Theory]
        [InlineData(@"Gr\X2\00FC\X0\n", "Grün")]
        [InlineData(@"\X2\00C400D6\X0\", "ÄÖ")]
        [InlineData(@"\S\d", "ä")]
        [InlineData(@"\X\E4", "ä")]
        [InlineData("plain", "plain")]
        public void UnescapesStepStrings(string raw, string expected)
        {
            Assert.Equal(expected, StepString.Unescape(raw));
        }
    }
}
