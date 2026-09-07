using System.Text.Json.Nodes;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using YelgLens.Intake.DAL.Data;
using YelgLens.Intake.Model.Mapping;
using YelgLens.Intake.BLL.Services.Odoo;

namespace YelgLens.Intake.BLL.Services.Mapping;

/// <summary>حصيلة استيراد كشفٍ من أودو.</summary>
public sealed record CustomerImportResult(
    int Read, int Added, int Restored, int Retired, int Total, bool HasDisplayNames)
{
    public bool IsEmpty => Read == 0;
}

/// <summary>اقتراحُ عميلٍ لفرعٍ، ومعه سببه — الاقتراح بلا سببٍ لا يُراجَع.</summary>
public sealed record CustomerSuggestion(OdooCustomer Customer, string Reason);

/// <summary>نتيجة فحص اسمٍ قبل حفظه في جدول الربط.</summary>
public sealed record CustomerCheck(bool IsKnown, bool PrefixUnverified, string? Message);

/// <summary>
/// من أين تُؤخذ أسماء العملاء الآن، وكم فيها.
///
/// يُعرض ليُفهم الفراغ: شاشةٌ بلا عملاء بعد تبديل القاعدة ليست عطلاً، بل
/// كشفاً لم يُزامَن بعد — والفرق بينهما هو ما يوفّر ساعةً من البحث.
/// </summary>
public sealed record CustomerSourceStatus(string Source, bool Connected, int Count, int SheetCount)
{
    /// <summary>لا أسماء من القاعدة المتصل بها. المزامنة هي الحل.</summary>
    public bool NeedsSync => Connected && Count == 0;

    /// <summary>عندنا كشفٌ مرفوع لكنه لقاعدةٍ أخرى، فلا يُعرض.</summary>
    public bool SheetIgnored => Connected && SheetCount > 0 && Source != OdooCustomerService.SheetSource;
}

public interface IOdooCustomerService
{
    Task<CustomerImportResult> ImportAsync(Stream spreadsheet, CancellationToken ct = default);

    /// <summary>
    /// يسحب العملاء من أودو مباشرةً. أدقّ من الكشف: الاسم المعروض يأتي كما
    /// يبنيه أودو نفسه، فلا حاجة إلى استنتاج بادئة الشركة الأم.
    /// </summary>
    Task<CustomerImportResult> SyncFromOdooAsync(CancellationToken ct = default);

    /// <summary>هل الاتصال بأودو مضبوط أصلاً؟</summary>
    bool CanSync { get; }

    /// <summary>القاعدة أو الكشف الذي تُؤخذ منه الأسماء الآن.</summary>
    Task<string> ActiveSourceAsync();

    /// <summary>حال الكشف مقابل القاعدة المتصل بها — يُعرض ليُعرف سبب الفراغ.</summary>
    Task<CustomerSourceStatus> StatusAsync(CancellationToken ct = default);

    /// <summary>العملاء المتاحون للاختيار، مرتبين بالاسم المعروض.</summary>
    Task<List<OdooCustomer>> GetActiveAsync();

    Task<int> CountActiveAsync();

    Task<DateTime?> LastImportDateAsync();

    /// <summary>هل يحمل الكشف المستورد أسماء أودو المعروضة كاملةً؟</summary>
    Task<bool> HasDisplayNamesAsync();

    /// <summary>يفحص اسماً قبل حفظه — قبل فشل الاستيراد لا بعده.</summary>
    Task<CustomerCheck> CheckAsync(string name);

    /// <summary>يقترح عميلاً لنصّ فرعٍ ورد في مستند، أو لا يقترح.</summary>
    Task<CustomerSuggestion?> SuggestAsync(string? branchLabel);
}

/// <summary>
/// يستورد كشف <c>res.partner</c> ويقترح منه.
///
/// الكشف صورةٌ من أودو تُحدَّث كلما صُدِّرت، فالاستيراد دمجٌ لا استبدال:
/// الاسم الذي ورد يُحدَّث أو يُضاف، والذي غاب يُعطَّل ولا يُمحى — لأن ربطاً
/// قائماً قد يشير إليه، ومحوُه يقطع أثر أوردرات سابقة.
/// </summary>
public class OdooCustomerService : IOdooCustomerService
{
    private readonly MainDbContext _db;
    private readonly IOdooClient _odoo;

    public OdooCustomerService(MainDbContext db, IOdooClient odoo)
    {
        _db = db;
        _odoo = odoo;
    }

    public bool CanSync => _odoo.IsConfigured;

