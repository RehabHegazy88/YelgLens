using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Mapping;

namespace YelgLens.Intake.BLL.Services.Odoo;

/// <summary>
/// ما سيحسبه أودو للسعر، مقابل ما في الورقة.
///
/// هذا أخطر ما يُفحص، لأنه الوحيد الذي يمرّ صامتاً: أوردرٌ ناقص بندٍ يُرفض،
/// وأوردرٌ بسعرٍ خاطئ يُقبل ويُسلَّم ويُفوتر ولا يُكتشف إلا في الحساب. وقد وقع
/// فعلاً — أوردرٌ خرج بـ١١٨٫٨٠ وكان يجب أن يخرج بنحو ٥٩٠٠، لأن قائمة أسعار
/// العميل فارغة والسعر العام في قاعدتهم جنيهٌ واحد لكل صنف.
/// </summary>
public sealed record PricingCheck(
    string? PricelistName,
    bool FromMapping,
    int PricelistItems,
    int LinesWithSpecialPrice,
    int LinesAtBasePrice,
    decimal DocumentTotal,
    decimal OdooTotal,
    IReadOnlyList<string> Warnings)
{
    public static readonly PricingCheck NotChecked =
        new(null, false, 0, 0, 0, 0m, 0m, Array.Empty<string>());

    /// <summary>قائمةٌ بلا بنود: كل صنفٍ سيُباع بسعره العام.</summary>
    public bool PricelistEmpty => PricelistItems == 0;

    /// <summary>هل في التسعير ما يستوجب نظر المراجع قبل الإرسال؟</summary>
    public bool NeedsAttention => Warnings.Count > 0;

    /// <summary>الفارق بين إجمالي الورقة وما سيحسبه أودو.</summary>
    public decimal Difference => OdooTotal - DocumentTotal;
}

/// <summary>ما وجده الفحص المسبق قبل توليد ملف الاستيراد.</summary>
public sealed record PreflightResult(
    bool CustomerFound,
    bool PricelistFound,
    IReadOnlyList<string> MissingBarcodes,
    int CheckedBarcodes,
    string? Error)
{
    /// <summary>حال التسعير. يُحذَّر منه ولا يُمنع به: القرار للمراجع.</summary>
    public PricingCheck Pricing { get; init; } = PricingCheck.NotChecked;

    /// <summary>هل يُتوقع للملف أن يُقبل؟</summary>
    public bool WillLikelyPass =>
        Error is null && CustomerFound && PricelistFound && MissingBarcodes.Count == 0;

    public bool WasChecked => Error is null;
}

public interface IOdooPreflight
{
    bool IsAvailable { get; }

    /// <summary>
    /// يسأل أودو عمّا سيطابقه الملف قبل توليده: العميل وقائمة الأسعار وكل
    /// باركود.
    /// </summary>
    Task<PreflightResult> CheckAsync(IntakeDocument document, BranchMapping mapping, CancellationToken ct = default);

    /// <summary>
    /// يفحص السعر وحده. يُطلب قبل الترحيل مباشرةً: الفحص الكامل يسأل أودو عن
    /// كل باركود، والسؤال هنا عن السعر فقط فيكون أسرع.
    /// </summary>
    Task<PricingCheck> CheckPricingAsync(IntakeDocument document, BranchMapping mapping, CancellationToken ct = default);
}

/// <summary>
/// يفحص ملف الاستيراد قبل توليده.
///
/// أودو يطابق أعمدة الملف بالنص: العميل باسمه المعروض، والمنتج بباركوده،
/// وقائمة الأسعار باسمها. وأيّ منها إن لم يُطابق رُفض الملف كله بعد رفعه —
/// وهذا اكتُشف عملياً: منتجات مستندٍ حقيقي لم يكن أيٌّ منها في قاعدة الاختبار.
///
/// والسؤال هنا أرخص من الرفض هناك: المراجع يعرف ما ينقص قبل أن يرسل، لا بعد
/// أن يُردّ عليه بخطأٍ لا يقول أي سطرٍ سببه.
/// </summary>
public sealed class OdooPreflight : IOdooPreflight
{
    private readonly IOdooClient _odoo;

    public OdooPreflight(IOdooClient odoo) => _odoo = odoo;

    public bool IsAvailable => _odoo.IsConfigured;

