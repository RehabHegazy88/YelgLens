using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Enums;
using YelgLens.Intake.Model.Mapping;

namespace YelgLens.Intake.BLL.Services.Odoo;

/// <summary>
/// حصيلة محاولة ترحيل.
///
/// الفشل يحمل سببه دائماً، والسبب يُصاغ بما يمكن للمراجع أن يفعله: أي حقلٍ
/// أو أي بندٍ منع الترحيل، لا نصّ الاستثناء كما جاء.
/// </summary>
public sealed record PublishOutcome(
    bool Success,
    long? OrderId,
    string? OrderName,
    string Message,
    IReadOnlyList<string> Reasons)
{
    public static PublishOutcome Fail(string message, params string[] reasons) =>
        new(false, null, null, message, reasons);

    public static PublishOutcome Ok(long id, string name, string message) =>
        new(true, id, name, message, Array.Empty<string>());
}

public interface IOdooOrderPublisher
{
    bool IsAvailable { get; }
    bool CanWrite { get; }
    string Database { get; }

    Task<PublishOutcome> PublishAsync(
        IntakeDocument document, BranchMapping? mapping, CancellationToken ct = default);
}

/// <summary>
/// ينشئ أمر بيع في أودو من مستندٍ معتمد.
///
/// يُنشأ <b>عرض سعر</b> (draft) لا أمراً مؤكداً. التأكيد يحجز مخزوناً ويولّد
/// إذن تسليم، ونقضه بعد ذلك عملٌ يدويّ في أودو. فيُترك القرار لمن يفتح الأوردر
/// هناك — النظام ينقل ما في الورقة، ولا يقرّر نيابةً عن المبيعات.
///
/// ولا يُرحَّل مستندٌ مرتين: رقمُ الأوردر يُحفظ عند الإنشاء، ويُبحث قبله عن
/// أوردرٍ يحمل رقم أمر الشراء نفسه — فضغطتان على زرٍّ بطيء لا تصنعان طلبيتين.
/// </summary>
public sealed class OdooOrderPublisher : IOdooOrderPublisher
{
    private readonly IOdooClient _odoo;
    private readonly ILogger<OdooOrderPublisher> _log;

    public OdooOrderPublisher(IOdooClient odoo, ILogger<OdooOrderPublisher> log)
    {
        _odoo = odoo;
        _log = log;
    }

    public bool IsAvailable => _odoo.IsConfigured;

    public bool CanWrite => _odoo.CanWrite;

    public string Database => _odoo.Database;

    public async Task<PublishOutcome> PublishAsync(
        IntakeDocument document, BranchMapping? mapping, CancellationToken ct = default)
    {
        if (!_odoo.IsConfigured)
            return PublishOutcome.Fail("الاتصال بأودو غير مضبوط.");

        if (!_odoo.CanWrite)
            return PublishOutcome.Fail(
                "الترحيل المباشر مغلق على هذه الوصلة. افتح «إعدادات أودو» وعدّل الوصلة المعمول بها، وعلّم «اسمح بالكتابة في أودو» — وعلى قاعدة اختبارٍ أولاً.");

        if (document.Status is not (IntakeStatus.Approved or IntakeStatus.Published))
            return PublishOutcome.Fail("لا يُرحَّل إلا مستند معتمد.");

        // ولا يُرحَّل مستندُ عميلٍ على أودو عميلٍ آخر. الفحص هنا آخر حاجزٍ قبل
        // الكتابة: الشاشات مقصورةٌ أصلاً، لكن رابطاً محفوظاً أو نافذةً بقيت
        // مفتوحةً قبل تبديل الوصلة تصل إلى هنا.
        if (!document.BelongsToDatabase(_odoo.Database))
            return PublishOutcome.Fail(
                $"هذا المستند يخصّ «{document.OwnerDatabase ?? document.OdooDatabase}» "
                + $"والنظام موصولٌ بـ «{_odoo.Database}». لا يُنشأ أوردرٌ لعميلٍ في أودو عميلٍ آخر.");

        if (document.IsPublished)
            return PublishOutcome.Fail(
                $"هذا المستند مُرحَّل بالفعل — الأوردر {document.OdooOrderName} على {document.OdooDatabase}.");

        if (document.HasBlockingIssue)
            return PublishOutcome.Fail("يوجد بند مانع في المستند. لا يُنشأ أوردر ناقص.");

        // العميل المسنَد للمستند وحده يُقبل كما يُقبل الربط: الورقة بلا فرع لا
        // تُربط، ومنعُ ترحيلها لذلك يوقف عملاً صحيحاً بلا سبب.
        var customer = !string.IsNullOrWhiteSpace(document.OdooCustomerOverride)
            ? document.OdooCustomerOverride
            : mapping is { IsComplete: true } ? mapping.OdooCustomer : null;

        if (string.IsNullOrWhiteSpace(customer))
            return PublishOutcome.Fail(
                "لا عميل لهذا المستند في أودو. اختر عميلاً من شاشة المراجعة، "
                + "أو اربط فرعه من جدول الربط.");

        var lines = document.Lines
            .OrderBy(l => l.Sequence)
            .Where(l => l.Barcode.HasValue && l.OrderedQty.HasValue)
            .ToList();

        if (lines.Count == 0)
            return PublishOutcome.Fail("لا توجد بنود مكتملة (كود وكمية) لترحيلها.");

        try
        {
            return await CreateAsync(document, customer, mapping, lines, ct);
        }
        catch (OdooException ex)
        {
            _log.LogWarning(ex, "تعذّر ترحيل المستند {Id}.", document.Id);

            // رسالة أودو تُعرض كما هي: هي أدقّ ما يُقال عن سبب الرفض، وإخفاؤها
            // خلف "حدث خطأ" يترك المراجع بلا ما يتصرف به.
            return PublishOutcome.Fail("رفض أودو الترحيل.", ex.Message);
        }
    }