    /// <summary>عناوين عمود الاسم المقبولة — أودو يصدّره بالإنجليزية وقد يُترجَم.</summary>
    private static readonly string[] NameHeaders = { "name", "customer", "partner", "الاسم" };

    /// <summary>
    /// عمود الاسم المعروض إن صُدِّر مباشرةً — أدقّ ما يمكن، لأنه نصُّ أودو نفسه.
    /// </summary>
    private static readonly string[] DisplayHeaders = { "display name", "display_name", "الاسم المعروض" };

    /// <summary>عمود الشركة الأم — منه تُبنى البادئة حين لا يُصدَّر الاسم المعروض.</summary>
    private static readonly string[] ParentHeaders =
        { "related company", "parent company", "parent_id", "parent", "company name", "الشركة الأم" };

    /// <summary>مصدر الكشف المرفوع يدوياً — يُميَّز عمّا يأتي من قاعدةٍ باسمها.</summary>
    public const string SheetSource = "sheet";

    public async Task<CustomerImportResult> ImportAsync(Stream spreadsheet, CancellationToken ct = default)
        => await MergeAsync(ReadRows(spreadsheet), SheetSource, ct);

    /// <summary>
    /// يسحب <c>res.partner</c> من أودو مباشرةً.
    ///
    /// هذا هو الطريق الصحيح: <c>display_name</c> يأتي كما يبنيه أودو، فينتهي
    /// كل خلافٍ حول بادئة الشركة الأم — لا استنتاج ولا كتابة بيد. والكشف يبقى
    /// طريقاً بديلاً لمن لا يملك مفتاحاً.
    ///
    /// ويُقرأ على دفعات: خادمهم يتجاوز المهلة على استعلامٍ واحد كبير، وقراءة
    /// نصف القائمة ثم الفشل تُعطِّل النصف الآخر بلا سبب.
    /// </summary>
    public async Task<CustomerImportResult> SyncFromOdooAsync(CancellationToken ct = default)
    {
        // العميل هو من له رتبة عميل. أخذُ كل جهات الاتصال يُدخل الموردين
        // والموظفين في قائمة يُختار منها عميلُ أمر بيع.
        var domain = new object[] { new object[] { "customer_rank", ">", 0 } };
        var fields = new[] { "name", "display_name", "parent_id" };

        const int batch = 200;
        var rows = new List<Row>();
        var seen = new HashSet<string>();

        for (var offset = 0; ; offset += batch)
        {
            var page = await _odoo.SearchReadAsync(
                "res.partner", domain, fields, offset, batch, order: "id", ct: ct);

            foreach (var record in page)
            {
                // كل حقلٍ يُقرأ عبر OdooValue: أودو يكتب false لا null للفارغ،
                // في كل نوع، وقراءته مباشرةً ترمي استثناءً يوقف المزامنة كلها.
                var name = OdooValue.Text(record["name"])?.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                var display = OdooValue.Text(record["display_name"])?.Trim();
                var parent = OdooValue.RelationName(record["parent_id"])?.Trim();

                if (seen.Add(name)) rows.Add(new Row(name, display, parent));
            }

            if (page.Count < batch) break;
        }

        return await MergeAsync(rows, _odoo.Database, ct);
    }

    /// <summary>
    /// يدمج قائمةً واردة في الجدول: ما ورد يُحدَّث أو يُضاف، وما غاب يُعطَّل
    /// ولا يُمحى — لأن ربطاً قائماً قد يشير إليه، ومحوُه يقطع أثر أوردرات سابقة.
    /// </summary>
    private async Task<CustomerImportResult> MergeAsync(List<Row> rows, string source, CancellationToken ct)
    {
        if (rows.Count == 0)
            return new CustomerImportResult(0, 0, 0, 0, await ActiveCountAsync(ct), false);

        var now = DateTime.Now;

        // المقارنة داخل المصدر وحده: قائمةٌ من قاعدة الاختبار لا تُعطّل عملاء
        // الإنتاج، وقد قِيس أن القاعدتين تحملان عملاء مختلفين لا نسخةً واحدة.
        var existing = await _db.OdooCustomers
            .Where(c => c.Source == source)
            .ToDictionaryAsync(c => c.Name, ct);

        var added = 0;
        var restored = 0;

        foreach (var (name, display, parent) in rows)
        {
            if (existing.TryGetValue(name, out var customer))
            {
                if (!customer.IsActive) { customer.IsActive = true; restored++; }

                // المشتقات تُعاد في كل استيراد: قاعدة الاشتقاق قد تتغيّر،
                // والسجل القديم لا يعرف أنها تغيّرت.
                Fill(customer, name, display, parent, source, now);
                customer.LastModifiedDate = now;
                continue;
            }

            var fresh = new OdooCustomer();
            Fill(fresh, name, display, parent, source, now);
            _db.OdooCustomers.Add(fresh);

            added++;
        }

        var seen = rows.Select(r => r.Name).ToHashSet();
        var retired = 0;

        foreach (var customer in existing.Values.Where(c => c.IsActive && !seen.Contains(c.Name)))
        {
            customer.IsActive = false;
            customer.LastModifiedDate = now;
            retired++;
        }

        await _db.SaveChangesAsync(ct);

        return new CustomerImportResult(
            rows.Count, added, restored, retired, await ActiveCountAsync(ct),
            rows.Any(r => r.Display is not null || r.Parent is not null));
    }

