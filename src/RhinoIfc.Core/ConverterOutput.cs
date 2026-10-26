using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RhinoIfc.Core
{
    public enum OutputKind
    {
        /// <summary>Any other line (help, unknown messages).</summary>
        Text,

        /// <summary><c>@status &lt;text&gt;</c>: what the converter does now ("IFC wird gelesen …").</summary>
        Status,

        /// <summary><c>@percent &lt;n&gt;</c>: progress of the conversion.</summary>
        Percent,

        /// <summary><c>@phase &lt;phase&gt; &lt;text&gt;</c>: a long phase (hull, writing …).</summary>
        Phase,

        /// <summary><c>✓ input → output (N elements…)</c>: the file is done.</summary>
        Success,

        /// <summary><c>⚠ …</c>: a warning (skipped elements, memory, retry after a crash).</summary>
        Warning,

        /// <summary><c>✗ …</c>: the file failed; the next line usually gives a hint.</summary>
        Error,

        /// <summary><c>■ …</c>: cancelled.</summary>
        Cancelled,
    }

    /// <summary>One line of the converter's output.</summary>
    public sealed class OutputLine
    {
        public OutputKind Kind { get; private set; }
        public string Text { get; private set; } = "";
        public int Percent { get; private set; } = -1;
        public string Phase { get; private set; } = "";

        /// <summary>For <see cref="OutputKind.Success"/>: the number of elements written, -1 if unknown.</summary>
        public int Elements { get; private set; } = -1;

        // "✓ C:\a.ifc → C:\b.3dm (1234 elements, 5 without geometry)" (the result lines stay English).
        private static readonly Regex ElementsPattern = new Regex(@"\((\d+) elements", RegexOptions.CultureInvariant);

        public static OutputLine Parse(string line)
        {
            line = (line ?? "").TrimEnd('\r', '\n');
            var trimmed = line.Trim();
            if (trimmed.StartsWith("@percent ", StringComparison.Ordinal))
            {
                if (int.TryParse(trimmed.Substring(9).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                    return new OutputLine { Kind = OutputKind.Percent, Percent = Math.Max(0, Math.Min(100, value)), Text = trimmed };
            }
            else if (trimmed.StartsWith("@status ", StringComparison.Ordinal))
                return new OutputLine { Kind = OutputKind.Status, Text = trimmed.Substring(8).Trim() };
            else if (trimmed.StartsWith("@phase ", StringComparison.Ordinal))
            {
                var rest = trimmed.Substring(7).Trim();
                int space = rest.IndexOf(' ');
                return space < 0
                    ? new OutputLine { Kind = OutputKind.Phase, Phase = rest }
                    : new OutputLine { Kind = OutputKind.Phase, Phase = rest.Substring(0, space), Text = rest.Substring(space + 1).Trim() };
            }
            else if (trimmed.StartsWith("✓", StringComparison.Ordinal))
            {
                var match = ElementsPattern.Match(trimmed);
                return new OutputLine
                {
                    Kind = OutputKind.Success,
                    Text = trimmed.Substring(1).Trim(),
                    Elements = match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : -1,
                };
            }
            else if (trimmed.StartsWith("⚠", StringComparison.Ordinal))
                return new OutputLine { Kind = OutputKind.Warning, Text = trimmed.Substring(1).Trim() };
            else if (trimmed.StartsWith("✗", StringComparison.Ordinal))
                return new OutputLine { Kind = OutputKind.Error, Text = trimmed.Substring(1).Trim() };
            else if (trimmed.StartsWith("■", StringComparison.Ordinal))
                return new OutputLine { Kind = OutputKind.Cancelled, Text = trimmed.Substring(1).Trim() };
            return new OutputLine { Kind = OutputKind.Text, Text = trimmed };
        }

        public override string ToString() => $"{Kind}: {Text}";
    }
}
