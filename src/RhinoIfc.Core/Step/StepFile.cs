using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using RhinoIfc.Core.Schema;

namespace RhinoIfc.Core.Step
{
    /// <summary>An IFC-SPF (STEP physical file) opened for reading, made for files of several GB: one pass over the
    /// file records where each entity instance starts and its type, nothing else; an entity's attributes are parsed only
    /// when asked for, straight from the memory-mapped file. About 10 bytes per entity stay in memory.
    /// Reading entities is thread-safe.</summary>
    public sealed class StepFile : IDisposable
    {
        private const int ChunkBits = 16;
        private const int ChunkSize = 1 << ChunkBits;
        private const int ChunkMask = ChunkSize - 1;

        // id -> file offset of the '(' after the type name (+1 so 0 means "no such id"), and the type code.
        private long[][] _offsetChunks = new long[16][];
        private ushort[][] _typeChunks = new ushort[16][];

        private readonly List<string> _typeNames = new List<string> { "" };
        private readonly List<EntityDef> _typeDefs = new List<EntityDef> { null };
        private readonly Dictionary<string, ushort> _typeCodes = new Dictionary<string, ushort>(StringComparer.Ordinal);

        private MemoryMappedFile _map;
        private MemoryMappedViewAccessor _view;
        private unsafe byte* _base;
        private long _length;

        private StepFile()
        {
        }

        public string Path { get; private set; }

        /// <summary>FILE_SCHEMA as written (IFC2X3, IFC4, IFC4X3_ADD2 …).</summary>
        public string SchemaIdentifier { get; private set; } = "";

        public IfcSchema Schema { get; private set; }

        /// <summary>FILE_NAME's originating system / preprocessor, for messages.</summary>
        public string Originator { get; private set; } = "";

        public int EntityCount { get; private set; }

        public int MaxId { get; private set; }

        public long Length => _length;

