using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace RhinoIfc.Core
{
    /// <summary>The outcome of one converter run.</summary>
    public sealed class ConverterRun
    {
        public int ExitCode { get; internal set; }
        public bool Cancelled { get; internal set; }
        public List<OutputLine> Lines { get; } = new List<OutputLine>();

        /// <summary>The ✓ line, null if the file failed.</summary>
        public OutputLine Success => Lines.LastOrDefault(l => l.Kind == OutputKind.Success);

        public IEnumerable<OutputLine> Warnings => Lines.Where(l => l.Kind == OutputKind.Warning);

        /// <summary>What went wrong, from the ✗ line and the lines after it (the hint), else the last lines.</summary>
        public string ErrorText()
        {
            int error = Lines.FindIndex(l => l.Kind == OutputKind.Error);
            var lines = error >= 0
                ? Lines.Skip(error).Where(l => l.Kind == OutputKind.Error || l.Kind == OutputKind.Text)
                : Lines.Where(l => l.Kind == OutputKind.Text).Reverse().Take(8).Reverse();
            return string.Join(Environment.NewLine, lines.Select(l => l.Text).Where(t => t.Length > 0));
        }
    }

    /// <summary>Runs the converter as a child process. A crash or hang of the geometry kernel then never takes
    /// Rhino with it, and the converter's own crash handling (retries in fresh processes) keeps working.</summary>
    public static class ConverterProcess
    {
        /// <summary>The environment for the converter: UTF-8 output (its ✓ ⚠ ✗ lines do not exist in the
        /// Windows code page), progress as lines (see interactive.ProgressLine in the converter).</summary>
        public static Dictionary<string, string> Environment(ImportOptions options)
        {
            var environment = new Dictionary<string, string>
            {
                ["PYTHONIOENCODING"] = "utf-8",
                ["PYTHONUTF8"] = "1",
                ["IFC_CONVERTER_PROGRESS"] = "lines",
            };
            if (!string.IsNullOrWhiteSpace(options?.Language))
                environment["IFC_CONVERTER_LANG"] = options.Language;
            return environment;
        }

        /// <param name="onLine">Each output line, on the calling thread.</param>
        /// <param name="cancelRequested">Asked every few milliseconds; true stops the converter and its children.</param>
        /// <param name="idle">Called while waiting, on the calling thread (Rhino: RhinoApp.Wait, so the window stays alive).</param>
        public static ConverterRun Run(
            ConverterBackend backend,
            IReadOnlyList<string> converterArguments,
            IDictionary<string, string> environment,
            Action<OutputLine> onLine,
            Func<bool> cancelRequested,
            Action idle)
        {
            var start = new ProcessStartInfo
            {
                FileName = backend.FileName,
                Arguments = backend.CommandLine(converterArguments),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            if (environment != null)
                foreach (var pair in environment)
                    start.Environment[pair.Key] = pair.Value;

            var run = new ConverterRun();
            var queue = new ConcurrentQueue<string>();
            using (var process = new Process { StartInfo = start })
            {
                process.OutputDataReceived += (s, e) => { if (e.Data != null) queue.Enqueue(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) queue.Enqueue(e.Data); };
                process.Start();
                process.StandardInput.Close();  // never waits for input
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                void Drain()
                {
                    while (queue.TryDequeue(out var text))
                    {
                        var line = OutputLine.Parse(text);
                        run.Lines.Add(line);
                        onLine?.Invoke(line);
                    }
                }

                while (!process.WaitForExit(40))
                {
                    Drain();
                    if (!run.Cancelled && cancelRequested != null && cancelRequested())
                    {
                        run.Cancelled = true;
                        KillTree(process);
                    }
                    idle?.Invoke();
                }
                process.WaitForExit();  // until the output is read to its end
                Drain();
                run.ExitCode = process.ExitCode;
            }
            return run;
        }

        /// <summary>Ends the converter with the worker processes it started.</summary>
        public static void KillTree(Process process)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    using (var kill = Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill.exe",
                        Arguments = $"/PID {process.Id} /T /F",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }))
                    {
                        kill?.WaitForExit(10000);
                    }
                }
                if (!process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException)  // already gone
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        /// <summary>Whether this Python (program and leading arguments, e.g. py.exe -3) is at least 3.10 and has the
        /// ifc_converter package.</summary>
        public static bool PythonHasConverter(string program, IReadOnlyList<string> arguments)
        {
            var all = (arguments ?? new string[0]).Concat(new[]
            {
                "-c", "import sys, ifc_converter.cli; sys.exit(0 if sys.version_info >= (3, 10) else 1)",
            });
            try
            {
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = program,
                    Arguments = CommandLineQuoting.Join(all),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }))
                {
                    if (process == null)
                        return false;
                    process.OutputDataReceived += (s, e) => { };
                    process.ErrorDataReceived += (s, e) => { };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(20000))
                    {
                        KillTree(process);
                        return false;
                    }
                    return process.ExitCode == 0;
                }
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception || exception is InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>The IFC Converter's version ("0.9.8"), null if it cannot be asked.</summary>
        public static string Version(ConverterBackend backend)
        {
            try
            {
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = backend.FileName,
                    Arguments = backend.CommandLine(new[] { "--version" }),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }))
                {
                    if (process == null)
                        return null;
                    var output = process.StandardOutput.ReadToEndAsync();
                    process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(30000))
                    {
                        KillTree(process);
                        return null;
                    }
                    // "ifc-convert 0.9.8"
                    var text = output.Result.Trim();
                    int space = text.LastIndexOf(' ');
                    return space >= 0 ? text.Substring(space + 1) : text;
                }
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception || exception is InvalidOperationException)
            {
                return null;
            }
        }
    }
}
