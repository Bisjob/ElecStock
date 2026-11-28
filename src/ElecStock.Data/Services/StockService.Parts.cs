using ElecStock.Contracts;
using ElecStock.Data.Entities;
using ElecStock.Data.Files;
using ElecStock.Stock;
using Microsoft.EntityFrameworkCore;

namespace ElecStock.Data.Services;

public sealed partial class StockService
{
    const int MaxDatasheetBytes = 64 * 1024 * 1024;

    public async Task<StockOverview> GetStockAsync(CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var snapshot = await LoadSnapshotAsync(db, ct);
        return new StockOverview
        {
            Parts = snapshot.Parts.Select(p => Summarize(p, snapshot)).ToList(),
            Locations = await LocationDtosAsync(db, ct),
        };
    }

    public async Task<PartDetail?> GetPartAsync(int id, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var snapshot = await LoadSnapshotAsync(db, ct);
        var part = snapshot.Parts.FirstOrDefault(p => p.Id == id);
        return part == null ? null : await DetailAsync(db, part, snapshot, ct);
    }

    public async Task<PartDetail?> GetPartByRefAsync(string reference, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var trimmed = Clean(reference);
        var id = await db.Parts.Where(p => p.Ref == trimmed).Select(p => (int?)p.Id).FirstOrDefaultAsync(ct);
        return id == null ? null : await GetPartAsync(id.Value, ct);
    }

    async Task<PartDetail> DetailAsync(StockDb db, Part part, Snapshot snapshot, CancellationToken ct)
    {
        var orderLines = await db.OrderLines.AsNoTracking().Include(l => l.Order)
            .Where(l => l.PartId == part.Id)
            .OrderByDescending(l => l.Order.CreatedAt)
            .Take(30)
            .ToListAsync(ct);
        var history = await db.Activities.AsNoTracking()
            .Where(a => a.PartId == part.Id)
            .OrderByDescending(a => a.At).ThenByDescending(a => a.Id)
            .Take(50)
            .ToListAsync(ct);

        return new PartDetail
        {
            Part = Summarize(part, snapshot),
            Sheet = part.Sheet,
            FreeNotes = part.FreeNotes,
            DatasheetFileUrl = part.DatasheetFile != null ? FileStore.Url(part.DatasheetFile) : null,
            Photos = ToDtos(snapshot.Photos[part.Id]),
            Allocations = snapshot.Allocations[part.Id]
                .OrderBy(a => a.Revision.Device.Project.Name).ThenBy(a => a.Revision.Device.Name).ThenBy(a => a.Revision.Rev)
                .Select(a => new PartAllocation
                {
                    Id = a.Id,
                    RevisionId = a.RevisionId,
                    ProjectId = a.Revision.Device.ProjectId,
                    Project = a.Revision.Device.Project.Name,
                    Device = a.Revision.Device.Name,
                    Rev = a.Revision.Rev,
                    RevisionStatus = a.Revision.Status,
                    Designators = a.Designators,
                    Qty = a.Qty,
                    Status = a.Status,
                    Notes = a.Notes,
                })
                .ToList(),
            OrderLines = orderLines.Select(l => new PartOrderLine
            {
                OrderId = l.OrderId,
                LineId = l.Id,
                Supplier = l.Order.Supplier,
                OrderNumber = l.Order.OrderNumber,
                Status = l.Order.Status,
                Qty = l.ReceivedQty ?? l.Qty,
                UnitPrice = l.UnitPrice,
                Currency = l.Order.Currency,
                OrderedAt = l.Order.OrderedAt,
                ReceivedAt = l.Order.ReceivedAt,
            }).ToList(),
            History = history.Select(ToDto).ToList(),
        };
    }

