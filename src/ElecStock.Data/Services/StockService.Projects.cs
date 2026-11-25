using ElecStock.Bom;
using ElecStock.Contracts;
using System.Xml;
using ElecStock.Data.Entities;
using ElecStock.Data.Files;
using ElecStock.Stock;
using Microsoft.EntityFrameworkCore;

namespace ElecStock.Data.Services;

public sealed partial class StockService
{
    /// <summary>Active projects first.</summary>
    static int StatusRank(ProjectStatus status) => status switch
    {
        ProjectStatus.Design => 0,
        ProjectStatus.Prototype => 1,
        ProjectStatus.Idea => 2,
        ProjectStatus.Built => 3,
        _ => 4,
    };

    static LineFlag Flag(Allocation a, Snapshot s)
    {
        if (a.Status == AllocationStatus.Used) return LineFlag.Used;
        var part = s.ById[a.PartId];
        var qty = Qty.Parse(part.Qty);
        int reserved = s.Reserved(part.Id);
        return StockMath.Status(qty, reserved) switch
        {
            StockStatus.Short => s.OnOrder(part.Id) >= StockMath.Shortage(qty, reserved) ? LineFlag.OnOrder : LineFlag.Short,
            StockStatus.Unknown or StockStatus.Check => LineFlag.Uncounted,
            _ => LineFlag.Ok,
        };
    }

    static RevisionSummary SummarizeRevision(Revision r, Snapshot s, int photoCount)
    {
        var lines = s.ByRevision[r.Id].ToList();
        var flags = lines.Select(a => Flag(a, s)).ToList();
        return new RevisionSummary
        {
            Id = r.Id,
            Rev = r.Rev,
            Status = r.Status,
            LineCount = lines.Count,
            ReservedLines = lines.Count(a => a.Status == AllocationStatus.Reserved),
            UsedLines = lines.Count(a => a.Status == AllocationStatus.Used),
            ShortLines = flags.Count(f => f == LineFlag.Short),
            OnOrderLines = flags.Count(f => f == LineFlag.OnOrder),
            UncountedLines = flags.Count(f => f == LineFlag.Uncounted),
            PhotoCount = photoCount,
        };
    }

    async Task<(List<Photo> Photos, Dictionary<int, int> RevisionPhotoCounts)> ProjectPhotosAsync(StockDb db, int? projectId, CancellationToken ct)
    {
        var photos = await db.Photos.AsNoTracking()
            .Where(p => p.ProjectId != null || p.RevisionId != null)
            .Where(p => projectId == null || p.ProjectId == projectId || p.Revision!.Device.ProjectId == projectId)
            .ToListAsync(ct);
        var counts = photos.Where(p => p.RevisionId != null).GroupBy(p => p.RevisionId!.Value).ToDictionary(g => g.Key, g => g.Count());
        return (photos, counts);
    }

