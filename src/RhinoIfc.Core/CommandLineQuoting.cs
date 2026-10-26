using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RhinoIfc.Core
{
    /// <summary>Quotes arguments so that the Microsoft C runtime (and Python on Windows) splits the command
    /// line back into exactly these arguments. .NET Framework's ProcessStartInfo takes one string only.</summary>
    public static class CommandLineQuoting
    {
        public static string Join(IEnumerable<string> arguments) => string.Join(" ", arguments.Select(Quote));

        public static string Quote(string argument)
        {
            if (argument == null)
                argument = "";
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
                return argument;
            var quoted = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                    // Backslashes before a quote are escaped, and so is the quote.
                    quoted.Append('\\', backslashes * 2 + 1);
                else
                    quoted.Append('\\', backslashes);
                backslashes = 0;
                quoted.Append(c);
            }
            // Backslashes before the closing quote are escaped so it stays a quote ("C:\dir\" breaks otherwise).
            quoted.Append('\\', backslashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }
    }
}
