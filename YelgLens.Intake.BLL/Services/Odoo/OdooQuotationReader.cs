using YelgLens.Intake.BLL.ViewModel;

namespace YelgLens.Intake.BLL.Services.Odoo;

/// <summary>أمر بيع في أودو كما يُعرض في شاشة الاستعلام.</summary>
public sealed record OdooQuotation(
    long Id,
    string Name,
    string Customer,
    string? Reference,
    DateTime? OrderDate,
    decimal Total,
    string Currency,
    string State)
{
    /// <summary>حالة أودو بالعربية — <c>draft</c> و<c>sale</c> لا تُقرأ.</summary>
    public string StateLabel => State switch
    {
        "draft"  => "عرض سعر",
        "sent"   => "عرض مُرسَل",
        "sale"   => "أمر بيع مؤكَّد",
        "done"   => "مُقفَل",
        "cancel" => "ملغى",
        _ => State
    };

    public string StateBadge => State switch
    {
        "draft"  => "badge-warning",
        "sent"   => "badge-info",
        "sale"   => "badge-success",
        "done"   => "badge-neutral",
        "cancel" => "badge-error",
        _ => "badge-neutral"
    };
}

/// <summary>بندٌ في أمر بيع كما هو في أودو.</summary>
public sealed record OdooOrderLine(
    long Id,
    long ProductId,
    string Product,
    string? Code,
    decimal Quantity,
    decimal UnitPrice,
    decimal Subtotal);

/// <summary>أمر بيع بتفاصيله وبنوده.</summary>
public sealed record OdooQuotationDetail(
    OdooQuotation Header,
    IReadOnlyList<OdooOrderLine> Lines,
    string? InvoiceAddress,
    string? DeliveryAddress,
    string? Pricelist,
    decimal Untaxed,
    decimal Tax)
{
    /// <summary>
    /// أمرٌ مؤكَّد لا يُعدَّل من هنا.
    ///
    /// التأكيد في أودو يحجز مخزوناً ويولّد إذن تسليم، وتعديل بنوده بعده يترك
    /// الإذن على حاله فيُسلَّم غير ما في الأمر. فيُقصر التعديل على المسوَّدة،
    /// وما بعدها يُعدَّل في أودو حيث تُعالَج توابعه.
    /// </summary>
    public bool IsEditable => Header.State is "draft" or "sent";
}

/// <summary>تعديلٌ على بندٍ قائم أو بندٌ جديد يُضاف.</summary>
public sealed record LineEdit(long? Id, string? Code, decimal Quantity, bool Remove = false);

/// <summary>شروط البحث في أوامر البيع.</summary>
public sealed record QuotationQuery
{
    /// <summary>نصٌّ يُطابق رقم الأوردر أو العميل أو مرجع العميل.</summary>
    public string? Text { get; init; }

    /// <summary>حالة أودو: <c>draft</c>، <c>sale</c>… أو الكل حين تكون فارغة.</summary>
    public string? State { get; init; }

    /// <summary>
    /// عميلٌ بعينه — لعرض كل أوردرات فرعٍ واحد.
    ///
    /// يُرشَّح بالمعرّف لا بالاسم: الاسم يتشابه بين فروعٍ ويختلف بمسافة، والمعرّف
    /// لا يلتبس.
    /// </summary>
    public long? PartnerId { get; init; }

    public DateTime? From { get; init; }

    public DateTime? To { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;

    public int Skip => Math.Max(0, (Page - 1) * PageSize);
}

public interface IOdooQuotationReader
{
    bool IsAvailable { get; }
    string Database { get; }

    /// <summary>يبحث في أوامر البيع في أودو ويعيد صفحةً منها.</summary>
    Task<PagedResult<OdooQuotation>> SearchAsync(QuotationQuery query, CancellationToken ct = default);

    /// <summary>يقرأ أوردراً بعينه بمعرّفه — لعرضه بجانب المستند الذي وُلِّد منه.</summary>
    Task<OdooQuotation?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>يقرأ الأوردر بتفاصيله وبنوده.</summary>
    Task<OdooQuotationDetail?> GetDetailAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// عدد أوامر البيع لكل عميل، ومعرّفه في أودو — مفهرسةً بالاسم المعروض.
    ///
    /// الكشف المحلي يحفظ الاسم لا المعرّف، لأن الاسم هو ما يُكتب في ملف
    /// الاستيراد. فيُترجَم هنا عند الحاجة، ولمن يُعرض منهم فقط.
    /// </summary>
    Task<Dictionary<string, (long PartnerId, int Orders)>> SummarizeByCustomerAsync(
        IReadOnlyList<string> displayNames, CancellationToken ct = default);