    public async Task<List<ProjectSummary>> GetProjectsAsync(CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var snapshot = await LoadSnapshotAsync(db, ct);
        var projects = await db.Projects.AsNoTracking().Include(p => p.Devices).ThenInclude(d => d.Revisions).ToListAsync(ct);
        var (photos, counts) = await ProjectPhotosAsync(db, null, ct);

        return projects
            .OrderBy(p => StatusRank(p.Status)).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new ProjectSummary
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                Status = p.Status,
                UpdatedAt = p.UpdatedAt,
                IconUrl = p.IconPath is { } icon ? FileStore.Url(icon) : null,
                CoverThumbUrl = Cover(photos.Where(ph => ph.ProjectId == p.Id)) is { } cover ? ThumbUrl(cover) : null,
                Devices = p.Devices.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(d => new DeviceSummary
                {
                    Id = d.Id,
                    Name = d.Name,
                    Description = d.Description,
                    Revisions = d.Revisions.OrderBy(r => r.Rev, StringComparer.OrdinalIgnoreCase)
                        .Select(r => SummarizeRevision(r, snapshot, counts.GetValueOrDefault(r.Id)))
                        .ToList(),
                }).ToList(),
            })
            .ToList();
    }

    public async Task<ProjectDetail?> GetProjectAsync(int id, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var project = await db.Projects.AsNoTracking().Include(p => p.Devices).ThenInclude(d => d.Revisions)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (project == null) return null;
        var snapshot = await LoadSnapshotAsync(db, ct);
        var (photos, counts) = await ProjectPhotosAsync(db, id, ct);

        return new ProjectDetail
        {
            Id = project.Id,
            Name = project.Name,
            Description = project.Description,
            Status = project.Status,
            RepoPath = project.RepoPath,
            Url = project.Url,
            Notes = project.Notes,
            IconUrl = project.IconPath is { } icon ? FileStore.Url(icon) : null,
            CreatedAt = project.CreatedAt,
            UpdatedAt = project.UpdatedAt,
            Photos = ToDtos(photos.Where(p => p.ProjectId == id)),
            Devices = project.Devices.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(d => new DeviceDetail
            {
                Id = d.Id,
                Name = d.Name,
                Description = d.Description,
                Path = d.Path,
                Notes = d.Notes,
                Revisions = d.Revisions.OrderBy(r => r.Rev, StringComparer.OrdinalIgnoreCase)
                    .Select(r => SummarizeRevision(r, snapshot, counts.GetValueOrDefault(r.Id)))
                    .ToList(),
            }).ToList(),
        };
    }

    public async Task<ProjectDetail> SaveProjectAsync(ProjectEdit edit, CancellationToken ct = default)
    {
        var name = Clean(edit.Name);
        if (name.Length == 0) throw new StockException("The project name is required.");
        await using var db = await Db(ct);
        if (await db.Projects.AnyAsync(p => p.Name == name && p.Id != (edit.Id ?? 0), ct))
            throw new StockException($"A project named '{name}' already exists.");

        Project project;
        string summary;
        if (edit.Id == null)
        {
            project = new Project { Name = name, CreatedAt = Now };
            db.Projects.Add(project);
            summary = $"Created project {name}";
        }
        else
        {
            project = await db.Projects.FirstOrDefaultAsync(p => p.Id == edit.Id, ct) ?? throw new StockException("This project no longer exists.");
            summary = project.Status != edit.Status ? $"{name}: status {project.Status} → {edit.Status}" : $"{name}: project details edited";
        }
        project.Name = name;
        project.Description = Clean(edit.Description);
        project.Status = edit.Status;
        project.RepoPath = Clean(edit.RepoPath);
        project.Url = Clean(edit.Url);
        project.Notes = (edit.Notes ?? "").Replace("\r\n", "\n");
        project.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        Log(db, ActivityKinds.ProjectChanged, summary, projectId: project.Id);
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.Project, project.Id);
        return (await GetProjectAsync(project.Id, ct))!;
    }

    public async Task DeleteProjectAsync(int id, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var project = await db.Projects.Include(p => p.Devices).ThenInclude(d => d.Revisions).ThenInclude(r => r.Allocations)
            .FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new StockException("This project no longer exists.");
        var revisionIds = project.Devices.SelectMany(d => d.Revisions).Select(r => r.Id).ToList();
        int released = project.Devices.SelectMany(d => d.Revisions).SelectMany(r => r.Allocations).Count(a => a.Status == AllocationStatus.Reserved);
        await TrashPhotosAsync(db, p => p.ProjectId == id || (p.RevisionId != null && revisionIds.Contains(p.RevisionId.Value)), ct);
        files.Trash(project.IconPath);
        db.Projects.Remove(project);
        Log(db, ActivityKinds.ProjectDeleted, $"Deleted project {project.Name}" + (released > 0 ? $" ({released} reserved line(s) released)" : ""), projectId: id);
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.All);
    }

    const long MaxIconBytes = 2 * 1024 * 1024;
    const int IconSide = 256;

    /// <summary>Replaces the project icon: an SVG is kept as is, another image is shrunk to a PNG. The old icon goes to the trash folder.</summary>
    public async Task<ProjectDetail> SetProjectIconAsync(int projectId, string fileName, Stream content, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct) ?? throw new StockException("This project no longer exists.");
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxIconBytes) throw new StockException("The icon is larger than 2 MB.");
        var bytes = buffer.ToArray();

        string path;
        if (Path.GetExtension(fileName).Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            CheckSvg(bytes, fileName);
            path = await files.SaveAsync(FileStore.IconsDir, ".svg", bytes, ct);
        }
        else
        {
            path = await images.StoreIconAsync(bytes, fileName, IconSide, ct);
        }
        files.Trash(project.IconPath);
        project.IconPath = path;
        project.UpdatedAt = Now;
        Log(db, ActivityKinds.ProjectChanged, $"{project.Name}: icon changed", projectId: project.Id);
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.Project, project.Id);
        return (await GetProjectAsync(project.Id, ct))!;
    }

    public async Task<ProjectDetail> RemoveProjectIconAsync(int projectId, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct) ?? throw new StockException("This project no longer exists.");
        if (project.IconPath != null)
        {
            files.Trash(project.IconPath);
            project.IconPath = null;
            project.UpdatedAt = Now;
            Log(db, ActivityKinds.ProjectChanged, $"{project.Name}: icon removed", projectId: project.Id);
            await db.SaveChangesAsync(ct);
            Notify(ChangeScope.Project, project.Id);
        }
        return (await GetProjectAsync(project.Id, ct))!;
    }

    /// <summary>An XML document whose root is &lt;svg&gt; (DTDs skipped, nothing resolved).</summary>
    static void CheckSvg(byte[] bytes, string fileName)
    {
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            if (reader.MoveToContent() == XmlNodeType.Element && reader.LocalName == "svg") return;
        }
        catch (XmlException) { }
        throw new StockException($"'{fileName}' is not an SVG image.");
    }

    async Task TrashPhotosAsync(StockDb db, System.Linq.Expressions.Expression<Func<Photo, bool>> filter, CancellationToken ct)
    {
        foreach (var photo in await db.Photos.Where(filter).ToListAsync(ct))
        {
            files.Trash(photo.StoragePath);
            files.Trash(photo.ThumbPath);
            db.Photos.Remove(photo);
        }
    }

    public async Task<ProjectDetail> SaveDeviceAsync(DeviceEdit edit, CancellationToken ct = default)
    {
        var name = Clean(edit.Name);
        if (name.Length == 0) throw new StockException("The device name is required.");
        await using var db = await Db(ct);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == edit.ProjectId, ct) ?? throw new StockException("This project no longer exists.");
        if (await db.Devices.AnyAsync(d => d.ProjectId == edit.ProjectId && d.Name == name && d.Id != (edit.Id ?? 0), ct))
            throw new StockException($"{project.Name} already has a device named '{name}'.");

        Device device;
        if (edit.Id == null)
        {
            device = new Device { Name = name, ProjectId = project.Id };
            // A new device starts with its first revision, so parts can be reserved right away.
            device.Revisions.Add(new Revision { Rev = "A", Status = RevisionStatus.Design, CreatedAt = Now });
            db.Devices.Add(device);
            Log(db, ActivityKinds.ProjectChanged, $"{project.Name}: added device {name}", projectId: project.Id);
        }
        else
        {
            device = await db.Devices.FirstOrDefaultAsync(d => d.Id == edit.Id, ct) ?? throw new StockException("This device no longer exists.");
            Log(db, ActivityKinds.ProjectChanged, $"{project.Name}: device {name} edited", projectId: project.Id);
        }
        device.Name = name;
        device.Description = Clean(edit.Description);
        device.Path = Clean(edit.Path).Replace('\\', '/');
        device.Notes = (edit.Notes ?? "").Replace("\r\n", "\n");
        project.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.Project, project.Id);
        return (await GetProjectAsync(project.Id, ct))!;
    }

    public async Task<ProjectDetail> DeleteDeviceAsync(int id, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var device = await db.Devices.Include(d => d.Project).Include(d => d.Revisions)
            .FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new StockException("This device no longer exists.");
        var revisionIds = device.Revisions.Select(r => r.Id).ToList();
        await TrashPhotosAsync(db, p => p.RevisionId != null && revisionIds.Contains(p.RevisionId.Value), ct);
        db.Devices.Remove(device);
        device.Project.UpdatedAt = Now;
        Log(db, ActivityKinds.ProjectChanged, $"{device.Project.Name}: deleted device {device.Name}", projectId: device.ProjectId);
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.All);
        return (await GetProjectAsync(device.ProjectId, ct))!;
    }

    // ---------- revisions and their lines ----------

    public async Task<RevisionDetail?> GetRevisionAsync(int id, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var revision = await db.Revisions.AsNoTracking()
            .Include(r => r.Device).ThenInclude(d => d.Project)
            .Include(r => r.Photos)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (revision == null) return null;
        var s = await LoadSnapshotAsync(db, ct);

        var lines = s.ByRevision[id]
            .OrderBy(a => a.Designators.Length == 0 ? 1 : 0)
            .ThenBy(a => a.Designators, Comparer<string>.Create(PartVisuals.CompareNatural))
            .ThenBy(a => s.ById[a.PartId].Ref, PartVisuals.RefComparer)
            .Select(a =>
            {
                var part = s.ById[a.PartId];
                return new AllocationLine
                {
                    Id = a.Id,
                    PartId = part.Id,
                    Ref = part.Ref,
                    Category = part.Category,
                    Designation = part.Designation,
                    Designators = a.Designators,
                    Qty = a.Qty,
                    Status = a.Status,
                    Notes = a.Notes,
                    Flag = Flag(a, s),
                    StockText = StockMath.AvailableLong(Qty.Parse(part.Qty), s.Reserved(part.Id)),
                    UnitPrice = s.LastPriced.GetValueOrDefault(part.Id)?.UnitPrice,
                    CoverThumbUrl = Cover(s.Photos[part.Id]) is { } cover ? ThumbUrl(cover) : null,
                };
            })
            .ToList();
        var priced = lines.Where(l => l.UnitPrice != null).ToList();
        var currency = lines.Select(l => s.LastPriced.GetValueOrDefault(l.PartId)?.Order.Currency).FirstOrDefault(c => c != null) ?? "EUR";

        return new RevisionDetail
        {
            Id = revision.Id,
            Rev = revision.Rev,
            Status = revision.Status,
            Notes = revision.Notes,
            PcbOrderedAt = revision.PcbOrderedAt,
            PcbReceivedAt = revision.PcbReceivedAt,
            BuiltAt = revision.BuiltAt,
            CreatedAt = revision.CreatedAt,
            DeviceId = revision.DeviceId,
            Device = revision.Device.Name,
            DevicePath = revision.Device.Path,
            ProjectId = revision.Device.ProjectId,
            Project = revision.Device.Project.Name,
            Lines = lines,
            Photos = ToDtos(revision.Photos),
            EstimatedCost = priced.Count > 0 ? priced.Sum(l => l.Qty * l.UnitPrice!.Value) : null,
            Currency = currency,
            PricedLines = priced.Count,
        };
    }

    public async Task<RevisionDetail> SaveRevisionAsync(RevisionEdit edit, CancellationToken ct = default)
    {
        var rev = Clean(edit.Rev);
        if (rev.Length == 0) throw new StockException("The revision is required (A, B…).");
        if (rev.Length > 20) throw new StockException("A revision is at most 20 characters.");
        await using var db = await Db(ct);
        var device = await db.Devices.Include(d => d.Project).FirstOrDefaultAsync(d => d.Id == edit.DeviceId, ct)
            ?? throw new StockException("This device no longer exists.");
        if (await db.Revisions.AnyAsync(r => r.DeviceId == edit.DeviceId && r.Rev == rev && r.Id != (edit.Id ?? 0), ct))
            throw new StockException($"{device.Name} already has a rev {rev}.");

        Revision revision;
        RevisionStatus? before = null;
        if (edit.Id == null)
        {
            revision = new Revision { Rev = rev, DeviceId = device.Id, CreatedAt = Now };
            db.Revisions.Add(revision);
        }
        else
        {
            revision = await db.Revisions.FirstOrDefaultAsync(r => r.Id == edit.Id, ct) ?? throw new StockException("This revision no longer exists.");
            before = revision.Status;
            if (edit.Status == RevisionStatus.Built && revision.Status != RevisionStatus.Built
                && await db.Allocations.AnyAsync(a => a.RevisionId == revision.Id && a.Status == AllocationStatus.Reserved, ct))
                throw new StockException("Use 'Mark as built' so that the reserved parts are taken out of the stock.");
        }

        revision.Rev = rev;
        revision.Status = edit.Status;
        revision.Notes = (edit.Notes ?? "").Replace("\r\n", "\n");
        revision.PcbOrderedAt = edit.PcbOrderedAt;
        revision.PcbReceivedAt = edit.PcbReceivedAt;
        revision.BuiltAt = edit.BuiltAt;
        if (before != edit.Status)
        {
            switch (edit.Status)
            {
                case RevisionStatus.PcbOrdered: revision.PcbOrderedAt ??= Today; break;
                case RevisionStatus.PcbReceived: revision.PcbReceivedAt ??= Today; break;
                case RevisionStatus.Built: revision.BuiltAt ??= Today; break;
            }
        }
        device.Project.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);

        var title = $"{device.Project.Name} · {device.Name} rev {rev}";
        Log(db, ActivityKinds.RevisionChanged,
            before == null ? $"{title}: created" : before != edit.Status ? $"{title}: {before} → {edit.Status}" : $"{title}: edited",
            projectId: device.ProjectId, revisionId: revision.Id);
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.Project, device.ProjectId);
        return (await GetRevisionAsync(revision.Id, ct))!;
    }

    public async Task DeleteRevisionAsync(int id, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var revision = await db.Revisions.Include(r => r.Device).ThenInclude(d => d.Project)
            .FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new StockException("This revision no longer exists.");
        await TrashPhotosAsync(db, p => p.RevisionId == id, ct);
        db.Revisions.Remove(revision);
        Log(db, ActivityKinds.RevisionChanged, $"{RevisionTitle(revision)}: deleted", projectId: revision.Device.ProjectId);
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.All);
    }

    public async Task<RevisionDetail> SaveAllocationAsync(AllocationEdit edit, CancellationToken ct = default)
    {
        if (edit.Qty <= 0) throw new StockException("The quantity must be at least 1.");
        await using var db = await Db(ct);
        var revision = await db.Revisions.Include(r => r.Device).ThenInclude(d => d.Project)
            .FirstOrDefaultAsync(r => r.Id == edit.RevisionId, ct) ?? throw new StockException("This revision no longer exists.");
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == edit.PartId, ct) ?? throw new StockException("This part no longer exists.");
        if (await db.Allocations.AnyAsync(a => a.RevisionId == edit.RevisionId && a.PartId == edit.PartId && a.Id != (edit.Id ?? 0), ct))
            throw new StockException($"{part.Ref} already has a line in {revision.Device.Name} rev {revision.Rev}: edit that line.");

        var title = RevisionTitle(revision);
        var designators = string.Join(' ', Clean(edit.Designators).Split([' ', ','], StringSplitOptions.RemoveEmptyEntries));
        Allocation line;
        if (edit.Id == null)
        {
            line = new Allocation { RevisionId = revision.Id, PartId = part.Id, Status = AllocationStatus.Reserved };
            db.Allocations.Add(line);
            Log(db, ActivityKinds.Reserved, $"Reserved {edit.Qty} × {part.Ref} for {title}" + (designators.Length > 0 ? $" ({designators})" : ""),
                part, revision.Device.ProjectId, revision.Id);
        }
        else
        {
            line = await db.Allocations.FirstOrDefaultAsync(a => a.Id == edit.Id && a.RevisionId == edit.RevisionId, ct)
                ?? throw new StockException("This line no longer exists.");
            Log(db, ActivityKinds.AllocationUpdated, $"{part.Ref} in {title}: {line.Qty} → {edit.Qty}" + (designators.Length > 0 ? $" ({designators})" : ""),
                part, revision.Device.ProjectId, revision.Id);
        }
        line.PartId = part.Id;
        line.Qty = edit.Qty;
        line.Designators = designators;
        line.Notes = Clean(edit.Notes);
        revision.Device.Project.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.Project, revision.Device.ProjectId);
        Notify(ChangeScope.Part, part.Id);
        return (await GetRevisionAsync(revision.Id, ct))!;
    }

    public async Task<RevisionDetail> DeleteAllocationAsync(int id, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var line = await db.Allocations.Include(a => a.Part).Include(a => a.Revision).ThenInclude(r => r.Device).ThenInclude(d => d.Project)
            .FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new StockException("This line no longer exists.");
        var title = RevisionTitle(line.Revision);
        Log(db, ActivityKinds.Released,
            line.Status == AllocationStatus.Reserved ? $"Released {line.Qty} × {line.Part.Ref} from {title}" : $"Removed the used line {line.Qty} × {line.Part.Ref} from {title}",
            line.Part, line.Revision.Device.ProjectId, line.RevisionId);
        db.Allocations.Remove(line);
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.Project, line.Revision.Device.ProjectId);
        Notify(ChangeScope.Part, line.PartId);
        return (await GetRevisionAsync(line.RevisionId, ct))!;
    }

    // ---------- build ----------

    public Task<BuildPlan> PlanBuildAsync(int revisionId, CancellationToken ct = default) => BuildAsync(revisionId, apply: false, ct);

    public Task<BuildPlan> ApplyBuildAsync(int revisionId, CancellationToken ct = default) => BuildAsync(revisionId, apply: true, ct);

    /// <summary>"Mark as built": decrements the stock by the reserved lines and switches them to used.</summary>
    async Task<BuildPlan> BuildAsync(int revisionId, bool apply, CancellationToken ct)
    {
        await using var db = await Db(ct);
        var revision = await db.Revisions.Include(r => r.Device).ThenInclude(d => d.Project)
            .Include(r => r.Allocations).ThenInclude(a => a.Part)
            .FirstOrDefaultAsync(r => r.Id == revisionId, ct) ?? throw new StockException("This revision no longer exists.");
        var title = RevisionTitle(revision);
        var lines = revision.Allocations.Where(a => a.Status == AllocationStatus.Reserved).ToList();
        if (apply && lines.Count == 0) throw new StockException($"Nothing is reserved for {title}.");

        var changes = new List<BuildChange>();
        foreach (var group in lines.GroupBy(a => a.PartId))
        {
            var part = group.First().Part;
            int consumed = group.Sum(a => a.Qty);
            var before = part.Qty;
            var (after, remark) = StockMath.Consume(Qty.Parse(before), consumed);
            changes.Add(new BuildChange(part.Ref, before, after, consumed, remark));
            if (!apply) continue;
            part.Qty = after;
            part.UpdatedAt = Now;
            Log(db, ActivityKinds.Built, $"{title} built: {consumed} × {part.Ref} used" + (remark.Length > 0 ? $". {remark}" : ""),
                part, revision.Device.ProjectId, revision.Id, qtyBefore: before, qtyAfter: after);
        }

        if (apply)
        {
            foreach (var line in lines) line.Status = AllocationStatus.Used;
            revision.Status = RevisionStatus.Built;
            revision.BuiltAt ??= Today;
            revision.Device.Project.UpdatedAt = Now;
            await db.SaveChangesAsync(ct);
            Notify(ChangeScope.All);
        }

        return new BuildPlan
        {
            RevisionId = revisionId,
            Title = title,
            LineCount = lines.Count,
            Applied = apply,
            Changes = changes.OrderBy(c => c.Ref, PartVisuals.RefComparer).ToList(),
        };
    }

    // ---------- BOM ----------

    public async Task<BomPreview> PreviewBomAsync(int revisionId, string csv, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        if (!await db.Revisions.AnyAsync(r => r.Id == revisionId, ct)) throw new StockException("This revision no longer exists.");
        var warnings = new List<string>();
        var rows = BomFile.Parse(csv ?? "", warnings);
        if (rows.Count == 0 && warnings.Count == 0) warnings.Add("The BOM has no lines.");

        var parts = await db.Parts.AsNoTracking().Select(p => new BomPart(p.Id, p.Ref, p.Designation)).ToListAsync(ct);
        var existing = await db.Allocations.AsNoTracking().Where(a => a.RevisionId == revisionId).ToDictionaryAsync(a => a.PartId, ct);

        var lines = rows.Select(row =>
        {
            var (part, kind) = BomMatcher.Match(row, parts);
            var line = part != null ? existing.GetValueOrDefault(part.Id) : null;
            return new BomLineMatch
            {
                Line = row.Line,
                Designators = row.Designators,
                Qty = row.Qty,
                Designation = row.Designation,
                Value = row.Value,
                LibraryRef = row.LibraryRef,
                Supplier = row.Supplier,
                Notes = row.Notes,
                PartId = part?.Id,
                PartRef = part?.Ref,
                PartDesignation = part?.Designation,
                Match = kind,
                ExistingAllocationId = line?.Id,
                ExistingQty = line?.Qty,
                ExistingDesignators = line?.Designators,
            };
        }).ToList();

        foreach (var dup in lines.Where(l => l.PartId != null).GroupBy(l => l.PartRef).Where(g => g.Count() > 1))
            warnings.Add($"{dup.Key} matches {dup.Count()} BOM lines: they are merged into one allocation line.");
        int unmatched = lines.Count(l => l.PartId == null);
        if (unmatched > 0) warnings.Add($"{unmatched} line(s) match no stock part: pick the part by hand, or add it to the stock first.");

        return new BomPreview { RevisionId = revisionId, Lines = lines, Warnings = warnings };
    }

    public async Task<BomApplyResult> ApplyBomAsync(BomApply apply, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var revision = await db.Revisions.Include(r => r.Device).ThenInclude(d => d.Project).Include(r => r.Allocations)
            .FirstOrDefaultAsync(r => r.Id == apply.RevisionId, ct) ?? throw new StockException("This revision no longer exists.");
        var partIds = apply.Lines.Select(l => l.PartId).Distinct().ToList();
        var known = await db.Parts.Where(p => partIds.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);
        if (known.Count != partIds.Count) throw new StockException("Some parts of the BOM no longer exist: preview it again.");

        var merged = apply.Lines.Where(l => l.Qty > 0).GroupBy(l => l.PartId).Select(g => new
        {
            PartId = g.Key,
            Qty = g.Sum(l => l.Qty),
            Designators = string.Join(' ', g.SelectMany(l => l.Designators.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)).Distinct()),
            Notes = string.Join(" ; ", g.Select(l => l.Notes.Trim()).Where(n => n.Length > 0).Distinct()),
        }).ToList();

        int added = 0, updated = 0, removed = 0;
        foreach (var m in merged)
        {
            var line = revision.Allocations.FirstOrDefault(a => a.PartId == m.PartId);
            if (line == null)
            {
                revision.Allocations.Add(new Allocation
                {
                    PartId = m.PartId, Qty = m.Qty, Designators = m.Designators, Notes = m.Notes, Status = AllocationStatus.Reserved,
                });
                added++;
            }
            else if (line.Status == AllocationStatus.Reserved && (line.Qty != m.Qty || line.Designators != m.Designators || line.Notes != m.Notes))
            {
                line.Qty = m.Qty;
                line.Designators = m.Designators;
                line.Notes = m.Notes;
                updated++;
            }
        }
        if (apply.RemoveOthers)
        {
            foreach (var line in revision.Allocations.Where(a => a.Status == AllocationStatus.Reserved && merged.All(m => m.PartId != a.PartId)).ToList())
            {
                db.Allocations.Remove(line);
                removed++;
            }
        }

        revision.Device.Project.UpdatedAt = Now;
        Log(db, ActivityKinds.BomImported, $"BOM applied to {RevisionTitle(revision)}: {added} added, {updated} updated, {removed} removed",
            projectId: revision.Device.ProjectId, revisionId: revision.Id);
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.All);
        return new BomApplyResult { Added = added, Updated = updated, Removed = removed, Revision = (await GetRevisionAsync(revision.Id, ct))! };
    }
}
