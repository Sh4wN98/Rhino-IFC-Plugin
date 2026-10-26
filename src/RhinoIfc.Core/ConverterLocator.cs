using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RhinoIfc.Core
{
    /// <summary>Finds the IFC Converter: a path set in the plugin, the installed <c>ifc-convert.exe</c>
    /// (installer registry entry, default folders, PATH), else a Python ≥ 3.10 with the
    /// <c>ifc_converter</c> package.</summary>
    public sealed class ConverterLocator
    {
        /// <summary>The installer's AppId (packaging/installer.iss in the converter); Inno Setup adds "_is1".</summary>
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F0B8E52-3C1D-4B7A-9E2F-5A8C1D4E7B90}_is1";

        public const string ExecutableName = "ifc-convert.exe";
        public const string DownloadUrl = "https://github.com/Sh4wN98/IFC-Converter/releases/latest";

        private readonly Func<string, bool> _fileExists;
        private readonly Func<string, string> _environment;
        private readonly Func<IEnumerable<string>> _installLocations;
        private readonly Func<string, IReadOnlyList<string>, bool> _pythonHasConverter;

        /// <param name="fileExists">File.Exists, replaceable in tests.</param>
        /// <param name="environment">Environment.GetEnvironmentVariable.</param>
        /// <param name="installLocations">InstallLocation values of the converter's uninstall entries (HKCU, HKLM).</param>
        /// <param name="pythonHasConverter">Whether a Python (program, its leading arguments) can import ifc_converter;
        /// it is started for that, so it is only asked when no ifc-convert.exe was found.</param>
        public ConverterLocator(
            Func<string, bool> fileExists,
            Func<string, string> environment,
            Func<IEnumerable<string>> installLocations,
            Func<string, IReadOnlyList<string>, bool> pythonHasConverter)
        {
            _fileExists = fileExists;
            _environment = environment;
            _installLocations = installLocations ?? (() => Enumerable.Empty<string>());
            _pythonHasConverter = pythonHasConverter;
        }

        /// <summary>The places looked at by the last <see cref="Find"/>, for the message when nothing is found.</summary>
        public List<string> Searched { get; } = new List<string>();

        /// <param name="configuredPath">ifc-convert.exe or python.exe chosen with the IfcConverter command; may be empty.</param>
        public ConverterBackend Find(string configuredPath)
        {
            Searched.Clear();
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                var path = configuredPath.Trim().Trim('"');
                Searched.Add(path);
                if (_fileExists(path))
                {
                    if (IsPython(path))
                    {
                        if (_pythonHasConverter == null || _pythonHasConverter(path, new string[0]))
                            return ConverterBackend.Python(path, null, $"Python (eingestellt): {path}");
                    }
                    else
                        return ConverterBackend.Executable(path, $"IFC Converter (eingestellt): {path}");
                }
            }

            foreach (var folder in _installLocations().Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                var found = Executable(folder, "IFC Converter (installiert)");
                if (found != null)
                    return found;
            }

            foreach (var (variable, sub) in new[]
            {
                ("LOCALAPPDATA", @"Programs\IFC Converter"),
                ("ProgramFiles", "IFC Converter"),
                ("ProgramFiles(x86)", "IFC Converter"),
            })
            {
                var root = _environment(variable);
                if (string.IsNullOrEmpty(root))
                    continue;
                var found = Executable(Path.Combine(root, sub), "IFC Converter");
                if (found != null)
                    return found;
            }

            foreach (var folder in PathFolders())
            {
                var found = Executable(folder, "IFC Converter (PATH)");
                if (found != null)
                    return found;
            }

            if (_pythonHasConverter == null)
                return null;
            foreach (var (program, arguments) in PythonCandidates())
            {
                Searched.Add($"{program} {string.Join(" ", arguments)} -m ifc_converter".Replace("  ", " "));
                if (_pythonHasConverter(program, arguments))
                    return ConverterBackend.Python(program, arguments, $"Python-Paket ifc_converter ({program})");
            }
            return null;
        }

        private ConverterBackend Executable(string folder, string description)
        {
            string path;
            try
            {
                path = Path.Combine(folder.Trim().Trim('"'), ExecutableName);
            }
            catch (ArgumentException)  // invalid characters in a PATH entry
            {
                return null;
            }
            Searched.Add(path);
            return _fileExists(path) ? ConverterBackend.Executable(path, $"{description}: {path}") : null;
        }

        /// <summary>The absolute PATH entries. A relative one (".", "bin") would be looked up in Rhino's current
        /// folder, often the folder of an opened file, where anyone could place an ifc-convert.exe or python.exe.</summary>
        private IEnumerable<string> PathFolders() =>
            (_environment("PATH") ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim().Trim('"'))
                .Where(IsFullyQualified);

        /// <summary>Whether a path names its folder without depending on the current folder or drive:
        /// C:\… or \\server\… (on Linux and macOS, where the tests run, /…).</summary>
        public static bool IsFullyQualified(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
                return true;
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
                return true;
            return Path.DirectorySeparatorChar == '/' && path[0] == '/';
        }

        /// <summary>The py launcher asking for any Python 3, python.exe on PATH and the per-user installs
        /// of Python 3.10 to 3.14.</summary>
        private IEnumerable<(string, IReadOnlyList<string>)> PythonCandidates()
        {
            var windows = _environment("SystemRoot") ?? _environment("windir");
            if (!string.IsNullOrEmpty(windows) && _fileExists(Path.Combine(windows, "py.exe")))
                yield return (Path.Combine(windows, "py.exe"), new[] { "-3" });
            foreach (var folder in PathFolders())
            {
                string path;
                try
                {
                    path = Path.Combine(folder.Trim().Trim('"'), "python.exe");
                }
                catch (ArgumentException)
                {
                    continue;
                }
                // The Microsoft Store placeholder under WindowsApps only opens the Store.
                if (_fileExists(path) && path.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) < 0)
                    yield return (path, new string[0]);
            }
            var local = _environment("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(local))
            {
                for (int minor = 14; minor >= 10; minor--)
                {
                    var path = Path.Combine(local, "Programs", "Python", $"Python3{minor}", "python.exe");
                    if (_fileExists(path))
                        yield return (path, new string[0]);
                }
            }
        }

        public static bool IsPython(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path) ?? "";
            return name.StartsWith("python", StringComparison.OrdinalIgnoreCase)
                || name.Equals("py", StringComparison.OrdinalIgnoreCase);
        }
    }
}
