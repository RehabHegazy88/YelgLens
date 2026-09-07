using YelgLens.Intake.BLL.Services.Odoo;
using YelgLens.Intake.Model.Mapping;

namespace YelgLens.Intake.BLL.Services.Mapping;

/// <summary>ما وجده الفحص لربطٍ واحد.</summary>
public sealed record MappingCheck(
    BranchMapping Mapping,
    bool CustomerFound,
    bool PricelistNamed,
    bool PricelistFound,
    int PricelistItems,
    string? NearestName)
{
    /// <summary>لا اسم عميلٍ أصلاً — ربطٌ لم يُكمَل بعد.</summary>
    public bool Incomplete => string.IsNullOrWhiteSpace(Mapping.OdooCustomer);

    /// <summary>سيرفضه أودو: الاسم أو القائمة غير موجودة هناك.</summary>
    public bool Broken => !Incomplete && (!CustomerFound || (PricelistNamed && !PricelistFound));

    /// <summary>يمرّ، لكن التسعير سيخرج بالسعر العام.</summary>
    public bool PricelistEmpty => !Broken && PricelistNamed && PricelistFound && PricelistItems == 0;

    public bool Healthy => !Incomplete && !Broken && !PricelistEmpty;
}

/// <summary>حصيلة فحص كل الربطات مقابل قاعدةٍ بعينها.</summary>
public sealed record MappingAuditReport(
    string Database,
    DateTime RunAt,
    IReadOnlyList<MappingCheck> Checks,
    string? Error)
{
    public int Total => Checks.Count;
    public int Broken => Checks.Count(c => c.Broken);
    public int Incomplete => Checks.Count(c => c.Incomplete);
    public int EmptyPricelist => Checks.Count(c => c.PricelistEmpty);
    public int Healthy => Checks.Count(c => c.Healthy);

    /// <summary>
    /// جاهزٌ للعمل على هذه القاعدة.
    ///
    /// ويُشترط وجود ربطاتٍ أصلاً: جدولٌ فارغ ليس «سليماً» بل «لم يُعدّ بعد»،
    /// والخلط بينهما يقول للمستخدم إنه جاهز وهو لم يبدأ.
    /// </summary>
    public bool ReadyToSwitch => Error is null && Total > 0 && Broken == 0 && Incomplete == 0;
}

public interface IMappingAudit
{
    bool IsAvailable { get; }

    /// <summary>يفحص كل ربطات القاعدة المتصل بها دفعةً واحدة.</summary>
    Task<MappingAuditReport> RunAsync(CancellationToken ct = default);
}

/// <summary>
/// يفحص جدول الربط كله مقابل أودو المتصل به.
///
/// الفحص المسبق يكشف خلل مستندٍ واحد عند ترحيله، وهو ما يكفي في اليوم
/// العادي. لكن الانتقال إلى أودو آخر — قاعدةٍ حيّة، أو أودو عميلٍ جديد من
/// عملائنا — يقلب كل الربطات دفعةً واحدة: الأسماء كُتبت لأودو غيره، ولا
/// يُعرف أيّها بقي صالحاً إلا بالسؤال. واكتشافُ ذلك ورقةً ورقة بعد الانتقال
/// يعني أن الاكتشاف يقع على المراجع في وقت العمل.
///
/// ويُسأل دفعةً واحدة لا اسماً اسماً: خادمهم بطيء، وثلاثون ربطاً تعني ثلاثين
/// رحلة أولاها قد يفشل.
/// </summary>
public sealed class MappingAudit : IMappingAudit
{
    private readonly IBranchMappingService _mappings;
    private readonly IOdooClient _odoo;
    private readonly IOdooCustomerService _customers;

    public MappingAudit(
        IBranchMappingService mappings, IOdooClient odoo, IOdooCustomerService customers)
    {
        _mappings = mappings;
        _odoo = odoo;
        _customers = customers;
    }

    public bool IsAvailable => _odoo.IsConfigured;

