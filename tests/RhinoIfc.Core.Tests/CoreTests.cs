using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using RhinoIfc.Core;
using Xunit;

namespace RhinoIfc.Core.Tests
{
    public class CommandLineQuotingTests
    {
        [Theory]
        [InlineData("plain", "plain")]
        [InlineData("", "\"\"")]
        [InlineData(@"C:\Pläne\Haus 1.ifc", "\"C:\\Pläne\\Haus 1.ifc\"")]
        [InlineData("a\"b", "\"a\\\"b\"")]
        [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
        [InlineData(@"C:\no\space\", @"C:\no\space\")]
        public void QuotesLikeTheCRuntimeSplits(string argument, string expected)
        {
            Assert.Equal(expected, CommandLineQuoting.Quote(argument));
        }
    }

    public class ConverterBackendTests
    {
        [Fact]
        public void ArgumentsForPolysurfacesWithBlocksAndHull()
        {
            var options = new ImportOptions { Blocks = true, EnvelopeOnly = true, Kernel = "hybrid", Language = "de" };
            var arguments = ConverterBackend.ConvertArguments(@"C:\a b.ifc", @"C:\t\a b.3dm", options);
            Assert.Equal(new[]
            {
                @"C:\a b.ifc", "-f", "3dm", "-g", "brep", "--blocks", "--envelope", "--kernel", "hybrid",
                "--lang", "de", "--overwrite", "-o", @"C:\t\a b.3dm",
            }, arguments);
        }

        [Fact]
        public void ArgumentsForMeshesWithDefaults()
        {
            var arguments = ConverterBackend.ConvertArguments("a.ifc", "a.3dm", new ImportOptions { Geometry = GeometryMode.Meshes });
            Assert.Equal(new[] { "a.ifc", "-f", "3dm", "-g", "mesh", "--overwrite", "-o", "a.3dm" }, arguments);
        }

        [Fact]
        public void PythonRunsTheCliModule()
        {
            var backend = ConverterBackend.Python(@"C:\Windows\py.exe", new[] { "-3" }, "py");
            Assert.Equal("-3 -m ifc_converter.cli --version", backend.CommandLine(new[] { "--version" }));
        }
    }

    public class ConverterLocatorTests
    {
        private static ConverterLocator Locator(
            ISet<string> files, Dictionary<string, string> environment, IEnumerable<string> installed = null,
            Func<string, IReadOnlyList<string>, bool> python = null)
        {
            return new ConverterLocator(
                path => files.Contains(path),
                name => environment.TryGetValue(name, out var value) ? value : null,
                () => installed ?? Enumerable.Empty<string>(),
                python ?? ((p, a) => false));
        }

        private static string P(params string[] parts) => Path.Combine(parts);

        [Fact]
        public void ConfiguredPathComesFirst()
        {
            var configured = P("x", "ifc-convert.exe");
            var installed = P("local", "Programs", "IFC Converter", "ifc-convert.exe");
            var locator = Locator(new HashSet<string> { configured, installed }, new Dictionary<string, string> { ["LOCALAPPDATA"] = "local" });
            Assert.Equal(configured, locator.Find(configured).FileName);
        }

        [Fact]
        public void MissingConfiguredPathFallsBackToTheInstallation()
        {
            var installed = P("local", @"Programs\IFC Converter", "ifc-convert.exe");
            var locator = Locator(new HashSet<string> { installed }, new Dictionary<string, string> { ["LOCALAPPDATA"] = "local" });
            var backend = locator.Find("gone.exe");
            Assert.Equal(installed, backend.FileName);
            Assert.Empty(backend.PrefixArguments);
            Assert.Contains("gone.exe", locator.Searched);
        }

        [Fact]
        public void RegistryInstallLocationBeforeDefaultFolders()
        {
            var registry = P("D", "Tools", "IFC Converter");
            var exe = Path.Combine(registry, "ifc-convert.exe");
            var other = P("local", @"Programs\IFC Converter", "ifc-convert.exe");
            var locator = Locator(new HashSet<string> { exe, other }, new Dictionary<string, string> { ["LOCALAPPDATA"] = "local" }, new[] { registry });
            Assert.Equal(exe, locator.Find("").FileName);
        }

        [Fact]
        public void PathEntries()
        {
            var exe = Path.Combine("bin", "ifc-convert.exe");
            var locator = Locator(new HashSet<string> { exe }, new Dictionary<string, string> { ["PATH"] = "nothing;\"bin\"" });
            Assert.Equal(exe, locator.Find(null).FileName);
        }

        [Fact]
        public void PythonWithThePackageAsLastResort()
        {
            var python = P("local", "Programs", "Python", "Python310", "python.exe");
            var store = P("WindowsApps", "python.exe");
            var asked = new List<string>();
            var locator = Locator(
                new HashSet<string> { python, store },
                new Dictionary<string, string> { ["LOCALAPPDATA"] = "local", ["PATH"] = "WindowsApps" },
                python: (program, arguments) => { asked.Add(program); return program == python; });
            var backend = locator.Find("");
            Assert.Equal(python, backend.FileName);
            Assert.Equal(new[] { "-m", "ifc_converter.cli" }, backend.PrefixArguments);
            Assert.DoesNotContain(store, asked);
        }

        [Fact]
        public void ConfiguredPythonIsUsedAsPython()
        {
            var python = P("py", "python.exe");
            var locator = Locator(new HashSet<string> { python }, new Dictionary<string, string>(), python: (p, a) => true);
            Assert.Equal(new[] { "-m", "ifc_converter.cli" }, locator.Find(python).PrefixArguments);
        }

        [Fact]
        public void NothingFound()
        {
            var locator = Locator(new HashSet<string>(), new Dictionary<string, string> { ["LOCALAPPDATA"] = "local" });
            Assert.Null(locator.Find(""));
            Assert.NotEmpty(locator.Searched);
        }
    }

    public class OutputLineTests
    {
        [Fact]
        public void ProgressLines()
        {
            Assert.Equal(42, OutputLine.Parse("@percent 42").Percent);
            Assert.Equal(100, OutputLine.Parse("@percent 140").Percent);
            Assert.Equal("IFC wird gelesen …", OutputLine.Parse("@status IFC wird gelesen …\r").Text);
            var phase = OutputLine.Parse("@phase write Datei wird geschrieben …");
            Assert.Equal(OutputKind.Phase, phase.Kind);
            Assert.Equal("write", phase.Phase);
            Assert.Equal("Datei wird geschrieben …", phase.Text);
            Assert.Equal("geometry", OutputLine.Parse("@phase geometry").Phase);
        }

        [Fact]
        public void ResultLines()
        {
            var success = OutputLine.Parse(@"✓ C:\a.ifc → C:\t\a.3dm (1234 elements, 5 without geometry)");
            Assert.Equal(OutputKind.Success, success.Kind);
            Assert.Equal(1234, success.Elements);
            Assert.Equal(OutputKind.Warning, OutputLine.Parse("⚠ a.ifc: 5 Bauteile ohne Geometrie").Kind);
            Assert.Equal(OutputKind.Error, OutputLine.Parse(@"✗ C:\a.ifc: kaputt").Kind);
            Assert.Equal(OutputKind.Cancelled, OutputLine.Parse("■ a.ifc: abgebrochen").Kind);
            Assert.Equal(OutputKind.Text, OutputLine.Parse("usage: ifc-convert").Kind);
        }

        [Fact]
        public void ErrorTextIsTheErrorAndItsHint()
        {
            var run = new ConverterRun();
            foreach (var line in new[] { "@percent 3", "✗ a.ifc: Datei beschädigt", "  Details in der Logdatei" })
                run.Lines.Add(OutputLine.Parse(line));
            Assert.Equal("a.ifc: Datei beschädigt" + Environment.NewLine + "Details in der Logdatei", run.ErrorText());
        }
    }

    public class UserTextMatchTests
    {
        [Theory]
        [InlineData("IfcWall", "ifcwall", true)]
        [InlineData("IfcWallStandardCase", "IfcWall", false)]
        [InlineData("IfcWallStandardCase", "IfcWall*", true)]
        [InlineData("EG", "?G", true)]
        [InlineData("1.OG (a)", "1.OG (*)", true)]
        [InlineData("", "*", true)]
        public void Matches(string value, string pattern, bool expected)
        {
            Assert.Equal(expected, UserTextMatch.Matches(value, pattern));
        }

        [Theory]
        [InlineData("IfcWallStandardCase", "IfcWall", true)]
        [InlineData("IfcSlabElementedCase", "ifcslab", true)]
        [InlineData("IfcWall", "IfcWall", true)]
        [InlineData("IfcStairFlight", "IfcStair", false)]
        [InlineData("IfcWallStandardCase", "IfcWal?", false)]
        public void ClassesTakeTheirCaseSubtypes(string ifcClass, string pattern, bool expected)
        {
            Assert.Equal(expected, UserTextMatch.MatchesClass(ifcClass, pattern));
        }

        [Fact]
        public void Groups()
        {
            Assert.Equal("Pset_WallCommon", UserTextMatch.Group("Pset_WallCommon.IsExternal"));
            Assert.Equal("Pset_X", UserTextMatch.Group("Pset_X.Range.UpperBoundValue"));
            Assert.Equal("IFC", UserTextMatch.Group("IfcGlobalId"));
        }
    }

    /// <summary>ConverterProcess with a stand-in converter in Python (python3 must be on PATH).</summary>
    public class ConverterProcessTests
    {
        private static string Python()
        {
            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                foreach (var name in new[] { "python3", "python3.exe", "python.exe" })
                {
                    var path = Path.Combine(folder, name);
                    if (File.Exists(path))
                        return path;
                }
            }
            return null;
        }

        private static ConverterBackend FakeConverter(string script)
        {
            var file = Path.Combine(Path.GetTempPath(), $"fake-converter-{Guid.NewGuid():N}.py");
            File.WriteAllText(file, script);
            return new ConverterBackend(Python(), new[] { file }, "fake");
        }

        [Fact]
        public void ReadsProgressAndResultInUtf8()
        {
            if (Python() == null)
                return;
            var backend = FakeConverter(@"
import os, sys
assert os.environ['IFC_CONVERTER_PROGRESS'] == 'lines'
print('@status IFC wird gelesen …', flush=True)
print('@percent 50', flush=True)
print('⚠ a.ifc: 2 Bauteile übersprungen', file=sys.stderr, flush=True)
open(sys.argv[sys.argv.index('-o') + 1], 'w').write('x')
print('✓ ' + sys.argv[1] + ' → out (7 elements)')
");
            var output = Path.Combine(Path.GetTempPath(), $"out-{Guid.NewGuid():N}.3dm");
            var seen = new List<OutputKind>();
            int idles = 0;
            var run = ConverterProcess.Run(backend, ConverterBackend.ConvertArguments("Haus 1.ifc", output, new ImportOptions()),
                ConverterProcess.Environment(new ImportOptions { Language = "de" }), l => seen.Add(l.Kind), () => false, () => idles++);
            Assert.Equal(0, run.ExitCode);
            Assert.False(run.Cancelled);
            Assert.Equal(7, run.Success.Elements);
            Assert.Contains("Haus 1.ifc", run.Success.Text);
            Assert.Contains(OutputKind.Percent, seen);
            Assert.Single(run.Warnings);
            Assert.True(File.Exists(output));
            File.Delete(output);
        }

        [Fact]
        public void CancelStopsTheConverter()
        {
            if (Python() == null)
                return;
            var backend = FakeConverter("import time\nprint('@status lese', flush=True)\ntime.sleep(60)\n");
            var started = DateTime.Now;
            var run = ConverterProcess.Run(backend, new[] { "a.ifc" }, null, null, () => (DateTime.Now - started).TotalSeconds > 1, null);
            Assert.True(run.Cancelled);
            Assert.True((DateTime.Now - started).TotalSeconds < 30);
            Assert.Null(run.Success);
        }
    }
}
