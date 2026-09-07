using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using YelgLens.Intake.BLL.Services.Odoo;
using YelgLens.Intake.DAL.Data;
using YelgLens.Intake.Model.Mapping;

namespace YelgLens.Intake.BLL.Services.Mapping;

public interface IBranchMappingService
{
    /// <summary>قاعدة أودو التي تُقرأ ربطاتها الآن.</summary>
    string Database { get; }

    /// <summary>ربطات القاعدة المتصل بها وحدها.</summary>
    IQueryable<BranchMapping> Get();

    /// <summary>ربطات كل القواعد — للإدارة والفحص لا للتشغيل.</summary>
    IQueryable<BranchMapping> GetEverywhere();
    Task<List<BranchMapping>> GetAllAsync();
    Task<BranchMapping?> GetAsync(long id);

    /// <summary>يبحث عن ربطٍ يطابق ما ورد في المستند.</summary>
    Task<BranchMapping?> ResolveAsync(string? sourceLabel);

    /// <summary>
    /// يلتقط فرعاً ورد في مستند ولا ربط له، فيُنشئ سجلاً ناقصاً باسم أودو فارغ.
    /// يعيد السجل القائم إن وُجد ولا ينشئ ثانياً.
    /// </summary>
    Task<BranchMapping?> CaptureAsync(string? sourceLabel, long documentId);

    Task<BranchMapping> SaveAsync(BranchMapping mapping);
    Task DeleteAsync(long id);
}

public sealed class BranchMappingService : IBranchMappingService
{
    private readonly IUnitOfWork<MainDbContext> _uow;
    private readonly IOdooClient _odoo;
    private readonly ILogger<BranchMappingService> _log;

    public BranchMappingService(
        IUnitOfWork<MainDbContext> uow, IOdooClient odoo, ILogger<BranchMappingService> log)
    {
        _uow = uow;
        _odoo = odoo;
        _log = log;
    }

    public string Database => _odoo.Database ?? "";

    /// <summary>
    /// الربطات المعمول بها: ربطات القاعدة المتصل بها وحدها.
    ///
    /// وما لا قاعدة له يُعدّ منها: سجلاتٌ سابقة لهذا العمود، ومنعها يُفرغ
    /// شاشةً تعمل. أما ربطُ قاعدةٍ أخرى فلا يظهر هنا بحال — هو لعميلٍ آخر.
    /// </summary>
    public IQueryable<BranchMapping> Get()
    {
        var database = Database;
        var all = _uow.Query<BranchMapping>().Where(m => !m.Deleted);

        // بلا اتصالٍ لا يُقصر شيء — كما في مستودع المستندات. وقصرُ أحدهما دون
        // الآخر يعرض مستنداتٍ بلا ربطاتها فتبدو كلها بلا عميل.
        if (string.IsNullOrWhiteSpace(database)) return all;

        return all.Where(m => m.OdooDatabase == null || m.OdooDatabase == database);
    }

    public IQueryable<BranchMapping> GetEverywhere() =>
        _uow.Query<BranchMapping>().Where(m => !m.Deleted);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    public Task<List<BranchMapping>> GetAllAsync() =>
        Get().OrderBy(m => m.OdooCustomer).ToListAsync();

    public Task<BranchMapping?> GetAsync(long id) =>
        Get().FirstOrDefaultAsync(m => m.Id == id);

    /// <summary>
    /// المطابقة على مرحلتين: تطابق تام على المفتاح المبسّط أولاً، فإن لم يكن
    /// فاحتواء. ولا يُقبل الاحتواء إلا إذا كان المرشّح واحداً — مرشّحان يعنيان
    /// أن النص يحتمل فرعين، وربطه بأحدهما تخمينٌ يرسل الأوردر لغير صاحبه.
    /// </summary>
    public async Task<BranchMapping?> ResolveAsync(string? sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(sourceLabel)) return null;

        var key = MatchKeyBuilder.Build(sourceLabel);
        if (key.Length == 0) return null;

        var active = await Get().Where(m => m.IsActive).ToListAsync();

        var exact = active.FirstOrDefault(m => m.MatchKey == key);
        if (exact is not null) return exact;

        var contained = active
            .Where(m => m.MatchKey.Length >= 4 &&
                        (key.Contains(m.MatchKey, StringComparison.Ordinal) ||
                         m.MatchKey.Contains(key, StringComparison.Ordinal)))
            .ToList();

        if (contained.Count == 1) return contained[0];