    public async Task<MappingAuditReport> RunAsync(CancellationToken ct = default)
    {
        var mappings = await _mappings.GetAllAsync();
        var now = DateTime.Now;

        if (!_odoo.IsConfigured)
            return new MappingAuditReport("", now, Array.Empty<MappingCheck>(),
                "الاتصال بأودو غير مضبوط، فلا شيء يُقاس عليه.");

        var database = _odoo.Database;

        if (mappings.Count == 0)
            return new MappingAuditReport(database, now, Array.Empty<MappingCheck>(), null);

        try
        {
            var customers = await FoundNamesAsync("res.partner", "display_name",
                mappings.Select(m => m.OdooCustomer), ct);

            var pricelists = await PricelistsAsync(mappings, ct);

            var checks = new List<MappingCheck>();

            foreach (var mapping in mappings)
            {
                var named = !string.IsNullOrWhiteSpace(mapping.OdooPricelist);
                var found = named && pricelists.TryGetValue(mapping.OdooPricelist!, out var items);

                var customerFound = !string.IsNullOrWhiteSpace(mapping.OdooCustomer)
                                    && customers.Contains(mapping.OdooCustomer!);

                // الاقتراح لا يُطلب إلا لما سقط: نداءٌ لكل ربطٍ سليم إسرافٌ
                // على خادمٍ بطيء.
                string? nearest = null;

                if (!customerFound && !string.IsNullOrWhiteSpace(mapping.OdooCustomer))
                    nearest = (await _customers.SuggestAsync(mapping.SourceLabel))?.Customer.DisplayName;

                checks.Add(new MappingCheck(mapping, customerFound, named, found,
                    found ? pricelists[mapping.OdooPricelist!] : 0, nearest));
            }

            return new MappingAuditReport(database, now, checks, null);
        }
        catch (OdooException ex)
        {
            return new MappingAuditReport(database, now, Array.Empty<MappingCheck>(),
                $"تعذّر الفحص: {ex.Message}");
        }
    }

    /// <summary>يسأل عن كل الأسماء في نداءٍ واحد ويعيد الموجود منها.</summary>
    private async Task<HashSet<string>> FoundNamesAsync(
        string model, string field, IEnumerable<string?> names, CancellationToken ct)
    {
        var wanted = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToArray();

        if (wanted.Length == 0) return new HashSet<string>();

        var rows = await _odoo.SearchReadAsync(model,
            new object[] { new object[] { field, "in", wanted } },
            new[] { field }, ct: ct);

        return rows.Select(r => OdooValue.Text(r[field]))
                   .Where(n => n is not null)
                   .ToHashSet()!;
    }

    /// <summary>
    /// قوائم الأسعار المذكورة في الربط، ومعها عدد بنودها.
    ///
    /// والعدد جزءٌ من الفحص لا زينة: قائمةٌ موجودةٌ بالاسم وفارغةٌ من البنود
    /// تمرّ من كل فحصٍ ثم تبيع كل صنفٍ بسعره العام — وهو جنيهٌ واحد في
    /// قاعدتهم.
    /// </summary>
    private async Task<Dictionary<string, int>> PricelistsAsync(
        IReadOnlyList<BranchMapping> mappings, CancellationToken ct)
    {
        var wanted = mappings.Select(m => m.OdooPricelist)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .ToArray();

        if (wanted.Length == 0) return new Dictionary<string, int>();

        var rows = await _odoo.SearchReadAsync("product.pricelist",
            new object[] { new object[] { "name", "in", wanted } },
            new[] { "id", "name" }, ct: ct);

        var byId = new Dictionary<long, string>();

        foreach (var row in rows)
            if (row["id"]?.GetValue<long>() is { } id && OdooValue.Text(row["name"]) is { } name)
                byId[id] = name;

        var counts = byId.Keys.ToDictionary(id => id, _ => 0);

        if (byId.Count > 0)
        {
            var groups = await _odoo.ReadGroupAsync("product.pricelist.item",
                new object[] { new object[] { "pricelist_id", "in", byId.Keys.ToArray() } },
                new[] { "pricelist_id" }, new[] { "pricelist_id" }, ct: ct);

            foreach (var group in groups)
                if (OdooValue.RelationId(group["pricelist_id"]) is { } id && counts.ContainsKey(id))
                    counts[id] = group["__count"]?.GetValue<int>()
                                 ?? group["pricelist_id_count"]?.GetValue<int>() ?? 0;
        }

        return byId.ToDictionary(p => p.Value, p => counts[p.Key]);
    }
}
