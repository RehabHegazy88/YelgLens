using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.StaticFiles;
using YelgLens.Intake.BLL.IRepository.Core;
using YelgLens.Intake.BLL.Services.Odoo;
using YelgLens.Intake.Model.Auth;
using YelgLens.Intake.Model.Documents;

namespace YelgLens.Intake.PL.Pages.Quotations;

[Authorize(Policy = PermissionCodes.DocumentReview)]
public class DetailModel : PageModel
{
    private readonly IOdooQuotationReader _quotations;
    private readonly IIntakeDocumentRepository _documents;
    private readonly IOdooAttachmentService _attachments;
    private readonly ISourceArchivist _archivist;
    private readonly UserManager<User> _users;
    private readonly IWebHostEnvironment _env;

    public DetailModel(
        IOdooQuotationReader quotations,
        IIntakeDocumentRepository documents,
        IOdooAttachmentService attachments,
        ISourceArchivist archivist,
        UserManager<User> users,
        IWebHostEnvironment env)
    {
        _quotations = quotations;
        _documents = documents;
        _attachments = attachments;
        _archivist = archivist;
        _users = users;
        _env = env;
    }

    /// <summary>مرفقات الأوردر في أودو — الدليل الذي يُنظر إليه قبل حذف نسختنا.</summary>
    public List<OdooAttachment> Attachments { get; private set; } = new();

    /// <summary>مستندنا المقابل، إن كان الأوردر من عندنا.</summary>
    public IntakeDocument? Document { get; private set; }

    /// <summary>هل ما زالت نسختنا على القرص؟</summary>
    public bool HasLocalCopy => Document?.HasLocalFile == true;

    public OdooQuotationDetail? Detail { get; private set; }

    public string Database { get; private set; } = "";

    public bool CanEdit { get; private set; }

    public string? Error { get; private set; }

    /// <summary>المستند عندنا الذي وُلِّد عنه هذا الأوردر، إن كان من عندنا.</summary>
    public long? DocumentId { get; private set; }

    /// <summary>تعديلات البنود كما أرسلها المراجع من الشاشة.</summary>
    [BindProperty]
    public List<LineInput> Lines { get; set; } = new();

    [BindProperty]
    public string? NewCode { get; set; }

    [BindProperty]
    public decimal? NewQuantity { get; set; }

    public class LineInput
    {
        public long Id { get; set; }
        public decimal Quantity { get; set; }
        public bool Remove { get; set; }
    }

    [TempData] public string? Feedback { get; set; }
    [TempData] public string? FeedbackKind { get; set; }

    public async Task OnGetAsync(long id, CancellationToken ct) => await LoadAsync(id, ct);

    /// <summary>
    /// يحفظ تعديلات البنود في أودو مباشرةً.
    ///
    /// الشاشة نفسها التي يُقرأ منها الأوردر يُعدَّل فيها، فلا يُفتح أودو لتغيير
    /// كمية — وهو ما طُلب صراحةً: أن تكون الدورة كلها في مكان واحد.
    /// </summary>
    public async Task<IActionResult> OnPostSaveAsync(long id, CancellationToken ct)
    {
        var edits = new List<LineEdit>();

        foreach (var line in Lines)
        {
            if (line.Remove) edits.Add(new LineEdit(line.Id, null, 0, Remove: true));
            else if (line.Quantity > 0) edits.Add(new LineEdit(line.Id, null, line.Quantity));
        }

        // البند الجديد يُضاف برمزه لا بمعرّفه: المراجع يقرأ الباركود من الورقة
        // ولا يعرف معرّف المنتج في أودو.
        if (!string.IsNullOrWhiteSpace(NewCode) && NewQuantity is > 0)
            edits.Add(new LineEdit(null, NewCode, NewQuantity.Value));

        if (edits.Count == 0)
        {
            Feedback = "لم تُغيَّر أي كمية ولم يُضف بند.";
            FeedbackKind = "bad";
            return RedirectToPage(new { id });
        }

        try
        {
            Feedback = await _quotations.ApplyLineEditsAsync(id, edits, ct);
            FeedbackKind = "ok";
        }
        catch (OdooException ex)
        {
            Feedback = ex.Message;
            FeedbackKind = "bad";
        }

        return RedirectToPage(new { id });
    }