        /// <summary>Indexes the file. <paramref name="progress"/> gets the fraction read (0..1); cancelling throws
        /// <see cref="OperationCanceledException"/>.</summary>
        public static StepFile Open(string path, Action<double> progress = null, CancellationToken cancel = default)
        {
            var file = new StepFile { Path = path };
            try
            {
                file.ReadHeader();
                file.Scan(progress, cancel);
                file.MapFile();
                return file;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        // ---------------------------------------------------------------- header

        private void ReadHeader()
        {
            var buffer = new byte[256 * 1024];
            int read;
            using (var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                read = stream.Read(buffer, 0, buffer.Length);
            var text = Encoding.ASCII.GetString(buffer, 0, read);
            if (text.IndexOf("ISO-10303-21", StringComparison.Ordinal) < 0)
                throw new InvalidDataException("Keine IFC-Datei im STEP-Format (ISO-10303-21 fehlt).");
            int schema = text.IndexOf("FILE_SCHEMA", StringComparison.OrdinalIgnoreCase);
            if (schema >= 0)
            {
                int q1 = text.IndexOf('\'', schema);
                int q2 = q1 >= 0 ? text.IndexOf('\'', q1 + 1) : -1;
                if (q2 > q1)
                    SchemaIdentifier = text.Substring(q1 + 1, q2 - q1 - 1).Trim();
            }
            int fileName = text.IndexOf("FILE_NAME", StringComparison.OrdinalIgnoreCase);
            if (fileName >= 0)
            {
                int end = text.IndexOf(");", fileName, StringComparison.Ordinal);
                if (end > fileName)
                {
                    var strings = new List<string>();
                    int i = fileName;
                    while (i < end)
                    {
                        int a = text.IndexOf('\'', i);
                        if (a < 0 || a > end)
                            break;
                        int b = text.IndexOf('\'', a + 1);
                        if (b < 0 || b > end)
                            break;
                        strings.Add(text.Substring(a + 1, b - a - 1));
                        i = b + 1;
                    }
                    // name, time_stamp, author…, organization…, preprocessor_version, originating_system, authorization
                    if (strings.Count >= 3)
                        Originator = strings[strings.Count - 2].Length > 0 ? strings[strings.Count - 2] : strings[strings.Count - 3];
                }
            }
            Schema = IfcSchema.ForIdentifier(SchemaIdentifier);
        }

        // ---------------------------------------------------------------- index

        private enum ScanState
        {
            Between,      // between records: waiting for '#'
            Id,           // reading the id digits
            AfterId,      // waiting for '='
            BeforeType,   // waiting for the type name
            Type,         // reading the type name
            Args,         // inside the attribute list
            String,       // inside '…' within the attribute list
            StringQuote,  // just saw a ' inside a string: '' or end
        }

        private void Scan(Action<double> progress, CancellationToken cancel)
        {
            const int BufferSize = 4 * 1024 * 1024;
            var buffer = new byte[BufferSize];
            var typeName = new byte[128];
            int typeLength = 0;
            var state = ScanState.Between;
            bool inData = false;
            int id = 0;
            int depth = 0;
            bool commentPending = false; // saw '/' outside a string
            bool inComment = false;
            bool starPending = false;   // saw '*' inside a comment
            var dataMarker = Encoding.ASCII.GetBytes("DATA;");
            int dataMatch = 0;
            long position = 0;

            using (var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            {
                long total = Math.Max(1, stream.Length);
                int read;
                while ((read = stream.Read(buffer, 0, BufferSize)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    progress?.Invoke((double)position / total);
                    for (int i = 0; i < read; i++, position++)
                    {
                        byte c = buffer[i];
                        if (!inData)
                        {
                            // Skip the header: records start after "DATA;" (STEP keywords are upper case).
                            dataMatch = c == dataMarker[dataMatch] ? dataMatch + 1 : (c == dataMarker[0] ? 1 : 0);
                            if (dataMatch == dataMarker.Length)
                            {
                                inData = true;
                                dataMatch = 0;
                            }
                            continue;
                        }

                        if (inComment)
                        {
                            if (starPending && c == '/')
                                inComment = false;
                            starPending = c == '*';
                            continue;
                        }
                        if (state != ScanState.String && state != ScanState.StringQuote)
                        {
                            if (commentPending)
                            {
                                commentPending = false;
                                if (c == '*')
                                {
                                    inComment = true;
                                    starPending = false;
                                    continue;
                                }
                            }
                            else if (c == '/')
                            {
                                commentPending = true;
                                continue;
                            }
                        }

                        switch (state)
                        {
                            case ScanState.Between:
                                if (c == '#')
                                {
                                    id = 0;
                                    state = ScanState.Id;
                                }
                                break;
                            case ScanState.Id:
                                if (c >= '0' && c <= '9')
                                    id = id * 10 + (c - '0');
                                else if (c == '=')
                                    state = ScanState.BeforeType;
                                else if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                                    state = ScanState.AfterId;
                                else
                                    state = ScanState.Between;
                                break;
                            case ScanState.AfterId:
                                if (c == '=')
                                    state = ScanState.BeforeType;
                                else if (!(c == ' ' || c == '\t' || c == '\r' || c == '\n'))
                                    state = ScanState.Between;
                                break;
                            case ScanState.BeforeType:
                                if (c == '(')
                                {
                                    // Complex instance #5=(IFCA(..)IFCB(..)); its parts are parsed on demand.
                                    Add(id, "(", position);
                                    depth = 1;
                                    state = ScanState.Args;
                                }
                                else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                                {
                                    typeLength = 0;
                                    typeName[typeLength++] = Upper(c);
                                    state = ScanState.Type;
                                }
                                break;
                            case ScanState.Type:
                                if (c == '(')
                                {
                                    Add(id, Encoding.ASCII.GetString(typeName, 0, typeLength), position);
                                    depth = 1;
                                    state = ScanState.Args;
                                }
                                else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')
                                {
                                    if (typeLength < typeName.Length)
                                        typeName[typeLength++] = Upper(c);
                                }
                                else if (!(c == ' ' || c == '\t' || c == '\r' || c == '\n'))
                                    state = ScanState.Between;
                                break;
                            case ScanState.Args:
                                if (c == '\'')
                                    state = ScanState.String;
                                else if (c == '(')
                                    depth++;
                                else if (c == ')')
                                {
                                    if (--depth == 0)
                                        state = ScanState.Between;
                                }
                                break;
                            case ScanState.String:
                                if (c == '\'')
                                    state = ScanState.StringQuote;
                                break;
                            case ScanState.StringQuote:
                                if (c == '\'')
                                    state = ScanState.String; // '' inside a string
                                else
                                {
                                    state = ScanState.Args;
                                    if (c == '(')
                                        depth++;
                                    else if (c == ')' && --depth == 0)
                                        state = ScanState.Between;
                                }
                                break;
                        }
                    }
                }
            }
            progress?.Invoke(1.0);
        }

        private static byte Upper(byte c) => c >= 'a' && c <= 'z' ? (byte)(c - 32) : c;

        private void Add(int id, string type, long offset)
        {
            if (id <= 0)
                return;
            if (!_typeCodes.TryGetValue(type, out var code))
            {
                code = (ushort)_typeNames.Count;
                _typeCodes[type] = code;
                _typeNames.Add(type);
                _typeDefs.Add(type == "(" ? null : Schema.Find(type));
            }
            int chunk = id >> ChunkBits;
            if (chunk >= _offsetChunks.Length)
            {
                int size = _offsetChunks.Length;
                while (size <= chunk)
                    size *= 2;
                Array.Resize(ref _offsetChunks, size);
                Array.Resize(ref _typeChunks, size);
            }
            if (_offsetChunks[chunk] == null)
            {
                _offsetChunks[chunk] = new long[ChunkSize];
                _typeChunks[chunk] = new ushort[ChunkSize];
            }
            if (_offsetChunks[chunk][id & ChunkMask] == 0)
                EntityCount++;
            _offsetChunks[chunk][id & ChunkMask] = offset + 1;
            _typeChunks[chunk][id & ChunkMask] = code;
            if (id > MaxId)
                MaxId = id;
        }

        private unsafe void MapFile()
        {
            _length = new FileInfo(Path).Length;
            if (_length == 0)
                throw new InvalidDataException("Die Datei ist leer.");
            _map = MemoryMappedFile.CreateFromFile(
                new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read), null, 0, MemoryMappedFileAccess.Read,
                HandleInheritability.None, false);
            _view = _map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* pointer = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            _base = pointer + _view.PointerOffset;
        }

        // ---------------------------------------------------------------- access

        /// <summary>The type code of an id (0 if the id does not exist).</summary>
        public ushort TypeCode(int id)
        {
            if (id <= 0)
                return 0;
            int chunk = id >> ChunkBits;
            if (chunk >= _typeChunks.Length || _typeChunks[chunk] == null)
                return 0;
            return _typeChunks[chunk][id & ChunkMask];
        }

        public bool Exists(int id) => TypeCode(id) != 0;

        /// <summary>The upper-case STEP type name of an id ("IFCWALL"), "" if it does not exist.</summary>
        public string TypeName(int id) => _typeNames[TypeCode(id)];

        /// <summary>The schema definition of an id's type (null for unknown types and complex instances).</summary>
        public EntityDef TypeDef(int id) => _typeDefs[TypeCode(id)];

        public EntityDef TypeDefOfCode(ushort code) => _typeDefs[code];

        public string TypeNameOfCode(ushort code) => _typeNames[code];

        public int TypeCount => _typeNames.Count;

        /// <summary>Type codes whose entity is <paramref name="upperName"/> or a subtype of it.</summary>
        public HashSet<ushort> CodesOf(params string[] upperNames)
        {
            var codes = new HashSet<ushort>();
            for (int code = 1; code < _typeDefs.Count; code++)
            {
                var def = _typeDefs[code];
                if (def == null)
                    continue;
                foreach (var name in upperNames)
                    if (def.IsA(name))
                    {
                        codes.Add((ushort)code);
                        break;
                    }
            }
            return codes;
        }

        /// <summary>All ids whose type code is in <paramref name="codes"/>, ascending.</summary>
        public List<int> IdsOf(HashSet<ushort> codes)
        {
            var ids = new List<int>();
            if (codes.Count == 0)
                return ids;
            for (int chunk = 0; chunk < _typeChunks.Length; chunk++)
            {
                var types = _typeChunks[chunk];
                if (types == null)
                    continue;
                for (int i = 0; i < ChunkSize; i++)
                    if (types[i] != 0 && codes.Contains(types[i]))
                        ids.Add((chunk << ChunkBits) | i);
            }
            return ids;
        }

        /// <summary>How many entities have each type (upper-case name), for statistics.</summary>
        public Dictionary<string, int> CountByType()
        {
            var counts = new int[_typeNames.Count];
            foreach (var types in _typeChunks)
                if (types != null)
                    foreach (var code in types)
                        counts[code]++;
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int code = 1; code < counts.Length; code++)
                if (counts[code] > 0)
                    result[_typeNames[code]] = counts[code];
            return result;
        }

        /// <summary>Parses an entity's attributes; null if the id does not exist.</summary>
        public unsafe StepEntity Get(int id)
        {
            ushort code = TypeCode(id);
            if (code == 0)
                return null;
            long offset = _offsetChunks[id >> ChunkBits][id & ChunkMask] - 1;
            var parser = new StepParser(_base, offset, _length);
            if (_typeNames[code] == "(")
            {
                // Complex instance: (IFCA(...)IFCB(...)) – the parts' attributes in order make up the entity.
                var parts = parser.ParseComplex();
                return new StepEntity(this, id, code, parts.Item1, parts.Item2);
            }
            var args = parser.ParseArguments();
            return new StepEntity(this, id, code, null, args);
        }

        public unsafe void Dispose()
        {
            if (_view != null)
            {
                if (_base != null)
                    _view.SafeMemoryMappedViewHandle.ReleasePointer();
                _base = null;
                _view.Dispose();
                _view = null;
            }
            _map?.Dispose();
            _map = null;
        }
    }
}
