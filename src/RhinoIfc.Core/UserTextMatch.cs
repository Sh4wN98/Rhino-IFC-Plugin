using System;
using System.Text.RegularExpressions;

namespace RhinoIfc.Core
{
    /// <summary>Compares user text values for IfcSelect: case-insensitive, <c>*</c> and <c>?</c> as wildcards.</summary>
    public static class UserTextMatch
    {
        public static bool Matches(string value, string pattern)
        {
            if (pattern == null)
                return true;
            value = value ?? "";
            if (pattern.IndexOfAny(new[] { '*', '?' }) < 0)
                return string.Equals(value.Trim(), pattern.Trim(), StringComparison.OrdinalIgnoreCase);
            var regex = "^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            return Regex.IsMatch(value.Trim(), regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        }

        /// <summary>The IFC subtypes that differ from their supertype only by these suffixes (IfcWallStandardCase,
        /// IfcSlabElementedCase …); IFC4 deprecates them, but many files still use them.</summary>
        private static readonly string[] CaseSuffixes = { "StandardCase", "ElementedCase" };

        /// <summary>Like <see cref="Matches"/>, but an IFC class without wildcards also takes its …StandardCase and
        /// …ElementedCase subtypes: "IfcWall" selects IfcWallStandardCase too.</summary>
        public static bool MatchesClass(string ifcClass, string pattern)
        {
            if (Matches(ifcClass, pattern))
                return true;
            if (pattern == null || ifcClass == null || pattern.IndexOfAny(new[] { '*', '?' }) >= 0)
                return false;
            foreach (var suffix in CaseSuffixes)
                if (string.Equals(ifcClass.Trim(), pattern.Trim() + suffix, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>The property set of a user text key: "Pset_WallCommon" for "Pset_WallCommon.IsExternal",
        /// "IFC" for the converter's own keys (IfcGlobalId, IfcClass …).</summary>
        public static string Group(string key)
        {
            if (string.IsNullOrEmpty(key))
                return "";
            int dot = key.IndexOf('.');
            return dot > 0 ? key.Substring(0, dot) : "IFC";
        }
    }
}
