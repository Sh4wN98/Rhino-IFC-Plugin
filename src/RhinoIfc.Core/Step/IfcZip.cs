using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace RhinoIfc.Core.Step
{
    /// <summary>.ifczip: the .ifc inside is extracted to a temporary file (streamed, never into memory) and read from there.</summary>
    public static class IfcZip
    {
        /// <summary>True if the file starts like a zip archive (PK\x03\x04), whatever its extension.</summary>
        public static bool IsZip(string path)
        {
            var head = new byte[4];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                if (stream.Read(head, 0, 4) < 4)
                    return false;
            return head[0] == 'P' && head[1] == 'K' && head[2] == 3 && head[3] == 4;
        }

        /// <summary>Extracts the first .ifc entry into <paramref name="folder"/> and returns its path. Stops when the
        /// extracted data grows beyond <paramref name="maxBytes"/> (a damaged or malicious archive).</summary>
        public static string Extract(string zipPath, string folder, long maxBytes, Action<double> progress = null, CancellationToken cancel = default)
        {
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase))
                    ?? archive.Entries.FirstOrDefault(e => e.Length > 0);
                if (entry == null)
                    throw new InvalidDataException("Die .ifczip-Datei enthält keine IFC-Datei.");
                Directory.CreateDirectory(folder);
                var target = Path.Combine(folder, "model.ifc");
                long total = Math.Max(1, entry.Length);
                long written = 0;
                var buffer = new byte[1 << 20];
                using (var input = entry.Open())
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        written += read;
                        if (written > maxBytes)
                            throw new InvalidDataException($"Die .ifczip-Datei entpackt sich auf mehr als {maxBytes / (1L << 30)} GB; Import abgebrochen.");
                        output.Write(buffer, 0, read);
                        progress?.Invoke(Math.Min(1.0, (double)written / total));
                    }
                }
                return target;
            }
        }
    }
}
