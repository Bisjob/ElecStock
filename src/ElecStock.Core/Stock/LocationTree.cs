namespace ElecStock.Stock;

/// <summary>
/// Locations group by their code: <c>DIO</c> holds <c>DIO-1</c>, which holds <c>DIO-1.1</c> to <c>DIO-1.5</c>.
/// A code is inside another when it starts with it followed by <c>-</c> or <c>.</c>; its parent is the longest such
/// code that exists. Nothing to set up: create <c>DIO</c> (named "Diodes") and it groups every <c>DIO-…</c>.
/// </summary>
public static class LocationTree
{
    /// <summary>True when <paramref name="code"/> is strictly inside <paramref name="ancestor"/> (DIO-1.3 in DIO-1 and in DIO).</summary>
    public static bool IsInside(string code, string ancestor) =>
        ancestor.Length > 0
        && code.Length > ancestor.Length + 1
        && code.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase)
        && code[ancestor.Length] is '-' or '.';

    /// <summary>True when <paramref name="code"/> is <paramref name="ancestor"/> itself or inside it.</summary>
    public static bool IsSameOrInside(string code, string ancestor) =>
        code.Equals(ancestor, StringComparison.OrdinalIgnoreCase) || IsInside(code, ancestor);

    /// <summary>The closest existing location that holds <paramref name="code"/>, null at the top.</summary>
    public static string? ParentCode(string code, IEnumerable<string> codes) =>
        codes.Where(c => IsInside(code, c)).MaxBy(c => c.Length);

    /// <summary>
    /// The codes in tree order (each parent followed by what it holds, siblings in natural order), with their parent
    /// and depth (0 at the top).
    /// </summary>
    public static List<(string Code, string? Parent, int Depth)> Order(IEnumerable<string> codes)
    {
        var all = codes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var parents = all.ToDictionary(c => c, c => ParentCode(c, all), StringComparer.OrdinalIgnoreCase);
        var children = all.ToLookup(c => parents[c] ?? "", StringComparer.OrdinalIgnoreCase);
        var natural = Comparer<string>.Create(PartVisuals.CompareNatural);
        var result = new List<(string, string?, int)>(all.Count);

        void Walk(string parent, int depth)
        {
            foreach (var code in children[parent].Order(natural))
            {
                result.Add((code, parents[code], depth));
                Walk(code, depth + 1);
            }
        }

        Walk("", 0);
        return result;
    }
}