    private async Task<PublishOutcome> CreateAsync(
        IntakeDocument document, string customer, BranchMapping? mapping,
        List<IntakeLine> lines, CancellationToken ct)
    {
        var reference = document.CustomerPoNumber.HasValue ? document.CustomerPoNumber.Value : null;

        // الحارس الأول ضد التكرار: أوردرٌ يحمل رقم أمر الشراء نفسه موجودٌ في
        // أودو ولو لم يُسجَّل عندنا — كأن تنقطع الشبكة بعد الإنشاء وقبل الحفظ.
        if (!string.IsNullOrWhiteSpace(reference))
        {
            var twin = await _odoo.SearchReadAsync("sale.order",
                new object[] { new object[] { "client_order_ref", "=", reference } },
                new[] { "name" }, limit: 1, ct: ct);

            if (twin.Count > 0)
                return PublishOutcome.Fail(
                    $"يوجد في أودو أوردر برقم أمر الشراء نفسه ({reference}) — "
                    + $"{OdooValue.Text(twin[0]["name"])}. لم يُنشأ ثانٍ.");
        }

        var partner = await FindOneAsync("res.partner",
            new object[] { "display_name", "=", customer }, ct);

        if (partner is null)
            return PublishOutcome.Fail("لم يُعثر على العميل في أودو.",
                $"العميل «{customer}» ليس في قاعدة {_odoo.Database}.");

        long? pricelist = null;
        if (!string.IsNullOrWhiteSpace(mapping?.OdooPricelist))
        {
            pricelist = await FindOneAsync("product.pricelist",
                new object[] { "name", "=", mapping.OdooPricelist }, ct);

            if (pricelist is null)
                return PublishOutcome.Fail("لم يُعثر على قائمة الأسعار في أودو.",
                    $"قائمة الأسعار «{mapping!.OdooPricelist}» غير موجودة. "
                    + "اتركها فارغة في جدول الربط ليأخذ أودو قائمة العميل الافتراضية.");
        }

        var (products, missing) = await ResolveProductsAsync(lines, ct);

        // بندٌ واحدٌ بلا منتج يوقف الترحيل كله. أوردرٌ ناقص بندين أسوأ من أوردر
        // لم يُنشأ: الأول يُصدَّق ويُسلَّم ناقصاً، والثاني يُعاد.
        if (missing.Count > 0)
            return PublishOutcome.Fail(
                $"{missing.Count} من {lines.Count} بند بلا منتج مطابق في أودو.",
                missing.ToArray());

        var orderLines = new JsonArray();
        foreach (var line in lines)
        {
            orderLines.Add(new JsonArray
            {
                0, 0,
                new JsonObject
                {
                    ["product_id"] = products[line.Barcode.Value!],
                    ["product_uom_qty"] = line.OrderedQty.Value
                }
            });
        }

        var values = new JsonObject
        {
            ["partner_id"] = partner,
            ["order_line"] = orderLines
        };

        if (pricelist is { } list) values["pricelist_id"] = list;
        if (!string.IsNullOrWhiteSpace(reference)) values["client_order_ref"] = reference;

        // تاريخ الأمر يُنقل إن كان في الورقة: الأوردر أثرُ ورقةٍ لها تاريخها،
        // لا أثرُ اللحظة التي ضُغط فيها الزر.
        if (document.OrderDate.HasValue)
            values["date_order"] = document.OrderDate.Value.ToString("yyyy-MM-dd HH:mm:ss");

        if (document.DeliveryDate.HasValue)
            values["commitment_date"] = document.DeliveryDate.Value.ToString("yyyy-MM-dd HH:mm:ss");

        var id = await _odoo.CreateAsync("sale.order", values, ct);

        // الاسم يُقرأ بعد الإنشاء لا يُخمَّن: أودو يولّده بتسلسله.
        var created = await _odoo.ReadAsync("sale.order", new[] { id }, new[] { "name", "state" }, ct);
        var name = created.Count > 0 ? OdooValue.Text(created[0]["name"]) ?? $"#{id}" : $"#{id}";

        _log.LogInformation("رُحِّل المستند {Document} إلى الأوردر {Order} ({Id}) على {Database}.",
            document.Id, name, id, _odoo.Database);

        return PublishOutcome.Ok(id, name,
            $"أُنشئ عرض السعر {name} على قاعدة {_odoo.Database} بـ{lines.Count} بند. "
            + "لم يُؤكَّد — التأكيد من أودو بعد المراجعة.");
    }