        if (contained.Count > 1)
            _log.LogWarning("النص {Label} يحتمل {Count} ربطاً، فلم يُختر أحدها.",
                sourceLabel, contained.Count);

        return null;
    }

    public async Task<BranchMapping?> CaptureAsync(string? sourceLabel, long documentId)
    {
        if (string.IsNullOrWhiteSpace(sourceLabel)) return null;

        var existing = await ResolveAsync(sourceLabel);
        if (existing is not null) return existing;

        // يُلتقط بالرمز لا بالجملة كاملةً: الرمز ثابت عبر مستندات الفرع،
        // والجملة تحمل عنواناً يتغيّر فتنشأ منها سجلات مكررة لفرع واحد.
        var hint = BranchCodeHint.Suggest(sourceLabel);
        var key = MatchKeyBuilder.Build(hint);
        if (key.Length == 0) return null;

        // سباق نادر: مستندان لفرع جديد في اللحظة نفسها. الفهرس المفرد يمنع
        // التكرار، ويُبتلع خطؤه لأن الغرض حاصل — السجل صار موجوداً.
        if (await Get().AnyAsync(m => m.MatchKey == key)) return null;

        var captured = new BranchMapping
        {
            SourceLabel = hint,
            MatchKey = key,
            OdooDatabase = NullIfBlank(Database),
            OdooCustomer = null,
            AutoCaptured = true,
            FirstSeenDocumentId = documentId,
            IsActive = true
        };

        _uow.Add(captured);

        try
        {
            await _uow.CommitAsync();
            _log.LogInformation("التُقط فرع جديد {Branch} من المستند {Document}.", hint, documentId);
            return captured;
        }
        catch (DbUpdateException)
        {
            return null;
        }
    }

    public async Task<BranchMapping> SaveAsync(BranchMapping mapping)
    {
        mapping.MatchKey = MatchKeyBuilder.Build(mapping.SourceLabel);

        // الربط يُوسم بقاعدته عند الحفظ: بدونه يظهر لكل عميل، ويرسل أوردراً
        // إلى عميلٍ في شركةٍ أخرى.
        mapping.OdooDatabase ??= NullIfBlank(Database);
        mapping.LastModifiedDate = DateTime.Now;

        if (mapping.Id == 0) _uow.Add(mapping);
        else _uow.Update(mapping);

        await _uow.CommitAsync();
        return mapping;
    }

    public async Task DeleteAsync(long id)
    {
        var mapping = await GetAsync(id);
        if (mapping is null) return;

        // حذف منطقي: الربط الذي رُحّل به أوردر أمس دليلٌ على كيف رُحّل.
        mapping.Deleted = true;
        mapping.DeleteDate = DateTime.Now;

        await _uow.CommitAsync();
    }
}

/// <summary>
/// يبسّط النص للمطابقة: يوحّد الهمزات والتاء المربوطة، ويسقط كل ما ليس حرفاً
/// ولا رقماً. الفرع الواحد يُكتب "DS_73" و"DS73" و"DS 73"، وكلها واحد.
/// </summary>
public static class MatchKeyBuilder
{
    public static string Build(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var builder = new StringBuilder(value.Length);

        foreach (var raw in value.Trim().ToLowerInvariant())
        {
            var c = raw switch
            {
                'أ' or 'إ' or 'آ' => 'ا',
                'ة' => 'ه',
                'ى' => 'ي',
                _ => raw
            };

            if (char.IsLetterOrDigit(c)) builder.Append(c);
        }

        return builder.ToString();
    }
}


/// <summary>
/// يستخرج رمز الفرع من نص المستند الطويل.
///
/// المستند يكتب الفرع داخل جملة: "Phoenix Distribution EG_Cairo_DS_7 N/A. 65
/// ... Cairo". الربط على الجملة كاملةً هشّ — يكفي أن يتغيّر العنوان أو تُقصّ
/// الصفحة فينكسر. أما الرمز فثابت، والمطابقة بالاحتواء تجعله يمسك كل مستندات
/// الفرع مهما اختلف ما حوله.
/// </summary>
public static class BranchCodeHint
{
    private static readonly Regex Pattern =
        new(@"\b([A-Za-z]{2}[_\s].{0,40}?DS[_\s]?\d+)", RegexOptions.Compiled);

    /// <summary>يعيد الرمز إن وُجد، وإلا النص كما هو.</summary>
    public static string Suggest(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "";

        var match = Pattern.Match(label);
        return match.Success ? match.Groups[1].Value.Trim() : label.Trim();
    }
}
