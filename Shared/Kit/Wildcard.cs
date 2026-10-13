using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace DiagKit;

/// <summary>File name patterns with * and ? (case-insensitive), as used in tools.json and setup's file lists.</summary>
public static class Wildcard
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsMatch(string pattern, string name)
    {
        Regex rx = Cache.GetOrAdd(pattern, p => new Regex(
            "^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        return rx.IsMatch(name);
    }
}
