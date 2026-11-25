using System.Globalization;
using ElecStock.Csv;

namespace ElecStock.Bom;

/// <summary>One line of a device BOM.</summary>
public sealed record BomRow(
    int Line,
    string Designators,
    int Qty,
    string Designation,
    string Value,
    string LibraryRef,
    string Supplier,
    string Notes);

/// <summary>
/// Reads a device <c>bom.csv</c>: the simple format (<c>ref,qty,designation,value,library_ref,supplier,notes</c>)
/// or a KiCad BOM export (<c>Reference,Value,Footprint,Qty…</c>), recognised by their column names.
/// </summary>
public static class BomFile
{
    static readonly string[] DesignatorColumns = ["ref", "reference", "references", "designator", "designators"];
    static readonly string[] QtyColumns = ["qty", "quantity", "quantity per pcb", "count"];
    static readonly string[] DesignationColumns = ["designation", "description"];
    static readonly string[] ValueColumns = ["value"];
    static readonly string[] LibraryRefColumns = ["library_ref", "stock", "stock_ref", "elecstock"];
    static readonly string[] SupplierColumns = ["supplier"];
    static readonly string[] NotesColumns = ["notes", "note", "comment"];

    public static List<BomRow> Parse(string csv, List<string>? warnings = null)
    {
        var records = CsvText.ParseRecords(csv);
        var rows = new List<BomRow>();
        if (records.Count == 0) return rows;

        var columns = records[0].Keys.ToList();
        string? Find(string[] names) => columns.FirstOrDefault(c => names.Contains(c.Trim().ToLowerInvariant()));
        var designators = Find(DesignatorColumns);
        var qty = Find(QtyColumns);
        if (designators == null)
        {
            warnings?.Add("No designator column (ref / Reference) in the BOM.");
            return rows;
        }
        var designation = Find(DesignationColumns);
        var value = Find(ValueColumns);
        var libraryRef = Find(LibraryRefColumns);
        var supplier = Find(SupplierColumns);
        var notes = Find(NotesColumns);
        string Get(Dictionary<string, string> r, string? column) => column != null && r.TryGetValue(column, out var v) ? v : "";

        int line = 1;
        foreach (var record in records)
        {
            line++;
            var refs = Get(record, designators).Replace(',', ' ').Trim();
            if (refs.Length == 0) continue;
            var qtyText = Get(record, qty);
            if (!int.TryParse(qtyText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
            {
                n = refs.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                if (qtyText.Length > 0) warnings?.Add($"Line {line}: qty '{qtyText}' is not a number, {n} assumed from the designators.");
            }
            rows.Add(new BomRow(line, string.Join(' ', refs.Split(' ', StringSplitOptions.RemoveEmptyEntries)), n,
                Get(record, designation), Get(record, value), Get(record, libraryRef), Get(record, supplier), Get(record, notes)));
        }
        return rows;
    }
}
