using System.Globalization;
using System.Text.RegularExpressions;

namespace ElecStock.Stock;

/// <summary>Which drawing is used as a part's picture when it has no photo.</summary>
public enum GlyphKind
{
    Generic,
    Resistor,
    Electrolytic,
    Ceramic,
    Film,
    To220,
    To92,
    Dip,
    Module,
    Power,
    Connector,
    LedStrip,
    Fuse,
    Button,
    Heatsink,
    Led,
}

/// <summary>Display rules of the stock: drawings, colour codes, family grouping and ref ordering.</summary>
public static partial class PartVisuals
{
    public static GlyphKind GlyphFor(string reference, string category, string designation)
    {
        var r = reference.ToUpperInvariant();
        var d = designation.ToUpperInvariant();
        var c = category.ToLowerInvariant();

        if (r.StartsWith("R-") && ResistorOhms(reference) != null) return GlyphKind.Resistor;
        if (r.StartsWith("CP-")) return GlyphKind.Electrolytic;
        if (r.StartsWith("CF-")) return GlyphKind.Film;
        if (r.StartsWith("C-")) return d.Contains("FILM") ? GlyphKind.Film : GlyphKind.Ceramic;
        if (c == "led" || (r.StartsWith("LED-") && !d.Contains("STRIP"))) return GlyphKind.Led;
        if (r.Contains("FUSE")) return GlyphKind.Fuse;
        if (r.Contains("HEATSINK")) return GlyphKind.Heatsink;
        if (r.Contains("BUTTON") || d.Contains("PUSH-BUTTON") || d.Contains("PUSHBUTTON")) return GlyphKind.Button;
        if (d.Contains("TO-220")) return GlyphKind.To220;
        if (d.Contains("TO-92")) return GlyphKind.To92;
        if (d.Contains("DIP")) return GlyphKind.Dip;
        if (c == "power") return GlyphKind.Power;
        if (c == "actuator" && (d.Contains("LED") || r.Contains("WS28"))) return GlyphKind.LedStrip;
        if (c == "connector" || r.StartsWith("JST") || r.StartsWith("TERMINAL") || r.StartsWith("PINSOCKET") || r.StartsWith("PINHEADER"))
            return GlyphKind.Connector;
        if (c is "mcu" or "io" or "sensor" or "signal" || d.Contains("MODULE") || d.Contains("BOARD")) return GlyphKind.Module;
        if (c is "ic" or "mosfet" or "transistor") return GlyphKind.Dip;
        return GlyphKind.Generic;
    }

    // R-10, R-4k7, R-2k2, R-1M, R-0R47: the letter is the decimal point and the multiplier.
    // Case-sensitive on purpose: "m" is milli (R-1m-2512 shunt), "M" is mega.
    [GeneratedRegex(@"^[Rr]-(\d+)([RrkKM]?)(\d*)$")]
    private static partial Regex ResistorRegex();

    public static double? ResistorOhms(string reference)
    {
        var m = ResistorRegex().Match(reference.Trim());
        if (!m.Success) return null;
        var letter = m.Groups[2].Value.ToUpperInvariant();
        var text = m.Groups[3].Value.Length > 0 ? $"{m.Groups[1].Value}.{m.Groups[3].Value}" : m.Groups[1].Value;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
        return letter switch { "K" => v * 1e3, "M" => v * 1e6, _ => v };
    }

    public static readonly string[] BandNames =
        ["black", "brown", "red", "orange", "yellow", "green", "blue", "violet", "grey", "white"];

    public static readonly string[] BandColors =
        ["#1A1A1A", "#7A4A1E", "#D32F2F", "#F57C00", "#F9D23C", "#2E9E4F", "#1E6FD9", "#8E44AD", "#8C8C8C", "#F5F5F5"];

    public const string GoldColor = "#C9A43A";
    public const string SilverColor = "#C7CCD1";

