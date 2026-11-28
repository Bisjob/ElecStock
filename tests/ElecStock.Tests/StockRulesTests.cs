using ElecStock.Bom;
using ElecStock.Contracts;
using ElecStock.Stock;

namespace ElecStock.Tests;

public class QtyTests
{
    [Theory]
    [InlineData("12", QtyKind.Exact, 12)]
    [InlineData(" 10+ ", QtyKind.AtLeast, 10)]
    [InlineData("", QtyKind.Blank, 0)]
    [InlineData("several", QtyKind.Text, 0)]
    public void Parse(string raw, QtyKind kind, int value)
    {
        var qty = Qty.Parse(raw);
        Assert.Equal(kind, qty.Kind);
        Assert.Equal(value, qty.Value);
    }

    [Theory]
    [InlineData("5", 2, StockStatus.Ok, "3")]
    [InlineData("4", 4, StockStatus.Empty, "0")]
    [InlineData("4", 6, StockStatus.Short, "−2")]
    [InlineData("10+", 3, StockStatus.Ok, "7+")]
    [InlineData("10+", 10, StockStatus.Check, "?")]
    [InlineData("", 1, StockStatus.Unknown, "?")]
    public void Availability(string raw, int reserved, StockStatus status, string available)
    {
        var qty = Qty.Parse(raw);
        Assert.Equal(status, StockMath.Status(qty, reserved));
        Assert.Equal(available, StockMath.AvailableShort(qty, reserved));
    }

    [Theory]
    [InlineData("9", 4, "5", false)]
    [InlineData("2", 4, "0", true)]
    [InlineData("10+", 4, "10+", true)]
    [InlineData("", 1, "", true)]
    public void Consume(string before, int used, string after, bool remark)
    {
        var (result, text) = StockMath.Consume(Qty.Parse(before), used);
        Assert.Equal(after, result);
        Assert.Equal(remark, text.Length > 0);
    }

    [Theory]
    [InlineData("3", 10, "13")]
    [InlineData("10+", 10, "20+")]
    [InlineData("", 5, "5")]
    [InlineData("several", 5, "several")]
    public void Receive(string before, int received, string after) =>
        Assert.Equal(after, StockMath.Receive(Qty.Parse(before), received).After);
}

public class PartVisualsTests
{
    [Fact]
    public void Refs_sort_by_value_then_naturally()
    {
        string[] refs = ["R-10k", "R-4k7", "R-1M", "R-100", "C-1u", "C-100n", "JST-XH-B10B", "JST-XH-B2B", "JST-XH-B3B"];
        var sorted = refs.Order(PartVisuals.RefComparer).ToArray();
        Assert.Equal(["C-100n", "C-1u", "JST-XH-B2B", "JST-XH-B3B", "JST-XH-B10B", "R-100", "R-4k7", "R-10k", "R-1M"], sorted);
    }

    [Theory]
    [InlineData("R-4k7", "yellow violet red gold")]
    [InlineData("R-10", "brown black black gold")]
    [InlineData("R-0R47", "yellow violet silver gold")]
    [InlineData("R-260", "red blue brown gold")]
    public void Resistor_colour_code(string reference, string bands) =>
        Assert.Equal(bands, string.Join(' ', PartVisuals.ResistorBands(reference)!.Select(b => b.Name)));

    [Theory]
    [InlineData("R-4k7", "passive", "Resistor 4k7", GlyphKind.Resistor)]
    [InlineData("CP-470u", "passive", "Electrolytic capacitor", GlyphKind.Electrolytic)]
    [InlineData("IRF4905", "mosfet", "P-channel MOSFET -55V 74A TO-220", GlyphKind.To220)]
    [InlineData("ESP32-C3-SuperMini", "mcu", "ESP32-C3 Super Mini board", GlyphKind.Module)]
    [InlineData("LED-RED", "led", "5 mm red LED", GlyphKind.Led)]
    public void Glyph(string reference, string category, string designation, GlyphKind kind) =>
        Assert.Equal(kind, PartVisuals.GlyphFor(reference, category, designation));
}

