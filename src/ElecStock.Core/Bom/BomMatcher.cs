using ElecStock.Contracts;
using ElecStock.Stock;

namespace ElecStock.Bom;

/// <summary>A stock part as seen by the BOM matcher.</summary>
public sealed record BomPart(int Id, string Ref, string Designation);

/// <summary>
/// Finds the stock part of each BOM line: the library ref when it is a stock ref, else the value
/// for passives (<c>R</c> 4k7 → <c>R-4k7</c>, <c>C</c> 470u → <c>CP-470u</c>, compared by value so 4.7k
/// and 4k7 match), else a part whose ref is the value, else an identical designation.
/// </summary>
public static class BomMatcher
{
    public static (BomPart? Part, BomMatchKind Kind) Match(BomRow row, IReadOnlyList<BomPart> parts)
    {
        if (row.LibraryRef.Length > 0)
        {
            var exact = parts.FirstOrDefault(p => p.Ref.Equals(row.LibraryRef.Trim(), StringComparison.OrdinalIgnoreCase));
            if (exact != null) return (exact, BomMatchKind.LibraryRef);
        }

        if (row.Value.Length > 0)
        {
            var byValue = MatchValue(row, parts);
            if (byValue != null) return (byValue, BomMatchKind.Value);
        }

        if (row.Designation.Length > 0)
        {
            var byDesignation = parts.FirstOrDefault(p => p.Designation.Equals(row.Designation.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byDesignation != null) return (byDesignation, BomMatchKind.Designation);
        }

        // INA226-Module-v2 in the BOM, INA226-Module in the stock: the longest stock ref the library ref starts with.
        if (row.LibraryRef.Length > 0)
        {
            var libraryRef = row.LibraryRef.Trim();
            var similar = parts
                .Where(p => libraryRef.Length > p.Ref.Length && libraryRef.StartsWith(p.Ref + "-", StringComparison.OrdinalIgnoreCase))
                .MaxBy(p => p.Ref.Length);
            if (similar != null) return (similar, BomMatchKind.SimilarRef);
        }

        return (null, BomMatchKind.None);
    }

    static BomPart? MatchValue(BomRow row, IReadOnlyList<BomPart> parts)
    {
        var exactRef = parts.FirstOrDefault(p => p.Ref.Equals(row.Value.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exactRef != null) return exactRef;
        var value = Normalize(row.Value);

        // "radial electrolytic" is often in the notes rather than in the designation
        var hint = (row.Designation + " " + row.Notes).ToLowerInvariant();
        var prefixes = DesignatorPrefix(row.Designators) switch
        {
            "R" or "RM" or "RV" => ["R-"],
            "C" when hint.Contains("electrolytic") => new[] { "CP-", "C-", "CF-" },
            "C" when hint.Contains("film") => new[] { "CF-", "C-", "CP-" },
            "C" => new[] { "C-", "CP-", "CF-" },
            _ => [],
        };
        var target = PartVisuals.SegmentValue(value);
        if (target == null || prefixes.Length == 0) return null;

        foreach (var prefix in prefixes)
        {
            var candidates = parts
                .Where(p => p.Ref.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(p => PartVisuals.SegmentValue(p.Ref.Split('-')[1]) is { } v && Math.Abs(v - target.Value) <= Math.Abs(target.Value) * 1e-9)
                .OrderBy(p => p.Ref.Length)
                .ToList();
            if (candidates.Count > 0) return candidates[0];
        }
        return null;
    }

    /// <summary>"100nF" → "100n", "4.7kΩ" → "4.7k", "470µ" → "470u", "470u >= 10V" → "470u" (the first word is the value).</summary>
    static string Normalize(string value)
    {
        var first = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var v = first.Replace('µ', 'u').Replace("Ω", "").Replace("ohm", "", StringComparison.OrdinalIgnoreCase);
        if (v.Length > 1 && (v.EndsWith('F') || v.EndsWith('f')) && PartVisuals.SegmentValue(v[..^1]) != null) v = v[..^1];
        return v;
    }

    /// <summary>Letters of the first designator: "R101 R105" → "R".</summary>
    static string DesignatorPrefix(string designators)
    {
        var first = designators.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return new string(first.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
    }
}