    private sealed record Row(string Name, string? Display, string? Parent);

    private static void Fill(
        OdooCustomer customer, string name, string? display, string? parent, string source, DateTime now)
    {
        customer.Name = name;
        customer.Source = source;
        customer.ParentName = parent;
        customer.DisplayName = display ?? OdooCustomer.BuildDisplayName(name, parent);
        customer.SearchKey = OdooCustomer.BuildSearchKey(customer.DisplayName);
        customer.StoreNumber = OdooCustomer.ReadStoreNumber(name);
        customer.LastSeenDate = now;
        customer.IsActive = true;
    }



    /// <summary>
    /// يقرأ الكشف. الأعمدة تُعرف بعناوينها لا بمواضعها: أودو يصدّر ما يُختار،
    /// فترتيب الأعمدة يختلف من تصديرٍ إلى آخر.
    /// </summary>
    private static List<Row> ReadRows(Stream spreadsheet)
    {
        using var workbook = new XLWorkbook(spreadsheet);
        var sheet = workbook.Worksheets.FirstOrDefault();
        if (sheet is null) return new List<Row>();

        var used = sheet.RangeUsed();
        if (used is null) return new List<Row>();

        var header = used.FirstRow();

        int? Find(string[] titles) => header.Cells()
            .FirstOrDefault(c => titles.Contains(c.GetString().Trim().ToLowerInvariant()))
            ?.Address.ColumnNumber;

        var nameColumn = Find(NameHeaders);
        var displayColumn = Find(DisplayHeaders);
        var parentColumn = Find(ParentHeaders);

        // بلا عنوان معروف يُؤخذ العمود الأول: كشف العمود الواحد قد يُرفع بلا رأس.
        var index = nameColumn ?? displayColumn ?? used.FirstColumn().ColumnNumber();
        var hasHeader = nameColumn is not null || displayColumn is not null || parentColumn is not null;

        var rows = new List<Row>();
        var seen = new HashSet<string>();

        foreach (var row in used.Rows())
        {
            if (hasHeader && row.RowNumber() == header.RowNumber()) continue;

            var name = row.Cell(index).GetString().Trim();
            if (name.Length == 0) continue;

            var display = displayColumn is { } d ? Blank(row.Cell(d).GetString()) : null;
            var parent = parentColumn is { } p ? Blank(row.Cell(p).GetString()) : null;

            // حين يُصدَّر الاسم المعروض وحده، يُشتقّ منه اسم الفرع ليبقى
            // مفتاح المطابقة ورقم المحل صحيحين.
            if (nameColumn is null && display is not null) name = OdooCustomer.BranchPartOf(display);

            // الكشف قد يحمل الاسم مرتين؛ الاسم مفتاح مفرد في الجدول، وإضافته
            // مرتين تُفشل الحفظ كله.
            if (seen.Add(name)) rows.Add(new Row(name, display, parent));
        }

        return rows;
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// المصدر الذي يُعتدّ به: القاعدة المتصل بها، لا غيرها.
    ///
    /// ولا رجوع إلى الكشف المرفوع ما دام هناك اتصال. الرجوع كان يبدو لطفاً —
    /// «لا تترك الشاشة فارغة» — وهو في الحقيقة عرضُ أسماءٍ من قاعدةٍ أخرى
    /// على أنها خيارات: يختار المراجع اسماً موجوداً في الكشف وغير موجود في
    /// القاعدة، فيُرفض الملف أو — أسوأ — يُنشأ الأوردر على عميلٍ غير المقصود.
    ///
    /// وشاشةٌ فارغة تقول «زامِن أولاً» أصدق من قائمةٍ ممتلئة بأسماءٍ لا وجود
    /// لها هناك.
    /// </summary>
    public Task<string> ActiveSourceAsync()
    {
        var configured = _odoo.Database;

        return Task.FromResult(string.IsNullOrWhiteSpace(configured) ? SheetSource : configured);
    }

    public async Task<CustomerSourceStatus> StatusAsync(CancellationToken ct = default)
    {
        var source = await ActiveSourceAsync();
        var connected = !string.IsNullOrWhiteSpace(_odoo.Database);
        var count = await ActiveIn(source).CountAsync(ct);

        var sheet = connected
            ? await _db.OdooCustomers.CountAsync(c => c.Source == SheetSource && c.IsActive && !c.Deleted, ct)
            : 0;

        return new CustomerSourceStatus(source, connected, count, sheet);
    }

    private IQueryable<OdooCustomer> ActiveIn(string source) =>
        _db.OdooCustomers.Where(c => c.Source == source && c.IsActive && !c.Deleted);

    private async Task<int> ActiveCountAsync(CancellationToken ct) =>
        await _db.OdooCustomers.CountAsync(c => c.IsActive && !c.Deleted, ct);

    public async Task<List<OdooCustomer>> GetActiveAsync() =>
        await ActiveIn(await ActiveSourceAsync()).OrderBy(c => c.DisplayName).ToListAsync();

    public async Task<int> CountActiveAsync() =>
        await ActiveIn(await ActiveSourceAsync()).CountAsync();

    public async Task<DateTime?> LastImportDateAsync()
    {
        var source = await ActiveSourceAsync();
        return await ActiveIn(source).AnyAsync()
            ? await ActiveIn(source).MaxAsync(c => (DateTime?)c.LastSeenDate)
            : null;
    }

    public async Task<bool> HasDisplayNamesAsync() =>
        await ActiveIn(await ActiveSourceAsync()).AnyAsync(c => c.ParentName != null);

    /// <summary>
    /// المطابقة حرفية عمداً: عمود <c>Customer</c> في أودو يُطابَق بالنص، فقبول
    /// اسمٍ يختلف بحرف يعني استيراداً يفشل أو ينسب الأوردر إلى غير صاحبه.
    ///
    /// وحين يغيب عمود الشركة الأم من الكشف لا يمكن التحقق من البادئة، فيُتحقَّق
    /// مما يمكن: أن جزء الفرع اسمٌ معروف. البادئة تُقبل ويُقال إنها غير محقَّقة —
    /// وهذا أصدق من رفضِ اسمٍ صحيح، أو قبولِ اسمٍ خاطئ بلا كلمة.
    /// </summary>
    public async Task<CustomerCheck> CheckAsync(string name)
    {
        var value = name.Trim();

        var source = await ActiveSourceAsync();

        if (!await ActiveIn(source).AnyAsync())
            return new CustomerCheck(true, false, null);

        if (await ActiveIn(source).AnyAsync(c => c.DisplayName == value))
            return new CustomerCheck(true, false, null);

        var branch = OdooCustomer.BranchPartOf(value);
        if (branch != value && await ActiveIn(source).AnyAsync(c => c.Name == branch))
            return new CustomerCheck(true, true,
                $"الفرع «{branch}» معروف، أما البادئة فلا سبيل إلى التحقق منها: " +
                "الكشف المرفوع بلا عمود الشركة الأم.");

        return new CustomerCheck(false, false,
            "هذا الاسم ليس في كشف عملاء أودو. اختر من القائمة، أو حدّث الكشف إن كان العميل جديداً.");
    }

    /// <summary>
    /// يقترح برقم المحل وحده، وبشرط أن يكون المطابق واحداً.
    ///
    /// رقم الـ DS هو المعرّف الحقيقي المشترك: المستند يكتب
    /// <c>EG_Nasr City (3)_DS_59</c> وأودو يكتب <c>Talabat  Nasr City (3)DS59</c>،
    /// وبينهما من الاختلاف ما يُفشل كل مقارنة لفظية. أما التشابه بالاسم فلا
    /// يُقترح به: فرعان في حيٍّ واحد يتشابهان لفظاً ويختلفان أوردراً.
    ///
    /// والنتيجة اقتراحٌ يُعرض ليُقرّه إنسان، لا ربطٌ يُكتب وحده.
    /// </summary>
    public async Task<CustomerSuggestion?> SuggestAsync(string? branchLabel)
    {
        var store = OdooCustomer.ReadStoreNumber(branchLabel);
        if (store is null) return null;

        var matches = await ActiveIn(await ActiveSourceAsync())
            .Where(c => c.StoreNumber == store)
            .Take(2)
            .ToListAsync();

        // مطابقان يعنيان أن الرقم لا يكفي للتمييز، فلا يُقترح أحدهما.
        return matches.Count == 1
            ? new CustomerSuggestion(matches[0], $"رقم المحل DS {store} يطابق عميلاً واحداً في أودو")
            : null;
    }
}