    /// <summary>Colour bands (4-band code, 5-band when 3 significant digits are needed), or null.</summary>
    public static List<(string Name, string Color)>? ResistorBands(string reference)
    {
        var ohms = ResistorOhms(reference);
        if (ohms is null or <= 0) return null;

        // Smallest exponent for which the value fits in 2 (else 3) significant digits.
        foreach (int digits in new[] { 2, 3 })
        {
            for (int exp = -2; exp <= 9; exp++)
            {
                double sig = ohms.Value / Math.Pow(10, exp);
                double rounded = Math.Round(sig);
                if (Math.Abs(sig - rounded) > 1e-6) continue;
                if (rounded >= Math.Pow(10, digits)) continue;
                if (rounded < Math.Pow(10, digits - 1)) break; // would need a leading zero
                var bands = new List<(string, string)>();
                foreach (var ch in ((int)rounded).ToString(CultureInfo.InvariantCulture))
                    bands.Add((BandNames[ch - '0'], BandColors[ch - '0']));
                bands.Add(exp switch
                {
                    -1 => ("gold", GoldColor),
                    -2 => ("silver", SilverColor),
                    _ => (BandNames[exp], BandColors[exp]),
                });
                bands.Add(digits == 2 ? ("gold", GoldColor) : ("brown", BandColors[1]));
                return bands;
            }
        }
        return null;
    }

    /// <summary>Lens colour of an LED from its ref and designation ("rgb" for multi-colour), or null.</summary>
    public static string? LedColor(string reference, string designation)
    {
        var text = (reference + " " + designation).ToLowerInvariant();
        if (text.Contains("rgb")) return "rgb";
        foreach (var (word, color) in LedColors)
            if (text.Contains(word)) return color;
        return null;
    }

    static readonly (string Word, string Color)[] LedColors =
    [
        ("infrared", "#5A4A4A"), ("red", "#FF3B30"), ("green", "#34C759"), ("blue", "#2F7BFF"), ("yellow", "#FFD60A"),
        ("orange", "#FF9500"), ("white", "#F2F2F2"), ("warm", "#FFE2A8"), ("uv", "#9B5CFF"),
    ];

    [GeneratedRegex(@"^C[PF]?-([\d.]+)\s*([pnuµm]?)", RegexOptions.IgnoreCase)]
    private static partial Regex CapRegex();

    /// <summary>Short value printed on capacitor drawings: <c>470µ</c>, <c>100n</c>.</summary>
    public static string CapacitorLabel(string reference)
    {
        var m = CapRegex().Match(reference.Trim());
        if (!m.Success) return "";
        var unit = m.Groups[2].Value.ToLowerInvariant() switch { "u" => "µ", var u => u };
        return m.Groups[1].Value + unit;
    }

    // A ref segment that is a value: 10, 2512, 4k7, 0R47, 1M, 100n, 0.22u, 1m (milli).
    // Upper-case P / N / U are not units, so connector sizes such as 3P stay text.
    [GeneratedRegex(@"^(\d+(?:\.\d+)?)([pnuµmkKMRr]?)(\d*)$")]
    private static partial Regex ValueRegex();

    internal static double? SegmentValue(string segment)
    {
        var m = ValueRegex().Match(segment);
        if (!m.Success) return null;
        var (whole, letter, fraction) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
        if (fraction.Length > 0 && whole.Contains('.')) return null;
        var text = fraction.Length > 0 ? $"{whole}.{fraction}" : whole;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
        return letter switch
        {
            "p" => v * 1e-12,
            "n" => v * 1e-9,
            "u" or "µ" => v * 1e-6,
            "m" => v * 1e-3,
            "k" or "K" => v * 1e3,
            "M" => v * 1e6,
            _ => v,
        };
    }

    /// <summary>
    /// Order of the parts inside a display group, so that series and kits sit together: the
    /// '-' separated segments are compared in turn, values by value (R-4k7 before R-10k, C-100n
    /// before C-1u) and other text in natural order (B2B, B3B, B10B). At the same position a value
    /// comes before text, and a shorter ref before a longer one.
    /// </summary>
    public static int CompareRefs(string a, string b)
    {
        var sa = a.Split('-');
        var sb = b.Split('-');
        for (int i = 0; i < Math.Min(sa.Length, sb.Length); i++)
        {
            int c = CompareSegments(sa[i], sb[i]);
            if (c != 0) return c;
        }
        return sa.Length.CompareTo(sb.Length);
    }

    public static IComparer<string> RefComparer { get; } = Comparer<string>.Create(CompareRefs);

