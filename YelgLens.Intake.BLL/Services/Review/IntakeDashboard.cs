using Microsoft.EntityFrameworkCore;
using YelgLens.Intake.BLL.Services.Mapping;
using YelgLens.Intake.BLL.Services.Odoo;
using YelgLens.Intake.DAL.Data;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.BLL.Services.Review;

/// <summary>مستندٌ متعطّل، ومعه سبب تعطّله ومدّته.</summary>
public sealed record StalledDocument(
    long Id,
    string File,
    string? PoNumber,
    string? Branch,
    IntakeStatus Status,
    DateTime Since,
    string Reason)
{
    public int Days => (int)(DateTime.Now - Since).TotalDays;

    public string Age => Days switch
    {
        0 => "اليوم",
        1 => "منذ يوم",
        2 => "منذ يومين",
        <= 10 => $"منذ {Days} أيام",
        _ => $"منذ {Days} يوماً"
    };
}

/// <summary>سببٌ مانع وعدد المستندات التي وقفت عليه.</summary>
public sealed record BlockingReason(string Code, string Label, int Documents);

/// <summary>حصيلة لوحة المتابعة.</summary>
public sealed record DashboardView
{
    public required Dictionary<IntakeStatus, int> Funnel { get; init; }

    public required int Total { get; init; }

    /// <summary>معتمدٌ ولم يُرحَّل — العطل الأهم: عملٌ تمّ ولم يصل.</summary>
    public required List<StalledDocument> AwaitingPublish { get; init; }

    /// <summary>فشل ترحيله، ومعه سبب آخر محاولة.</summary>
    public required List<StalledDocument> PublishFailed { get; init; }

    /// <summary>موقوفٌ بملاحظة مانعة.</summary>
    public required List<StalledDocument> Blocked { get; init; }

    /// <summary>مرفوضٌ ولم يُعَد رفعه.</summary>
    public required List<StalledDocument> RejectedNotReplaced { get; init; }

    /// <summary>
    /// أوردره في أودو وأصله لم يُرفع.
    ///
    /// عملٌ تمّ ناقصاً: الطلبية وصلت والدليل لم يصل. ويُعالج بضغطة إعادة، فلا
    /// يصحّ أن يمرّ دون أن يُرى.
    /// </summary>
    public required List<StalledDocument> SourceNotSent { get; init; }

    /// <summary>أقدم ما ينتظر مراجعاً.</summary>
    public required List<StalledDocument> OldestWaiting { get; init; }

    public required List<BlockingReason> Reasons { get; init; }

    public required int UnmappedBranches { get; init; }

    public required int CustomerCount { get; init; }

    public required string CustomerSource { get; init; }

    public required DateTime? CustomerSyncedOn { get; init; }

    public required bool OdooConfigured { get; init; }

    public required string OdooDatabase { get; init; }

    public required int PublishedThisWeek { get; init; }

    /// <summary>مجموع ما يحتاج تدخّلاً بشرياً الآن.</summary>
    public int NeedsAttention =>
        AwaitingPublish.Count + PublishFailed.Count + Blocked.Count
        + RejectedNotReplaced.Count + SourceNotSent.Count;
}

public interface IIntakeDashboard
{
    Task<DashboardView> BuildAsync(CancellationToken ct = default);
}

/// <summary>
/// يجمع ما يحتاج نظراً الآن.
///
/// اللوحة ليست عدّادات: العدّاد يقول «خمسة معتمدة» ولا يقول أيّها وقف ولماذا.
/// فتُبنى حول العطل — المستند الذي تمّ العمل عليه ولم يصل أودو، والذي فشل
/// ترحيله، والذي وقف على ملاحظةٍ مانعة — ومعه اسمه وعمره وسببه، ليُفتح ويُعالج
/// لا ليُعدّ.
///
/// وكل هذا من قاعدتنا لا من أودو: اللوحة تُفتح كل صباح، وخادمهم يتقلّب، ولوحةٌ
/// تنتظر شبكةً بطيئة لا تُفتح.
/// </summary>
public sealed class IntakeDashboard : IIntakeDashboard
{
    private readonly MainDbContext _db;
    private readonly IOdooClient _odoo;
    private readonly IOdooCustomerService _customers;

