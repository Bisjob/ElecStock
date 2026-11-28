using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ElecStock.Contracts;
using ElecStock.Data.Files;
using ElecStock.Data.Services;
using ElecStock.Kicad;
using ElecStock.Stock;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ElecStock.Server.Mcp;

/// <summary>
/// The stock for Claude. Every change goes through <see cref="StockService"/> like the UI, so it
/// is checked the same way, written to the history as "Mcp" and shown live in the open apps.
/// </summary>
[McpServerToolType]
public sealed class StockTools(StockService stock, CallerContext caller, FileStore files, IHttpContextAccessor http, IOptions<ServerOptions> server)
{
    public const string Instructions = """
        The user's electronics stock, shared by all their projects.
        - Before proposing a part, search the stock: available = qty - reserved. "10+" means at least 10; an empty qty
          means not counted. Prefer parts in stock and say clearly when something must be bought.
        - get_part returns the technical sheet, the user's free notes (measurements, tests, wiring) and the photo captions
          and markers (pinouts). These facts override generic datasheet assumptions. Look at the photos
          (get_part_photos) when the pinout or the form factor matters.
        - When a design adopts a stock part, reserve it on the board revision (reserve_parts, with the designators).
          When a design drops a part, release it (release_parts).
        - mark_built decrements the stock: call it only when the user confirms the board is physically built.
          receive_order adds an order to the stock: only when the user confirms the parcel arrived.
        - A part about to be bought: add_part with qty 0, then add_to_order. Modules and ICs get a technical sheet
          (update_sheet) with: key characteristics, pinout, used in, gotchas, links.
        - The part notes hold facts about the part, never project usage (that is what allocations are for).
          Edit the free notes only when the user asks.
        - KiCad: get_part gives the part's symbol, footprint and 3D model. Stock:<name> links are in the stock KiCad library
          (get_kicad_library), synced on every PC to ${ELEC_STOCK}/KiCad. When a design uses a stock part, take its links.
          When you find or make the right symbol/footprint for a part, record it (update_part with kicadSymbol /
          kicadFootprint); a custom one is uploaded to the library first (get_kicad_library explains how).
        """;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static string Out(object value) => JsonSerializer.Serialize(value, Json);