    /// <summary>
    /// يطابق بنود المستند بمنتجات أودو.
    ///
    /// يُبحث في <c>barcode</c> و<c>default_code</c> معاً: الأول موضعه الطبيعي،
    /// والثاني حيث وجدناه فعلاً في قاعدتهم — كل منتجاتها بلا باركود والرمز في
    /// المرجع الداخلي.
    /// </summary>
    private async Task<(Dictionary<string, long> Found, List<string> Missing)> ResolveProductsAsync(
        List<IntakeLine> lines, CancellationToken ct)
    {
        var codes = lines.Select(l => l.Barcode.Value!).Distinct().ToList();

        var domain = new object[]
        {
            "|",
            new object[] { "barcode", "in", codes },
            new object[] { "default_code", "in", codes }
        };

        var records = await _odoo.SearchReadAsync("product.product", domain,
            new[] { "id", "barcode", "default_code" }, ct: ct);

        var found = new Dictionary<string, long>();
        var ambiguous = new HashSet<string>();

        foreach (var record in records)
        {
            var id = record["id"]?.GetValue<long>() ?? 0;
            if (id == 0) continue;

            foreach (var code in new[] { OdooValue.Text(record["barcode"]), OdooValue.Text(record["default_code"]) })
            {
                if (code is null || !codes.Contains(code)) continue;

                // رمزٌ على منتجين لا يُختار أحدهما: البند يذهب إلى الخطأ منهما
                // بنصف احتمال، ولا يُكتشف إلا في المخزن.
                if (found.TryGetValue(code, out var existing) && existing != id) ambiguous.Add(code);
                else found[code] = id;
            }
        }

        var missing = new List<string>();

        foreach (var line in lines)
        {
            var code = line.Barcode.Value!;

            if (ambiguous.Contains(code))
                missing.Add($"البند {line.Sequence}: الرمز {code} يطابق أكثر من منتج في أودو");
            else if (!found.ContainsKey(code))
                missing.Add($"البند {line.Sequence}: لا منتج بالرمز {code}");
        }

        return (found, missing);
    }

    private async Task<long?> FindOneAsync(string model, object[] condition, CancellationToken ct)
    {
        var rows = await _odoo.SearchReadAsync(model, new object[] { condition },
            new[] { "id" }, limit: 1, ct: ct);

        return rows.Count > 0 ? rows[0]["id"]?.GetValue<long>() : null;
    }
}