    public async Task<PartDetail> SavePartAsync(PartEdit edit, CancellationToken ct = default)
    {
        var reference = Clean(edit.Ref);
        if (reference.Length == 0) throw new StockException("The ref is required.");
        if (reference.Any(char.IsWhiteSpace)) throw new StockException("A ref has no spaces: use '-' (R-4k7, JST-XH-B3B).");
        if (reference.Length > 100) throw new StockException("A ref is at most 100 characters.");

        await using var db = await Db(ct);
        if (await db.Parts.AnyAsync(p => p.Ref == reference && p.Id != (edit.Id ?? 0), ct))
            throw new StockException($"'{reference}' already exists.");
        if (edit.LocationId != null && !await db.Locations.AnyAsync(l => l.Id == edit.LocationId, ct))
            throw new StockException("This location no longer exists.");
        if (edit.SpareLocationId != null)
        {
            if (edit.SpareLocationId == edit.LocationId) throw new StockException("The spare location is the main location: pick another one.");
            if (!await db.Locations.AnyAsync(l => l.Id == edit.SpareLocationId, ct)) throw new StockException("The spare location no longer exists.");
        }
        var links = await CheckLinksAsync(db, edit, ct);

        Part part;
        if (edit.Id == null)
        {
            part = new Part { Ref = reference, CreatedAt = Now };
            db.Parts.Add(part);
        }
        else
        {
            part = await db.Parts.FirstOrDefaultAsync(p => p.Id == edit.Id, ct)
                ?? throw new StockException("This part no longer exists.");
        }

        var changed = new List<string>();
        void Set(string name, string before, string after, Action<string> apply)
        {
            if (before == after) return;
            changed.Add(name);
            apply(after);
        }

        var oldRef = part.Ref;
        var qtyBefore = part.Qty;
        Set("ref", part.Ref, reference, v => part.Ref = v);
        Set("category", part.Category, Clean(edit.Category).ToLowerInvariant(), v => part.Category = v);
        Set("designation", part.Designation, Clean(edit.Designation), v => part.Designation = v);
        Set("datasheet link", part.DatasheetUrl, Clean(edit.DatasheetUrl), v => part.DatasheetUrl = v);
        Set("notes", part.Notes, Clean(edit.Notes), v => part.Notes = v);
        Set("KiCad symbol", part.KicadSymbol, links.Symbol, v => part.KicadSymbol = v);
        Set("KiCad footprint", part.KicadFootprint, links.Footprint, v => part.KicadFootprint = v);
        Set("3D model", part.Kicad3dModel, links.Model, v => part.Kicad3dModel = v);
        if (part.LocationId != edit.LocationId)
        {
            changed.Add("location");
            part.LocationId = edit.LocationId;
        }
        if (part.SpareLocationId != edit.SpareLocationId)
        {
            changed.Add("spare location");
            part.SpareLocationId = edit.SpareLocationId;
        }
        part.Qty = Clean(edit.Qty);
        part.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);

