using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YelgLens.Intake.BLL.IRepository.Core;
using YelgLens.Intake.BLL.ViewModel;
using YelgLens.Intake.Model.Auth;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.PL.Pages.Documents;

[Authorize(Policy = PermissionCodes.DocumentReview)]
public class IndexModel : PageModel
{
    private readonly IIntakeDocumentRepository _documents;
    private readonly UserManager<User> _users;
    private readonly IAuthorizationService _authorization;

    public IndexModel(
        IIntakeDocumentRepository documents,
        UserManager<User> users,
        IAuthorizationService authorization)
    {
        _documents = documents;
        _users = users;
        _authorization = authorization;
    }

    /// <summary>الأرشفة فعلٌ يخفي عن الجميع، فلا يملكه إلا من يملك الاعتماد.</summary>
    public bool CanArchive { get; private set; }

    /// <summary>عدد المؤرشف — يُعرض على شارته ليُعرف أن هناك ما يُرى.</summary>
    public int ArchivedCount { get; private set; }

    [TempData] public string? Feedback { get; set; }
    [TempData] public string? FeedbackKind { get; set; }

    public PagedResult<IntakeDocument> Result { get; private set; } =
        new(Array.Empty<IntakeDocument>(), 0, 1, PageSizes[0]);

    public Dictionary<IntakeStatus, int> Counts { get; private set; } = new();