    static int CompareSegments(string a, string b)
    {
        double? va = SegmentValue(a), vb = SegmentValue(b);
        if (va != null && vb != null) return va.Value.CompareTo(vb.Value);
        if (va != null || vb != null) return va != null ? -1 : 1;
        return CompareNatural(a, b);
    }

    /// <summary>Case-insensitive, digit runs compared as numbers.</summary>
    public static int CompareNatural(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var da = a[si..i].TrimStart('0');
                var db = b[sj..j].TrimStart('0');
                int c = da.Length != db.Length ? da.Length.CompareTo(db.Length) : string.CompareOrdinal(da, db);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    /// <summary>Display group used by the tile view (passives are split by family).</summary>
    public static string DisplayGroup(string reference, string category)
    {
        var c = category.ToLowerInvariant();
        var r = reference.ToUpperInvariant();
        if (c == "passive")
        {
            if (r.StartsWith("R-")) return "Resistors";
            if (r.StartsWith("C-") || r.StartsWith("CP-") || r.StartsWith("CF-")) return "Capacitors";
            if (r.Contains("FUSE")) return "Fuses";
        }
        return CategoryTitle(category);
    }

    public static string CategoryTitle(string category) => category.ToLowerInvariant() switch
    {
        "" => "Uncategorised",
        "mcu" => "MCU boards",
        "io" => "I/O modules",
        "ic" => "ICs",
        "mosfet" => "MOSFETs",
        "passive" => "Passives",
        "sensor" => "Sensors",
        "power" => "Power",
        "actuator" => "Actuators",
        "connector" => "Connectors",
        "transistor" => "Transistors",
        "diode" => "Diodes",
        "led" => "LEDs",
        "mechanical" => "Mechanical",
        "signal" => "Signal",
        var other => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(other),
    };

    static readonly string[] CategoryOrder =
        ["mcu", "io", "sensor", "signal", "actuator", "power", "ic", "mosfet", "transistor", "diode", "led", "passive", "connector", "mechanical"];

    static readonly string[] GroupOrder = ["Resistors", "Capacitors", "Fuses"];

    public static int CategoryRank(string category)
    {
        int i = Array.IndexOf(CategoryOrder, category.ToLowerInvariant());
        return i < 0 ? CategoryOrder.Length : i;
    }

    /// <summary>Sort key of a display group: category rank, then the passive sub-family.</summary>
    public static int GroupRank(string reference, string category)
    {
        int sub = Array.IndexOf(GroupOrder, DisplayGroup(reference, category));
        return CategoryRank(category) * 10 + (sub < 0 ? 9 : sub);
    }

    /// <summary>Tile order: group rank, group title, then refs so that series sit together.</summary>
    public static int CompareTiles(string refA, string categoryA, string refB, string categoryB)
    {
        int c = GroupRank(refA, categoryA).CompareTo(GroupRank(refB, categoryB));
        if (c != 0) return c;
        c = string.Compare(DisplayGroup(refA, categoryA), DisplayGroup(refB, categoryB), StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : CompareRefs(refA, refB);
    }

    static readonly Dictionary<string, string> CategoryColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mcu"] = "#3B82F6",
        ["io"] = "#8B5CF6",
        ["sensor"] = "#10B981",
        ["signal"] = "#EC4899",
        ["actuator"] = "#EF4444",
        ["power"] = "#F59E0B",
        ["ic"] = "#0EA5E9",
        ["mosfet"] = "#64748B",
        ["transistor"] = "#6B7280",
        ["led"] = "#E11D48",
        ["passive"] = "#B7791F",
        ["connector"] = "#14B8A6",
        ["mechanical"] = "#78716C",
    };

    static readonly string[] SpareColors = ["#06B6D4", "#84CC16", "#F43F5E", "#A855F7", "#22C55E", "#EAB308"];

    public static string CategoryColor(string category)
    {
        if (CategoryColors.TryGetValue(category, out var color)) return color;
        int hash = 0;
        foreach (var ch in category.ToLowerInvariant()) hash = hash * 31 + ch;
        return SpareColors[Math.Abs(hash % SpareColors.Length)];
    }

    public static string Initials(string category) =>
        category.Length == 0 ? "?" : category.Length <= 4 ? category.ToUpperInvariant() : category[..3].ToUpperInvariant();
}
