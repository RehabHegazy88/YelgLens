using Microsoft.EntityFrameworkCore;
using YelgLens.Intake.BLL.IRepository.Core;
using YelgLens.Intake.BLL.Services.Odoo;
using YelgLens.Intake.BLL.ViewModel;
using YelgLens.Intake.DAL.Data;
using YelgLens.Intake.Model.Basic;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.BLL.Repository.Core;

public class IntakeDocumentRepository : IIntakeDocumentRepository
{
    private readonly IUnitOfWork<MainDbContext> _uow;
    private readonly IOdooClient _odoo;

    public IntakeDocumentRepository(IUnitOfWork<MainDbContext> uow, IOdooClient odoo)
    {
        _uow = uow;
        _odoo = odoo;
    }

    /// <summary>العميل المعمول عنده الآن — قاعدة أودو الموصولة.</summary>
    public string OwnerDatabase => _odoo.Database ?? "";

    /// <summary>
    /// مستندات العميل المعمول عنده وحده.
    ///
    /// القصر هنا لا في كل شاشة: القوائم ولوحة المتابعة والمراجعة والبحث كلها
    /// تمرّ من هنا، وتركُ القصر لكل شاشة يعني أن تُنسى واحدة — والمنسيّة هي
    /// التي تعرض ورقة عميلٍ لموظّف عميلٍ آخر.
    ///
    /// وما لا صاحب له يظهر للجميع: مستنداتٌ سابقة لهذا العمود، وإخفاؤها يمحو
    /// تاريخاً قائماً من الشاشة. وحين لا يكون ثمّة اتصالٌ أصلاً لا يُقصر شيء.
    /// </summary>
    public IQueryable<IntakeDocument> Get()
    {
        var owner = OwnerDatabase;

        var all = _uow.Query<IntakeDocument>().Where(d => !d.Deleted);

        if (string.IsNullOrWhiteSpace(owner)) return all;

        return all.Where(d => d.OwnerDatabase == null || d.OwnerDatabase == owner);
    }

    /// <summary>مستندات كل العملاء — للصيانة والفحص لا للتشغيل.</summary>
    public IQueryable<IntakeDocument> GetEverywhere() =>
        _uow.Query<IntakeDocument>().Where(d => !d.Deleted);

    public Task<IntakeDocument?> GetAsync(long id) =>
        Get().FirstOrDefaultAsync(d => d.Id == id);

    public Task<IntakeDocument?> GetWithDetailsAsync(long id) =>
        Get()
            .Include(d => d.Lines.OrderBy(l => l.Sequence))
            .Include(d => d.Issues)
            .Include(d => d.Pages.OrderBy(p => p.PageNumber))
            .Include(d => d.UploadedBy)
            .Include(d => d.ReviewedBy)
            .Include(d => d.ReopenedBy)
            .FirstOrDefaultAsync(d => d.Id == id);

    /// <summary>
    /// تُفحص بصمة المستند وبصمات صفحاته معاً: الورقة قد تكون صفحةً ثانية في
    /// مستند سابق، ورفعها من جديد ينشئ طلبيةً ثانية من ورقة واحدة.
    /// </summary>
    public Task<IntakeDocument?> FindByHashAsync(string sha256) =>
        Get().FirstOrDefaultAsync(d =>
            d.SourceSha256 == sha256 || d.Pages.Any(p => p.Sha256 == sha256));

    public Task<List<IntakeDocument>> GetQueueAsync(IntakeStatus? status, int take = 200)
    {
        var query = Get().Include(d => d.UploadedBy).Include(d => d.Lines).AsQueryable();

        if (status.HasValue) query = query.Where(d => d.Status == status.Value);

        // الأقدم أولاً: المستند الذي طال انتظاره أحق بالنظر.
        return query.OrderBy(d => d.UploadedDate).Take(take).ToListAsync();
    }