public class BomTests
{
    static readonly List<BomPart> Parts =
    [
        new(1, "R-4k7", "Resistor 4k7 1/4 W axial"),
        new(2, "R-1m-2512", "Current shunt 1 mOhm"),
        new(3, "C-1u", "Ceramic capacitor 1uF"),
        new(4, "CP-1u", "Electrolytic capacitor 1uF radial"),
        new(5, "CP-470u", "Electrolytic capacitor 470uF radial"),
        new(6, "IRF4905", "P-channel MOSFET"),
        new(7, "INA226-Module", "INA226 current monitor module"),
        new(8, "PINSOCKET-1x8", "Pin socket 1x8 2.54 mm"),
    ];

    const string Csv = """
        ref,qty,designation,value,library_ref,supplier,notes
        R601-R602,2,I2C pull-ups,4.7k,,stock,
        R101,1,Current shunt,1m,,stock,
        C202-C203 C302-C303,4,Bulk capacitor,470u >= 10V,,stock,
        C201 C301,2,Electrolytic decoupling,1u 16V,,stock,
        Q201 Q202,2,High-side MOSFET,IRF4905,IRF4905,stock,TO-220
        U101,1,Current monitor,,INA226-Module-v2,stock,
        SOCK1,1,Pin socket 1x8 2.54 mm,,,stock,
        W1,1 m,Wire,,,stock,
        J1,1,Unknown connector,,,lcsc,
        """;

    [Fact]
    public void Parses_the_simple_format()
    {
        var warnings = new List<string>();
        var rows = BomFile.Parse(Csv, warnings);
        Assert.Equal(9, rows.Count);
        Assert.Equal(4, rows[2].Qty);
        Assert.Equal("C202-C203 C302-C303", rows[2].Designators);
        Assert.Single(warnings); // "1 m" of wire
    }

    [Theory]
    [InlineData(0, "R-4k7", BomMatchKind.Value)]
    [InlineData(1, "R-1m-2512", BomMatchKind.Value)]
    [InlineData(2, "CP-470u", BomMatchKind.Value)]
    [InlineData(3, "CP-1u", BomMatchKind.Value)]
    [InlineData(4, "IRF4905", BomMatchKind.LibraryRef)]
    [InlineData(5, "INA226-Module", BomMatchKind.SimilarRef)]
    [InlineData(6, "PINSOCKET-1x8", BomMatchKind.Designation)]
    [InlineData(8, null, BomMatchKind.None)]
    public void Matches_stock_parts(int line, string? expected, BomMatchKind kind)
    {
        var row = BomFile.Parse(Csv)[line];
        var (part, how) = BomMatcher.Match(row, Parts);
        Assert.Equal(expected, part?.Ref);
        Assert.Equal(kind, how);
    }
}

public class LocationTreeTests
{
    [Theory]
    [InlineData("DIO-1.3", "DIO", true)]
    [InlineData("DIO-1.3", "DIO-1", true)]
    [InlineData("dio-1.3", "DIO-1", true)]
    [InlineData("DIO-1", "DIO-1", false)]
    [InlineData("DIO-10", "DIO-1", false)]
    [InlineData("DIODE", "DIO", false)]
    [InlineData("A3", "A", false)]
    [InlineData("DIO-", "DIO", false)]
    public void IsInside(string code, string ancestor, bool inside) =>
        Assert.Equal(inside, LocationTree.IsInside(code, ancestor));

    [Fact]
    public void ParentIsTheClosestExistingLocation()
    {
        string[] codes = ["DIO", "DIO-1", "DIO-1.3", "RES"];
        Assert.Equal("DIO-1", LocationTree.ParentCode("DIO-1.3", codes));
        Assert.Equal("DIO", LocationTree.ParentCode("DIO-2.1", codes));
        Assert.Null(LocationTree.ParentCode("DIO", codes));
        Assert.Null(LocationTree.ParentCode("CAP-1", codes));
    }

    [Fact]
    public void OrderPutsEachLocationUnderItsParent()
    {
        var order = LocationTree.Order(["DIO-10", "RES", "DIO-1.2", "DIO", "DIO-2", "DIO-1", "DIO-1.10", "A3"]);
        Assert.Equal(["A3", "DIO", "DIO-1", "DIO-1.2", "DIO-1.10", "DIO-2", "DIO-10", "RES"], order.Select(o => o.Code));
        Assert.Equal([0, 0, 1, 2, 2, 1, 1, 0], order.Select(o => o.Depth));
        Assert.Equal("DIO-1", order.Single(o => o.Code == "DIO-1.10").Parent);
    }
}
