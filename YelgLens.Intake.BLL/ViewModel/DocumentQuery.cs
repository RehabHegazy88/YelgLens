using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.BLL.ViewModel;

/// <summary>
/// شروط البحث في قائمة المراجعة.
///
/// تُمرَّر كوحدة واحدة لا كوسائط متفرقة: الشرط الذي يُنسى تمريره في موضع
/// يجعل الصفحة الثانية تعرض غير ما عرضته الأولى.
/// </summary>
public sealed record DocumentQuery
{
    public IntakeStatus? Status { get; init; }

    /// <summary>نص واحد يُطابق رقم أمر الشراء أو اسم العميل أو الفرع أو اسم الملف.</summary>
    public string? Text { get; init; }

    public DateTime? From { get; init; }

    public DateTime? To { get; init; }

    /// <summary>
    /// حال الترحيل إلى أودو: مُرحَّل، أو لم يُرحَّل، أو فشلت آخر محاولة.
    ///
    /// مستقلٌّ عن حالة المستند عمداً: مستندٌ معتمد قد يكون رُحِّل وقد لا يكون،
    /// والسؤال «أين وصل؟» غير السؤال «هل اعتُمد؟».
    /// </summary>
    public PublishFilter Publish { get; init; } = PublishFilter.Any;

    /// <summary>
    /// رؤية المؤرشف.
    ///
    /// المؤرشف مخفيٌّ لا ممحوّ، ولا بدّ من بابٍ يُرى منه: أرشفةٌ لا رجعة منها
    /// ليست أرشفة بل حذف.
    /// </summary>
    public ArchiveFilter Archived { get; init; } = ArchiveFilter.Live;

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;

    /// <summary>الأقدم أولاً في قائمة العمل، والأحدث أولاً في السجل.</summary>
    public bool OldestFirst { get; init; } = true;

    public int Skip => Math.Max(0, (Page - 1) * PageSize);
}

/// <summary>رؤية المستندات المؤرشفة.</summary>
public enum ArchiveFilter
{
    /// <summary>القائم وحده — وهو الافتراض.</summary>
    Live,

    /// <summary>المؤرشف وحده.</summary>
    Archived,

    /// <summary>الاثنان معاً.</summary>
    All
}

/// <summary>ترشيح المستندات بحال ترحيلها إلى أودو.</summary>
public enum PublishFilter
{
    /// <summary>لا يُرشَّح بحال الترحيل.</summary>
    Any,

    /// <summary>له أوردر في أودو.</summary>
    Published,

    /// <summary>لم يُرحَّل بعد ولم تفشل له محاولة.</summary>
    NotPublished,

    /// <summary>فشلت آخر محاولة ترحيل — هذه أولى ما يُنظر فيه.</summary>
    Failed
}

/// <summary>
/// صفحة من نتيجة مع عدد الكل — العدد لازم لرسم أزرار التنقل، ولا يُستنتج
/// من طول الصفحة.
/// </summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int PageCount => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));

    public bool HasPrevious => Page > 1;

    public bool HasNext => Page < PageCount;

    /// <summary>ترتيب أول عنصر معروض، وصفرٌ إن كانت الصفحة بعد آخر النتيجة.</summary>
    public int FirstIndex => Total == 0 || (Page - 1) * PageSize >= Total ? 0 : (Page - 1) * PageSize + 1;

    public int LastIndex => FirstIndex == 0 ? 0 : Math.Min(Total, Page * PageSize);
}