    /// <summary>
    /// يُرشَّح ثم يُعدّ ثم تُقتطع الصفحة — بهذا الترتيب.
    ///
    /// العدّ يجري على الاستعلام المرشَّح قبل الاقتطاع، لأن عدد الصفحات لا
    /// يُستنتج من طول الصفحة المعروضة. والبنود تُحمَّل بعد الاقتطاع لا قبله،
    /// حتى لا تُجلب بنود مئات المستندات لعرض خمسة وعشرين.
    /// </summary>
    public async Task<PagedResult<IntakeDocument>> SearchAsync(DocumentQuery query)
    {
        var filtered = query.Archived switch
        {
            ArchiveFilter.Archived => _uow.Query<IntakeDocument>().Where(d => d.Deleted),
            ArchiveFilter.All      => _uow.Query<IntakeDocument>(),
            _ => Get()
        };

        if (query.Status is { } status)
            filtered = filtered.Where(d => d.Status == status);

        filtered = query.Publish switch
        {
            PublishFilter.Published    => filtered.Where(d => d.OdooOrderId != null),
            PublishFilter.NotPublished => filtered.Where(d => d.OdooOrderId == null && d.PublishError == null),
            PublishFilter.Failed       => filtered.Where(d => d.OdooOrderId == null && d.PublishError != null),
            _ => filtered
        };

        if (query.From is { } from)
            filtered = filtered.Where(d => d.UploadedDate >= from.Date);

        // الحد الأعلى يشمل يومه كله: من يكتب "إلى ٥ سبتمبر" يقصد نهايته.
        if (query.To is { } to)
            filtered = filtered.Where(d => d.UploadedDate < to.Date.AddDays(1));

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var text = query.Text.Trim();

            // رقم المستند يُعرض في الجدول ويُنسخ منه، فيُبحث به كما يُبحث
            // بالنص. وهو مطابقة تامة لا احتواء: "1" لا يعني كل ما فيه واحد.
            var id = long.TryParse(text, out var parsed) ? parsed : (long?)null;

            // SQL Server كان يقارن بلا حساسيةٍ لحالة الحرف بحكم ترتيبه، وPostgreSQL
            // يقارن حرفاً بحرف. لولا ILike لصار من يكتب "ali" لا يجد "Ali" — ينكسر
            // البحث في صمتٍ لا يُبلَّغ عنه. والنص يُهرَّب لأن _ و% محرفا نمطٍ في LIKE.
            var like = "%" + text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";

            filtered = filtered.Where(d =>
                (id != null && d.Id == id) ||
                (d.CustomerPoNumber.Value != null && EF.Functions.ILike(d.CustomerPoNumber.Value, like, @"\")) ||
                (d.CustomerName.Value != null && EF.Functions.ILike(d.CustomerName.Value, like, @"\")) ||
                (d.BranchLabel.Value != null && EF.Functions.ILike(d.BranchLabel.Value, like, @"\")) ||
                EF.Functions.ILike(d.SourceFileName, like, @"\"));
        }

        var total = await filtered.CountAsync();

        var ordered = query.OldestFirst
            ? filtered.OrderBy(d => d.UploadedDate)
            : filtered.OrderByDescending(d => d.UploadedDate);

        var items = await ordered
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Include(d => d.UploadedBy)
            .Include(d => d.Lines)
            .ToListAsync();

        return new PagedResult<IntakeDocument>(items, total, query.Page, query.PageSize);
    }

    /// <summary>
    /// عدّ واحد مُجمَّع بدل استعلامٍ لكل حالة: القائمة تُفتح في كل مرة، وخمسة
    /// استعلامات لعدّادٍ فوق الجدول ثمنٌ يُدفع بلا مقابل.
    /// </summary>
    public async Task<Dictionary<IntakeStatus, int>> CountByStatusAsync()
    {
        var counts = await Get()
            .GroupBy(d => d.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        // الحالة الخالية تُعرض صفراً لا تُحذف: غياب العدّاد يُقرأ خطأً على أنه
        // غياب الحالة نفسها.
        foreach (var status in Enum.GetValues<IntakeStatus>())
            counts.TryAdd(status, 0);

        return counts;
    }

    public Task<List<IntakeDocument>> GetByUploaderAsync(long userId, int take = 100) =>
        Get().Include(d => d.ReviewedBy)
             .Include(d => d.Lines)
             .Where(d => d.UploadedByUserId == userId)
             .OrderByDescending(d => d.UploadedDate)
             .Take(take)
             .ToListAsync();

    public Task<IntakeDocument?> GetIncludingArchivedAsync(long id) =>
        _uow.Query<IntakeDocument>()
            .Include(d => d.Lines).Include(d => d.Issues).Include(d => d.Pages)
            .Include(d => d.UploadedBy).Include(d => d.ReviewedBy)
            .FirstOrDefaultAsync(d => d.Id == id);

    public async Task<int> ArchiveAsync(IReadOnlyList<long> ids, long userId)
    {
        if (ids.Count == 0) return 0;

        var documents = await _uow.Query<IntakeDocument>()
            .Include(d => d.Lines).Include(d => d.Issues).Include(d => d.Pages)
            .Where(d => ids.Contains(d.Id) && !d.Deleted)
            .ToListAsync();

        var now = DateTime.Now;

        foreach (var document in documents)
        {
            Mark(document, now);
            document.ArchivedByUserId = userId;

            // الأبناء يُؤرشفون مع أبيهم: بندٌ حيٌّ تحت مستندٍ مؤرشف يظهر في
            // إحصاءٍ لا يعرف من أين جاء.
            foreach (var line in document.Lines) Mark(line, now);
            foreach (var issue in document.Issues) Mark(issue, now);
            foreach (var page in document.Pages) Mark(page, now);
        }

        await _uow.CommitAsync();
        return documents.Count;
    }

    public async Task<int> RestoreAsync(IReadOnlyList<long> ids, long userId)
    {
        if (ids.Count == 0) return 0;

        var documents = await _uow.Query<IntakeDocument>()
            .Include(d => d.Lines).Include(d => d.Issues).Include(d => d.Pages)
            .Where(d => ids.Contains(d.Id) && d.Deleted)
            .ToListAsync();

        var now = DateTime.Now;

        foreach (var document in documents)
        {
            Revive(document, now);

            // من أعادها يُسجَّل مكان من أرشفها: آخر فعلٍ هو ما يُسأل عنه.
            document.ArchivedByUserId = userId;

            foreach (var line in document.Lines) Revive(line, now);
            foreach (var issue in document.Issues) Revive(issue, now);
            foreach (var page in document.Pages) Revive(page, now);
        }

        await _uow.CommitAsync();
        return documents.Count;
    }

    private static void Mark<TKey>(BaseEntity<TKey> entity, DateTime now)
    {
        entity.Deleted = true;
        entity.DeleteDate = now;
        entity.LastModifiedDate = now;
    }

    private static void Revive<TKey>(BaseEntity<TKey> entity, DateTime now)
    {
        entity.Deleted = false;
        entity.DeleteDate = null;
        entity.LastModifiedDate = now;
    }

    public Task<IntakeDocument> AddAsync(IntakeDocument document)
    {
        _uow.Add(document);
        return Task.FromResult(document);
    }

    public Task SaveAsync() => _uow.CommitAsync();
}