        if (edit.Id == null)
        {
            Log(db, ActivityKinds.PartCreated, $"Added {part.Ref} ({part.Designation}), qty {StockMath.QtyDisplay(Qty.Parse(part.Qty))}",
                part, qtyAfter: part.Qty);
        }
        else
        {
            if (qtyBefore != part.Qty)
            {
                var reason = Clean(edit.QtyReason);
                Log(db, ActivityKinds.QtyChanged,
                    $"{part.Ref}: qty {StockMath.QtyDisplay(Qty.Parse(qtyBefore))} → {StockMath.QtyDisplay(Qty.Parse(part.Qty))}" + (reason.Length > 0 ? $" ({reason})" : ""),
                    part, qtyBefore: qtyBefore, qtyAfter: part.Qty);
            }
            if (changed.Count > 0)
            {
                var renamed = changed.Contains("ref") ? $" (renamed from {oldRef})" : "";
                Log(db, ActivityKinds.PartUpdated, $"{part.Ref}: changed {string.Join(", ", changed)}{renamed}", part);
            }
        }
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.Part, part.Id);
        return (await GetPartAsync(part.Id, ct))!;
    }

    public async Task DeletePartAsync(int id, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var part = await db.Parts.Include(p => p.Photos).FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new StockException("This part no longer exists.");
        int lines = await db.Allocations.CountAsync(a => a.PartId == id, ct);
        if (lines > 0) throw new StockException($"{part.Ref} is on {lines} project line(s): remove them first.");
        int orderLines = await db.OrderLines.CountAsync(l => l.PartId == id, ct);
        if (orderLines > 0) throw new StockException($"{part.Ref} is on {orderLines} order line(s): remove them first.");

        foreach (var photo in part.Photos)
        {
            files.Trash(photo.StoragePath);
            files.Trash(photo.ThumbPath);
        }
        files.Trash(part.DatasheetFile);
        Log(db, ActivityKinds.PartDeleted, $"Deleted {part.Ref} ({part.Designation}), qty was {StockMath.QtyDisplay(Qty.Parse(part.Qty))}",
            part, qtyBefore: part.Qty);
        db.Parts.Remove(part);
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.Part, id);
    }

    public async Task SaveSheetAsync(int partId, string? markdown, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == partId, ct)
            ?? throw new StockException("This part no longer exists.");
        var sheet = string.IsNullOrWhiteSpace(markdown) ? null : markdown.Replace("\r\n", "\n");
        if (sheet == part.Sheet) return;
        var created = part.Sheet == null;
        part.Sheet = sheet;
        part.UpdatedAt = Now;
        Log(db, ActivityKinds.SheetUpdated, sheet == null ? $"{part.Ref}: sheet removed" : created ? $"{part.Ref}: sheet created" : $"{part.Ref}: sheet edited", part);
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.Part, partId);
    }

    public async Task SaveFreeNotesAsync(int partId, string markdown, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == partId, ct)
            ?? throw new StockException("This part no longer exists.");
        var notes = (markdown ?? "").Replace("\r\n", "\n");
        if (notes == part.FreeNotes) return;
        part.FreeNotes = notes;
        part.UpdatedAt = Now;

        // Notes are saved as you type: one history line per editing session, not per keystroke.
        var last = await db.Activities.Where(a => a.PartId == partId).OrderByDescending(a => a.Id).FirstOrDefaultAsync(ct);
        if (last != null && last.Kind == ActivityKinds.NotesUpdated && last.Source == caller.Source && Now - last.At < TimeSpan.FromMinutes(15))
            last.At = Now;
        else
            Log(db, ActivityKinds.NotesUpdated, $"{part.Ref}: notes edited", part);
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.Part, partId);
    }

    // ---------- photos ----------

    public async Task<PhotoDto> UploadPhotoAsync(PhotoOwner owner, int ownerId, string fileName, Stream content, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var photo = new Photo { StoragePath = "", FileName = Path.GetFileName(fileName), CreatedAt = Now };
        Part? part = null;
        int? projectId = null, revisionId = null;
        string title;
        switch (owner)
        {
            case PhotoOwner.Part:
                part = await db.Parts.FirstOrDefaultAsync(p => p.Id == ownerId, ct) ?? throw new StockException("This part no longer exists.");
                photo.PartId = part.Id;
                title = part.Ref;
                break;
            case PhotoOwner.Project:
                var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == ownerId, ct) ?? throw new StockException("This project no longer exists.");
                photo.ProjectId = projectId = project.Id;
                title = project.Name;
                break;
            default:
                var revision = await db.Revisions.Include(r => r.Device).ThenInclude(d => d.Project).FirstOrDefaultAsync(r => r.Id == ownerId, ct)
                    ?? throw new StockException("This revision no longer exists.");
                photo.RevisionId = revisionId = revision.Id;
                projectId = revision.Device.ProjectId;
                title = RevisionTitle(revision);
                break;
        }

        var stored = await images.StoreAsync(content, fileName, ct);
        photo.StoragePath = stored.StoragePath;
        photo.ThumbPath = stored.ThumbPath;
        photo.Width = stored.Width;
        photo.Height = stored.Height;
        db.Photos.Add(photo);
        Log(db, ActivityKinds.PhotoAdded, $"Photo added to {title}", part, projectId, revisionId);
        await db.SaveChangesAsync(ct);
        NotifyPhoto(photo, projectId);
        return await PhotoDtoAsync(db, photo, ct);
    }

    public async Task<PhotoDto> UpdatePhotoAsync(int photoId, PhotoEdit edit, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var photo = await db.Photos.FirstOrDefaultAsync(p => p.Id == photoId, ct) ?? throw new StockException("This photo no longer exists.");
        photo.Caption = Clean(edit.Caption);
        photo.Markers = edit.Markers
            .Select(m => new Marker { X = Math.Clamp(m.X, 0, 1), Y = Math.Clamp(m.Y, 0, 1), Label = Clean(m.Label) })
            .ToList();
        await db.SaveChangesAsync(ct);
        NotifyPhoto(photo, await PhotoProjectIdAsync(db, photo, ct));
        return await PhotoDtoAsync(db, photo, ct);
    }

    public async Task SetCoverAsync(int photoId, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var photo = await db.Photos.FirstOrDefaultAsync(p => p.Id == photoId, ct) ?? throw new StockException("This photo no longer exists.");
        foreach (var sibling in await Siblings(db, photo).ToListAsync(ct))
            sibling.IsCover = sibling.Id == photoId;
        await db.SaveChangesAsync(ct);
        NotifyPhoto(photo, await PhotoProjectIdAsync(db, photo, ct));
    }

    public async Task DeletePhotoAsync(int photoId, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var photo = await db.Photos.Include(p => p.Part).FirstOrDefaultAsync(p => p.Id == photoId, ct)
            ?? throw new StockException("This photo no longer exists.");
        var projectId = await PhotoProjectIdAsync(db, photo, ct);
        files.Trash(photo.StoragePath);
        files.Trash(photo.ThumbPath);
        db.Photos.Remove(photo);
        Log(db, ActivityKinds.PhotoRemoved, $"Photo {photo.FileName} removed" + (photo.Part != null ? $" from {photo.Part.Ref}" : ""),
            photo.Part, projectId, photo.RevisionId);
        await db.SaveChangesAsync(ct);
        NotifyPhoto(photo, projectId);
    }

    static IQueryable<Photo> Siblings(StockDb db, Photo photo) =>
        db.Photos.Where(p => p.PartId == photo.PartId && p.ProjectId == photo.ProjectId && p.RevisionId == photo.RevisionId);

    static async Task<PhotoDto> PhotoDtoAsync(StockDb db, Photo photo, CancellationToken ct)
    {
        var siblings = await Siblings(db, photo).AsNoTracking().ToListAsync(ct);
        return ToDtos(siblings).First(p => p.Id == photo.Id);
    }

    static async Task<int?> PhotoProjectIdAsync(StockDb db, Photo photo, CancellationToken ct) =>
        photo.ProjectId ?? (photo.RevisionId == null ? null
            : await db.Revisions.Where(r => r.Id == photo.RevisionId).Select(r => (int?)r.Device.ProjectId).FirstOrDefaultAsync(ct));

    void NotifyPhoto(Photo photo, int? projectId)
    {
        if (photo.PartId != null) Notify(ChangeScope.Part, photo.PartId);
        else Notify(ChangeScope.Project, projectId);
    }

    // ---------- datasheets ----------

    public async Task UploadDatasheetAsync(int partId, string fileName, Stream content, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxDatasheetBytes) throw new StockException("The PDF is larger than 64 MB.");
        await StoreDatasheetAsync(partId, buffer.ToArray(), $"attached {Path.GetFileName(fileName)}", ct);
    }

    public async Task FetchDatasheetAsync(int partId, CancellationToken ct = default)
    {
        string url;
        await using (var db = await Db(ct))
        {
            url = await db.Parts.Where(p => p.Id == partId).Select(p => p.DatasheetUrl).FirstOrDefaultAsync(ct)
                ?? throw new StockException("This part no longer exists.");
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new StockException("The part has no datasheet link to download.");

        byte[] bytes;
        try
        {
            var client = httpClients.CreateClient(DatasheetClient);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxDatasheetBytes) throw new StockException("The PDF is larger than 64 MB.");
            bytes = await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new StockException($"Download failed: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new StockException("Download timed out.");
        }
        await StoreDatasheetAsync(partId, bytes, $"downloaded from {uri.Host}", ct);
    }

    public const string DatasheetClient = "datasheets";

    async Task StoreDatasheetAsync(int partId, byte[] bytes, string how, CancellationToken ct)
    {
        if (bytes.Length < 5 || bytes[0] != '%' || bytes[1] != 'P' || bytes[2] != 'D' || bytes[3] != 'F')
            throw new StockException("This is not a PDF (a link to a web page?): download the PDF by hand and attach it.");

        await using var db = await Db(ct);
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == partId, ct) ?? throw new StockException("This part no longer exists.");
        var old = part.DatasheetFile;
        part.DatasheetFile = await files.SaveAsync(FileStore.DatasheetsDir, ".pdf", bytes, ct);
        part.UpdatedAt = Now;
        Log(db, ActivityKinds.DatasheetUpdated, $"{part.Ref}: offline datasheet {how}", part);
        await db.SaveChangesAsync(ct);
        files.Trash(old);
        Notify(ChangeScope.Part, partId);
    }

    public async Task DeleteDatasheetAsync(int partId, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == partId, ct) ?? throw new StockException("This part no longer exists.");
        if (part.DatasheetFile == null) return;
        files.Trash(part.DatasheetFile);
        part.DatasheetFile = null;
        part.UpdatedAt = Now;
        Log(db, ActivityKinds.DatasheetUpdated, $"{part.Ref}: offline datasheet removed", part);
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.Part, partId);
    }
}