    public async Task<PreflightResult> CheckAsync(
        IntakeDocument document, BranchMapping mapping, CancellationToken ct = default)
    {
        if (!_odoo.IsConfigured)
            return new PreflightResult(false, false, Array.Empty<string>(), 0,
                "الاتصال بأودو غير مضبوط، فلم يُفحص الملف قبل توليده.");

        var barcodes = document.Lines
            .Where(l => l.Barcode.HasValue && l.OrderedQty.HasValue)
            .Select(l => l.Barcode.Value!)
            .Distinct()
            .ToList();

        try
        {
            var customer = await _odoo.SearchCountAsync("res.partner",
                new object[] { new object[] { "display_name", "=", mapping.OdooCustomer ?? "" } }, ct) > 0;

            // قائمة أسعارٍ فارغة ليست خطأً: أودو يأخذ قائمة العميل الافتراضية.
            var pricelist = string.IsNullOrWhiteSpace(mapping.OdooPricelist)
                || await _odoo.SearchCountAsync("product.pricelist",
                    new object[] { new object[] { "name", "=", mapping.OdooPricelist } }, ct) > 0;

            var missing = new List<string>();

            if (barcodes.Count > 0)
            {
                // يُبحث في الحقلين معاً. حقل barcode هو موضعه الطبيعي، لكن في
                // قاعدتهم كل المنتجات barcode=false والرمز مكتوب في
                // default_code — قِيس ذلك على ٤٠٢ منتج، ولو فُحص حقلٌ واحد
                // لقيل إن كل بند مفقود وهو موجود.
                //
                // وتُطلب دفعةً واحدة: نداءٌ لكل باركود على خادمٍ يتقلّب يعني
                // عشرات الرحلات وأولها قد يفشل.
                var domain = new object[]
                {
                    "|",
                    new object[] { "barcode", "in", barcodes },
                    new object[] { "default_code", "in", barcodes }
                };

                var found = await _odoo.SearchReadAsync("product.product",
                    domain, new[] { "barcode", "default_code" }, ct: ct);

                var present = found
                    .SelectMany(p => new[] { OdooValue.Text(p["barcode"]), OdooValue.Text(p["default_code"]) })
                    .Where(b => b is not null)
                    .ToHashSet()!;

                missing.AddRange(barcodes.Where(b => !present.Contains(b)));
            }

            return new PreflightResult(customer, pricelist, missing, barcodes.Count, null)
            {
                Pricing = await CheckPricingAsync(document, mapping, ct)
            };
        }
        catch (OdooException ex)
        {
            return new PreflightResult(false, false, Array.Empty<string>(), barcodes.Count,
                $"تعذّر فحص الملف قبل توليده: {ex.Message}");
        }
    }