    /// <summary>هل الكتابة مفتوحة، فيمكن التعديل من هنا؟</summary>
    bool CanEdit { get; }

    /// <summary>
    /// يطبّق تعديلات البنود على أمرٍ مسوَّدة: تغيير كمية، أو حذف بند، أو إضافة
    /// بندٍ برمز منتج.
    /// </summary>
    Task<string> ApplyLineEditsAsync(long orderId, IReadOnlyList<LineEdit> edits, CancellationToken ct = default);
}

/// <summary>
/// يقرأ أوامر البيع من أودو ليُستعلَم عنها من هنا.
///
/// المراجع كان يفتح أودو ليرى ما وصله ويعود ليقارن. والذهاب والإياب بين
/// نظامين ليس بطئاً فحسب: ما يُقارَن من الذاكرة يُخطئ، ورقمٌ يُنقل بالعين
/// يُنقل خطأً.
///
/// والتعديل منها مقصورٌ على المسوَّدة، ومغلقٌ بالافتراض كسائر الكتابة.
/// </summary>
public sealed class OdooQuotationReader : IOdooQuotationReader
{
    private readonly IOdooClient _odoo;

    public OdooQuotationReader(IOdooClient odoo) => _odoo = odoo;

    public bool IsAvailable => _odoo.IsConfigured;

    public string Database => _odoo.Database;

    private static readonly string[] Fields =
    {
        "id", "name", "partner_id", "client_order_ref",
        "date_order", "amount_total", "currency_id", "state"
    };

    public async Task<PagedResult<OdooQuotation>> SearchAsync(
        QuotationQuery query, CancellationToken ct = default)
    {
        var domain = BuildDomain(query);

        // العدّ والصفحة نداءان لا واحد: أودو لا يعيد العدد الكلي مع النتائج،
        // وبلا العدد لا تُرسم أزرار التنقل.
        var total = await _odoo.SearchCountAsync("sale.order", domain, ct);

        var records = await _odoo.SearchReadAsync("sale.order", domain, Fields,
            query.Skip, query.PageSize, order: "date_order desc, id desc", ct: ct);

        var items = records.Select(Map).ToList();

        return new PagedResult<OdooQuotation>(items, total, query.Page, query.PageSize);
    }

    public async Task<OdooQuotation?> GetAsync(long id, CancellationToken ct = default)
    {
        var records = await _odoo.ReadAsync("sale.order", new[] { id }, Fields, ct);
        return records.Count > 0 ? Map(records[0]) : null;
    }

    public bool CanEdit => _odoo.CanWrite;

    /// <summary>
    /// يترجم الأسماء إلى معرّفات ثم يعدّ أوردرات كلٍّ منها.
    ///
    /// نداءان لا نداءٌ لكل عميل: <c>read_group</c> يجمع في الخادم بدل جلب
    /// سبعمئة أوردر لتُعدّ هنا، وترجمةُ الأسماء دفعةً واحدة تغني عن ثلاثمئة
    /// رحلة على شبكةٍ بطيئة.
    /// </summary>
    public async Task<Dictionary<string, (long PartnerId, int Orders)>> SummarizeByCustomerAsync(
        IReadOnlyList<string> displayNames, CancellationToken ct = default)
    {
        var summary = new Dictionary<string, (long, int)>();
        if (displayNames.Count == 0) return summary;

        var partners = await _odoo.SearchReadAsync("res.partner",
            new object[] { new object[] { "display_name", "in", displayNames.ToArray() } },
            new[] { "id", "display_name" }, ct: ct);

        var byName = new Dictionary<string, long>();
        foreach (var partner in partners)
        {
            var name = OdooValue.Text(partner["display_name"]);
            var id = partner["id"]?.GetValue<long>();
            if (name is not null && id is { } value) byName[name] = value;
        }

        if (byName.Count == 0) return summary;

        var groups = await _odoo.ReadGroupAsync("sale.order",
            new object[] { new object[] { "partner_id", "in", byName.Values.ToArray() } },
            new[] { "id" }, new[] { "partner_id" }, ct);

        var counts = new Dictionary<long, int>();
        foreach (var group in groups)
        {
            var id = OdooValue.RelationId(group["partner_id"]);
            if (id is not { } partner) continue;

            // أودو يسمّي حقل العدّ بحسب وضع التجميع: __count حين lazy=false،
            // و<الحقل>_count حين lazy=true. وقراءة أحدهما وحده تُرجع صفراً
            // صامتاً — وهو أسوأ من خطأ، لأن الشاشة تقول "لا أوردرات" وهي موجودة.
            var count = group["__count"]?.GetValue<int>()
                        ?? group["partner_id_count"]?.GetValue<int>()
                        ?? 0;

            counts[partner] = count;
        }

        foreach (var (name, id) in byName)
            summary[name] = (id, counts.GetValueOrDefault(id));

        return summary;
    }

