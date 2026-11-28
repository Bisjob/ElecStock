using ElecStock.Contracts;
using ElecStock.Data;
using ElecStock.Data.Files;
using ElecStock.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using SkiaSharp;

namespace ElecStock.Tests;

/// <summary>
/// The stock service against a real PostgreSQL: the elecstock_test database of the dev compose
/// (postgres-compose/, port 5433), dropped and migrated for each test. Skipped when it is not running;
/// ELECSTOCK_TEST_DB overrides the connection string, and then the database must answer: the tests fail
/// instead of skipping (the CI sets it).
/// </summary>
public sealed class StockServiceTests : IAsyncLifetime
{
    static readonly string? ConnectionStringOverride = Environment.GetEnvironmentVariable("ELECSTOCK_TEST_DB");
    static readonly string ConnectionString = ConnectionStringOverride
        ?? "Host=localhost;Port=5433;Database=elecstock_test;Username=elecstock;Password=password";

    readonly string _storage = Path.Combine(Path.GetTempPath(), "elecstock-tests-" + Guid.NewGuid().ToString("N"));
    readonly List<ChangeEvent> _changes = [];
    StockService _stock = null!;
    CallerContext _caller = null!;
    KicadLibrary _kicad = null!;

    sealed class Factory(DbContextOptions<StockDb> options) : IDbContextFactory<StockDb>
    {
        public StockDb CreateDbContext() => new(options);
    }

    sealed class Notifier(List<ChangeEvent> changes) : IChangeNotifier
    {
        public void Publish(ChangeEvent change) => changes.Add(change);
    }

