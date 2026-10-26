using System.Collections.Generic;
using System.Linq;

namespace RhinoIfc.Core
{
    /// <summary>A way to start the IFC Converter's command line: the installed <c>ifc-convert.exe</c> or a
    /// Python with the <c>ifc_converter</c> package.</summary>
    public sealed class ConverterBackend
    {
        public ConverterBackend(string fileName, IEnumerable<string> prefixArguments, string description)
        {
            FileName = fileName;
            PrefixArguments = prefixArguments?.ToArray() ?? new string[0];
            Description = description;
        }

        /// <summary>The program to start.</summary>
        public string FileName { get; }

        /// <summary>Arguments ahead of the converter's own, e.g. <c>-m ifc_converter.cli</c> for Python.</summary>
        public IReadOnlyList<string> PrefixArguments { get; }

        /// <summary>Where it was found, for messages.</summary>
        public string Description { get; }

        public static ConverterBackend Executable(string path, string description) =>
            new ConverterBackend(path, null, description);

        public static ConverterBackend Python(string python, IEnumerable<string> pythonArguments, string description) =>
            new ConverterBackend(python, (pythonArguments ?? new string[0]).Concat(new[] { "-m", "ifc_converter.cli" }), description);

        /// <summary>The converter's arguments for converting <paramref name="input"/> to the Rhino file
        /// <paramref name="output"/>.</summary>
        public static IReadOnlyList<string> ConvertArguments(string input, string output, ImportOptions options)
        {
            var arguments = new List<string>
            {
                input, "-f", "3dm", "-g", options.Geometry == GeometryMode.Meshes ? "mesh" : "brep",
            };
            if (options.Blocks)
                arguments.Add("--blocks");
            if (options.EnvelopeOnly)
                arguments.Add("--envelope");
            if (!string.IsNullOrWhiteSpace(options.Kernel))
                arguments.AddRange(new[] { "--kernel", options.Kernel.Trim() });
            if (!string.IsNullOrWhiteSpace(options.Language))
                arguments.AddRange(new[] { "--lang", options.Language.Trim() });
            // The output is a fresh temporary file; without this the converter would pick "name (2).3dm".
            arguments.Add("--overwrite");
            arguments.AddRange(new[] { "-o", output });
            return arguments;
        }

        /// <summary>The whole command line after the program name, quoted for Windows.</summary>
        public string CommandLine(IEnumerable<string> converterArguments) =>
            CommandLineQuoting.Join(PrefixArguments.Concat(converterArguments));

        public override string ToString() => Description;
    }
}
