using YelgLens.Intake.BLL.ViewModel;

namespace YelgLens.Intake.BLL.Services.Odoo;

/// <summary>منتجٌ في أودو كما يُعرض في شاشة الاستعلام.</summary>
public sealed record OdooProduct(
    long Id,
    long TemplateId,
    string Name,
    string? Code,
    string? Barcode,
    decimal ListPrice,
    string Uom,
    bool SaleOk,
    bool Active)
{
    /// <summary>سعر قائمة الأسعار المختارة، إن كان لها سعرٌ لهذا المنتج.</summary>
    public decimal? PricelistPrice { get; init; }
}

/// <summary>قائمة أسعار وعدد بنودها.</summary>
public sealed record OdooPricelist(long Id, string Name, string Currency, int Items);

public sealed record ProductQuery
{
    /// <summary>نصٌّ يُطابق الاسم أو المرجع الداخلي أو الباركود.</summary>
    public string? Text { get; init; }

    /// <summary>قائمة أسعارٍ تُعرض أسعارها بجانب سعر القائمة العامة.</summary>
    public long? PricelistId { get; init; }

    /// <summary>لا يُعرض إلا ما له سعرٌ في القائمة المختارة.</summary>
    public bool PricedOnly { get; init; }

    public bool SaleableOnly { get; init; } = true;

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;

    public int Skip => Math.Max(0, (Page - 1) * PageSize);
}

public interface IOdooProductReader
{
    bool IsAvailable { get; }
    string Database { get; }

    Task<PagedResult<OdooProduct>> SearchAsync(ProductQuery query, CancellationToken ct = default);

    Task<List<OdooPricelist>> GetPricelistsAsync(CancellationToken ct = default);
}

/// <summary>
/// يقرأ منتجات أودو وأسعارها من هنا.
///
/// السؤال الذي تُجيبه هذه الشاشة ليس «ما المنتجات؟» بل «بكم يبيع هذا الصنف
/// لهذا العميل؟» — وهو سؤالٌ كان يُجاب بفتح أودو ونقل الرقم بالعين.
///
/// وقراءةٌ محضة: لا تُنشئ منتجاً ولا تغيّر سعراً.
/// </summary>
public sealed class OdooProductReader : IOdooProductReader
{
    private readonly IOdooClient _odoo;

    public OdooProductReader(IOdooClient odoo) => _odoo = odoo;

    public bool IsAvailable => _odoo.IsConfigured;

    public string Database => _odoo.Database;

    private static readonly string[] Fields =
    {
        "id", "product_tmpl_id", "display_name", "default_code", "barcode",
        "lst_price", "uom_id", "sale_ok", "active"
    };

    public async Task<List<OdooPricelist>> GetPricelistsAsync(CancellationToken ct = default)
    {
        var lists = await _odoo.SearchReadAsync("product.pricelist",
            Array.Empty<object>(), new[] { "id", "name", "currency_id" }, order: "name", ct: ct);

        var counts = new Dictionary<long, int>();

        // عدد البنود يُطلب مجمَّعاً: قائمةٌ بلا بنود تُعرض ولا يُبحث فيها،
        // ومعرفةُ ذلك قبل الاختيار توفّر على المستخدم بحثاً فارغاً.
        try
        {
            var groups = await _odoo.ReadGroupAsync("product.pricelist.item",
                Array.Empty<object>(), new[] { "id" }, new[] { "pricelist_id" }, ct);

            foreach (var group in groups)
            {
                var id = OdooValue.RelationId(group["pricelist_id"]);
                var count = group["__count"]?.GetValue<int>()
                            ?? group["pricelist_id_count"]?.GetValue<int>() ?? 0;
                if (id is { } pricelist) counts[pricelist] = count;
            }
        }
        catch (OdooException)
        {
            // العدّ زينةٌ لا شرط: تُعرض القوائم بلا عدّادات.
        }

        return lists.Select(l =>
        {
            var id = l["id"]?.GetValue<long>() ?? 0;
            return new OdooPricelist(
                id,
                OdooValue.Text(l["name"]) ?? "—",
                OdooValue.RelationName(l["currency_id"]) ?? "",
                counts.GetValueOrDefault(id));
        }).ToList();
    }

