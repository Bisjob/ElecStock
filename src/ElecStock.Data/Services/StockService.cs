using System.Reflection;
using ElecStock.Contracts;
using ElecStock.Data.Entities;
using ElecStock.Data.Files;
using ElecStock.Stock;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ElecStock.Data.Services;

/// <summary>
/// The stock on the database: the in-process <see cref="IStockApi"/> of the server, also behind
/// the HTTP API and the MCP tools. Every write is logged in the history with the caller
/// (<see cref="CallerContext"/>) and pushed to the open clients.
/// </summary>
public sealed partial class StockService(
    IDbContextFactory<StockDb> dbFactory,
    CallerContext caller,
    IChangeNotifier notifier,
    FileStore files,
    ImageProcessor images,
    StockImporter importer,
    IHttpClientFactory httpClients,
    IOptions<ServerOptions> serverOptions,
    TimeProvider time) : IStockApi
{
    public string ResolveUrl(string relative) => relative;

    public ActivitySource Source => caller.Source;

    DateTimeOffset Now => time.GetUtcNow();
    DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    Task<StockDb> Db(CancellationToken ct) => dbFactory.CreateDbContextAsync(ct);

    Activity Log(StockDb db, string kind, string summary, Part? part = null, int? projectId = null, int? revisionId = null,
        int? orderId = null, string? qtyBefore = null, string? qtyAfter = null)
    {
        var activity = new Activity
        {
            At = Now,
            Source = caller.Source,
            Kind = kind,
            Summary = summary,
            PartId = part?.Id is > 0 ? part.Id : null,
            PartRef = part?.Ref ?? "",
            ProjectId = projectId,
            RevisionId = revisionId,
            OrderId = orderId,
            QtyBefore = qtyBefore,
            QtyAfter = qtyAfter,
        };
        db.Activities.Add(activity);
        return activity;
    }

    void Notify(ChangeScope scope, int? id = null) => notifier.Publish(new ChangeEvent(scope, id));

    static string Clean(string? value) => (value ?? "").Trim();

    static string RevisionTitle(Revision r) => $"{r.Device.Project.Name} · {r.Device.Name} rev {r.Rev}";

    // ---------- snapshot of the whole stock, for statuses ----------

    /// <summary>
    /// Everything needed to compute availabilities, loaded in one go: the stock is a few hundred
    /// parts, so in-memory aggregation is simpler and faster than clever SQL.
    /// </summary>
    sealed class Snapshot
    {
        public required List<Part> Parts { get; init; }
        public required Dictionary<int, Part> ById { get; init; }
        /// <summary>Allocation lines by part.</summary>
        public required ILookup<int, Allocation> Allocations { get; init; }
        public required ILookup<int, Allocation> ByRevision { get; init; }
        public required ILookup<int, OrderLine> OpenLines { get; init; }
        public required ILookup<int, Photo> Photos { get; init; }
        public required Dictionary<int, OrderLine> LastPriced { get; init; }

        public int Reserved(int partId) => Allocations[partId].Where(a => a.Status == AllocationStatus.Reserved).Sum(a => a.Qty);
        public int Used(int partId) => Allocations[partId].Where(a => a.Status == AllocationStatus.Used).Sum(a => a.Qty);
        public int OnOrder(int partId) => OpenLines[partId].Where(l => l.Order.Status == OrderStatus.Ordered).Sum(l => l.Qty);
        public int Planned(int partId) => OpenLines[partId].Where(l => l.Order.Status == OrderStatus.Draft).Sum(l => l.Qty);
    }

    static async Task<Snapshot> LoadSnapshotAsync(StockDb db, CancellationToken ct)
    {
        var parts = await db.Parts.AsNoTracking().Include(p => p.Location).ToListAsync(ct);
        var allocations = await db.Allocations.AsNoTracking()
            .Include(a => a.Revision).ThenInclude(r => r.Device).ThenInclude(d => d.Project)
            .ToListAsync(ct);
        var lines = await db.OrderLines.AsNoTracking().Include(l => l.Order)
            .Where(l => l.Order.Status != OrderStatus.Cancelled)
            .ToListAsync(ct);
        var photos = await db.Photos.AsNoTracking().Where(p => p.PartId != null).ToListAsync(ct);

        return new Snapshot
        {
            Parts = parts,
            ById = parts.ToDictionary(p => p.Id),
            Allocations = allocations.ToLookup(a => a.PartId),
            ByRevision = allocations.ToLookup(a => a.RevisionId),
            OpenLines = lines.Where(l => l.Order.Status is OrderStatus.Draft or OrderStatus.Ordered).ToLookup(l => l.PartId),
            Photos = photos.ToLookup(p => p.PartId!.Value),
            LastPriced = lines.Where(l => l.UnitPrice != null)
                .GroupBy(l => l.PartId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.Order.OrderedAt ?? DateOnly.FromDateTime(l.Order.CreatedAt.UtcDateTime)).ThenByDescending(l => l.Id).First()),
        };
    }

    static PartSummary Summarize(Part p, Snapshot s)
    {
        var allocations = s.Allocations[p.Id].ToList();
        var photos = s.Photos[p.Id].ToList();
        int reserved = s.Reserved(p.Id);
        var projects = allocations
            .Select(a => RevisionTitle(a.Revision))
            .Distinct()
            .ToList();
        var search = new List<string> { p.Ref, p.Designation, p.Notes, p.Category, p.Location?.Code ?? "", p.Location?.Name ?? "" };
        search.AddRange(projects);
        search.AddRange(allocations.Select(a => a.Designators));
        search.AddRange(photos.Select(ph => ph.Caption));
        search.AddRange(photos.SelectMany(ph => ph.Markers).Select(m => m.Label));

        return new PartSummary
        {
            Id = p.Id,
            Ref = p.Ref,
            Category = p.Category,
            Designation = p.Designation,
            Qty = p.Qty,
            Reserved = reserved,
            Used = s.Used(p.Id),
            OnOrder = s.OnOrder(p.Id),
            Planned = s.Planned(p.Id),
            Status = StockMath.Status(Qty.Parse(p.Qty), reserved),
            LocationId = p.LocationId,
            LocationCode = p.Location?.Code ?? "",
            LocationName = p.Location?.Name ?? "",
            DatasheetUrl = p.DatasheetUrl,
            HasLocalDatasheet = p.DatasheetFile != null,
            HasSheet = !string.IsNullOrWhiteSpace(p.Sheet),
            HasFreeNotes = !string.IsNullOrWhiteSpace(p.FreeNotes),
            Notes = p.Notes,
            PhotoCount = photos.Count,
            CoverThumbUrl = Cover(photos) is { } cover ? ThumbUrl(cover) : null,
            Projects = projects,
            SearchText = string.Join(' ', search.Where(t => t.Length > 0)).ToLowerInvariant(),
            UpdatedAt = p.UpdatedAt,
        };
    }

    /// <summary>The photo marked as cover, else the first one.</summary>
    static Photo? Cover(IEnumerable<Photo> photos) =>
        photos.OrderByDescending(p => p.IsCover).ThenBy(p => p.Id).FirstOrDefault();

    static string ThumbUrl(Photo p) => FileStore.Url(p.ThumbPath.Length > 0 ? p.ThumbPath : p.StoragePath);

    static PhotoDto ToDto(Photo p, bool isCover) => new()
    {
        Id = p.Id,
        Url = FileStore.Url(p.StoragePath),
        ThumbUrl = ThumbUrl(p),
        FileName = p.FileName,
        Caption = p.Caption,
        Markers = p.Markers.Select(m => new PhotoMarker { X = m.X, Y = m.Y, Label = m.Label }).ToList(),
        IsCover = isCover,
        Width = p.Width,
        Height = p.Height,
    };

    static List<PhotoDto> ToDtos(IEnumerable<Photo> photos)
    {
        var list = photos.OrderBy(p => p.Id).ToList();
        var cover = Cover(list);
        return list.Select(p => ToDto(p, p == cover)).ToList();
    }

    static ActivityDto ToDto(Activity a) => new()
    {
        Id = a.Id,
        At = a.At,
        Source = a.Source,
        Kind = a.Kind,
        Summary = a.Summary,
        PartId = a.PartId,
        PartRef = a.PartRef,
        ProjectId = a.ProjectId,
        RevisionId = a.RevisionId,
        OrderId = a.OrderId,
        QtyBefore = a.QtyBefore,
        QtyAfter = a.QtyAfter,
    };

    // ---------- history and administration ----------

    public async Task<List<ActivityDto>> GetActivityAsync(ActivityQuery query, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var q = db.Activities.AsNoTracking();
        if (query.PartId != null) q = q.Where(a => a.PartId == query.PartId);
        if (query.ProjectId != null) q = q.Where(a => a.ProjectId == query.ProjectId);
        if (query.RevisionId != null) q = q.Where(a => a.RevisionId == query.RevisionId);
        if (query.OrderId != null) q = q.Where(a => a.OrderId == query.OrderId);
        if (query.Source != null) q = q.Where(a => a.Source == query.Source);
        var list = await q.OrderByDescending(a => a.At).ThenByDescending(a => a.Id)
            .Take(Math.Clamp(query.Take, 1, 1000))
            .ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    public async Task<ImportReport> ImportAsync(Stream zip, bool replace, CancellationToken ct = default)
    {
        var report = await importer.ImportZipAsync(zip, replace, caller.Source, ct);
        Notify(ChangeScope.All);
        return report;
    }

    public async Task<ServerInfo> GetServerInfoAsync(CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        return new ServerInfo
        {
            Version = typeof(StockService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "",
            PublicBaseUrl = serverOptions.Value.PublicBaseUrl,
            PartCount = await db.Parts.CountAsync(ct),
        };
    }
}