    sealed class Env(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "ElecStock.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    sealed class Http : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    public async ValueTask InitializeAsync()
    {
        try
        {
            await using var probe = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres", Timeout = 2 }.ConnectionString);
            await probe.OpenAsync();
        }
        catch (Exception ex) when (ConnectionStringOverride is null
            && ex is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException)
        {
            Assert.Skip($"No test database ({ex.Message}): start postgres-compose.");
        }

        var options = new DbContextOptionsBuilder<StockDb>().UseNpgsql(ConnectionString).Options;
        var factory = new Factory(options);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
        }

        var files = new FileStore(Options.Create(new StorageOptions { Root = _storage }), new Env(_storage));
        var images = new ImageProcessor(files);
        _caller = new CallerContext();
        _stock = new StockService(factory, _caller, new Notifier(_changes), files, images,
            new StockImporter(factory, files, images, TimeProvider.System), new Http(),
            Options.Create(new ServerOptions()), TimeProvider.System);
        _kicad = new KicadLibrary(factory, files);
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_storage, recursive: true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    async Task<PartDetail> AddPart(string reference, string qty) =>
        await _stock.SavePartAsync(new PartEdit { Ref = reference, Category = "mosfet", Designation = reference, Qty = qty });

    async Task<RevisionDetail> NewRevision()
    {
        var project = await _stock.SaveProjectAsync(new ProjectEdit { Name = "WeatherStation" });
        project = await _stock.SaveDeviceAsync(new DeviceEdit { ProjectId = project.Id, Name = "LedStrip2" });
        return (await _stock.GetRevisionAsync(project.Devices[0].Revisions[0].Id))!;
    }

    [Fact]
    public async Task Refs_are_unique_ignoring_case()
    {
        await AddPart("IRF4905", "9");
        var ex = await Assert.ThrowsAsync<StockException>(() => AddPart("irf4905", "1"));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public async Task Reserve_then_build_decrements_the_stock()
    {
        var part = await AddPart("IRF4905", "9");
        var rev = await NewRevision();
        rev = await _stock.SaveAllocationAsync(new AllocationEdit { RevisionId = rev.Id, PartId = part.Part.Id, Qty = 4, Designators = "Q201, Q202 Q301 Q302" });
        Assert.Equal("Q201 Q202 Q301 Q302", rev.Lines[0].Designators);

        var stock = await _stock.GetStockAsync();
        Assert.Equal(4, stock.Parts[0].Reserved);
        Assert.Equal("5", stock.Parts[0].AvailableShort);

        var plan = await _stock.PlanBuildAsync(rev.Id);
        Assert.Equal(new BuildChange("IRF4905", "9", "5", 4, ""), plan.Changes.Single());

        await _stock.ApplyBuildAsync(rev.Id);
        var after = (await _stock.GetPartAsync(part.Part.Id))!;
        Assert.Equal("5", after.Part.Qty);
        Assert.Equal(0, after.Part.Reserved);
        Assert.Equal(4, after.Part.Used);
        Assert.Equal(RevisionStatus.Built, (await _stock.GetRevisionAsync(rev.Id))!.Status);
        Assert.Contains(after.History, h => h.Kind == ActivityKinds.Built && h.QtyBefore == "9" && h.QtyAfter == "5");
    }

    [Fact]
    public async Task Status_built_requires_mark_as_built_while_parts_are_reserved()
    {
        var part = await AddPart("IRLB8721", "9");
        var rev = await NewRevision();
        await _stock.SaveAllocationAsync(new AllocationEdit { RevisionId = rev.Id, PartId = part.Part.Id, Qty = 1 });
        await Assert.ThrowsAsync<StockException>(() =>
            _stock.SaveRevisionAsync(new RevisionEdit { Id = rev.Id, DeviceId = rev.DeviceId, Rev = rev.Rev, Status = RevisionStatus.Built }));
    }

    [Fact]
    public async Task Short_lines_become_on_order_then_receiving_fills_the_stock()
    {
        var part = await AddPart("CP-470u", "2");
        var rev = await NewRevision();
        rev = await _stock.SaveAllocationAsync(new AllocationEdit { RevisionId = rev.Id, PartId = part.Part.Id, Qty = 4 });
        Assert.Equal(LineFlag.Short, rev.Lines[0].Flag);

        var shopping = await _stock.GetShoppingListAsync();
        Assert.Equal(2, shopping.Items.Single().Shortage);

        var order = await _stock.AddToOrderAsync(new AddToOrder { PartId = part.Part.Id, Qty = 10, Supplier = "LCSC", UnitPrice = 0.05m });
        Assert.Equal(OrderStatus.Draft, order.Status);
        order = await _stock.SaveOrderAsync(new OrderEdit { Id = order.Id, Supplier = "LCSC", Status = OrderStatus.Ordered });
        Assert.NotNull(order.OrderedAt);
        Assert.Equal(LineFlag.OnOrder, (await _stock.GetRevisionAsync(rev.Id))!.Lines[0].Flag);

        _caller.Source = ActivitySource.Mcp;
        await _stock.ReceiveOrderAsync(new ReceiveOrder { OrderId = order.Id });
        var after = (await _stock.GetPartAsync(part.Part.Id))!;
        Assert.Equal("12", after.Part.Qty);
        Assert.Contains(after.History, h => h.Kind == ActivityKinds.OrderReceived && h.Source == ActivitySource.Mcp);
        Assert.Equal(0.2m, (await _stock.GetRevisionAsync(rev.Id))!.EstimatedCost);
    }

    [Fact]
    public async Task Removing_from_the_shopping_list_drops_drafts_and_stops_restocking_until_ordered_again()
    {
        var part = await AddPart("IPP120P04P4L-03", "0");
        var shortPart = await AddPart("BZX55C12", "0");
        var rev = await NewRevision();
        await _stock.SaveAllocationAsync(new AllocationEdit { RevisionId = rev.Id, PartId = shortPart.Part.Id, Qty = 2 });
        var draft = await _stock.AddToOrderAsync(new AddToOrder { PartId = part.Part.Id, Qty = 3 });
        await _stock.AddToOrderAsync(new AddToOrder { PartId = shortPart.Part.Id, Qty = 5, OrderId = draft.Id });

        // planned, out of stock and needed by nothing: off the draft and off the list
        await _stock.RemoveFromShoppingAsync(part.Part.Id);
        // short for a reservation: off the draft, still listed, and nothing more to remove
        await _stock.RemoveFromShoppingAsync(shortPart.Part.Id);
        Assert.Empty((await _stock.GetOrderAsync(draft.Id))!.Lines);
        var item = (await _stock.GetShoppingListAsync()).Items.Single();
        Assert.Equal((shortPart.Part.Id, ShoppingReason.Short, 0), (item.PartId, item.Reason, item.Planned));
        await Assert.ThrowsAsync<StockException>(() => _stock.RemoveFromShoppingAsync(shortPart.Part.Id));

        await _stock.AddToOrderAsync(new AddToOrder { PartId = part.Part.Id, Qty = 1, OrderId = draft.Id });
        await _stock.DeleteOrderAsync(draft.Id);
        Assert.Contains((await _stock.GetShoppingListAsync()).Items, i => i.PartId == part.Part.Id && i.Reason == ShoppingReason.OutOfStock);
    }

    [Fact]
    public async Task Project_icon_keeps_an_svg_shrinks_an_image_and_trashes_the_old_one()
    {
        var project = await _stock.SaveProjectAsync(new ProjectEdit { Name = "WeatherStation" });
        // a DOCTYPE, as Inkscape and Illustrator write: skipped, never fetched
        var svg = """<?xml version="1.0"?><!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd"><svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><rect width="10" height="10"/></svg>"""u8.ToArray();
        project = await _stock.SetProjectIconAsync(project.Id, "logo.svg", new MemoryStream(svg));
        var svgFile = Path.Combine(_storage, project.IconUrl!["files/".Length..]);
        Assert.Equal(svg, await File.ReadAllBytesAsync(svgFile));
        await Assert.ThrowsAsync<StockException>(() => _stock.SetProjectIconAsync(project.Id, "fake.svg", new MemoryStream("<html/>"u8.ToArray())));

        using var bitmap = new SKBitmap(600, 300);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        project = await _stock.SetProjectIconAsync(project.Id, "logo.jpg", new MemoryStream(png.ToArray()));
        Assert.EndsWith(".png", project.IconUrl);
        using (var icon = SKBitmap.Decode(Path.Combine(_storage, project.IconUrl!["files/".Length..])))
            Assert.Equal((256, 128), (icon.Width, icon.Height));
        Assert.False(File.Exists(svgFile));

        project = await _stock.RemoveProjectIconAsync(project.Id);
        Assert.Null(project.IconUrl);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_storage, FileStore.TrashDir)).Length);
    }

    [Fact]
    public async Task Bom_apply_merges_duplicates_and_releases_others()
    {
        var r1k = await AddPart("R-1k", "10+");
        var r10k = await AddPart("R-10k", "10+");
        var old = await AddPart("R-47k", "10+");
        var rev = await NewRevision();
        await _stock.SaveAllocationAsync(new AllocationEdit { RevisionId = rev.Id, PartId = old.Part.Id, Qty = 2 });

        var preview = await _stock.PreviewBomAsync(rev.Id, """
            ref,qty,designation,value,library_ref,supplier,notes
            R1,1,pull-down,1k,,stock,
            R2 R3,2,divider,1k,,stock,
            R4,1,pull-up,10k,,stock,
            """);
        Assert.All(preview.Lines, l => Assert.NotNull(l.PartId));
        Assert.Contains(preview.Warnings, w => w.Contains("R-1k"));

        var result = await _stock.ApplyBomAsync(new BomApply
        {
            RevisionId = rev.Id,
            RemoveOthers = true,
            Lines = preview.Lines.Select(l => new BomApplyLine { PartId = l.PartId!.Value, Qty = l.Qty, Designators = l.Designators }).ToList(),
        });
        Assert.Equal((2, 0, 1), (result.Added, result.Updated, result.Removed));
        var line1k = result.Revision.Lines.Single(l => l.PartId == r1k.Part.Id);
        Assert.Equal(3, line1k.Qty);
        Assert.Equal("R1 R2 R3", line1k.Designators);
        Assert.Contains(result.Revision.Lines, l => l.PartId == r10k.Part.Id);
    }

    [Fact]
    public async Task Changes_are_logged_and_pushed()
    {
        var part = await AddPart("BC547B", "");
        await _stock.SavePartAsync(new PartEdit { Id = part.Part.Id, Ref = "BC547B", Category = "transistor", Designation = "NPN", Qty = "12", QtyReason = "counted" });
        var history = await _stock.GetActivityAsync(new ActivityQuery { PartId = part.Part.Id });
        Assert.Contains(history, h => h.Kind == ActivityKinds.QtyChanged && h.Summary.Contains("(counted)") && h.QtyAfter == "12");
        Assert.Contains(history, h => h.Kind == ActivityKinds.PartUpdated && h.Summary.Contains("category"));
        Assert.Contains(_changes, c => c.Scope == ChangeScope.Part && c.Id == part.Part.Id);
    }

    // ---------- KiCad library ----------

    static MemoryStream Text(string text) => new(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task An_upload_for_a_part_fills_its_empty_links()
    {
        var part = await AddPart("INA226-Module", "3");
        var result = await _stock.UploadKicadFileAsync("Stock.kicad_sym", Text(KicadFilesTests.SymbolLib), part.Part.Id);
        Assert.Equal(["INA226-Module", "INA226-Module-v2"], result.Added.Select(i => i.Name));
        Assert.Empty(result.Linked); // two symbols: which one is ambiguous

        result = await _stock.UploadKicadFileAsync("fp.kicad_mod", Text(KicadFilesTests.Footprint), part.Part.Id, "github.com/mrtnvgr/KiCad_ESP32-C3-SuperMini");
        Assert.Equal(["footprint Stock:ESP32-C3-SuperMini"], result.Linked);
        Assert.Equal("github.com/mrtnvgr/KiCad_ESP32-C3-SuperMini", result.Added.Single().Source);

        var detail = (await _stock.GetPartAsync(part.Part.Id))!;
        Assert.Equal("Stock:ESP32-C3-SuperMini", detail.Part.KicadFootprint);
        Assert.Contains(detail.History, h => h.Kind == ActivityKinds.KicadChanged && h.Summary.Contains("linked"));
        Assert.Contains(_changes, c => c.Scope == ChangeScope.Kicad);

        // Same name again: replaced, not duplicated.
        result = await _stock.UploadKicadFileAsync("again.kicad_mod", Text(KicadFilesTests.Footprint));
        Assert.Single(result.Replaced);
        var library = await _stock.GetKicadLibraryAsync();
        Assert.Equal(3, library.Items.Count);
        var footprint = library.Items.Single(i => i.Kind == KicadItemKind.Footprint);
        Assert.Equal(["INA226-Module"], footprint.UsedBy);
        Assert.Empty(footprint.MissingModels); // its model paths are outside the stock library
    }

    [Fact]
    public async Task Stock_links_must_exist_and_get_the_library_case()
    {
        var part = await AddPart("ESP32-C3-SuperMini", "2");
        var edit = PartEdit.From(part.Part);
        edit.KicadFootprint = "Stock:ESP32-C3-SuperMini";
        var ex = await Assert.ThrowsAsync<StockException>(() => _stock.SavePartAsync(edit));
        Assert.Contains("upload it first", ex.Message);

        await _stock.UploadKicadFileAsync("fp.kicad_mod", Text(KicadFilesTests.Footprint));
        edit.KicadFootprint = "stock:esp32-c3-supermini";
        edit.KicadSymbol = "MCU_Espressif:ESP32-C3";
        var saved = await _stock.SavePartAsync(edit);
        Assert.Equal(("MCU_Espressif:ESP32-C3", "Stock:ESP32-C3-SuperMini"), (saved.Part.KicadSymbol, saved.Part.KicadFootprint));

        edit = PartEdit.From(saved.Part);
        edit.KicadSymbol = "ESP32-C3";
        ex = await Assert.ThrowsAsync<StockException>(() => _stock.SavePartAsync(edit));
        Assert.Contains("Library:Name", ex.Message);

        // A quantity change keeps the links.
        edit = PartEdit.From(saved.Part);
        edit.Qty = "5";
        saved = await _stock.SavePartAsync(edit);
        Assert.Equal("Stock:ESP32-C3-SuperMini", saved.Part.KicadFootprint);
    }

    [Fact]
    public async Task Linked_items_cannot_be_removed()
    {
        var part = await AddPart("INA226-Module", "3");
        await _stock.UploadKicadFileAsync("Stock.kicad_sym", Text(KicadFilesTests.SymbolLib));
        var edit = PartEdit.From(part.Part);
        edit.KicadSymbol = "Stock:INA226-Module";
        await _stock.SavePartAsync(edit);

        var items = (await _stock.GetKicadLibraryAsync()).Items;
        var parent = items.Single(i => i.Name == "INA226-Module");
        var ex = await Assert.ThrowsAsync<StockException>(() => _stock.DeleteKicadItemAsync(parent.Id));
        Assert.Contains("INA226-Module", ex.Message);

        edit.KicadSymbol = "";
        edit.Id = part.Part.Id;
        await _stock.SavePartAsync(edit);
        ex = await Assert.ThrowsAsync<StockException>(() => _stock.DeleteKicadItemAsync(parent.Id));
        Assert.Contains("extended by INA226-Module-v2", ex.Message);

        await _stock.DeleteKicadItemAsync(items.Single(i => i.Name == "INA226-Module-v2").Id);
        await _stock.DeleteKicadItemAsync(parent.Id);
        Assert.Empty((await _stock.GetKicadLibraryAsync()).Items);
    }

    [Fact]
    public async Task A_derived_symbol_needs_its_parent()
    {
        var derivedOnly = KicadFilesTests.SymbolLib[..KicadFilesTests.SymbolLib.IndexOf("\t(symbol \"INA226-Module\"", StringComparison.Ordinal)]
            + KicadFilesTests.SymbolLib[KicadFilesTests.SymbolLib.IndexOf("\t(symbol \"INA226-Module-v2\"", StringComparison.Ordinal)..];
        var ex = await Assert.ThrowsAsync<StockException>(() => _stock.UploadKicadFileAsync("v2.kicad_sym", Text(derivedOnly)));
        Assert.Contains("extends 'INA226-Module'", ex.Message);
    }

    [Fact]
    public async Task The_library_zip_has_the_stock_layout_with_models_linked()
    {
        await _stock.UploadKicadFileAsync("Stock.kicad_sym", Text(KicadFilesTests.SymbolLib));
        await _stock.UploadKicadFileAsync("fp.kicad_mod", Text(KicadFilesTests.Footprint));
        await _stock.UploadKicadFileAsync("ESP32-C3-SuperMini.step", Text("ISO-10303-21;\nHEADER;\n"));

        using var buffer = new MemoryStream();
        var version = await _kicad.WriteZipAsync(buffer, CancellationToken.None);
        Assert.Equal(version, await _kicad.VersionAsync(CancellationToken.None));
        Assert.Equal(version, (await _stock.GetKicadLibraryAsync()).Version);

        buffer.Position = 0;
        using var zip = new System.IO.Compression.ZipArchive(buffer);
        string Read(string path)
        {
            using var reader = new StreamReader(zip.GetEntry(path)!.Open());
            return reader.ReadToEnd();
        }
        Assert.Equal(["KiCad/3D/ESP32-C3-SuperMini.step", "KiCad/README.md", "KiCad/Stock.kicad_sym", "KiCad/Stock.pretty/ESP32-C3-SuperMini.kicad_mod", KicadLibrary.ManifestFile],
            zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.Equal(2, ElecStock.Kicad.KicadFiles.ReadSymbols(Read("KiCad/Stock.kicad_sym")).Count);
        Assert.Equal("${ELEC_STOCK}/KiCad/3D/ESP32-C3-SuperMini.step", ElecStock.Kicad.KicadFiles.Models(Read("KiCad/Stock.pretty/ESP32-C3-SuperMini.kicad_mod"))[0]);
        Assert.Contains(version, Read(KicadLibrary.ManifestFile));

        // The model is used by the footprint: it stays.
        var model = (await _stock.GetKicadLibraryAsync()).Items.Single(i => i.Kind == KicadItemKind.Model);
        var ex = await Assert.ThrowsAsync<StockException>(() => _stock.DeleteKicadItemAsync(model.Id));
        Assert.Contains("used by the footprint", ex.Message);
    }

    [Fact]
    public async Task Locations_group_by_code_and_hold_spare_stock()
    {
        var module = await _stock.SaveLocationAsync(new LocationEdit { Code = "DIO", Name = "Diodes" });
        var drawer = await _stock.SaveLocationAsync(new LocationEdit { Code = "DIO-1" });
        var compartment = await _stock.SaveLocationAsync(new LocationEdit { Code = "DIO-1.5" });
        var spare = await _stock.SaveLocationAsync(new LocationEdit { Code = "DIO-4" });
        var other = await _stock.SaveLocationAsync(new LocationEdit { Code = "DIODE" });

        var part = await AddPart("1N4007", "80");
        var edit = PartEdit.From(part.Part);
        edit.LocationId = compartment.Id;
        edit.SpareLocationId = compartment.Id;
        Assert.Contains("main location", (await Assert.ThrowsAsync<StockException>(() => _stock.SavePartAsync(edit))).Message);
        edit.SpareLocationId = spare.Id;
        var saved = (await _stock.SavePartAsync(edit)).Part;
        Assert.Equal("DIO-1.5", saved.LocationCode);
        Assert.Equal("DIO-4", saved.SpareLocationCode);
        Assert.Contains("dio-4", saved.SearchText);
        Assert.Contains("spare location", (await _stock.GetPartAsync(saved.Id))!.History[0].Summary);
        await _stock.SavePartAsync(new PartEdit { Ref = "1N5819", Category = "diode", Designation = "Schottky", Qty = "30", LocationId = other.Id });

        var locations = await _stock.GetLocationsAsync();
        Assert.Equal(["DIO", "DIO-1", "DIO-1.5", "DIO-4", "DIODE"], locations.Select(l => l.Code));
        var dio = locations.Single(l => l.Code == "DIO");
        Assert.Equal((0, 1, 0), (dio.PartCount, dio.TotalPartCount, dio.Depth));
        Assert.Equal(("DIO-1", 2), (locations.Single(l => l.Code == "DIO-1.5").ParentCode, locations.Single(l => l.Code == "DIO-1.5").Depth));
        Assert.Equal(1, locations.Single(l => l.Code == "DIO-4").SparePartCount);
        Assert.Equal("", locations.Single(l => l.Code == "DIODE").ParentCode);

        var detail = (await _stock.GetLocationAsync("dio"))!;
        Assert.Equal(["DIO-1", "DIO-4"], detail.Children.Select(l => l.Code));
        Assert.Equal(["1N4007"], detail.Parts.Select(p => p.Ref));
        Assert.Equal(["1N4007"], detail.SpareParts.Select(p => p.Ref));
        var inner = (await _stock.GetLocationAsync("DIO-1.5"))!;
        Assert.Equal(["DIO", "DIO-1"], inner.Parents.Select(l => l.Code));
        Assert.Empty(inner.SpareParts);

        await _stock.DeleteLocationAsync(spare.Id);
        var after = (await _stock.GetPartAsync(saved.Id))!.Part;
        Assert.Equal(("DIO-1.5", (int?)null), (after.LocationCode, after.SpareLocationId));
        Assert.Equal(module.Id, (await _stock.GetLocationAsync("DIO"))!.Location.Id);
    }
}
