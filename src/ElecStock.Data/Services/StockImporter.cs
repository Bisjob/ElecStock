using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ElecStock.Contracts;
using ElecStock.Csv;
using ElecStock.Data.Entities;
using ElecStock.Data.Files;
using Microsoft.EntityFrameworkCore;

namespace ElecStock.Data.Services;

/// <summary>
/// Imports a stock folder (zipped, the format of the export or of the earlier desktop app): <c>inventory.csv</c>, <c>allocations.csv</c>,
/// the <c>.md</c> sheets (<c>Components/</c>, <c>Modules/</c>, or <c>Sheets/</c> of an export),
/// <c>Media/&lt;ref&gt;/</c> (photos, <c>media.json</c>, <c>notes.md</c>) and <c>Datasheets/*.pdf</c>.
/// Other folders (KiCad, 3D, Docs…) are ignored.
/// </summary>
public sealed class StockImporter(IDbContextFactory<StockDb> dbFactory, FileStore files, ImageProcessor images, TimeProvider time)
{
    public async Task<ImportReport> ImportZipAsync(Stream zip, bool replace, ActivitySource source, CancellationToken ct)
    {
        // ZipArchive needs a seekable stream; uploads are not.
        await using var buffer = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose);
        await zip.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        return await ImportAsync(new ZipSource(archive), replace, source, ct);
    }

    public async Task<ImportReport> ImportFolderAsync(string folder, bool replace, ActivitySource source, CancellationToken ct) =>
        await ImportAsync(new FolderSource(folder), replace, source, ct);

    async Task<ImportReport> ImportAsync(IImportSource src, bool replace, ActivitySource source, CancellationToken ct)
    {
        var report = new ImportReport();
        var inventoryText = src.ReadText("inventory.csv") ?? throw new StockException("No inventory.csv in the archive.");
        var now = time.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Parts.AnyAsync(ct) || await db.Projects.AnyAsync(ct))
        {
            if (!replace) throw new StockException("The stock is not empty: tick 'Replace the current stock' to import anyway.");
            await ClearAsync(db, ct);
        }

        // ---------- locations and parts ----------
        var locations = new Dictionary<string, Location>(StringComparer.OrdinalIgnoreCase);
        var parts = new Dictionary<string, Part>(StringComparer.OrdinalIgnoreCase);
        var sheetColumn = new Dictionary<Part, string>();
        foreach (var row in CsvText.ParseRecords(inventoryText))
        {
            var reference = row.GetValueOrDefault("ref", "").Trim();
            if (reference.Length == 0) continue;
            if (parts.ContainsKey(reference))
            {
                report.Warnings.Add($"inventory.csv: duplicate ref '{reference}', only the first line is imported.");
                continue;
            }
            var part = new Part
            {
                Ref = reference,
                Category = row.GetValueOrDefault("category", "").ToLowerInvariant(),
                Designation = row.GetValueOrDefault("designation", ""),
                Qty = row.GetValueOrDefault("qty", ""),
                DatasheetUrl = row.GetValueOrDefault("datasheet_url", ""),
                Notes = row.GetValueOrDefault("notes", ""),
                CreatedAt = now,
                UpdatedAt = now,
            };
            var locationText = row.GetValueOrDefault("location", "");
            if (locationText.Length > 0)
            {
                if (!locations.TryGetValue(locationText, out var location))
                {
                    location = new Location { Code = LocationCode(locationText, locations.Values), Name = locationText };
                    locations[locationText] = location;
                    db.Locations.Add(location);
                }
                part.Location = location;
            }
            if (row.GetValueOrDefault("sheet", "") is { Length: > 0 } sheet) sheetColumn[part] = sheet;
            parts[reference] = part;
            db.Parts.Add(part);
        }
        report.Parts = parts.Count;
        report.Locations = locations.Count;

        // ---------- sheets ----------
        var sheetIndex = src.Files()
            .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                && (f.StartsWith("Components/", StringComparison.OrdinalIgnoreCase)
                    || f.StartsWith("Modules/", StringComparison.OrdinalIgnoreCase)
                    || f.StartsWith("Sheets/", StringComparison.OrdinalIgnoreCase)))
            .GroupBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var usedSheets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Values)
        {
            string? path = null;
            if (sheetColumn.TryGetValue(part, out var declared) && src.Exists(declared.TrimStart('.', '/'))) path = declared.TrimStart('.', '/');
            path ??= sheetIndex.GetValueOrDefault(part.Ref);
            if (path == null)
            {
                if (sheetColumn.ContainsKey(part)) report.Warnings.Add($"{part.Ref}: sheet '{sheetColumn[part]}' not found.");
                continue;
            }
            part.Sheet = src.ReadText(path);
            usedSheets.Add(path);
            report.Sheets++;
        }
        foreach (var orphan in sheetIndex.Values.Where(p => !usedSheets.Contains(p) && !Path.GetFileName(p).Equals("README.md", StringComparison.OrdinalIgnoreCase)))
            report.Warnings.Add($"Sheet {orphan} matches no part (not imported).");

        await db.SaveChangesAsync(ct);

        // ---------- media: photos, captions, markers, free notes ----------
        var mediaFolders = src.Files()
            .Where(f => f.StartsWith("Media/", StringComparison.OrdinalIgnoreCase) && f.Count(c => c == '/') == 2)
            .GroupBy(f => f.Split('/')[1], StringComparer.OrdinalIgnoreCase);
        var bySafeName = parts.Values.GroupBy(p => SafeName(p.Ref), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var folder in mediaFolders)
        {
            if (!bySafeName.TryGetValue(folder.Key, out var part))
            {
                report.Warnings.Add($"Media/{folder.Key}: no part with this ref (not imported).");
                continue;
            }
            var doc = ReadMediaDoc(src, $"Media/{folder.Key}/media.json", report);
            if (src.ReadText($"Media/{folder.Key}/notes.md") is { } notes)
            {
                part.FreeNotes = notes.Replace("\r\n", "\n");
                if (notes.Trim().Length > 0) report.FreeNotes++;
            }
            foreach (var file in folder.Where(ImageProcessor.IsImage).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                try
                {
                    await using var stream = src.Open(file);
                    var stored = await images.StoreAsync(stream, name, ct);
                    var meta = doc?.Images.GetValueOrDefault(name);
                    db.Photos.Add(new Photo
                    {
                        PartId = part.Id,
                        FileName = name,
                        StoragePath = stored.StoragePath,
                        ThumbPath = stored.ThumbPath,
                        Width = stored.Width,
                        Height = stored.Height,
                        Caption = meta?.Caption ?? "",
                        Markers = meta?.Markers.Select(m => new Marker { X = m.X, Y = m.Y, Label = m.Label }).ToList() ?? [],
                        IsCover = doc?.Cover != null && doc.Cover.Equals(name, StringComparison.OrdinalIgnoreCase),
                        CreatedAt = now,
                    });
                    report.Photos++;
                }
                catch (StockException ex)
                {
                    report.Warnings.Add($"Media/{folder.Key}/{name}: {ex.Message}");
                }
            }
        }

        // ---------- datasheets ----------
        var pdfs = src.Files()
            .Where(f => f.StartsWith("Datasheets/", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => f, StringComparer.OrdinalIgnoreCase);
        var usedPdfs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Values)
        {
            var pdf = ResolveDatasheet(part.Ref, pdfs);
            if (pdf == null) continue;
            await using var stream = src.Open(pdf);
            part.DatasheetFile = await files.SaveAsync(FileStore.DatasheetsDir, ".pdf", stream, ct);
            usedPdfs.Add(pdf);
            report.Datasheets++;
        }
        foreach (var orphan in pdfs.Values.Where(p => !usedPdfs.Contains(p)))
            report.Warnings.Add($"{orphan} matches no part (not imported).");

        await db.SaveChangesAsync(ct);

        // ---------- projects, devices, revisions, allocations ----------
        if (src.ReadText("allocations.csv") is { } allocationsText)
        {
            var projects = new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase);
            var revisions = new Dictionary<string, Revision>(StringComparer.OrdinalIgnoreCase);
            var lines = new Dictionary<(Revision, int), Allocation>();
            foreach (var row in CsvText.ParseRecords(allocationsText))
            {
                var reference = row.GetValueOrDefault("ref", "");
                if (reference.Length == 0) continue;
                if (!parts.TryGetValue(reference, out var part))
                {
                    report.Warnings.Add($"allocations.csv: '{reference}' is not in inventory.csv (line skipped).");
                    continue;
                }
                var projectName = row.GetValueOrDefault("project", "") is { Length: > 0 } p ? p : "Unsorted";
                var deviceName = row.GetValueOrDefault("device", "") is { Length: > 0 } d ? d : projectName;
                var rev = row.GetValueOrDefault("rev", "") is { Length: > 0 } r ? r : "A";
                if (!projects.TryGetValue(projectName, out var project))
                {
                    project = new Project
                    {
                        Name = projectName,
                        Status = ProjectStatus.Design,
                        CreatedAt = now,
                        UpdatedAt = now,
                    };
                    projects[projectName] = project;
                    db.Projects.Add(project);
                }
                var key = $"{projectName}\u001f{deviceName}\u001f{rev}";
                if (!revisions.TryGetValue(key, out var revision))
                {
                    var device = project.Devices.FirstOrDefault(x => x.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
                    if (device == null)
                    {
                        device = new Device { Name = deviceName };
                        project.Devices.Add(device);
                    }
                    revision = new Revision { Rev = rev, Status = RevisionStatus.Design, CreatedAt = now };
                    device.Revisions.Add(revision);
                    revisions[key] = revision;
                }

                int.TryParse(row.GetValueOrDefault("qty", ""), out var qty);
                var status = row.GetValueOrDefault("status", "").Equals("used", StringComparison.OrdinalIgnoreCase)
                    ? AllocationStatus.Used : AllocationStatus.Reserved;
                var designators = row.GetValueOrDefault("designators", "");
                var notes = row.GetValueOrDefault("notes", "");
                if (lines.TryGetValue((revision, part.Id), out var existing))
                {
                    // One line per part and revision: duplicates of the CSV are merged.
                    existing.Qty += qty;
                    existing.Designators = $"{existing.Designators} {designators}".Trim();
                    existing.Notes = string.Join(" ; ", new[] { existing.Notes, notes }.Where(n => n.Length > 0));
                    report.Warnings.Add($"allocations.csv: two lines for {reference} in {deviceName} rev {rev}, merged.");
                    continue;
                }
                var line = new Allocation { PartId = part.Id, Qty = qty, Status = status, Designators = designators, Notes = notes };
                revision.Allocations.Add(line);
                lines[(revision, part.Id)] = line;
                report.Allocations++;
            }

            // A revision whose lines are all used was built.
            foreach (var revision in revisions.Values)
            {
                if (revision.Allocations.Count > 0 && revision.Allocations.All(a => a.Status == AllocationStatus.Used))
                    revision.Status = RevisionStatus.Built;
            }
            foreach (var project in projects.Values)
            {
                var all = project.Devices.SelectMany(d => d.Revisions).ToList();
                if (all.Count > 0 && all.All(r => r.Status == RevisionStatus.Built)) project.Status = ProjectStatus.Built;
            }
            report.Projects = projects.Count;
            report.Revisions = revisions.Count;
        }

        db.Activities.Add(new Activity
        {
            At = now,
            Source = source,
            Kind = ActivityKinds.Import,
            Summary = $"Stock imported: {report.Parts} parts, {report.Photos} photos, {report.Sheets} sheets, {report.Datasheets} datasheets, "
                + $"{report.Projects} projects, {report.Allocations} allocation lines",
        });
        await db.SaveChangesAsync(ct);
        return report;
    }

    /// <summary>Empties the database (the history included) and moves the stored files to the trash.</summary>
    async Task ClearAsync(StockDb db, CancellationToken ct)
    {
        await db.Allocations.ExecuteDeleteAsync(ct);
        await db.OrderLines.ExecuteDeleteAsync(ct);
        await db.Orders.ExecuteDeleteAsync(ct);
        await db.Photos.ExecuteDeleteAsync(ct);
        await db.Revisions.ExecuteDeleteAsync(ct);
        await db.Devices.ExecuteDeleteAsync(ct);
        await db.Projects.ExecuteDeleteAsync(ct);
        await db.Parts.ExecuteDeleteAsync(ct);
        await db.Locations.ExecuteDeleteAsync(ct);
        await db.Activities.ExecuteDeleteAsync(ct);
        files.TrashAll();
    }

    /// <summary>"TO-220 heatsink kit box" → "TO-220-HEATSINK-KIT-BOX", unique among <paramref name="existing"/>.</summary>
    static string LocationCode(string text, IEnumerable<Location> existing)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.Trim().ToUpperInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        var code = string.Join('-', sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (code.Length > 40) code = code[..40].TrimEnd('-');
        if (code.Length == 0) code = "LOC";
        var taken = existing.Select(l => l.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unique = code;
        for (int i = 2; taken.Contains(unique); i++) unique = $"{code}-{i}";
        return unique;
    }

    /// <summary>Folder name the stock folder uses for a ref.</summary>
    static string SafeName(string reference)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(reference.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        return s.Length == 0 ? "_" : s;
    }

    /// <summary>IRF4905.pdf, else IRF4905-datasheet.pdf, IRF4905 rev2.pdf…</summary>
    static string? ResolveDatasheet(string reference, Dictionary<string, string> pdfs)
    {
        if (pdfs.TryGetValue(reference, out var exact) || pdfs.TryGetValue(SafeName(reference), out exact)) return exact;
        return pdfs
            .Where(kv => kv.Key.Length > reference.Length
                && kv.Key.StartsWith(reference, StringComparison.OrdinalIgnoreCase)
                && " _-.(".Contains(kv.Key[reference.Length]))
            .Select(kv => kv.Value)
            .OrderBy(p => p.Length)
            .FirstOrDefault();
    }

    static MediaDoc? ReadMediaDoc(IImportSource src, string path, ImportReport report)
    {
        var text = src.ReadText(path);
        if (text == null) return null;
        try
        {
            var doc = JsonSerializer.Deserialize<MediaDoc>(text);
            if (doc != null) doc.Images = new Dictionary<string, ImageDoc>(doc.Images, StringComparer.OrdinalIgnoreCase);
            return doc;
        }
        catch (JsonException ex)
        {
            report.Warnings.Add($"{path}: unreadable ({ex.Message}).");
            return null;
        }
    }

    sealed class MediaDoc
    {
        [JsonPropertyName("cover")] public string? Cover { get; set; }
        [JsonPropertyName("images")] public Dictionary<string, ImageDoc> Images { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    sealed class ImageDoc
    {
        [JsonPropertyName("caption")] public string? Caption { get; set; }
        [JsonPropertyName("markers")] public List<MarkerDoc> Markers { get; set; } = [];
    }

    sealed class MarkerDoc
    {
        [JsonPropertyName("x")] public double X { get; set; }
        [JsonPropertyName("y")] public double Y { get; set; }
        [JsonPropertyName("label")] public string Label { get; set; } = "";
    }

    // ---------- sources: a zip or a folder, with paths relative to the stock root ----------

    interface IImportSource
    {
        IEnumerable<string> Files();
        bool Exists(string path);
        string? ReadText(string path);
        Stream Open(string path);
    }

    sealed class ZipSource : IImportSource
    {
        readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public ZipSource(ZipArchive archive)
        {
            // The stock may be zipped with its folder (stock/inventory.csv): paths start after it.
            var files = archive.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
            var inventory = files
                .Where(e => e.FullName.Replace('\\', '/').Split('/')[^1].Equals("inventory.csv", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName.Length)
                .FirstOrDefault();
            var prefix = inventory == null ? "" : inventory.FullName.Replace('\\', '/')[..^"inventory.csv".Length];
            foreach (var entry in files)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) _entries.TryAdd(name[prefix.Length..], entry);
            }
        }

        public IEnumerable<string> Files() => _entries.Keys;
        public bool Exists(string path) => _entries.ContainsKey(path);

        public string? ReadText(string path)
        {
            if (!_entries.TryGetValue(path, out var entry)) return null;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }

        public Stream Open(string path) => _entries[path].Open();
    }

    sealed class FolderSource(string root) : IImportSource
    {
        public IEnumerable<string> Files() =>
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .Where(f => !f.StartsWith("3D/", StringComparison.OrdinalIgnoreCase) && !f.StartsWith(".git/", StringComparison.OrdinalIgnoreCase));

        string Full(string path) => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        public bool Exists(string path) => File.Exists(Full(path));
        public string? ReadText(string path) => File.Exists(Full(path)) ? File.ReadAllText(Full(path)) : null;
        public Stream Open(string path) => File.OpenRead(Full(path));
    }
}
