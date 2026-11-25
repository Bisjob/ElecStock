using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElecStock.Contracts;

namespace ElecStock.Appli.Services;

/// <summary><see cref="IStockApi"/> over the HTTP API of the server (see <c>StockApi</c> in ElecStock.Server).</summary>
public sealed class HttpStockApi(ServerConnection server) : IStockApi
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    HttpClient Http => server.Http;

    /// <summary>App-origin prefix of the server files, served by <see cref="MainPage"/> (no mixed content in the WebView).</summary>
    public const string ProxyPrefix = "_server/";

    public string ResolveUrl(string relative) =>
        relative.StartsWith("files/", StringComparison.OrdinalIgnoreCase) ? ProxyPrefix + relative : ExternalUrl(relative);

    public string ExternalUrl(string relative) =>
        Uri.TryCreate(relative, UriKind.Absolute, out var absolute) && absolute.Scheme.StartsWith("http")
            ? relative
            : new Uri(Http.BaseAddress!, relative.TrimStart('/')).ToString();

    // ---------- plumbing ----------

    static async Task Check(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string? detail = null;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            if (problem.TryGetProperty("detail", out var d)) detail = d.GetString();
            else if (problem.TryGetProperty("title", out var t)) detail = t.GetString();
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }

        if (response.StatusCode == HttpStatusCode.BadRequest && detail != null) throw new StockException(detail);
        throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}" + (detail != null ? $": {detail}" : ""), null, response.StatusCode);
    }

    async Task<T> Read<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await Check(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    async Task<T> Get<T>(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct);
        return await Read<T>(response, ct);
    }

    async Task<T?> GetOrNull<T>(string url, CancellationToken ct) where T : class
    {
        using var response = await Http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await Read<T>(response, ct);
    }

    async Task<T> Post<T>(string url, object? body, CancellationToken ct)
    {
        using var response = await Http.PostAsJsonAsync(url, body, Json, ct);
        return await Read<T>(response, ct);
    }

    async Task Send(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body != null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await Http.SendAsync(request, ct);
        await Check(response, ct);
    }

    async Task<T> Delete<T>(string url, CancellationToken ct)
    {
        using var response = await Http.DeleteAsync(url, ct);
        return await Read<T>(response, ct);
    }

    async Task<HttpResponseMessage> Upload(string url, Stream content, string contentType, CancellationToken ct)
    {
        var body = new StreamContent(content);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        var response = await Http.PostAsync(url, body, ct);
        await Check(response, ct);
        return response;
    }

    static string Q(string value) => Uri.EscapeDataString(value);

    static string ImageType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        _ => "image/jpeg",
    };

    // ---------- stock ----------

    public Task<StockOverview> GetStockAsync(CancellationToken ct = default) => Get<StockOverview>("api/stock", ct);
    public Task<PartDetail?> GetPartAsync(int id, CancellationToken ct = default) => GetOrNull<PartDetail>($"api/parts/{id}", ct);
    public Task<PartDetail?> GetPartByRefAsync(string reference, CancellationToken ct = default) => GetOrNull<PartDetail>($"api/parts/by-ref/{Q(reference)}", ct);
    public Task<PartDetail> SavePartAsync(PartEdit edit, CancellationToken ct = default) => Post<PartDetail>("api/parts", edit, ct);
    public Task DeletePartAsync(int id, CancellationToken ct = default) => Send(HttpMethod.Delete, $"api/parts/{id}", null, ct);
    public Task SaveSheetAsync(int partId, string? markdown, CancellationToken ct = default) => Send(HttpMethod.Put, $"api/parts/{partId}/sheet", new { markdown }, ct);
    public Task SaveFreeNotesAsync(int partId, string markdown, CancellationToken ct = default) => Send(HttpMethod.Put, $"api/parts/{partId}/notes", new { markdown }, ct);

    public async Task<PhotoDto> UploadPhotoAsync(PhotoOwner owner, int ownerId, string fileName, Stream content, CancellationToken ct = default)
    {
        using var response = await Upload($"api/photos/{owner}/{ownerId}?fileName={Q(fileName)}", content, ImageType(fileName), ct);
        return (await response.Content.ReadFromJsonAsync<PhotoDto>(Json, ct))!;
    }

    public Task<PhotoDto> UpdatePhotoAsync(int photoId, PhotoEdit edit, CancellationToken ct = default) => Put<PhotoDto>($"api/photos/{photoId}", edit, ct);
    public Task SetCoverAsync(int photoId, CancellationToken ct = default) => Send(HttpMethod.Post, $"api/photos/{photoId}/cover", null, ct);
    public Task DeletePhotoAsync(int photoId, CancellationToken ct = default) => Send(HttpMethod.Delete, $"api/photos/{photoId}", null, ct);

    async Task<T> Put<T>(string url, object body, CancellationToken ct)
    {
        using var response = await Http.PutAsJsonAsync(url, body, Json, ct);
        return await Read<T>(response, ct);
    }

    public async Task UploadDatasheetAsync(int partId, string fileName, Stream content, CancellationToken ct = default)
    {
        using var _ = await Upload($"api/parts/{partId}/datasheet?fileName={Q(fileName)}", content, "application/pdf", ct);
    }

    public Task FetchDatasheetAsync(int partId, CancellationToken ct = default) => Send(HttpMethod.Post, $"api/parts/{partId}/datasheet/fetch", null, ct);
    public Task DeleteDatasheetAsync(int partId, CancellationToken ct = default) => Send(HttpMethod.Delete, $"api/parts/{partId}/datasheet", null, ct);

    // ---------- projects ----------

    public Task<List<ProjectSummary>> GetProjectsAsync(CancellationToken ct = default) => Get<List<ProjectSummary>>("api/projects", ct);
    public Task<ProjectDetail?> GetProjectAsync(int id, CancellationToken ct = default) => GetOrNull<ProjectDetail>($"api/projects/{id}", ct);
    public Task<ProjectDetail> SaveProjectAsync(ProjectEdit edit, CancellationToken ct = default) => Post<ProjectDetail>("api/projects", edit, ct);
    public Task DeleteProjectAsync(int id, CancellationToken ct = default) => Send(HttpMethod.Delete, $"api/projects/{id}", null, ct);

    public async Task<ProjectDetail> SetProjectIconAsync(int projectId, string fileName, Stream content, CancellationToken ct = default)
    {
        using var response = await Upload($"api/projects/{projectId}/icon?fileName={Q(fileName)}", content, ImageType(fileName), ct);
        return (await response.Content.ReadFromJsonAsync<ProjectDetail>(Json, ct))!;
    }

    public Task<ProjectDetail> RemoveProjectIconAsync(int projectId, CancellationToken ct = default) => Delete<ProjectDetail>($"api/projects/{projectId}/icon", ct);
    public Task<ProjectDetail> SaveDeviceAsync(DeviceEdit edit, CancellationToken ct = default) => Post<ProjectDetail>("api/devices", edit, ct);
    public Task<ProjectDetail> DeleteDeviceAsync(int id, CancellationToken ct = default) => Delete<ProjectDetail>($"api/devices/{id}", ct);
    public Task<RevisionDetail?> GetRevisionAsync(int id, CancellationToken ct = default) => GetOrNull<RevisionDetail>($"api/revisions/{id}", ct);
    public Task<RevisionDetail> SaveRevisionAsync(RevisionEdit edit, CancellationToken ct = default) => Post<RevisionDetail>("api/revisions", edit, ct);
    public Task DeleteRevisionAsync(int id, CancellationToken ct = default) => Send(HttpMethod.Delete, $"api/revisions/{id}", null, ct);
    public Task<RevisionDetail> SaveAllocationAsync(AllocationEdit edit, CancellationToken ct = default) => Post<RevisionDetail>("api/allocations", edit, ct);
    public Task<RevisionDetail> DeleteAllocationAsync(int id, CancellationToken ct = default) => Delete<RevisionDetail>($"api/allocations/{id}", ct);
    public Task<BuildPlan> PlanBuildAsync(int revisionId, CancellationToken ct = default) => Get<BuildPlan>($"api/revisions/{revisionId}/build", ct);
    public Task<BuildPlan> ApplyBuildAsync(int revisionId, CancellationToken ct = default) => Post<BuildPlan>($"api/revisions/{revisionId}/build", null, ct);
    public Task<BomPreview> PreviewBomAsync(int revisionId, string csv, CancellationToken ct = default) => Post<BomPreview>($"api/revisions/{revisionId}/bom/preview", new { csv }, ct);
    public Task<BomApplyResult> ApplyBomAsync(BomApply apply, CancellationToken ct = default) => Post<BomApplyResult>("api/bom/apply", apply, ct);

    // ---------- shopping and orders ----------

    public Task<ShoppingList> GetShoppingListAsync(CancellationToken ct = default) => Get<ShoppingList>("api/shopping", ct);
    public Task RemoveFromShoppingAsync(int partId, CancellationToken ct = default) => Send(HttpMethod.Delete, $"api/shopping/{partId}", null, ct);
    public Task<List<OrderSummary>> GetOrdersAsync(CancellationToken ct = default) => Get<List<OrderSummary>>("api/orders", ct);
    public Task<OrderDetail?> GetOrderAsync(int id, CancellationToken ct = default) => GetOrNull<OrderDetail>($"api/orders/{id}", ct);
    public Task<OrderDetail> SaveOrderAsync(OrderEdit edit, CancellationToken ct = default) => Post<OrderDetail>("api/orders", edit, ct);
    public Task DeleteOrderAsync(int id, CancellationToken ct = default) => Send(HttpMethod.Delete, $"api/orders/{id}", null, ct);
    public Task<OrderDetail> SaveOrderLineAsync(OrderLineEdit edit, CancellationToken ct = default) => Post<OrderDetail>("api/order-lines", edit, ct);
    public Task<OrderDetail> DeleteOrderLineAsync(int lineId, CancellationToken ct = default) => Delete<OrderDetail>($"api/order-lines/{lineId}", ct);
    public Task<OrderDetail> AddToOrderAsync(AddToOrder request, CancellationToken ct = default) => Post<OrderDetail>("api/orders/add", request, ct);
    public Task<OrderDetail> ReceiveOrderAsync(ReceiveOrder request, CancellationToken ct = default) => Post<OrderDetail>($"api/orders/{request.OrderId}/receive", request, ct);

    // ---------- locations ----------

    public Task<List<LocationDto>> GetLocationsAsync(CancellationToken ct = default) => Get<List<LocationDto>>("api/locations", ct);
    public Task<LocationDetail?> GetLocationAsync(string code, CancellationToken ct = default) => GetOrNull<LocationDetail>($"api/locations/{Q(code)}", ct);
    public Task<LocationDto> SaveLocationAsync(LocationEdit edit, CancellationToken ct = default) => Post<LocationDto>("api/locations", edit, ct);
    public Task DeleteLocationAsync(int id, CancellationToken ct = default) => Send(HttpMethod.Delete, $"api/locations/{id}", null, ct);

    // ---------- history and administration ----------

    public Task<List<ActivityDto>> GetActivityAsync(ActivityQuery query, CancellationToken ct = default)
    {
        var args = new List<string> { $"take={query.Take}" };
        if (query.PartId != null) args.Add($"partId={query.PartId}");
        if (query.ProjectId != null) args.Add($"projectId={query.ProjectId}");
        if (query.RevisionId != null) args.Add($"revisionId={query.RevisionId}");
        if (query.OrderId != null) args.Add($"orderId={query.OrderId}");
        if (query.Source != null) args.Add($"source={query.Source}");
        return Get<List<ActivityDto>>("api/activity?" + string.Join('&', args), ct);
    }

    public async Task<ImportReport> ImportAsync(Stream zip, bool replace, CancellationToken ct = default)
    {
        using var response = await Upload($"api/import?replace={(replace ? "true" : "false")}", zip, "application/zip", ct);
        return (await response.Content.ReadFromJsonAsync<ImportReport>(Json, ct))!;
    }

    public Task<ServerInfo> GetServerInfoAsync(CancellationToken ct = default) => Get<ServerInfo>("api/info", ct);
}
