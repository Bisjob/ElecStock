using ElecStock.Contracts;
using ElecStock.Data.Entities;
using ElecStock.Stock;
using Microsoft.EntityFrameworkCore;

namespace ElecStock.Data.Services;

public sealed partial class StockService
{
    static decimal? OrderTotal(Order o) =>
        o.Lines.Count > 0 && o.Lines.All(l => l.UnitPrice != null)
            ? o.Lines.Sum(l => (l.ReceivedQty ?? l.Qty) * l.UnitPrice!.Value) + (o.Shipping ?? 0)
            : null;

    static OrderSummary SummarizeOrder(Order o) => new()
    {
        Id = o.Id,
        Supplier = o.Supplier,
        OrderNumber = o.OrderNumber,
        Status = o.Status,
        OrderedAt = o.OrderedAt,
        ExpectedAt = o.ExpectedAt,
        ReceivedAt = o.ReceivedAt,
        LineCount = o.Lines.Count,
        Total = OrderTotal(o),
        Currency = o.Currency,
        CreatedAt = o.CreatedAt,
    };

    /// <summary>Drafts first, then orders on the way, then the rest by date.</summary>
    static IEnumerable<Order> SortOrders(IEnumerable<Order> orders) => orders
        .OrderBy(o => o.Status switch { OrderStatus.Draft => 0, OrderStatus.Ordered => 1, _ => 2 })
        .ThenByDescending(o => o.ReceivedAt ?? o.OrderedAt ?? DateOnly.FromDateTime(o.CreatedAt.UtcDateTime))
        .ThenByDescending(o => o.Id);

    public async Task<ShoppingList> GetShoppingListAsync(CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var s = await LoadSnapshotAsync(db, ct);
        var items = new List<ShoppingItem>();
        foreach (var part in s.Parts)
        {
            var qty = Qty.Parse(part.Qty);
            int reserved = s.Reserved(part.Id);
            int shortage = StockMath.Shortage(qty, reserved);
            int planned = s.Planned(part.Id);
            bool outOfStock = qty.Kind == QtyKind.Exact && qty.Value == 0 && !part.NoRestock;
            if (shortage == 0 && planned == 0 && !outOfStock) continue;

            var last = s.LastPriced.GetValueOrDefault(part.Id);
            items.Add(new ShoppingItem
            {
                PartId = part.Id,
                Ref = part.Ref,
                Designation = part.Designation,
                Category = part.Category,
                Qty = part.Qty,
                Reserved = reserved,
                Shortage = shortage,
                Reason = shortage > 0 ? ShoppingReason.Short : planned > 0 ? ShoppingReason.Planned : ShoppingReason.OutOfStock,
                OnOrder = s.OnOrder(part.Id),
                Planned = planned,
                NeededBy = s.Allocations[part.Id].Where(a => a.Status == AllocationStatus.Reserved)
                    .Select(a => $"{RevisionTitle(a.Revision)} ({a.Qty})").ToList(),
                LastSupplier = last?.Order.Supplier ?? "",
                LastUnitPrice = last?.UnitPrice,
                Currency = last?.Order.Currency ?? "EUR",
                CoverThumbUrl = Cover(s.Photos[part.Id]) is { } cover ? ThumbUrl(cover) : null,
            });
        }

        var open = await db.Orders.AsNoTracking().Include(o => o.Lines)
            .Where(o => o.Status == OrderStatus.Draft || o.Status == OrderStatus.Ordered)
            .ToListAsync(ct);
        return new ShoppingList
        {
            Items = items
                .OrderBy(i => i.Reason)
                .ThenByDescending(i => i.ToBuy)
                .ThenBy(i => i.Ref, PartVisuals.RefComparer)
                .ToList(),
            OpenOrders = SortOrders(open).Select(SummarizeOrder).ToList(),
        };
    }