    /// <summary>يعرض مرفقاً من أودو داخل شاشتنا ليُرى قبل الإقرار.</summary>
    public async Task<IActionResult> OnGetAttachmentAsync(long id, long attachment, CancellationToken ct)
    {
        var described = await _attachments.DescribeAsync(attachment, ct);
        if (described is null) return NotFound("لم يُعثر على المرفق في أودو.");

        var content = await _attachments.DownloadAsync(attachment, ct);
        if (content is null) return NotFound("تعذّر تنزيل المرفق من أودو.");

        new FileExtensionContentTypeProvider().TryGetContentType(described.Name, out var type);

        // يُعرض داخل المتصفح لا يُنزَّل: الغرض أن يراه المراجع لا أن يحفظه.
        Response.Headers.ContentDisposition = $"inline; filename=\"{Uri.EscapeDataString(described.Name)}\"";
        return File(content, type ?? described.MimeType);
    }

    /// <summary>
    /// يحذف نسختنا بإقرار المراجع بعد أن رأى المرفق في أودو.
    ///
    /// الإقرار هو الفرق بين هذا وبين حذفٍ آليّ: الفحص يُثبت أن البايتات وصلت،
    /// والعين تُثبت أن الملف يُفتح ويُقرأ هناك.
    /// </summary>
    public async Task<IActionResult> OnPostReleaseAsync(long id, CancellationToken ct)
    {
        var document = OurDocument(id);

        if (document is null)
        {
            Feedback = "لا مستند عندنا لهذا الأوردر في قاعدة " + _quotations.Database + ".";
            FeedbackKind = "bad";
            return RedirectToPage(new { id });
        }

        var outcome = await _archivist.ReleaseLocalAsync(
            document, Path.Combine(_env.ContentRootPath, "Storage"), ct);

        if (outcome.Removed)
        {
            document.ArchivedByUserId ??= long.Parse(_users.GetUserId(User)!);
            await _documents.SaveAsync();
        }

        Feedback = outcome.Message;
        FeedbackKind = outcome.Removed ? "ok" : "bad";

        return RedirectToPage(new { id });
    }

    /// <summary>
    /// مستندنا المقابل لهذا الأوردر — في هذه القاعدة وحدها.
    ///
    /// معرّف الأوردر ليس فريداً بين القواعد: الرقم ٧١٢ أوردرٌ في قاعدة
    /// الاختبار وأوردرٌ آخر تماماً في الحيّة. ومطابقةٌ بلا قاعدة تقرن أوردراً
    /// حيّاً بمستندٍ من الاختبار، فيصير زرّ «احذف نسختنا» عاملاً على الورقة
    /// الخطأ.
    /// </summary>
    private IntakeDocument? OurDocument(long orderId) =>
        _documents.Get().FirstOrDefault(d =>
            d.OdooOrderId == orderId
            && (d.OdooDatabase == null || d.OdooDatabase == _quotations.Database));

    private async Task LoadAsync(long id, CancellationToken ct)
    {
        Database = _quotations.Database;
        CanEdit = _quotations.CanEdit;

        if (!_quotations.IsAvailable)
        {
            Error = "الاتصال بأودو غير مضبوط.";
            return;
        }

        try
        {
            Detail = await _quotations.GetDetailAsync(id, ct);

            if (Detail is null)
            {
                Error = $"لم يُعثر على أوردر بالمعرّف {id} في قاعدة {Database}.";
                return;
            }

            Lines = Detail.Lines
                .Select(l => new LineInput { Id = l.Id, Quantity = l.Quantity })
                .ToList();

            Document = OurDocument(id);
            DocumentId = Document?.Id;

            // المرفقات تُقرأ من أودو لا من عندنا: الشاشة تُقنع بأن الدليل هناك،
            // وقائمةٌ مبنيةٌ على سجلّنا لا تُقنع بشيء.
            Attachments = await _attachments.ListAsync("sale.order", id, ct);
        }
        catch (OdooException ex)
        {
            Error = ex.Message;
        }
    }
}