    public async Task<PagedResult<OdooProduct>> SearchAsync(
        ProductQuery query, CancellationToken ct = default)
    {
        // الأسعار تُقرأ أولاً حين يُطلب القصر عليها: بها يُبنى شرطُ البحث نفسه.
        Dictionary<long, decimal>? prices = null;
        if (query.PricelistId is { } pricelist)
            prices = await ReadPricesAsync(pricelist, ct);

        var domain = BuildDomain(query, prices);

        var total = await _odoo.SearchCountAsync("product.product", domain, ct);

        var records = await _odoo.SearchReadAsync("product.product", domain, Fields,
            query.Skip, query.PageSize, order: "default_code, display_name", ct: ct);

        var items = records.Select(r =>
        {
            var template = OdooValue.RelationId(r["product_tmpl_id"]) ?? 0;

            return new OdooProduct(
                r["id"]?.GetValue<long>() ?? 0,
                template,
                Clean(OdooValue.Text(r["display_name"]) ?? "—"),
                OdooValue.Text(r["default_code"]),
                OdooValue.Text(r["barcode"]),
                Number(r["lst_price"]),
                OdooValue.RelationName(r["uom_id"]) ?? "",
                r["sale_ok"]?.GetValue<bool>() ?? false,
                r["active"]?.GetValue<bool>() ?? false)
            {
                PricelistPrice = prices is not null && prices.TryGetValue(template, out var price)
                    ? price
                    : null
            };
        }).ToList();

        return new PagedResult<OdooProduct>(items, total, query.Page, query.PageSize);
    }

    /// <summary>
    /// أسعار قائمةٍ بعينها، مفهرسةً بقالب المنتج.
    ///
    /// بنود قائمة الأسعار تُسنَد إلى القالب (<c>product_tmpl_id</c>) لا إلى
    /// المتغيّر، فالفهرسة عليه — وقد قِيس ذلك على قائمة <c>Talabat</c>: كل
    /// بنودها الستة والستون <c>applied_on = 1_product</c> بقالبٍ ومعرّفٍ فارغ
    /// للمتغيّر.
    /// </summary>
    private async Task<Dictionary<long, decimal>> ReadPricesAsync(long pricelistId, CancellationToken ct)
    {
        var prices = new Dictionary<long, decimal>();

        var items = await _odoo.SearchReadAsync("product.pricelist.item",
            new object[] { new object[] { "pricelist_id", "=", pricelistId } },
            new[] { "product_tmpl_id", "product_id", "fixed_price", "applied_on" }, ct: ct);

        foreach (var item in items)
        {
            var template = OdooValue.RelationId(item["product_tmpl_id"]);
            if (template is not { } id) continue;

            prices[id] = Number(item["fixed_price"]);
        }

        return prices;
    }

    private static object[] BuildDomain(ProductQuery query, Dictionary<long, decimal>? prices)
    {
        var terms = new List<object>();

        if (query.SaleableOnly) terms.Add(new object[] { "sale_ok", "=", true });

        // القصر على المسعَّر يتم بمعرّفات القوالب: أودو لا يصل المنتج بقائمة
        // الأسعار في شرطٍ واحد، فيُقرأ الطرفان ويُجمعان هنا.
        if (query.PricedOnly && prices is not null)
            terms.Add(new object[] { "product_tmpl_id", "in", prices.Keys.ToArray() });

        var domain = new List<object>();
        for (var i = 1; i < terms.Count; i++) domain.Add("&");
        domain.AddRange(terms);

        if (string.IsNullOrWhiteSpace(query.Text)) return domain.ToArray();

        var text = query.Text.Trim();
        var search = new List<object>
        {
            "|", "|",
            new object[] { "name", "ilike", text },
            new object[] { "default_code", "ilike", text },
            new object[] { "barcode", "ilike", text }
        };

        if (domain.Count == 0) return search.ToArray();

        var combined = new List<object> { "&" };
        combined.AddRange(domain);
        combined.AddRange(search);
        return combined.ToArray();
    }

    /// <summary>أودو يسبق الاسم برمزه بين قوسين معقوفين، والرمز معروضٌ في عموده.</summary>
    private static string Clean(string displayName)
    {
        if (!displayName.StartsWith('[')) return displayName;
        var close = displayName.IndexOf(']');
        return close > 0 ? displayName[(close + 1)..].Trim() : displayName;
    }

    private static decimal Number(System.Text.Json.Nodes.JsonNode? node) =>
        decimal.TryParse(OdooValue.Text(node), out var value) ? value : 0m;
}