    private static readonly string[] LineFields =
    {
        "id", "product_id", "name", "product_uom_qty", "price_unit", "price_subtotal"
    };

    public async Task<OdooQuotationDetail?> GetDetailAsync(long id, CancellationToken ct = default)
    {
        var header = await _odoo.ReadAsync("sale.order", new[] { id },
            Fields.Concat(new[]
            {
                "partner_invoice_id", "partner_shipping_id", "pricelist_id",
                "amount_untaxed", "amount_tax", "order_line"
            }).ToArray(), ct);

        if (header.Count == 0) return null;

        var record = header[0];

        // البنود تُقرأ بمعرّفاتها من الأمر، لا ببحثٍ في كل البنود: الأول نداءٌ
        // محدود، والثاني يمسح جدولاً كاملاً على خادمٍ بطيء.
        var lineIds = record["order_line"] is System.Text.Json.Nodes.JsonArray array
            ? array.Select(n => n?.GetValue<long>() ?? 0).Where(n => n > 0).ToArray()
            : Array.Empty<long>();

        var lineRecords = await _odoo.ReadAsync("sale.order.line", lineIds, LineFields, ct);

        var lines = lineRecords.Select(l =>
        {
            var product = OdooValue.RelationName(l["product_id"]) ?? "—";

            // أودو يسبق اسم المنتج برمزه بين قوسين معقوفين، وهو الرمز الذي
            // يُطابَق به البند — فيُقتطع ليُعرض في عموده.
            string? code = null;
            if (product.StartsWith('[') && product.IndexOf(']') > 1)
            {
                code = product[1..product.IndexOf(']')];
                product = product[(product.IndexOf(']') + 1)..].Trim();
            }

            return new OdooOrderLine(
                l["id"]?.GetValue<long>() ?? 0,
                OdooValue.RelationId(l["product_id"]) ?? 0,
                product,
                code,
                Number(l["product_uom_qty"]),
                Number(l["price_unit"]),
                Number(l["price_subtotal"]));
        }).ToList();

        return new OdooQuotationDetail(
            Map(record),
            lines,
            OdooValue.RelationName(record["partner_invoice_id"]),
            OdooValue.RelationName(record["partner_shipping_id"]),
            OdooValue.RelationName(record["pricelist_id"]),
            Number(record["amount_untaxed"]),
            Number(record["amount_tax"]));
    }

    /// <summary>
    /// يطبّق تعديلات البنود دفعةً واحدة.
    ///
    /// أودو يقبل أوامر البنود بصيغة ثلاثيات: <c>[1, id, values]</c> تعديل،
    /// و<c>[2, id, 0]</c> حذف، و<c>[0, 0, values]</c> إضافة. وتُرسل كلها في
    /// نداءٍ واحد لا نداءٍ لكل بند: خادمهم يتقلّب، ونصفُ تعديلٍ أسوأ من لا شيء.
    /// </summary>
    public async Task<string> ApplyLineEditsAsync(
        long orderId, IReadOnlyList<LineEdit> edits, CancellationToken ct = default)
    {
        if (!_odoo.CanWrite)
            throw new OdooException(
                "التعديل في أودو مغلق. يُفتح من «إعدادات أودو» بتعليم «اسمح بالكتابة في أودو» على الوصلة المعمول بها.");

        var detail = await GetDetailAsync(orderId, ct)
            ?? throw new OdooException("لم يُعثر على هذا الأوردر في أودو.");

        if (!detail.IsEditable)
            throw new OdooException(
                $"الأوردر {detail.Header.Name} حالته «{detail.Header.StateLabel}» ولا يُعدَّل من هنا. "
                + "التأكيد يحجز مخزوناً ويولّد إذن تسليم، فتعديله يُعالَج في أودو.");

        var commands = new System.Text.Json.Nodes.JsonArray();
        var added = 0; var changed = 0; var removed = 0;

        foreach (var edit in edits)
        {
            if (edit.Remove && edit.Id is { } removeId)
            {
                commands.Add(new System.Text.Json.Nodes.JsonArray { 2, removeId, 0 });
                removed++;
                continue;
            }

            if (edit.Id is { } updateId)
            {
                var line = detail.Lines.FirstOrDefault(l => l.Id == updateId);
                if (line is null || line.Quantity == edit.Quantity) continue;

                commands.Add(new System.Text.Json.Nodes.JsonArray
                {
                    1, updateId,
                    new System.Text.Json.Nodes.JsonObject { ["product_uom_qty"] = edit.Quantity }
                });
                changed++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(edit.Code)) continue;

            var product = await FindProductAsync(edit.Code.Trim(), ct)
                ?? throw new OdooException($"لا منتج في أودو بالرمز {edit.Code}.");

            commands.Add(new System.Text.Json.Nodes.JsonArray
            {
                0, 0,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["product_id"] = product,
                    ["product_uom_qty"] = edit.Quantity
                }
            });
            added++;
        }

