using System.Globalization;
using System.Text.Json.Serialization;

namespace ElecStock.Stock;

public enum QtyKind
{
    /// <summary>Empty: not counted yet.</summary>
    Blank,
    /// <summary>Plain integer.</summary>
    Exact,
    /// <summary><c>10+</c>: at least that many.</summary>
    AtLeast,
    /// <summary>Free text such as <c>several</c>.</summary>
    Text,
}

/// <summary>The quantity of a part as typed: <c>12</c>, <c>10+</c>, empty or free text.</summary>
public readonly record struct Qty(QtyKind Kind, int Value, string Raw)
{
    public static Qty Parse(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return new(QtyKind.Blank, 0, s);
        if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return new(QtyKind.Exact, n, s);
        if (s.EndsWith('+') && int.TryParse(s[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out n))
            return new(QtyKind.AtLeast, n, s);
        return new(QtyKind.Text, 0, s);
    }

    public override string ToString() => Raw;
}

[JsonConverter(typeof(JsonStringEnumConverter<StockStatus>))]
public enum StockStatus
{
    /// <summary>Something is available.</summary>
    Ok,
    /// <summary>Exactly zero available.</summary>
    Empty,
    /// <summary>More reserved than in stock.</summary>
    Short,
    /// <summary><c>10+</c> with as many reserved: needs a real count.</summary>
    Check,
    /// <summary>Quantity not filled in.</summary>
    Unknown,
}
