namespace ElecStock.Contracts;

/// <summary>
/// Everything the UI can read or change. The web host implements it in-process on the database;
/// the MAUI app implements it over the HTTP API of the server. Writes throw
/// <see cref="StockException"/> when a stock rule is broken, and return the refreshed data.
/// </summary>
public interface IStockApi
{
    /// <summary>URL to embed a server-relative file (img, iframe) in the page.</summary>
    string ResolveUrl(string relative);

    /// <summary>URL of a server-relative file for a link opened outside the app (new tab, browser, download).</summary>
    string ExternalUrl(string relative) => ResolveUrl(relative);

    // ---------- stock ----------

    Task<StockOverview> GetStockAsync(CancellationToken ct = default);
    Task<PartDetail?> GetPartAsync(int id, CancellationToken ct = default);
    Task<PartDetail?> GetPartByRefAsync(string reference, CancellationToken ct = default);
    Task<PartDetail> SavePartAsync(PartEdit edit, CancellationToken ct = default);
    Task DeletePartAsync(int id, CancellationToken ct = default);
    Task SaveSheetAsync(int partId, string? markdown, CancellationToken ct = default);
    Task SaveFreeNotesAsync(int partId, string markdown, CancellationToken ct = default);

    Task<PhotoDto> UploadPhotoAsync(PhotoOwner owner, int ownerId, string fileName, Stream content, CancellationToken ct = default);
    Task<PhotoDto> UpdatePhotoAsync(int photoId, PhotoEdit edit, CancellationToken ct = default);
    Task SetCoverAsync(int photoId, CancellationToken ct = default);
    Task DeletePhotoAsync(int photoId, CancellationToken ct = default);

    Task UploadDatasheetAsync(int partId, string fileName, Stream content, CancellationToken ct = default);
    /// <summary>Downloads the datasheet link of the part and keeps it as the offline copy.</summary>
    Task FetchDatasheetAsync(int partId, CancellationToken ct = default);
    Task DeleteDatasheetAsync(int partId, CancellationToken ct = default);

    // ---------- projects ----------

    Task<List<ProjectSummary>> GetProjectsAsync(CancellationToken ct = default);
    Task<ProjectDetail?> GetProjectAsync(int id, CancellationToken ct = default);
    Task<ProjectDetail> SaveProjectAsync(ProjectEdit edit, CancellationToken ct = default);
    Task DeleteProjectAsync(int id, CancellationToken ct = default);
    /// <summary>Replaces the project icon with an SVG (kept as is) or an image (shrunk to a PNG).</summary>
    Task<ProjectDetail> SetProjectIconAsync(int projectId, string fileName, Stream content, CancellationToken ct = default);
    Task<ProjectDetail> RemoveProjectIconAsync(int projectId, CancellationToken ct = default);
    Task<ProjectDetail> SaveDeviceAsync(DeviceEdit edit, CancellationToken ct = default);
    Task<ProjectDetail> DeleteDeviceAsync(int id, CancellationToken ct = default);
    Task<RevisionDetail?> GetRevisionAsync(int id, CancellationToken ct = default);
    Task<RevisionDetail> SaveRevisionAsync(RevisionEdit edit, CancellationToken ct = default);
    Task DeleteRevisionAsync(int id, CancellationToken ct = default);
    Task<RevisionDetail> SaveAllocationAsync(AllocationEdit edit, CancellationToken ct = default);
    Task<RevisionDetail> DeleteAllocationAsync(int id, CancellationToken ct = default);
    Task<BuildPlan> PlanBuildAsync(int revisionId, CancellationToken ct = default);
    Task<BuildPlan> ApplyBuildAsync(int revisionId, CancellationToken ct = default);
    Task<BomPreview> PreviewBomAsync(int revisionId, string csv, CancellationToken ct = default);
    Task<BomApplyResult> ApplyBomAsync(BomApply apply, CancellationToken ct = default);

    // ---------- shopping and orders ----------

    Task<ShoppingList> GetShoppingListAsync(CancellationToken ct = default);
    /// <summary>Removes the part from the draft orders; out of stock and needed by nothing, it is also marked not to restock.</summary>
    Task RemoveFromShoppingAsync(int partId, CancellationToken ct = default);
    Task<List<OrderSummary>> GetOrdersAsync(CancellationToken ct = default);
    Task<OrderDetail?> GetOrderAsync(int id, CancellationToken ct = default);
    Task<OrderDetail> SaveOrderAsync(OrderEdit edit, CancellationToken ct = default);
    Task DeleteOrderAsync(int id, CancellationToken ct = default);
    Task<OrderDetail> SaveOrderLineAsync(OrderLineEdit edit, CancellationToken ct = default);
    Task<OrderDetail> DeleteOrderLineAsync(int lineId, CancellationToken ct = default);
    Task<OrderDetail> AddToOrderAsync(AddToOrder request, CancellationToken ct = default);
    Task<OrderDetail> ReceiveOrderAsync(ReceiveOrder request, CancellationToken ct = default);

    // ---------- locations ----------

    Task<List<LocationDto>> GetLocationsAsync(CancellationToken ct = default);
    Task<LocationDetail?> GetLocationAsync(string code, CancellationToken ct = default);
    Task<LocationDto> SaveLocationAsync(LocationEdit edit, CancellationToken ct = default);
    Task DeleteLocationAsync(int id, CancellationToken ct = default);

    // ---------- history and administration ----------

    Task<List<ActivityDto>> GetActivityAsync(ActivityQuery query, CancellationToken ct = default);
    /// <summary>Imports a zip of the stock folder (inventory.csv, allocations.csv, sheets, Media, Datasheets).</summary>
    Task<ImportReport> ImportAsync(Stream zip, bool replace, CancellationToken ct = default);
    Task<ServerInfo> GetServerInfoAsync(CancellationToken ct = default);
}

/// <summary>Raised when anything changes on the server (another client, Claude through MCP…).</summary>
public interface IStockEvents
{
    event Action<ChangeEvent>? Changed;
}