        if (commands.Count == 0) return "لا تغيير — لم يختلف شيء عمّا في أودو.";

        await _odoo.WriteAsync("sale.order", orderId,
            new System.Text.Json.Nodes.JsonObject { ["order_line"] = commands }, ct);

        var parts = new List<string>();
        if (changed > 0) parts.Add($"عُدِّل {changed} بند");
        if (added > 0) parts.Add($"أُضيف {added}");
        if (removed > 0) parts.Add($"حُذف {removed}");

        return $"حُفظ في {detail.Header.Name}: {string.Join(" · ", parts)}.";
    }

    /// <summary>يبحث عن منتجٍ برمزه في الحقلين اللذين قد يحملانه.</summary>
    private async Task<long?> FindProductAsync(string code, CancellationToken ct)
    {
        var rows = await _odoo.SearchReadAsync("product.product",
            new object[]
            {
                "|",
                new object[] { "barcode", "=", code },
                new object[] { "default_code", "=", code }
            },
            new[] { "id" }, limit: 2, ct: ct);

        // رمزٌ على منتجين لا يُختار أحدهما بنصف احتمال.
        return rows.Count == 1 ? rows[0]["id"]?.GetValue<long>() : null;
    }

    private static decimal Number(System.Text.Json.Nodes.JsonNode? node) =>
        decimal.TryParse(OdooValue.Text(node), out var value) ? value : 0m;

    /// <summary>
    /// يبني شرط أودو. الشروط تُسبق بعوامل بادئة (<c>&amp;</c> و<c>|</c>)، وهي
    /// صيغة أودو: العامل يسبق ما يعمل فيه، لا يقع بينهما.
    /// </summary>
    private static object[] BuildDomain(QuotationQuery query)
    {
        var terms = new List<object>();

        if (!string.IsNullOrWhiteSpace(query.State))
            terms.Add(new object[] { "state", "=", query.State });

        if (query.PartnerId is { } partner)
            terms.Add(new object[] { "partner_id", "=", partner });

        if (query.From is { } from)
            terms.Add(new object[] { "date_order", ">=", from.Date.ToString("yyyy-MM-dd HH:mm:ss") });

        if (query.To is { } to)
            terms.Add(new object[] { "date_order", "<=", to.Date.AddDays(1).AddSeconds(-1).ToString("yyyy-MM-dd HH:mm:ss") });

        var domain = new List<object>();
        for (var i = 1; i < terms.Count; i++) domain.Add("&");
        domain.AddRange(terms);

        if (string.IsNullOrWhiteSpace(query.Text)) return domain.ToArray();

        // النص يُطابق ثلاثة حقول، فيُسبق باثنين من "|" لثلاثة أطراف.
        var text = query.Text.Trim();
        var search = new List<object>
        {
            "|", "|",
            new object[] { "name", "ilike", text },
            new object[] { "client_order_ref", "ilike", text },
            new object[] { "partner_id", "ilike", text }
        };

        // الشرطان يُجمعان بـ"&" واحدة تسبقهما معاً.
        if (domain.Count == 0) return search.ToArray();

        var combined = new List<object> { "&" };
        combined.AddRange(domain);
        combined.AddRange(search);
        return combined.ToArray();
    }

    private static OdooQuotation Map(System.Text.Json.Nodes.JsonObject record)
    {
        var dateText = OdooValue.Text(record["date_order"]);
        DateTime? date = DateTime.TryParse(dateText, out var parsed) ? parsed : null;

        var totalText = OdooValue.Text(record["amount_total"]);
        var total = decimal.TryParse(totalText, out var amount) ? amount : 0m;

        return new OdooQuotation(
            record["id"]?.GetValue<long>() ?? 0,
            OdooValue.Text(record["name"]) ?? "—",
            OdooValue.RelationName(record["partner_id"]) ?? "—",
            OdooValue.Text(record["client_order_ref"]),
            date,
            total,
            OdooValue.RelationName(record["currency_id"]) ?? "",
            OdooValue.Text(record["state"]) ?? "draft");
    }
}