    [BindProperty(SupportsGet = true)]
    public IntakeStatus? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true, Name = "from")]
    public DateTime? From { get; set; }

    [BindProperty(SupportsGet = true, Name = "to")]
    public DateTime? To { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true, Name = "size")]
    public int PageSize { get; set; } = 25;

    /// <summary>الأحدث أولاً حين يُبحث في السجل، والأقدم أولاً في طابور العمل.</summary>
    [BindProperty(SupportsGet = true, Name = "newest")]
    public bool NewestFirst { get; set; }

    /// <summary>ترشيح بحال الترحيل إلى أودو — مستقل عن حالة المراجعة.</summary>
    [BindProperty(SupportsGet = true, Name = "odoo")]
    public PublishFilter Publish { get; set; } = PublishFilter.Any;

    /// <summary>رؤية المؤرشف — القائم وحده افتراضاً.</summary>
    [BindProperty(SupportsGet = true, Name = "arch")]
    public ArchiveFilter Archived { get; set; } = ArchiveFilter.Live;

    /// <summary>المستندات المؤشَّرة في الجدول.</summary>
    [BindProperty]
    public List<long> Selected { get; set; } = new();

    public static readonly int[] PageSizes = { 25, 50, 100 };

    public bool HasFilter =>
        Status is not null || !string.IsNullOrWhiteSpace(Q) || From is not null || To is not null
        || Publish != PublishFilter.Any || Archived != ArchiveFilter.Live;

    public async Task OnGetAsync()
    {
        // القيم تأتي من شريط العنوان، وهو نصٌّ يكتبه من شاء. تُقصر على المدى
        // المسموح بدل أن تُصدَّق: صفحةٌ رقمها سالب أو حجمها ألف ليست خطأ
        // مستخدم بل استعلامٌ يُثقل قاعدة البيانات.
        if (!PageSizes.Contains(PageSize)) PageSize = PageSizes[0];
        if (PageNumber < 1) PageNumber = 1;

        var query = new DocumentQuery
        {
            Status = Status,
            Text = Q,
            From = From,
            To = To,
            Page = PageNumber,
            PageSize = PageSize,
            OldestFirst = !NewestFirst,
            Publish = Publish,
            Archived = Archived
        };

        Result = await _documents.SearchAsync(query);

        // طلب صفحةٍ بعد آخر صفحة يُردّ إلى الأخيرة بدل أن يُعرض فراغ.
        if (Result.Total > 0 && PageNumber > Result.PageCount)
        {
            PageNumber = Result.PageCount;
            Result = await _documents.SearchAsync(query with { Page = PageNumber });
        }

        Counts = await _documents.CountByStatusAsync();
        CanArchive = (await _authorization.AuthorizeAsync(User, PermissionCodes.DocumentApprove)).Succeeded;

        ArchivedCount = (await _documents.SearchAsync(new DocumentQuery
        {
            Archived = ArchiveFilter.Archived,
            PageSize = 1
        })).Total;
    }

    /// <summary>يؤرشف ما أُشِّر عليه — إخفاءٌ من الشاشات لا محوٌ من القرص.</summary>
    public async Task<IActionResult> OnPostArchiveAsync()
    {
        if (!(await _authorization.AuthorizeAsync(User, PermissionCodes.DocumentApprove)).Succeeded)
            return Forbid();

        if (Selected.Count == 0)
        {
            Feedback = "لم تُؤشِّر على مستند.";
            FeedbackKind = "bad";
            return RedirectToPage(Link());
        }

        var userId = long.Parse(_users.GetUserId(User)!);
        var count = await _documents.ArchiveAsync(Selected, userId);

        Feedback = $"أُرشف {count} مستنداً. الملفات الأصلية باقية، ويمكن إعادتها من مرشِّح «المؤرشف».";
        FeedbackKind = "ok";

        return RedirectToPage(Link());
    }

    /// <summary>يعيد ما أُرشف.</summary>
    public async Task<IActionResult> OnPostRestoreAsync()
    {
        if (!(await _authorization.AuthorizeAsync(User, PermissionCodes.DocumentApprove)).Succeeded)
            return Forbid();

        if (Selected.Count == 0)
        {
            Feedback = "لم تُؤشِّر على مستند.";
            FeedbackKind = "bad";
            return RedirectToPage(Link());
        }

        var userId = long.Parse(_users.GetUserId(User)!);
        var count = await _documents.RestoreAsync(Selected, userId);

        Feedback = $"أُعيد {count} مستنداً إلى القائمة.";
        FeedbackKind = "ok";

        return RedirectToPage(Link());
    }

    /// <summary>يبني رابطاً يحفظ الترشيح القائم ويغيّر ما طُلب تغييره وحده.</summary>
    public Dictionary<string, string?> Link(
        IntakeStatus? status = null,
        int? page = null,
        int? size = null,
        bool keepStatus = true)
    {
        var route = new Dictionary<string, string?>();

        var effective = keepStatus ? status ?? Status : status;
        if (effective is not null) route["status"] = effective.ToString();

        if (!string.IsNullOrWhiteSpace(Q)) route["q"] = Q;
        if (From is not null) route["from"] = From.Value.ToString("yyyy-MM-dd");
        if (To is not null) route["to"] = To.Value.ToString("yyyy-MM-dd");
        if (NewestFirst) route["newest"] = "true";
        if (Publish != PublishFilter.Any) route["odoo"] = Publish.ToString();
        if (Archived != ArchiveFilter.Live) route["arch"] = Archived.ToString();

        var effectiveSize = size ?? PageSize;
        if (effectiveSize != PageSizes[0]) route["size"] = effectiveSize.ToString();

        // تغيير الترشيح يعيد إلى الصفحة الأولى: الصفحة الرابعة من نتيجةٍ
        // أخرى ليست الصفحة الرابعة من هذه.
        var effectivePage = page ?? 1;
        if (effectivePage > 1) route["p"] = effectivePage.ToString();

        return route;
    }

    public static string PublishLabel(PublishFilter filter) => filter switch
    {
        PublishFilter.Published    => "مُرحَّل إلى أودو",
        PublishFilter.NotPublished => "لم يُرحَّل",
        PublishFilter.Failed       => "فشل الترحيل",
        _ => "الكل"
    };

    public static string StatusLabel(IntakeStatus status) => status switch
    {
        IntakeStatus.AwaitingReview => "بانتظار المراجعة",
        IntakeStatus.UnderReview    => "قيد المراجعة",
        IntakeStatus.Approved       => "معتمد",
        IntakeStatus.Rejected       => "مرفوض",
        IntakeStatus.Published      => "مُرحَّل",
        _ => status.ToString()
    };

    /// <summary>
    /// الحالة تُقرأ باللون والنص معاً. اللون وحده لا يكفي: مَن لا يميّز الأحمر
    /// من الأخضر يرى شارتين متطابقتين.
    /// </summary>
    public static string StatusBadge(IntakeStatus status) => status switch
    {
        IntakeStatus.AwaitingReview => "badge-warning",
        IntakeStatus.UnderReview    => "badge-info",
        IntakeStatus.Approved       => "badge-success",
        IntakeStatus.Rejected       => "badge-error",
        IntakeStatus.Published      => "badge-neutral",
        _ => "badge-neutral"
    };
}