    /// <summary>
    /// Takes a part off the shopping list: its draft order lines are removed and, when it is out of stock with no
    /// reservation and no placed order, it is marked not to restock. A shortage for reservations stays listed.
    /// </summary>
    public async Task RemoveFromShoppingAsync(int partId, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == partId, ct) ?? throw new StockException("This part no longer exists.");
        var lines = await db.OrderLines.Include(l => l.Order)
            .Where(l => l.PartId == partId && (l.Order.Status == OrderStatus.Draft || l.Order.Status == OrderStatus.Ordered))
            .ToListAsync(ct);
        var drafts = lines.Where(l => l.Order.Status == OrderStatus.Draft).ToList();
        var qty = Qty.Parse(part.Qty);
        bool reserved = await db.Allocations.AnyAsync(a => a.PartId == partId && a.Status == AllocationStatus.Reserved, ct);
        bool noRestock = qty.Kind == QtyKind.Exact && qty.Value == 0 && !reserved && drafts.Count == lines.Count;
        if (drafts.Count == 0 && (!noRestock || part.NoRestock))
            throw new StockException(reserved
                ? $"{part.Ref} is short for project reservations: release them in the board revision."
                : $"{part.Ref} is not planned in a draft order.");

        foreach (var line in drafts)
        {
            db.OrderLines.Remove(line);
            line.Order.UpdatedAt = Now;
            Log(db, ActivityKinds.OrderChanged, $"{line.Qty} × {part.Ref} removed from the draft order {(line.Order.Supplier.Length > 0 ? line.Order.Supplier : $"#{line.OrderId}")}",
                part, orderId: line.OrderId);
        }
        if (noRestock && !part.NoRestock)
        {
            part.NoRestock = true;
            part.UpdatedAt = Now;
            Log(db, ActivityKinds.PartUpdated, $"{part.Ref}: not to restock (off the shopping list)", part);
        }
        await db.SaveChangesAsync(ct);
        foreach (var orderId in drafts.Select(l => l.OrderId).Distinct()) Notify(ChangeScope.Order, orderId);
        Notify(ChangeScope.Part, partId);
    }

    public async Task<List<OrderSummary>> GetOrdersAsync(CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var orders = await db.Orders.AsNoTracking().Include(o => o.Lines).ToListAsync(ct);
        return SortOrders(orders).Select(SummarizeOrder).ToList();
    }

    public async Task<OrderDetail?> GetOrderAsync(int id, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var order = await db.Orders.AsNoTracking().Include(o => o.Lines).ThenInclude(l => l.Part)
            .FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order == null) return null;
        var partIds = order.Lines.Select(l => l.PartId).ToList();
        var photos = (await db.Photos.AsNoTracking().Where(p => p.PartId != null && partIds.Contains(p.PartId.Value)).ToListAsync(ct))
            .ToLookup(p => p.PartId!.Value);

        return new OrderDetail
        {
            Id = order.Id,
            Supplier = order.Supplier,
            OrderNumber = order.OrderNumber,
            Url = order.Url,
            Status = order.Status,
            OrderedAt = order.OrderedAt,
            ExpectedAt = order.ExpectedAt,
            ReceivedAt = order.ReceivedAt,
            Notes = order.Notes,
            Currency = order.Currency,
            Shipping = order.Shipping,
            Total = OrderTotal(order),
            CreatedAt = order.CreatedAt,
            Lines = order.Lines
                .OrderBy(l => PartVisuals.CategoryRank(l.Part.Category))
                .ThenBy(l => l.Part.Ref, PartVisuals.RefComparer)
                .Select(l => new OrderLineDto
                {
                    Id = l.Id,
                    PartId = l.PartId,
                    Ref = l.Part.Ref,
                    Category = l.Part.Category,
                    Designation = l.Part.Designation,
                    Qty = l.Qty,
                    UnitPrice = l.UnitPrice,
                    ReceivedQty = l.ReceivedQty,
                    Notes = l.Notes,
                    PartQty = l.Part.Qty,
                    CoverThumbUrl = Cover(photos[l.PartId]) is { } cover ? ThumbUrl(cover) : null,
                })
                .ToList(),
        };
    }

    public async Task<OrderDetail> SaveOrderAsync(OrderEdit edit, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        Order order;
        OrderStatus? before = null;
        if (edit.Id == null)
        {
            order = new Order { CreatedAt = Now };
            db.Orders.Add(order);
        }
        else
        {
            order = await db.Orders.FirstOrDefaultAsync(o => o.Id == edit.Id, ct) ?? throw new StockException("This order no longer exists.");
            before = order.Status;
        }
        if (edit.Status == OrderStatus.Received && before != OrderStatus.Received)
            throw new StockException("Use 'Receive' so that the quantities go into the stock.");
        if (before == OrderStatus.Received && edit.Status != OrderStatus.Received)
            throw new StockException("This order was received: its quantities are already in the stock.");

        order.Supplier = Clean(edit.Supplier);
        order.OrderNumber = Clean(edit.OrderNumber);
        order.Url = Clean(edit.Url);
        order.Status = edit.Status;
        order.OrderedAt = edit.OrderedAt;
        order.ExpectedAt = edit.ExpectedAt;
        order.Notes = (edit.Notes ?? "").Replace("\r\n", "\n");
        order.Currency = Clean(edit.Currency).ToUpperInvariant() is { Length: > 0 } currency ? currency : "EUR";
        order.Shipping = edit.Shipping;
        if (edit.Status == OrderStatus.Ordered) order.OrderedAt ??= Today;
        order.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);

        var title = order.Supplier.Length > 0 ? order.Supplier : $"order #{order.Id}";
        Log(db, ActivityKinds.OrderChanged,
            before == null ? $"Created a {edit.Status.ToString().ToLowerInvariant()} order ({title})"
            : before != edit.Status ? $"{title}: {before} → {edit.Status}" : $"{title}: order edited",
            orderId: order.Id);
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.Order, order.Id);
        return (await GetOrderAsync(order.Id, ct))!;
    }

    public async Task DeleteOrderAsync(int id, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw new StockException("This order no longer exists.");
        if (order.Status == OrderStatus.Received)
            throw new StockException("A received order is kept as the history of what was bought (prices, dates).");
        db.Orders.Remove(order);
        Log(db, ActivityKinds.OrderChanged, $"Deleted the {order.Status.ToString().ToLowerInvariant()} order {order.Supplier} {order.OrderNumber}".TrimEnd(), orderId: id);
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.Order, id);
    }

    public async Task<OrderDetail> SaveOrderLineAsync(OrderLineEdit edit, CancellationToken ct = default)
    {
        if (edit.Qty <= 0) throw new StockException("The quantity must be at least 1.");
        await using var db = await Db(ct);
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == edit.OrderId, ct) ?? throw new StockException("This order no longer exists.");
        if (order.Status == OrderStatus.Received) throw new StockException("This order was received: its lines can no longer change.");
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == edit.PartId, ct) ?? throw new StockException("This part no longer exists.");
        // ordered again: back on the shopping list once out of stock
        part.NoRestock = false;

        OrderLine line;
        if (edit.Id == null)
        {
            line = new OrderLine { OrderId = order.Id };
            db.OrderLines.Add(line);
        }
        else
        {
            line = await db.OrderLines.FirstOrDefaultAsync(l => l.Id == edit.Id && l.OrderId == edit.OrderId, ct)
                ?? throw new StockException("This line no longer exists.");
        }
        line.PartId = edit.PartId;
        line.Qty = edit.Qty;
        line.UnitPrice = edit.UnitPrice;
        line.Notes = Clean(edit.Notes);
        order.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.Order, order.Id);
        Notify(ChangeScope.Part, edit.PartId);
        return (await GetOrderAsync(order.Id, ct))!;
    }

    public async Task<OrderDetail> DeleteOrderLineAsync(int lineId, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var line = await db.OrderLines.Include(l => l.Order).FirstOrDefaultAsync(l => l.Id == lineId, ct)
            ?? throw new StockException("This line no longer exists.");
        if (line.Order.Status == OrderStatus.Received) throw new StockException("This order was received: its lines can no longer change.");
        db.OrderLines.Remove(line);
        line.Order.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.Order, line.OrderId);
        Notify(ChangeScope.Part, line.PartId);
        return (await GetOrderAsync(line.OrderId, ct))!;
    }

    public async Task<OrderDetail> AddToOrderAsync(AddToOrder request, CancellationToken ct = default)
    {
        if (request.Qty <= 0) throw new StockException("The quantity must be at least 1.");
        await using var db = await Db(ct);
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == request.PartId, ct) ?? throw new StockException("This part no longer exists.");
        // ordered again: back on the shopping list once out of stock
        part.NoRestock = false;
        var supplier = Clean(request.Supplier);

        Order? order;
        if (request.OrderId != null)
        {
            order = await db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == request.OrderId, ct)
                ?? throw new StockException("This order no longer exists.");
            if (order.Status is OrderStatus.Received or OrderStatus.Cancelled)
                throw new StockException($"This order is {order.Status.ToString().ToLowerInvariant()}: add the part to a draft.");
        }
        else
        {
            var drafts = db.Orders.Include(o => o.Lines).Where(o => o.Status == OrderStatus.Draft);
            order = supplier.Length > 0
                ? await drafts.Where(o => o.Supplier.ToLower() == supplier.ToLower()).OrderByDescending(o => o.Id).FirstOrDefaultAsync(ct)
                : await drafts.OrderByDescending(o => o.UpdatedAt).FirstOrDefaultAsync(ct);
            if (order == null)
            {
                order = new Order { Supplier = supplier, Status = OrderStatus.Draft, CreatedAt = Now };
                db.Orders.Add(order);
            }
        }

        var line = order.Lines.FirstOrDefault(l => l.PartId == part.Id);
        if (line == null)
        {
            line = new OrderLine { PartId = part.Id, Qty = request.Qty, UnitPrice = request.UnitPrice, Notes = Clean(request.Notes) };
            order.Lines.Add(line);
        }
        else
        {
            line.Qty += request.Qty;
            if (request.UnitPrice != null) line.UnitPrice = request.UnitPrice;
        }
        order.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        var title = order.Supplier.Length > 0 ? order.Supplier : $"order #{order.Id}";
        Log(db, ActivityKinds.OrderChanged, $"{request.Qty} × {part.Ref} added to the {order.Status.ToString().ToLowerInvariant()} order {title}",
            part, orderId: order.Id);
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.Order, order.Id);
        Notify(ChangeScope.Part, part.Id);
        return (await GetOrderAsync(order.Id, ct))!;
    }

    public async Task<OrderDetail> ReceiveOrderAsync(ReceiveOrder request, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var order = await db.Orders.Include(o => o.Lines).ThenInclude(l => l.Part)
            .FirstOrDefaultAsync(o => o.Id == request.OrderId, ct) ?? throw new StockException("This order no longer exists.");
        if (order.Status is OrderStatus.Received or OrderStatus.Cancelled)
            throw new StockException($"This order is already {order.Status.ToString().ToLowerInvariant()}.");
        if (order.Lines.Count == 0) throw new StockException("This order has no lines.");

        var title = order.Supplier.Length > 0 ? order.Supplier : $"order #{order.Id}";
        foreach (var line in order.Lines)
        {
            int received = request.Lines.FirstOrDefault(r => r.LineId == line.Id)?.Qty ?? line.Qty;
            if (received < 0) throw new StockException($"{line.Part.Ref}: a received quantity cannot be negative.");
            line.ReceivedQty = received;
            if (received == 0) continue;
            var before = line.Part.Qty;
            var (after, remark) = StockMath.Receive(Qty.Parse(before), received);
            line.Part.Qty = after;
            line.Part.NoRestock = false;
            line.Part.UpdatedAt = Now;
            Log(db, ActivityKinds.OrderReceived, $"Received {received} × {line.Part.Ref} ({title})" + (remark.Length > 0 ? $". {remark}" : ""),
                line.Part, orderId: order.Id, qtyBefore: before, qtyAfter: after);
        }
        order.Status = OrderStatus.Received;
        order.ReceivedAt = Today;
        order.OrderedAt ??= Today;
        order.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.All);
        return (await GetOrderAsync(order.Id, ct))!;
    }

    // ---------- locations ----------

    /// <summary>Every location in tree order (each one followed by the locations it holds), see <see cref="LocationTree"/>.</summary>
    static async Task<List<LocationDto>> LocationDtosAsync(StockDb db, CancellationToken ct)
    {
        var rows = await db.Locations.AsNoTracking()
            .Select(l => new { l.Id, l.Code, l.Name, l.Description, Parts = l.Parts.Count, Spares = l.SpareParts.Count })
            .ToListAsync(ct);
        var byCode = rows.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);
        return LocationTree.Order(rows.Select(r => r.Code))
            .Select(t =>
            {
                var r = byCode[t.Code];
                return new LocationDto
                {
                    Id = r.Id, Code = r.Code, Name = r.Name, Description = r.Description,
                    PartCount = r.Parts,
                    TotalPartCount = rows.Where(o => LocationTree.IsSameOrInside(o.Code, r.Code)).Sum(o => o.Parts),
                    SparePartCount = r.Spares,
                    ParentCode = t.Parent ?? "",
                    Depth = t.Depth,
                };
            })
            .ToList();
    }

    public async Task<List<LocationDto>> GetLocationsAsync(CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        return await LocationDtosAsync(db, ct);
    }

    public async Task<LocationDetail?> GetLocationAsync(string code, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var trimmed = Clean(code);
        var all = await LocationDtosAsync(db, ct);
        var location = all.FirstOrDefault(l => l.Code.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (location == null) return null;
        var s = await LoadSnapshotAsync(db, ct);
        var natural = Comparer<string>.Create(PartVisuals.CompareNatural);
        var tiles = Comparer<PartSummary>.Create((a, b) => PartVisuals.CompareTiles(a.Ref, a.Category, b.Ref, b.Category));
        bool Here(string code) => code.Length > 0 && LocationTree.IsSameOrInside(code, location.Code);
        return new LocationDetail
        {
            Location = location,
            Parents = all.Where(l => LocationTree.IsInside(location.Code, l.Code)).OrderBy(l => l.Code.Length).ToList(),
            Children = all.Where(l => l.ParentCode.Equals(location.Code, StringComparison.OrdinalIgnoreCase)).ToList(),
            Parts = s.Parts.Where(p => Here(p.Location?.Code ?? ""))
                .Select(p => Summarize(p, s))
                .OrderBy(p => p.LocationCode, natural).ThenBy(p => p, tiles)
                .ToList(),
            SpareParts = s.Parts.Where(p => Here(p.SpareLocation?.Code ?? ""))
                .Select(p => Summarize(p, s))
                .OrderBy(p => p.SpareLocationCode, natural).ThenBy(p => p, tiles)
                .ToList(),
        };
    }

    public async Task<LocationDto> SaveLocationAsync(LocationEdit edit, CancellationToken ct = default)
    {
        var code = Clean(edit.Code);
        if (code.Length == 0) throw new StockException("The location code is required (A1, BOX-3…).");
        if (code.Contains('/') || code.Contains('?') || code.Contains('#')) throw new StockException("A location code cannot contain / ? or #.");
        await using var db = await Db(ct);
        if (await db.Locations.AnyAsync(l => l.Code == code && l.Id != (edit.Id ?? 0), ct))
            throw new StockException($"The location {code} already exists.");

        Location location;
        if (edit.Id == null)
        {
            location = new Location { Code = code };
            db.Locations.Add(location);
        }
        else
        {
            location = await db.Locations.FirstOrDefaultAsync(l => l.Id == edit.Id, ct) ?? throw new StockException("This location no longer exists.");
        }
        location.Code = code;
        location.Name = Clean(edit.Name);
        location.Description = Clean(edit.Description);
        Log(db, ActivityKinds.LocationChanged, edit.Id == null ? $"Location {code} created" : $"Location {code} edited");
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.Location, location.Id);
        return (await LocationDtosAsync(db, ct)).First(l => l.Id == location.Id);
    }

    public async Task DeleteLocationAsync(int id, CancellationToken ct = default)
    {
        await using var db = await Db(ct);
        var location = await db.Locations.Include(l => l.Parts).Include(l => l.SpareParts).FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw new StockException("This location no longer exists.");
        foreach (var part in location.Parts) part.LocationId = null;
        foreach (var part in location.SpareParts) part.SpareLocationId = null;
        db.Locations.Remove(location);
        var left = new List<string>();
        if (location.Parts.Count > 0) left.Add($"{location.Parts.Count} part(s) left without location");
        if (location.SpareParts.Count > 0) left.Add($"{location.SpareParts.Count} part(s) left without spare location");
        Log(db, ActivityKinds.LocationChanged, $"Location {location.Code} deleted" + (left.Count > 0 ? $" ({string.Join(", ", left)})" : ""));
        await db.SaveChangesAsync(ct);
        Notify(ChangeScope.All);
    }
}
