using YelgLens.Intake.BLL.ViewModel;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.BLL.IRepository.Core;

public interface IIntakeDocumentRepository
{
    /// <summary>العميل المعمول عنده الآن — قاعدة أودو الموصولة.</summary>
    string OwnerDatabase { get; }

    IQueryable<IntakeDocument> Get();

    /// <summary>مستندات كل العملاء — للصيانة والفحص لا للتشغيل.</summary>
    IQueryable<IntakeDocument> GetEverywhere();

    Task<IntakeDocument?> GetAsync(long id);

    /// <summary>يجلب المستند بكل بنوده وملاحظاته — لشاشة المراجعة.</summary>
    Task<IntakeDocument?> GetWithDetailsAsync(long id);

    /// <summary>
    /// يبحث ببصمة الملف. الورقة الواحدة تُصوَّر مرتين في الميدان، وإنشاء
    /// مستندين منها يعني أوردرين في أودو.
    /// </summary>
    Task<IntakeDocument?> FindByHashAsync(string sha256);

    Task<List<IntakeDocument>> GetQueueAsync(IntakeStatus? status, int take = 200);

    /// <summary>قائمة المراجعة مُرشَّحة ومُقسَّمة صفحاتٍ مع عدد الكل.</summary>
    Task<PagedResult<IntakeDocument>> SearchAsync(DocumentQuery query);

    /// <summary>عدد المستندات في كل حالة — لعدّادات شريط الترشيح.</summary>
    Task<Dictionary<IntakeStatus, int>> CountByStatusAsync();

    /// <summary>مستندات مستخدمٍ بعينه — المندوب يرى ما رفعه هو لا ما رفعه غيره.</summary>
    Task<List<IntakeDocument>> GetByUploaderAsync(long userId, int take = 100);

    Task<IntakeDocument> AddAsync(IntakeDocument document);

    /// <summary>
    /// يؤرشف مستندات بمعرّفاتها ويعيد عدد ما أُرشف.
    ///
    /// أرشفةٌ لا محو: السجل يُعلَّم فيختفي من الشاشات، والملف الأصلي يبقى —
    /// هو الدليل عند الخلاف، ولا يجوز أن تزيله ضغطة.
    /// </summary>
    Task<int> ArchiveAsync(IReadOnlyList<long> ids, long userId);

    /// <summary>يعيد المؤرشف إلى القائمة.</summary>
    Task<int> RestoreAsync(IReadOnlyList<long> ids, long userId);

    /// <summary>يجلب مستنداً ولو كان مؤرشفاً — لعرضه أو إعادته.</summary>
    Task<IntakeDocument?> GetIncludingArchivedAsync(long id);

    Task SaveAsync();
}