    public IntakeDashboard(MainDbContext db, IOdooClient odoo, IOdooCustomerService customers)
    {
        _db = db;
        _odoo = odoo;
        _customers = customers;
    }

    private const int Limit = 8;

    public async Task<DashboardView> BuildAsync(CancellationToken ct = default)
    {
        var live = _db.IntakeDocuments.Where(d => !d.Deleted);

        var funnel = await live
            .GroupBy(d => d.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        foreach (var status in Enum.GetValues<IntakeStatus>())
            funnel.TryAdd(status, 0);

        // معتمدٌ ولم يُرحَّل: عملُ المراجع تمّ والطلبية لم تصل. وهذا أخطر ما في
        // اللوحة لأنه لا يُعلن عن نفسه — لا خطأ ولا رسالة، مجرّد صمت.
        var awaitingPublish = await live
            .Where(d => d.Status == IntakeStatus.Approved && d.OdooOrderId == null && d.PublishError == null)
            .OrderBy(d => d.ReviewedDate ?? d.UploadedDate)
            .Take(Limit)
            .Select(d => new
            {
                d.Id, d.SourceFileName, Po = d.CustomerPoNumber.Value, Branch = d.BranchLabel.Value,
                d.Status, Since = d.ReviewedDate ?? d.UploadedDate
            })
            .ToListAsync(ct);

        var failed = await live
            .Where(d => d.OdooOrderId == null && d.PublishError != null)
            .OrderByDescending(d => d.LastModifiedDate)
            .Take(Limit)
            .Select(d => new
            {
                d.Id, d.SourceFileName, Po = d.CustomerPoNumber.Value, Branch = d.BranchLabel.Value,
                d.Status, Since = d.LastModifiedDate, d.PublishError
            })
            .ToListAsync(ct);

        var blocked = await live
            .Where(d => d.Status != IntakeStatus.Published && d.Status != IntakeStatus.Rejected
                        && d.Issues.Any(i => i.Severity == IssueSeverity.Blocking && !i.Resolved))
            .OrderBy(d => d.UploadedDate)
            .Take(Limit)
            .Select(d => new
            {
                d.Id, d.SourceFileName, Po = d.CustomerPoNumber.Value, Branch = d.BranchLabel.Value,
                d.Status, Since = d.UploadedDate,
                First = d.Issues.Where(i => i.Severity == IssueSeverity.Blocking && !i.Resolved)
                                .Select(i => i.Message).FirstOrDefault()
            })
            .ToListAsync(ct);

        // مرفوضٌ ولم يُعَد رفعه: الورقة عادت إلى المندوب ولم تعد. تُعرف بأن لا
        // مستند آخر يشير إليها بديلاً.
        var rejected = await live
            .Where(d => d.Status == IntakeStatus.Rejected
                        && !_db.IntakeDocuments.Any(o => o.ReplacesDocumentId == d.Id && !o.Deleted))
            .OrderBy(d => d.ReviewedDate ?? d.UploadedDate)
            .Take(Limit)
            .Select(d => new
            {
                d.Id, d.SourceFileName, Po = d.CustomerPoNumber.Value, Branch = d.BranchLabel.Value,
                d.Status, Since = d.ReviewedDate ?? d.UploadedDate, d.ReviewNote
            })
            .ToListAsync(ct);

        var sourceMissing = await live
            .Where(d => d.OdooOrderId != null && d.OdooAttachmentId == null)
            .OrderByDescending(d => d.PublishedDate)
            .Take(Limit)
            .Select(d => new
            {
                d.Id, d.SourceFileName, Po = d.CustomerPoNumber.Value, Branch = d.BranchLabel.Value,
                d.Status, Since = d.PublishedDate ?? d.UploadedDate, d.AttachError, d.OdooOrderName
            })
            .ToListAsync(ct);

        var waiting = await live
            .Where(d => d.Status == IntakeStatus.AwaitingReview || d.Status == IntakeStatus.UnderReview)
            .OrderBy(d => d.UploadedDate)
            .Take(Limit)
            .Select(d => new
            {
                d.Id, d.SourceFileName, Po = d.CustomerPoNumber.Value, Branch = d.BranchLabel.Value,
                d.Status, Since = d.UploadedDate
            })
            .ToListAsync(ct);

        var reasons = await _db.IntakeIssues
            .Where(i => i.Severity == IssueSeverity.Blocking && !i.Resolved
                        && !i.IntakeDocument.Deleted
                        && i.IntakeDocument.Status != IntakeStatus.Published)
            .GroupBy(i => i.Code)
            .Select(g => new { Code = g.Key, Documents = g.Select(i => i.IntakeDocumentId).Distinct().Count() })
            .OrderByDescending(x => x.Documents)
            .ToListAsync(ct);

        var weekAgo = DateTime.Now.AddDays(-7);

        return new DashboardView
        {
            Funnel = funnel,
            Total = funnel.Values.Sum(),

            AwaitingPublish = awaitingPublish.Select(d => new StalledDocument(
                d.Id, d.SourceFileName, d.Po, d.Branch, d.Status, d.Since,
                "معتمد ولم يُرحَّل إلى أودو")).ToList(),

            PublishFailed = failed.Select(d => new StalledDocument(
                d.Id, d.SourceFileName, d.Po, d.Branch, d.Status, d.Since,
                d.PublishError ?? "فشل الترحيل")).ToList(),

            Blocked = blocked.Select(d => new StalledDocument(
                d.Id, d.SourceFileName, d.Po, d.Branch, d.Status, d.Since,
                d.First ?? "ملاحظة مانعة")).ToList(),

            RejectedNotReplaced = rejected.Select(d => new StalledDocument(
                d.Id, d.SourceFileName, d.Po, d.Branch, d.Status, d.Since,
                string.IsNullOrWhiteSpace(d.ReviewNote)
                    ? "مرفوض ولم يُعَد رفعه"
                    : $"مرفوض ولم يُعَد رفعه — {d.ReviewNote}")).ToList(),

            SourceNotSent = sourceMissing.Select(d => new StalledDocument(
                d.Id, d.SourceFileName, d.Po, d.Branch, d.Status, d.Since,
                string.IsNullOrWhiteSpace(d.AttachError)
                    ? $"الأوردر {d.OdooOrderName} أُنشئ والأصل لم يُرفع عليه"
                    : $"الأصل لم يُرفع على {d.OdooOrderName} — {d.AttachError}")).ToList(),

            OldestWaiting = waiting.Select(d => new StalledDocument(
                d.Id, d.SourceFileName, d.Po, d.Branch, d.Status, d.Since,
                "ينتظر مراجعاً")).ToList(),

            Reasons = reasons.Select(r => new BlockingReason(r.Code, Label(r.Code), r.Documents)).ToList(),

            UnmappedBranches = await _db.BranchMappings
                .CountAsync(m => !m.Deleted && (m.OdooCustomer == null || m.OdooCustomer == ""), ct),

            CustomerCount = await _customers.CountActiveAsync(),
            CustomerSource = await _customers.ActiveSourceAsync(),
            CustomerSyncedOn = await _customers.LastImportDateAsync(),

            OdooConfigured = _odoo.IsConfigured,
            OdooDatabase = _odoo.Database,

            PublishedThisWeek = await live.CountAsync(d => d.PublishedDate >= weekAgo, ct)
        };
    }

    /// <summary>رمز الملاحظة رمزٌ للنظام، واللوحة تُقرأ بلغة من يعمل.</summary>
    private static string Label(string code) => code switch
    {
        "CUSTOMER_NOT_IN_ODOO"  => "عميل غير معروف في أودو",
        "PO_NUMBER_MISSING"     => "رقم أمر الشراء غائب",
        "BARCODE_NOT_FOUND"     => "لم يُقرأ الباركود",
        "BARCODE_NO_PRODUCT"    => "باركود بلا منتج في أودو",
        "BARCODE_CHECK_DIGIT"   => "باركود يكسر رقم التحقق",
        "NO_LINES_FOUND"        => "لم يُتعرَّف على بنود",
        "NO_LINES_RECOGNIZED"   => "لم يُتعرَّف على بنود",
        "QUANTITY_NOT_NUMERIC"  => "كمية غير رقمية",
        "QUANTITY_INVALID"      => "كمية غير صالحة",
        "OCR_NOT_CONFIGURED"    => "لا محرك تعرّف ضوئي",
        "IMAGE_UNREADABLE"      => "صورة غير مقروءة",
        "UNSUPPORTED_DOCUMENT"  => "نوع مستند غير مدعوم",
        _ => code
    };
}