    async Task<T> Run<T>(Func<Task<T>> action)
    {
        caller.Source = ActivitySource.Mcp;
        try
        {
            return await action();
        }
        catch (StockException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    /// <summary>The server as Claude reaches it (the MCP request), else the configured public address.</summary>
    string BaseUrl()
    {
        var request = http.HttpContext?.Request;
        if (request != null) return $"{request.Scheme}://{request.Host}{request.PathBase}/";
        return server.Value.PublicBaseUrl.TrimEnd('/') + "/";
    }

    async Task<PartDetail> RequirePart(string reference) =>
        await stock.GetPartByRefAsync(reference)
        ?? throw new McpException($"No part '{reference}' in the stock (search_parts finds similar refs).");

    static object Brief(PartSummary p) => new
    {
        @ref = p.Ref,
        p.Category,
        p.Designation,
        qty = p.Qty.Length > 0 ? p.Qty : null,
        available = p.AvailableShort,
        status = p.StatusTitle,
        location = p.LocationDisplay.Length > 0 ? p.LocationDisplay : null,
        notes = p.Notes.Length > 0 ? p.Notes : null,
        onOrder = p.OnOrder > 0 ? p.OnOrder : (int?)null,
        projects = p.Projects.Count > 0 ? p.Projects : null,
    };

    // ---------- reading the stock ----------

    [McpServerTool(Name = "search_parts", ReadOnly = true), Description(
        "Searches the stock. Every word of the query must appear in the ref, designation, notes, location, projects, "
        + "designators or photo captions/markers. Without a query, lists the parts (optionally of one category).")]
    public Task<string> SearchParts(
        [Description("Words to look for, e.g. 'mosfet logic' or '10k'. Empty for all.")] string? query = null,
        [Description("Category filter: mcu, io, sensor, signal, actuator, power, ic, mosfet, transistor, diode, led, passive, connector, mechanical.")] string? category = null,
        [Description("all | in_stock | reserved | to_buy | uncounted | no_photo | no_kicad (no symbol or no footprint)")] string filter = "all",
        [Description("Maximum number of parts returned.")] int limit = 60) => Run(async () =>
    {
        var overview = await stock.GetStockAsync();
        var terms = (query ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var parts = overview.Parts
            .Where(p => category == null || p.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .Where(p => terms.All(t => p.SearchText.Contains(t)))
            .Where(p => filter switch
            {
                "in_stock" => p.Status == StockStatus.Ok,
                "reserved" => p.Reserved > 0,
                "to_buy" => p.NeedsBuying,
                "uncounted" => p.ParsedQty.Kind == QtyKind.Blank || p.Status == StockStatus.Check,
                "no_photo" => p.PhotoCount == 0,
                "no_kicad" => p.KicadSymbol.Length == 0 || p.KicadFootprint.Length == 0,
                _ => true,
            })
            .OrderBy(p => p, Comparer<PartSummary>.Create((a, b) => PartVisuals.CompareTiles(a.Ref, a.Category, b.Ref, b.Category)))
            .ToList();
        return Out(new { total = parts.Count, parts = parts.Take(Math.Clamp(limit, 1, 500)).Select(Brief) });
    });

    [McpServerTool(Name = "get_part", ReadOnly = true), Description(
        "Everything about one part: quantities, location, notes, KiCad symbol/footprint/3D model (with download links for "
        + "the stock library files), technical sheet (Markdown), the user's free notes, photo captions and markers (pinouts), "
        + "project lines, orders and recent history.")]
    public Task<string> GetPart([Description("Part ref, e.g. IRF4905 or R-4k7.")] string reference) => Run(async () =>
    {
        var d = await RequirePart(reference);
        var p = d.Part;
        return Out(new
        {
            @ref = p.Ref,
            p.Category,
            p.Designation,
            qty = p.Qty.Length > 0 ? p.Qty : "(not counted)",
            availability = p.AvailableLong,
            status = p.StatusTitle,
            location = p.LocationDisplay.Length > 0 ? p.LocationDisplay : null,
            datasheetUrl = p.DatasheetUrl.Length > 0 ? p.DatasheetUrl : null,
            offlineDatasheet = p.HasLocalDatasheet,
            notes = p.Notes.Length > 0 ? p.Notes : null,
            kicad = await KicadOf(p),
            sheet = d.Sheet,
            freeNotes = d.FreeNotes.Length > 0 ? d.FreeNotes : null,
            photos = d.Photos.Select(ph => new
            {
                id = ph.Id,
                file = ph.FileName,
                caption = ph.Caption.Length > 0 ? ph.Caption : null,
                markers = ph.Markers.Count > 0 ? ph.Markers.Select((m, i) => $"{i + 1}: {m.Label}") : null,
            }),
            projectLines = d.Allocations.Select(a => new
            {
                revision = $"{a.Project} · {a.Device} rev {a.Rev}",
                a.Qty,
                designators = a.Designators.Length > 0 ? a.Designators : null,
                status = a.Status.ToString().ToLowerInvariant(),
                notes = a.Notes.Length > 0 ? a.Notes : null,
            }),
            orders = d.OrderLines.Select(o => new { o.Supplier, status = o.Status.ToString(), o.Qty, o.UnitPrice, o.Currency, o.OrderedAt, o.ReceivedAt }),
            history = d.History.Take(10).Select(h => $"{h.At.ToLocalTime():yyyy-MM-dd} [{h.Source}] {h.Summary}"),
        });
    });

    async Task<object?> KicadOf(PartSummary p)
    {
        if (p.KicadSymbol.Length == 0 && p.KicadFootprint.Length == 0 && p.Kicad3dModel.Length == 0) return null;
        var library = await stock.GetKicadLibraryAsync();
        var linked = library.Items.Where(i => i.UsedBy.Contains(p.Ref, StringComparer.OrdinalIgnoreCase)).ToList();
        // A stock footprint brings its own 3D models.
        var models = linked.Where(i => i.Kind == KicadItemKind.Footprint).SelectMany(i => i.Models).Select(KicadLib.StockModelName).OfType<string>().ToList();
        linked.AddRange(library.Items.Where(i => i.Kind == KicadItemKind.Model && models.Contains(i.Name, StringComparer.OrdinalIgnoreCase) && !linked.Contains(i)));
        return new
        {
            symbol = p.KicadSymbol.Length > 0 ? p.KicadSymbol : null,
            footprint = p.KicadFootprint.Length > 0 ? p.KicadFootprint : null,
            model3d = p.Kicad3dModel.Length > 0 ? p.Kicad3dModel : null,
            stockLibraryFiles = linked.Count > 0
                ? linked.Select(i => new { kind = i.Kind.ToString().ToLowerInvariant(), i.Name, url = BaseUrl() + i.Url, source = i.Source.Length > 0 ? i.Source : null })
                : null,
        };
    }

    [McpServerTool(Name = "get_part_photos", ReadOnly = true), Description(
        "Returns the photos of a part as images (cover first), with their captions and numbered markers. Use it when the "
        + "pinout, the form factor or the markings matter.")]
    public Task<IEnumerable<ContentBlock>> GetPartPhotos(
        [Description("Part ref.")] string reference,
        [Description("Photo ids from get_part; empty for the first photos.")] int[]? photoIds = null,
        [Description("Maximum number of photos.")] int max = 4) => Run(async () =>
    {
        var d = await RequirePart(reference);
        var photos = d.Photos
            .Where(ph => photoIds == null || photoIds.Length == 0 || photoIds.Contains(ph.Id))
            .OrderByDescending(ph => ph.IsCover)
            .Take(Math.Clamp(max, 1, 10))
            .ToList();
        var blocks = new List<ContentBlock>();
        if (photos.Count == 0) blocks.Add(new TextContentBlock { Text = $"{d.Part.Ref} has no photo." });
        foreach (var ph in photos)
        {
            var path = ph.Url["files/".Length..];
            var bytes = await File.ReadAllBytesAsync(files.FullPath(path));
            var mime = Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/jpeg" };
            var text = $"Photo {ph.Id} ({ph.FileName}, {ph.Width}×{ph.Height})"
                + (ph.Caption.Length > 0 ? $": {ph.Caption}" : "")
                + (ph.Markers.Count > 0 ? "\nMarkers (x/y as fractions of the image): " + string.Join("; ", ph.Markers.Select((m, i) => $"{i + 1} at ({m.X:0.00}, {m.Y:0.00}) = {m.Label}")) : "");
            blocks.Add(new TextContentBlock { Text = text });
            blocks.Add(ImageContentBlock.FromBytes(bytes, mime));
        }
        return (IEnumerable<ContentBlock>)blocks;
    });

    public sealed class QtyRequest
    {
        [Description("Part ref.")] public string Ref { get; set; } = "";
        [Description("Quantity needed.")] public int Qty { get; set; } = 1;
    }

    [McpServerTool(Name = "check_availability", ReadOnly = true), Description(
        "Checks whether the stock covers a list of parts (available = qty - reserved), e.g. for a BOM being designed.")]
    public Task<string> CheckAvailability([Description("Parts and quantities needed.")] QtyRequest[] items) => Run(async () =>
    {
        var overview = await stock.GetStockAsync();
        var byRef = overview.Parts.ToDictionary(p => p.Ref, StringComparer.OrdinalIgnoreCase);
        return Out(items.Select(item =>
        {
            if (!byRef.TryGetValue(item.Ref.Trim(), out var p)) return (object)new { @ref = item.Ref, enough = false, availability = "not in the stock" };
            var qty = p.ParsedQty;
            int available = qty.Value - p.Reserved;
            bool? enough = qty.Kind switch
            {
                QtyKind.Exact => available >= item.Qty,
                QtyKind.AtLeast => available >= item.Qty ? true : null,
                _ => null,
            };
            return new { @ref = p.Ref, needed = item.Qty, enough, availability = p.AvailableLong, onOrder = p.OnOrder > 0 ? p.OnOrder : (int?)null };
        }));
    });

    // ---------- projects ----------

    [McpServerTool(Name = "list_projects", ReadOnly = true), Description(
        "Projects with their devices and board revisions: status, number of lines, lines short / on order / to count.")]
    public Task<string> ListProjects() => Run(async () =>
    {
        var projects = await stock.GetProjectsAsync();
        return Out(projects.Select(p => new
        {
            p.Name,
            status = p.Status.ToString(),
            description = p.Description.Length > 0 ? p.Description : null,
            devices = p.Devices.Select(d => new
            {
                d.Name,
                revisions = d.Revisions.Select(r => new
                {
                    r.Rev,
                    status = r.Status.ToString(),
                    lines = r.LineCount,
                    reserved = r.ReservedLines,
                    used = r.UsedLines,
                    shortLines = r.ShortLines > 0 ? r.ShortLines : (int?)null,
                    onOrderLines = r.OnOrderLines > 0 ? r.OnOrderLines : (int?)null,
                    toCount = r.UncountedLines > 0 ? r.UncountedLines : (int?)null,
                }),
            }),
        }));
    });

    async Task<(ProjectSummary Project, DeviceSummary Device, RevisionSummary Revision)?> FindRevision(string project, string device, string rev)
    {
        var projects = await stock.GetProjectsAsync();
        var p = projects.FirstOrDefault(x => x.Name.Equals(project.Trim(), StringComparison.OrdinalIgnoreCase));
        var d = p?.Devices.FirstOrDefault(x => x.Name.Equals(device.Trim(), StringComparison.OrdinalIgnoreCase));
        var r = d?.Revisions.FirstOrDefault(x => x.Rev.Equals(rev.Trim(), StringComparison.OrdinalIgnoreCase));
        return r == null ? null : (p!, d!, r);
    }

    async Task<int> RequireRevision(string project, string device, string rev) =>
        (await FindRevision(project, device, rev))?.Revision.Id
        ?? throw new McpException($"No revision {project} · {device} rev {rev} (list_projects shows them, save_revision creates one).");

    /// <summary>The revision, created (with its project and device) when it does not exist yet.</summary>
    async Task<(int Id, bool Created)> EnsureRevision(string project, string device, string rev)
    {
        if (await FindRevision(project, device, rev) is { } found) return (found.Revision.Id, false);
        var projects = await stock.GetProjectsAsync();
        var p = projects.FirstOrDefault(x => x.Name.Equals(project.Trim(), StringComparison.OrdinalIgnoreCase));
        int projectId = p?.Id ?? (await stock.SaveProjectAsync(new ProjectEdit { Name = project })).Id;
        var detail = await stock.GetProjectAsync(projectId) ?? throw new McpException("The project vanished.");
        var d = detail.Devices.FirstOrDefault(x => x.Name.Equals(device.Trim(), StringComparison.OrdinalIgnoreCase));
        if (d == null)
        {
            detail = await stock.SaveDeviceAsync(new DeviceEdit { ProjectId = projectId, Name = device });
            d = detail.Devices.First(x => x.Name.Equals(device.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        var r = d.Revisions.FirstOrDefault(x => x.Rev.Equals(rev.Trim(), StringComparison.OrdinalIgnoreCase));
        return r != null ? (r.Id, true) : ((await stock.SaveRevisionAsync(new RevisionEdit { DeviceId = d.Id, Rev = rev })).Id, true);
    }

    static object RevisionOut(RevisionDetail r) => new
    {
        revision = $"{r.Project} · {r.Device} rev {r.Rev}",
        status = r.Status.ToString(),
        devicePath = r.DevicePath.Length > 0 ? r.DevicePath : null,
        r.PcbOrderedAt,
        r.PcbReceivedAt,
        r.BuiltAt,
        notes = r.Notes.Length > 0 ? r.Notes : null,
        estimatedCost = r.EstimatedCost is { } cost ? $"{cost:0.00} {r.Currency} ({r.PricedLines}/{r.Lines.Count} lines priced)" : null,
        lines = r.Lines.Select(l => new
        {
            @ref = l.Ref,
            l.Designation,
            l.Qty,
            designators = l.Designators.Length > 0 ? l.Designators : null,
            status = l.Status.ToString().ToLowerInvariant(),
            flag = l.Flag.ToString(),
            stock = l.StockText,
            notes = l.Notes.Length > 0 ? l.Notes : null,
        }),
    };

    [McpServerTool(Name = "get_revision", ReadOnly = true), Description(
        "One board revision: status, dates, notes and its allocation lines with the stock state of each part "
        + "(Ok, Short, OnOrder, Uncounted, Used).")]
    public Task<string> GetRevision(
        [Description("Project name, e.g. WeatherStation.")] string project,
        [Description("Device (board) name, e.g. LedStrip2-Env-Meter.")] string device,
        [Description("Revision, e.g. A.")] string rev) => Run(async () =>
            Out(RevisionOut((await stock.GetRevisionAsync(await RequireRevision(project, device, rev)))!)));

    [McpServerTool(Name = "save_project"), Description(
        "Creates a project or updates it (only the given fields change).")]
    public Task<string> SaveProject(
        [Description("Project name.")] string name,
        [Description("One-line description.")] string? description = null,
        [Description("Idea | Design | Prototype | Built | Archived")] ProjectStatus? status = null,
        [Description("Project folder or repository path (optional), e.g. ~/projects/WeatherStation.")] string? repoPath = null,
        [Description("Link (repository, page).")] string? url = null,
        [Description("Notes (Markdown), replaces the current ones.")] string? notes = null) => Run(async () =>
    {
        var existing = (await stock.GetProjectsAsync()).FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        var current = existing == null ? null : await stock.GetProjectAsync(existing.Id);
        var saved = await stock.SaveProjectAsync(new ProjectEdit
        {
            Id = current?.Id,
            Name = name,
            Description = description ?? current?.Description ?? "",
            Status = status ?? current?.Status ?? ProjectStatus.Design,
            RepoPath = repoPath ?? current?.RepoPath ?? "",
            Url = url ?? current?.Url ?? "",
            Notes = notes ?? current?.Notes ?? "",
        });
        return Out(new { saved = saved.Name, status = saved.Status.ToString(), created = current == null });
    });

    [McpServerTool(Name = "save_revision"), Description(
        "Creates a board revision (and its project and device when missing) or updates it. Status Built is refused while "
        + "parts are reserved: use mark_built.")]
    public Task<string> SaveRevision(
        [Description("Project name.")] string project,
        [Description("Device (board) name.")] string device,
        [Description("Revision, e.g. A.")] string rev,
        [Description("Design | PcbOrdered | PcbReceived | Built | Abandoned")] RevisionStatus? status = null,
        [Description("Notes (Markdown), replaces the current ones.")] string? notes = null,
        [Description("Device folder inside the project, e.g. Elec/Devices/LedStrip2-Env-Meter.")] string? devicePath = null,
        [Description("One-line description of the device.")] string? deviceDescription = null) => Run(async () =>
    {
        var (id, created) = await EnsureRevision(project, device, rev);
        var current = (await stock.GetRevisionAsync(id))!;
        if (devicePath != null || deviceDescription != null)
        {
            var projectDetail = (await stock.GetProjectAsync(current.ProjectId))!;
            var d = projectDetail.Devices.First(x => x.Id == current.DeviceId);
            await stock.SaveDeviceAsync(new DeviceEdit
            {
                Id = d.Id, ProjectId = current.ProjectId, Name = d.Name, Notes = d.Notes,
                Path = devicePath ?? d.Path, Description = deviceDescription ?? d.Description,
            });
        }
        var saved = await stock.SaveRevisionAsync(new RevisionEdit
        {
            Id = id,
            DeviceId = current.DeviceId,
            Rev = current.Rev,
            Status = status ?? current.Status,
            Notes = notes ?? current.Notes,
            PcbOrderedAt = current.PcbOrderedAt,
            PcbReceivedAt = current.PcbReceivedAt,
            BuiltAt = current.BuiltAt,
        });
        return Out(new { revision = saved.Title, project = saved.Project, status = saved.Status.ToString(), created });
    });

    public sealed class ReserveLine
    {
        [Description("Part ref.")] public string Ref { get; set; } = "";
        [Description("Quantity for the board.")] public int Qty { get; set; } = 1;
        [Description("Space-separated designators, e.g. 'Q201 Q202'.")] public string? Designators { get; set; }
        [Description("Free text about this use.")] public string? Notes { get; set; }
    }

    [McpServerTool(Name = "reserve_parts"), Description(
        "Reserves stock parts for a board revision (created with its project and device when missing). A part already "
        + "on the revision has its line updated (qty, designators, notes).")]
    public Task<string> ReserveParts(
        [Description("Project name.")] string project,
        [Description("Device (board) name.")] string device,
        [Description("Revision, e.g. A.")] string rev,
        [Description("Lines to reserve.")] ReserveLine[] lines) => Run(async () =>
    {
        var (id, created) = await EnsureRevision(project, device, rev);
        var overview = await stock.GetStockAsync();
        var byRef = overview.Parts.ToDictionary(p => p.Ref, StringComparer.OrdinalIgnoreCase);
        var missing = lines.Where(l => !byRef.ContainsKey(l.Ref.Trim())).Select(l => l.Ref).ToList();
        if (missing.Count > 0) throw new McpException($"Not in the stock: {string.Join(", ", missing)} (add_part first).");

        var revision = (await stock.GetRevisionAsync(id))!;
        foreach (var line in lines)
        {
            var part = byRef[line.Ref.Trim()];
            var existing = revision.Lines.FirstOrDefault(l => l.PartId == part.Id);
            revision = await stock.SaveAllocationAsync(new AllocationEdit
            {
                Id = existing?.Id,
                RevisionId = id,
                PartId = part.Id,
                Qty = line.Qty,
                Designators = line.Designators ?? existing?.Designators ?? "",
                Notes = line.Notes ?? existing?.Notes ?? "",
            });
        }
        return Out(new { revisionCreated = created, result = RevisionOut(revision) });
    });

    [McpServerTool(Name = "release_parts", Destructive = true), Description(
        "Removes parts from a board revision (the design dropped them): their reservation is released.")]
    public Task<string> ReleaseParts(
        [Description("Project name.")] string project,
        [Description("Device (board) name.")] string device,
        [Description("Revision, e.g. A.")] string rev,
        [Description("Refs to remove.")] string[] refs) => Run(async () =>
    {
        var revision = (await stock.GetRevisionAsync(await RequireRevision(project, device, rev)))!;
        var notFound = new List<string>();
        foreach (var reference in refs)
        {
            var line = revision.Lines.FirstOrDefault(l => l.Ref.Equals(reference.Trim(), StringComparison.OrdinalIgnoreCase));
            if (line == null) notFound.Add(reference);
            else revision = await stock.DeleteAllocationAsync(line.Id);
        }
        return Out(new { notOnTheRevision = notFound.Count > 0 ? notFound : null, result = RevisionOut(revision) });
    });

    [McpServerTool(Name = "plan_build", ReadOnly = true), Description(
        "Preview of mark_built: the quantity of each reserved part before and after, with remarks. Changes nothing.")]
    public Task<string> PlanBuild(string project, string device, string rev) => Run(async () =>
        Out(await stock.PlanBuildAsync(await RequireRevision(project, device, rev))));

    [McpServerTool(Name = "mark_built", Destructive = true), Description(
        "Marks a board revision as built: decrements the stock by its reserved lines and switches them to used. "
        + "Only when the user confirms the board is physically built.")]
    public Task<string> MarkBuilt(string project, string device, string rev) => Run(async () =>
        Out(await stock.ApplyBuildAsync(await RequireRevision(project, device, rev))));

    [McpServerTool(Name = "import_bom"), Description(
        "Matches a device bom.csv (columns ref,qty,designation,value,library_ref,supplier,notes, or a KiCad BOM) "
        + "against the stock. With apply=false it only previews; with apply=true it reserves the matched lines on the "
        + "revision (created when missing).")]
    public Task<string> ImportBom(
        [Description("Project name.")] string project,
        [Description("Device (board) name.")] string device,
        [Description("Revision, e.g. A.")] string rev,
        [Description("Content of the BOM CSV file.")] string csv,
        [Description("false = preview only.")] bool apply = false,
        [Description("With apply: also release the reserved lines that are not in the BOM.")] bool removeOthers = false) => Run(async () =>
    {
        var (id, created) = apply ? await EnsureRevision(project, device, rev) : (await RequireRevision(project, device, rev), false);
        var preview = await stock.PreviewBomAsync(id, csv);
        var lines = preview.Lines.Select(l => new
        {
            l.Line,
            l.Designators,
            l.Qty,
            bom = $"{l.Designation} {l.Value} {l.LibraryRef}".Trim(),
            match = l.PartRef,
            how = l.Match.ToString(),
            existing = l.ExistingQty,
        });
        if (!apply) return Out(new { preview = lines, preview.Warnings });

        var result = await stock.ApplyBomAsync(new BomApply
        {
            RevisionId = id,
            RemoveOthers = removeOthers,
            Lines = preview.Lines.Where(l => l.PartId != null)
                .Select(l => new BomApplyLine { PartId = l.PartId!.Value, Qty = l.Qty, Designators = l.Designators, Notes = l.Notes })
                .ToList(),
        });
        return Out(new
        {
            revisionCreated = created, result.Added, result.Updated, result.Removed, preview.Warnings,
            unmatched = preview.Lines.Where(l => l.PartId == null).Select(l => $"line {l.Line}: {l.Designators} {l.Designation} {l.Value}"),
        });
    });

    // ---------- editing parts ----------

    async Task<int?> LocationId(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var locations = await stock.GetLocationsAsync();
        var found = locations.FirstOrDefault(l => l.Code.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase) || l.Name.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));
        return found?.Id ?? (await stock.SaveLocationAsync(new LocationEdit { Code = code.Trim() })).Id;
    }

    [McpServerTool(Name = "add_part"), Description(
        "Adds a part to the stock. A part about to be bought gets qty 0 (then add_to_order). Modules and ICs also get a "
        + "technical sheet (update_sheet).")]
    public Task<string> AddPart(
        [Description("Short identifier, no spaces: IRF4905, ESP32-C3-SuperMini, R-4k7, JST-XH-B3B.")] string reference,
        [Description("mcu, io, sensor, signal, actuator, power, ic, mosfet, transistor, diode, led, passive, connector, mechanical…")] string category,
        [Description("Human-readable description.")] string designation,
        [Description("Quantity: a number, '10+' for at least 10, empty when not counted.")] string qty = "",
        [Description("Location code (created when unknown).")] string? location = null,
        [Description("Datasheet link.")] string? datasheetUrl = null,
        [Description("Facts about the part (never project usage).")] string? notes = null,
        [Description(KicadSymbolHelp)] string? kicadSymbol = null,
        [Description(KicadFootprintHelp)] string? kicadFootprint = null,
        [Description(Kicad3dModelHelp)] string? kicad3dModel = null) => Run(async () =>
    {
        var saved = await stock.SavePartAsync(new PartEdit
        {
            Ref = reference, Category = category, Designation = designation, Qty = qty,
            LocationId = await LocationId(location), DatasheetUrl = datasheetUrl ?? "", Notes = notes ?? "",
            KicadSymbol = kicadSymbol ?? "", KicadFootprint = kicadFootprint ?? "", Kicad3dModel = kicad3dModel ?? "",
        });
        return Out(Brief(saved.Part));
    });

    [McpServerTool(Name = "update_part"), Description(
        "Changes a part (only the given fields). A quantity change is kept in the history with its reason.")]
    public Task<string> UpdatePart(
        [Description("Current ref of the part.")] string reference,
        [Description("New ref, to rename the part.")] string? newRef = null,
        string? category = null,
        string? designation = null,
        [Description("New quantity: a number, '10+', or empty string for not counted.")] string? qty = null,
        [Description("Why the quantity changes (count, found more…).")] string? qtyReason = null,
        [Description("Location code (created when unknown); empty string removes it.")] string? location = null,
        string? datasheetUrl = null,
        [Description("Facts about the part, replaces the current notes.")] string? notes = null,
        [Description(KicadSymbolHelp + " Empty string removes it.")] string? kicadSymbol = null,
        [Description(KicadFootprintHelp + " Empty string removes it.")] string? kicadFootprint = null,
        [Description(Kicad3dModelHelp + " Empty string removes it.")] string? kicad3dModel = null) => Run(async () =>
    {
        var p = (await RequirePart(reference)).Part;
        var edit = PartEdit.From(p);
        edit.Ref = newRef ?? p.Ref;
        edit.Category = category ?? p.Category;
        edit.Designation = designation ?? p.Designation;
        edit.Qty = qty ?? p.Qty;
        edit.QtyReason = qtyReason ?? "via Claude";
        if (location != null) edit.LocationId = await LocationId(location);
        edit.DatasheetUrl = datasheetUrl ?? p.DatasheetUrl;
        edit.Notes = notes ?? p.Notes;
        edit.KicadSymbol = kicadSymbol ?? p.KicadSymbol;
        edit.KicadFootprint = kicadFootprint ?? p.KicadFootprint;
        edit.Kicad3dModel = kicad3dModel ?? p.Kicad3dModel;
        var saved = await stock.SavePartAsync(edit);
        return Out(new
        {
            part = Brief(saved.Part),
            kicad = new { symbol = saved.Part.KicadSymbol, footprint = saved.Part.KicadFootprint, model3d = saved.Part.Kicad3dModel },
        });
    });

    const string KicadSymbolHelp = "KiCad symbol as Library:Name, e.g. Device:R, MCU_Espressif:ESP32-C3, or Stock:<name> for the stock library.";
    const string KicadFootprintHelp = "KiCad footprint as Library:Name, e.g. Resistor_SMD:R_0603_1608Metric, or Stock:<name>.";
    const string Kicad3dModelHelp = "3D model path, only when the footprint has none or the wrong one, e.g. ${ELEC_STOCK}/KiCad/3D/<file>.step.";

    // ---------- KiCad library ----------

    [McpServerTool(Name = "get_kicad_library", ReadOnly = true), Description(
        "The stock KiCad library (nickname Stock): custom symbols, footprints and 3D models stored on the server, with the parts "
        + "using each one, download links, and how to sync it to this PC or upload new files.")]
    public Task<string> GetKicadLibrary() => Run(async () =>
    {
        var library = await stock.GetKicadLibraryAsync();
        var baseUrl = BaseUrl();
        return Out(new
        {
            library.Version,
            layout = new
            {
                pathVariable = $"{KicadLib.PathVariable} = the synced folder (holds KiCad/)",
                symbols = $"${{{KicadLib.PathVariable}}}/{KicadLib.SymbolFile}",
                footprints = $"${{{KicadLib.PathVariable}}}/{KicadLib.FootprintDir}",
                models = KicadLib.ModelPrefix + "<file>",
            },
            sync = $"PowerShell on the PC: irm {baseUrl}kicad/sync.ps1 | iex  (downloads {baseUrl}kicad/library.zip when the version changed, "
                + "sets the user variable ELEC_STOCK; restart KiCad after the first sync)",
            upload = $"curl -f -X POST --data-binary @<file> \"{baseUrl}kicad/upload?fileName=<file name>&part=<ref>&source=<where it comes from>\" (a .kicad_sym gives one item per symbol; "
                + "also .kicad_mod, .step/.stp/.wrl or a .zip of them; same name = replaced; part is optional and gets the links it lacks; "
                + "source: the link it was downloaded from, or how it was made). "
                + "Make or fix symbols and footprints with KiCad / Copperpour, never by editing the files as text.",
            items = library.Items.Select(i => new
            {
                kind = i.Kind.ToString().ToLowerInvariant(),
                i.Name,
                i.Link,
                url = baseUrl + i.Url,
                usedBy = i.UsedBy.Count > 0 ? i.UsedBy : null,
                extends = i.Extends,
                source = i.Source.Length > 0 ? i.Source : null,
                models = i.Models.Count > 0 ? i.Models : null,
                missingModels = i.MissingModels.Count > 0 ? i.MissingModels : null,
            }),
        });
    });

    [McpServerTool(Name = "update_sheet"), Description(
        "Creates or replaces the technical sheet of a part (Markdown): title '# <Ref> – <designation>', then "
        + "**Type** · **Package** · **Manufacturer**, Key characteristics, Pinout, Used in (plain text), Gotchas, Links.")]
    public Task<string> UpdateSheet(string reference, [Description("Full Markdown of the sheet.")] string markdown) => Run(async () =>
    {
        var p = (await RequirePart(reference)).Part;
        await stock.SaveSheetAsync(p.Id, markdown);
        return Out(new { saved = p.Ref });
    });

    [McpServerTool(Name = "update_free_notes"), Description(
        "Replaces the user's free notes of a part (Markdown: measurements, tests, wiring). Only when the user asks.")]
    public Task<string> UpdateFreeNotes(string reference, string markdown) => Run(async () =>
    {
        var p = (await RequirePart(reference)).Part;
        await stock.SaveFreeNotesAsync(p.Id, markdown);
        return Out(new { saved = p.Ref });
    });

    [McpServerTool(Name = "fetch_datasheet"), Description(
        "Downloads the datasheet link of a part and keeps it as its offline PDF.")]
    public Task<string> FetchDatasheet(string reference) => Run(async () =>
    {
        var p = (await RequirePart(reference)).Part;
        await stock.FetchDatasheetAsync(p.Id);
        return Out(new { saved = p.Ref });
    });

    // ---------- buying ----------

    [McpServerTool(Name = "get_shopping_list", ReadOnly = true), Description(
        "What to buy: parts short for the reservations (with the revisions needing them), parts planned in draft orders, "
        + "parts out of stock; with the quantities already ordered and the last supplier and price.")]
    public Task<string> GetShoppingList() => Run(async () =>
    {
        var list = await stock.GetShoppingListAsync();
        return Out(new
        {
            items = list.Items.Select(i => new
            {
                @ref = i.Ref,
                i.Designation,
                reason = i.Reason.ToString(),
                qty = i.Qty.Length > 0 ? i.Qty : "?",
                i.Reserved,
                shortage = i.Shortage > 0 ? i.Shortage : (int?)null,
                onOrder = i.OnOrder > 0 ? i.OnOrder : (int?)null,
                planned = i.Planned > 0 ? i.Planned : (int?)null,
                toBuy = i.ToBuy > 0 ? i.ToBuy : (int?)null,
                neededBy = i.NeededBy.Count > 0 ? i.NeededBy : null,
                lastSupplier = i.LastSupplier.Length > 0 ? i.LastSupplier : null,
                lastUnitPrice = i.LastUnitPrice,
            }),
            openOrders = list.OpenOrders.Select(o => new { o.Id, supplier = o.Title, status = o.Status.ToString(), o.LineCount, o.Total, o.Currency }),
        });
    });

    [McpServerTool(Name = "add_to_order"), Description(
        "Adds a part to an order: the given order, else the draft order of that supplier, else a new draft order.")]
    public Task<string> AddToOrder(
        string reference,
        int qty,
        [Description("Supplier (AliExpress, LCSC, Amazon…).")] string? supplier = null,
        [Description("Unit price in the order currency.")] decimal? unitPrice = null,
        [Description("Order id from list_orders, to add to a specific order.")] int? orderId = null,
        [Description("Listing link or remark.")] string? notes = null) => Run(async () =>
    {
        var p = (await RequirePart(reference)).Part;
        var order = await stock.AddToOrderAsync(new AddToOrder
        {
            PartId = p.Id, Qty = qty, Supplier = supplier, UnitPrice = unitPrice, OrderId = orderId, Notes = notes ?? "",
        });
        return Out(new { order = order.Id, supplier = order.Title, status = order.Status.ToString(), lines = order.Lines.Select(l => $"{l.Qty} × {l.Ref}") });
    });

    [McpServerTool(Name = "list_orders", ReadOnly = true), Description("Orders: drafts, placed, received, cancelled.")]
    public Task<string> ListOrders([Description("Include received and cancelled orders.")] bool includeClosed = false) => Run(async () =>
    {
        var orders = await stock.GetOrdersAsync();
        return Out(orders
            .Where(o => includeClosed || o.Status is OrderStatus.Draft or OrderStatus.Ordered)
            .Select(o => new { o.Id, supplier = o.Title, o.OrderNumber, status = o.Status.ToString(), o.OrderedAt, o.ReceivedAt, o.LineCount, o.Total, o.Currency }));
    });

    [McpServerTool(Name = "get_order", ReadOnly = true), Description("One order with its lines.")]
    public Task<string> GetOrder(int orderId) => Run(async () =>
    {
        var o = await stock.GetOrderAsync(orderId) ?? throw new McpException($"No order {orderId}.");
        return Out(new
        {
            o.Id, supplier = o.Title, o.OrderNumber, o.Url, status = o.Status.ToString(), o.OrderedAt, o.ExpectedAt, o.ReceivedAt, o.Total, o.Currency,
            notes = o.Notes.Length > 0 ? o.Notes : null,
            lines = o.Lines.Select(l => new { @ref = l.Ref, l.Designation, l.Qty, l.UnitPrice, l.ReceivedQty, stockNow = l.PartQty }),
        });
    });

    [McpServerTool(Name = "receive_order", Destructive = true), Description(
        "Marks an order as received and adds its quantities to the stock. Only when the user confirms the parcel arrived.")]
    public Task<string> ReceiveOrder(int orderId) => Run(async () =>
    {
        var o = await stock.ReceiveOrderAsync(new ReceiveOrder { OrderId = orderId });
        return Out(new { o.Id, supplier = o.Title, status = o.Status.ToString(), lines = o.Lines.Select(l => $"{l.ReceivedQty} × {l.Ref} → stock {l.PartQty}") });
    });

    // ---------- misc ----------

    [McpServerTool(Name = "list_locations", ReadOnly = true), Description("Storage locations (boxes, drawers) and how many parts each holds.")]
    public Task<string> ListLocations() => Run(async () =>
        Out((await stock.GetLocationsAsync()).Select(l => new { l.Code, name = l.Name.Length > 0 ? l.Name : null, parts = l.PartCount })));

    [McpServerTool(Name = "get_history", ReadOnly = true), Description(
        "History of the changes (who: Web, App, Mcp = Claude, Import), for one part or the whole stock.")]
    public Task<string> GetHistory([Description("Part ref; empty for everything.")] string? reference = null, int take = 30) => Run(async () =>
    {
        int? partId = reference == null ? null : (await RequirePart(reference)).Part.Id;
        var list = await stock.GetActivityAsync(new ActivityQuery { PartId = partId, Take = take });
        return Out(list.Select(a => $"{a.At.ToLocalTime():yyyy-MM-dd HH:mm} [{a.Source}] {a.Summary}"));
    });
}
