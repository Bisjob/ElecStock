using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ElecStock.Contracts;
using ElecStock.Csv;
using ElecStock.Data.Files;
using ElecStock.Stock;
using Microsoft.EntityFrameworkCore;

namespace ElecStock.Data.Services;

/// <summary>
/// Writes the stock as a zip in the stock folder format (<c>inventory.csv</c>,
/// <c>allocations.csv</c>, <c>Sheets/</c>, <c>Media/&lt;ref&gt;/</c>, <c>Datasheets/</c>), which
/// <see cref="StockImporter"/> reads back, plus the KiCad library (<c>KiCad/</c>, the layout of the sync) and CSVs
/// of what the folder never had (locations, projects, revisions, orders). A readable snapshot; the real backup is
/// the database dump.
/// </summary>
public sealed class StockExporter(IDbContextFactory<StockDb> dbFactory, FileStore files, KicadLibrary kicad)
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task WriteZipAsync(Stream output, CancellationToken ct)
    {
        // ZipArchive writes synchronously: build it in a temp file, then stream it out.
        await using var temp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        using (var zip = new ZipArchive(temp, ZipArchiveMode.Create, leaveOpen: true))
        {
            var parts = (await db.Parts.AsNoTracking().Include(p => p.Location).Include(p => p.Photos).ToListAsync(ct))
                .OrderBy(p => p, Comparer<Entities.Part>.Create((a, b) => PartVisuals.CompareTiles(a.Ref, a.Category, b.Ref, b.Category)))
                .ToList();

            Text(zip, "inventory.csv", CsvText.Write(
                ["ref", "category", "designation", "qty", "location", "sheet", "datasheet_url", "notes", "kicad_symbol", "kicad_footprint", "kicad_3d_model"],
                parts.Select(p => new[]
                {
                    p.Ref, p.Category, p.Designation, p.Qty, p.Location?.Name is { Length: > 0 } n ? n : p.Location?.Code ?? "",
                    p.Sheet != null ? $"Sheets/{p.Ref}.md" : "", p.DatasheetUrl, p.Notes, p.KicadSymbol, p.KicadFootprint, p.Kicad3dModel,
                })));

            var kicadItems = await db.KicadItems.AsNoTracking().ToListAsync(ct);
            if (kicadItems.Count > 0) kicad.Write(zip, kicadItems, manifest: false);

            foreach (var part in parts)
            {
                if (part.Sheet != null) Text(zip, $"Sheets/{part.Ref}.md", part.Sheet);
                if (part.DatasheetFile != null && files.Exists(part.DatasheetFile))
                    zip.CreateEntryFromFile(files.FullPath(part.DatasheetFile), $"Datasheets/{part.Ref}.pdf", CompressionLevel.NoCompression);

                var folder = $"Media/{part.Ref}";
                if (part.FreeNotes.Length > 0) Text(zip, $"{folder}/notes.md", part.FreeNotes);
                if (part.Photos.Count == 0) continue;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var images = new Dictionary<string, object>();
                string? cover = null;
                foreach (var photo in part.Photos.OrderBy(p => p.Id))
                {
                    if (!files.Exists(photo.StoragePath)) continue;
                    var name = UniqueName(names, photo.FileName.Length > 0 ? photo.FileName : Path.GetFileName(photo.StoragePath), Path.GetExtension(photo.StoragePath));
                    zip.CreateEntryFromFile(files.FullPath(photo.StoragePath), $"{folder}/{name}", CompressionLevel.NoCompression);
                    images[name] = new { caption = photo.Caption.Length > 0 ? photo.Caption : null, markers = photo.Markers.Select(m => new { x = m.X, y = m.Y, label = m.Label }) };
                    if (photo.IsCover) cover = name;
                }
                Text(zip, $"{folder}/media.json", JsonSerializer.Serialize(new { cover, images }, Json));
            }

            var allocations = await db.Allocations.AsNoTracking()
                .Include(a => a.Part).Include(a => a.Revision).ThenInclude(r => r.Device).ThenInclude(d => d.Project)
                .ToListAsync(ct);
            Text(zip, "allocations.csv", CsvText.Write(
                ["ref", "project", "device", "rev", "designators", "qty", "status", "notes"],
                allocations
                    .OrderBy(a => a.Part.Ref, PartVisuals.RefComparer).ThenBy(a => a.Revision.Device.Project.Name).ThenBy(a => a.Revision.Device.Name).ThenBy(a => a.Revision.Rev)
                    .Select(a => new[]
                    {
                        a.Part.Ref, a.Revision.Device.Project.Name, a.Revision.Device.Name, a.Revision.Rev, a.Designators,
                        a.Qty.ToString(), a.Status == AllocationStatus.Used ? "used" : "reserved", a.Notes,
                    })));

            var locations = await db.Locations.AsNoTracking().OrderBy(l => l.Code).ToListAsync(ct);
            Text(zip, "locations.csv", CsvText.Write(["code", "name", "description"], locations.Select(l => new[] { l.Code, l.Name, l.Description })));

            var revisions = await db.Revisions.AsNoTracking().Include(r => r.Device).ThenInclude(d => d.Project).ToListAsync(ct);
            Text(zip, "projects.csv", CsvText.Write(
                ["project", "project_status", "description", "repo_path", "device", "device_path", "rev", "rev_status", "pcb_ordered_at", "pcb_received_at", "built_at"],
                revisions.OrderBy(r => r.Device.Project.Name).ThenBy(r => r.Device.Name).ThenBy(r => r.Rev).Select(r => new[]
                {
                    r.Device.Project.Name, r.Device.Project.Status.ToString(), r.Device.Project.Description, r.Device.Project.RepoPath,
                    r.Device.Name, r.Device.Path, r.Rev, r.Status.ToString(), Date(r.PcbOrderedAt), Date(r.PcbReceivedAt), Date(r.BuiltAt),
                })));

            var lines = await db.OrderLines.AsNoTracking().Include(l => l.Order).Include(l => l.Part).ToListAsync(ct);
            Text(zip, "orders.csv", CsvText.Write(
                ["order_id", "supplier", "order_number", "status", "ordered_at", "received_at", "ref", "qty", "received_qty", "unit_price", "currency"],
                lines.OrderBy(l => l.OrderId).ThenBy(l => l.Part.Ref, PartVisuals.RefComparer).Select(l => new[]
                {
                    l.OrderId.ToString(), l.Order.Supplier, l.Order.OrderNumber, l.Order.Status.ToString(), Date(l.Order.OrderedAt), Date(l.Order.ReceivedAt),
                    l.Part.Ref, l.Qty.ToString(), l.ReceivedQty?.ToString() ?? "", l.UnitPrice?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", l.Order.Currency,
                })));
        }

        temp.Position = 0;
        await temp.CopyToAsync(output, ct);
    }

    static string Date(DateOnly? date) => date?.ToString("yyyy-MM-dd") ?? "";

    static void Text(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        stream.Write(bytes);
    }

    static string UniqueName(HashSet<string> taken, string name, string fallbackExtension)
    {
        if (Path.GetExtension(name).Length == 0) name += fallbackExtension;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var unique = name;
        for (int i = 2; !taken.Add(unique); i++) unique = $"{stem} ({i}){ext}";
        return unique;
    }
}