    /// <summary>
    /// يحسب ما سيحسبه أودو للأوردر، ويقارنه بما في الورقة.
    ///
    /// الترتيب هو ترتيب أودو نفسه: أيّ قائمة أسعارٍ ستُطبَّق، ثم أيّ بنودٍ فيها،
    /// ثم كل منتجٍ — إن وجد له بندٌ فيها أخذ سعره، وإلا فسعره العام. ولا يُفترض
    /// أن السعر العام صحيح: في قاعدتهم هو جنيهٌ واحد لكل الأصناف.
    /// </summary>
    public async Task<PricingCheck> CheckPricingAsync(
        IntakeDocument document, BranchMapping mapping, CancellationToken ct = default)
    {
        if (!_odoo.IsConfigured) return PricingCheck.NotChecked;

        var lines = document.Lines
            .Where(l => l.Barcode.HasValue && l.OrderedQty.HasValue)
            .ToList();

        if (lines.Count == 0) return PricingCheck.NotChecked;

        var warnings = new List<string>();

        // ١) أيّ قائمةٍ ستُطبَّق: المكتوبة في جدول الربط، وإلا فقائمة العميل
        //    الافتراضية في أودو. والفرق مهم: الثانية لا يراها المراجع عندنا.
        long? pricelistId = null;
        var pricelistName = mapping.OdooPricelist;
        var fromMapping = !string.IsNullOrWhiteSpace(pricelistName);

        if (fromMapping)
        {
            var rows = await _odoo.SearchReadAsync("product.pricelist",
                new object[] { new object[] { "name", "=", pricelistName! } },
                new[] { "id" }, limit: 1, ct: ct);

            if (rows.Count > 0) pricelistId = rows[0]["id"]?.GetValue<long>();
        }
        else
        {
            var partner = await _odoo.SearchReadAsync("res.partner",
                new object[] { new object[] { "display_name", "=", mapping.OdooCustomer ?? "" } },
                new[] { "property_product_pricelist" }, limit: 1, ct: ct);

            if (partner.Count > 0)
            {
                pricelistId = OdooValue.RelationId(partner[0]["property_product_pricelist"]);
                pricelistName = OdooValue.RelationName(partner[0]["property_product_pricelist"]);
            }
        }

        if (pricelistId is null)
            return PricingCheck.NotChecked with
            {
                PricelistName = pricelistName,
                Warnings = new[] { "تعذّر تحديد قائمة الأسعار التي سيطبّقها أودو، فلم يُفحص السعر." }
            };

        // ٢) بنود القائمة. تُقرأ كلها لا المطلوبة وحدها: عددها هو الدليل على أن
        //    القائمة فارغة، وهو أهمّ ما في هذا الفحص.
        var items = await _odoo.SearchReadAsync("product.pricelist.item",
            new object[] { new object[] { "pricelist_id", "=", pricelistId.Value } },
            new[] { "product_tmpl_id", "fixed_price", "applied_on", "compute_price" }, ct: ct);

        var special = new Dictionary<long, decimal>();
        var beyondCheck = 0;

        foreach (var item in items)
        {
            var template = OdooValue.RelationId(item["product_tmpl_id"]);
            var fixedPrice = OdooValue.Text(item["compute_price"]) == "fixed";

            // ما ليس سعراً ثابتاً على صنفٍ بعينه (نسبةٌ أو قاعدةٌ عامة) لا
            // يُحسب هنا، ويُقال إنه لم يُحسب بدل أن يُتجاهل صامتاً.
            if (template is null || !fixedPrice) { beyondCheck++; continue; }

            special[template.Value] = Money(item["fixed_price"]);
        }

        if (items.Count == 0)
            warnings.Add($"قائمة الأسعار «{pricelistName}» فارغة — لا سعر خاص لأيّ صنف، "
                       + "وسيُباع كل بندٍ بسعره العام في أودو.");

        if (!fromMapping && pricelistName is not null)
            warnings.Add($"لا قائمة أسعار في ربط الفرع، فسيأخذ أودو قائمة العميل «{pricelistName}».");

        // ٣) المنتجات: القالب لمطابقة بنود القائمة، والسعر العام لِما لا بند له.
        var codes = lines.Select(l => l.Barcode.Value!).Distinct().ToArray();

        var products = await _odoo.SearchReadAsync("product.product",
            new object[]
            {
                "|",
                new object[] { "barcode", "in", codes },
                new object[] { "default_code", "in", codes }
            },
            new[] { "product_tmpl_id", "lst_price", "barcode", "default_code" }, ct: ct);

        var byCode = new Dictionary<string, (long Template, decimal ListPrice)>();

        foreach (var product in products)
        {
            var template = OdooValue.RelationId(product["product_tmpl_id"]);
            if (template is null) continue;

            var entry = (template.Value, Money(product["lst_price"]));

            foreach (var code in new[] { OdooValue.Text(product["barcode"]), OdooValue.Text(product["default_code"]) })
                if (code is not null && codes.Contains(code)) byCode[code] = entry;
        }

        // ٤) الحساب سطراً سطراً
        decimal odooTotal = 0m, documentTotal = 0m;
        int withSpecial = 0, atBase = 0;

        foreach (var line in lines)
        {
            var quantity = line.OrderedQty.Value;

            if (line.DocumentUnitPrice is { HasValue: true } sheetPrice)
                documentTotal += sheetPrice.Value * quantity;

            if (!byCode.TryGetValue(line.Barcode.Value!, out var product)) continue;

            decimal price;

            if (special.TryGetValue(product.Template, out var discounted)) { price = discounted; withSpecial++; }
            else { price = product.ListPrice; atBase++; }

            odooTotal += price * quantity;
        }

        // بندٌ لم يُطابق منتجاً لم يدخل الحساب أصلاً. يُقال هنا أيضاً: مسار
        // الترحيل يفحص السعر وحده، فلو سكت هنا مرّ المستند بلا أي تنبيه.
        var unmatched = lines.Count - withSpecial - atBase;

        if (unmatched > 0)
            warnings.Add($"{unmatched} من {lines.Count} بند لا منتج له في أودو، فلم يدخل حساب السعر.");

        if (atBase > 0 && items.Count > 0)
            warnings.Add($"{atBase} من {lines.Count} بند لا سعر خاص له في «{pricelistName}» — "
                       + "سيُباع بسعره العام.");

        if (beyondCheck > 0)
            warnings.Add($"في القائمة {beyondCheck} بند تسعيرٍ بنسبةٍ أو قاعدةٍ عامة، "
                       + "لم يدخل في هذا التقدير.");

        // ٥) الفارق عن الورقة: أوضح إشارةٍ على أن التسعير ذاهبٌ في طريقٍ خاطئ.
        if (documentTotal > 0 && odooTotal > 0)
        {
            var ratio = odooTotal / documentTotal;

            if (ratio < 0.5m || ratio > 2m)
                warnings.Add($"إجمالي المستند {documentTotal:N2} وأودو سيحسبه {odooTotal:N2} — "
                           + "فارقٌ كبير. راجع قائمة الأسعار قبل الإرسال.");
        }
        else if (documentTotal > 0 && odooTotal == 0)
        {
            warnings.Add("المستند يحمل أسعاراً ولم يُحسب من أودو سعرٌ لأيّ بند.");
        }

        return new PricingCheck(pricelistName, fromMapping, items.Count,
            withSpecial, atBase, documentTotal, odooTotal, warnings);
    }

    private static decimal Money(System.Text.Json.Nodes.JsonNode? node) =>
        decimal.TryParse(OdooValue.Text(node), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0m;
}
