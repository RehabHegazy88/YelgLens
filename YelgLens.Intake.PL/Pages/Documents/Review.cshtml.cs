using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.StaticFiles;
using YelgLens.Intake.BLL.IRepository.Core;
using YelgLens.Intake.BLL.Services.Export;
using YelgLens.Intake.BLL.Services.Extraction;
using YelgLens.Intake.BLL.Services.Mapping;
using YelgLens.Intake.BLL.Services.Odoo;
using YelgLens.Intake.Model.Mapping;
using YelgLens.Intake.BLL.Services.Review;
using YelgLens.Intake.BLL.ViewModel;
using YelgLens.Intake.Model.Auth;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Enums;
using YelgLens.Intake.Model.Mapping;

namespace YelgLens.Intake.PL.Pages.Documents;

[Authorize(Policy = PermissionCodes.DocumentReview)]
public class ReviewModel : PageModel
{
    private readonly IIntakeDocumentRepository _documents;
    private readonly IIntakeReviewService _review;
    private readonly ExcelExporter _exporter;
    private readonly SpreadsheetPreview _sheets;
    private readonly OdooImportExporter _odoo;
    private readonly IOdooCustomerService _customers;
    private readonly IOdooPreflight _preflight;
    private readonly IOdooOrderPublisher _publisher;
    private readonly ISourceArchivist _archivist;
    private readonly IOdooAttachmentService _attachments;
    private readonly ICustomerGate _gate;
    private readonly ILineVerifier _verifier;
    private readonly IOdooCustomerService _customerList;
    private readonly IBranchMappingService _mappings;
    private readonly UserManager<User> _users;
    private readonly IWebHostEnvironment _env;

    public ReviewModel(
        IIntakeDocumentRepository documents,
        IIntakeReviewService review,
        ExcelExporter exporter,
        SpreadsheetPreview sheets,
        OdooImportExporter odoo,
        IBranchMappingService mappings,
        IOdooCustomerService customers,
        IOdooPreflight preflight,
        IOdooOrderPublisher publisher,
        ISourceArchivist archivist,
        IOdooAttachmentService attachments,
        ICustomerGate gate,
        ILineVerifier verifier,
        IOdooCustomerService customerList,
        UserManager<User> users,
        IWebHostEnvironment env)
    {
        _documents = documents;
        _review = review;
        _exporter = exporter;
        _sheets = sheets;
        _odoo = odoo;
        _mappings = mappings;
        _customers = customers;
        _preflight = preflight;
        _publisher = publisher;
        _archivist = archivist;
        _attachments = attachments;
        _gate = gate;
        _verifier = verifier;
        _customerList = customerList;
        _users = users;
        _env = env;
    }

    public IntakeDocument Document { get; private set; } = null!;

    public bool CanApprove { get; private set; }

    /// <summary>pdf أو image أو other — يحدد بماذا يُعرض الأصل داخل الصفحة.</summary>
    public string PreviewKind { get; private set; } = "other";

    /// <summary>الصفحة المعروضة الآن في اللوحة.</summary>
    public int CurrentPage { get; private set; } = 1;

    /// <summary>الربط المطابق لفرع هذا المستند — أو لا شيء إن لم يوجد.</summary>
    public BranchMapping? Mapping { get; private set; }

    /// <summary>رمز الفرع المقترح للربط — يُملأ به النموذج بنقرة واحدة.</summary>
    public string BranchHint { get; private set; } = "";

    public bool ReadOnly => Document.IsDecided || !CanApprove;

    [BindProperty]
    public ReviewSubmission Input { get; set; } = new();

    [TempData]
    public string? Feedback { get; set; }

    [TempData]
    public string? FeedbackKind { get; set; }

    /// <summary>ما وجده الفحص المسبق — يُقال ولا يمنع التنزيل.</summary>
    [TempData]
    public string? ExportWarning { get; set; }

    /// <summary>
    /// ما وجده فحص السعر قبل الترحيل، إن وُجد ما يستوجب النظر.
    ///
    /// يُعرض ومعه زرّ «أرسل رغم ذلك»: المنع الدائم خطأ — قد تكون الأسعار في
    /// أودو هي الصحيحة والورقة هي القديمة — لكن المرور الصامت خطأٌ أكبر.
    /// </summary>
    [TempData]
    public string? PricingWarning { get; set; }

    /// <summary>أقرّ المراجع أنه رأى تحذير السعر ويريد الإرسال.</summary>
    [BindProperty(SupportsGet = false)]
    public bool PricingAcknowledged { get; set; }

    public async Task<IActionResult> OnGetAsync(long id, int page = 1)
    {
        var document = await _documents.GetWithDetailsAsync(id);
        if (document is null) return NotFound();

        Document = document;
        CanApprove = (await AuthorizeApprove()).Succeeded;
        CanPublishToOdoo = _publisher.CanWrite;
        OdooDatabase = _publisher.Database;
        FromAnotherDatabase = !document.BelongsToDatabase(_publisher.Database);

        // يُعاد الفحص عند كل فتح: الكشف قد يكون حُدِّث، والفرع قد يكون رُبط
        // من شاشة الربط بعد رفع المستند.
        Customer = await _gate.EvaluateAsync(document);

        // البنود تُفحص عند كل فتح: الباركود الخاطئ يجب أن يُرى قبل الاعتماد
        // لا بعد رفض ملف الاستيراد.
        await _verifier.VerifyAsync(document, HttpContext.RequestAborted);
        await _documents.SaveAsync();

        Matches = await _verifier.MatchAsync(document, HttpContext.RequestAborted);

        // الاختيارات تُحمَّل دائماً لا حين المنع وحده: تغيير العميل المسنَد
        // حقٌّ قائم، لا فعلٌ يُتاح مرةً ثم يُغلق.
        CustomerChoices = await _customerList.GetActiveAsync();
        CustomerHint = await _customerList.SuggestAsync(Customer.BranchLabel);
        CurrentPage = page < 1 ? 1 : page;
        PreviewKind = KindOf(FileOf(document, CurrentPage));
        var branch = document.BranchLabel.HasValue ? document.BranchLabel.Value : null;
        Mapping = await _mappings.ResolveAsync(branch);
        BranchHint = BranchCodeHint.Suggest(branch);
        Input.DocumentId = id;

        // يُملأ من المستند وإلا كتب مساعد العرض قيمة النموذج الفارغة فوقه،
        // فيرى المراجع الحقل خالياً ويظنّ الرقم غير مقروء.
        Input.CustomerPoNumber = document.CustomerPoNumber.HasValue ? document.CustomerPoNumber.Value : null;
        Input.CustomerName = document.CustomerName.HasValue ? document.CustomerName.Value : null;
        Input.BranchLabel = document.BranchLabel.HasValue ? document.BranchLabel.Value : null;
        Input.OrderDate = document.OrderDate.HasValue ? document.OrderDate.Value : null;
        Input.DeliveryDate = document.DeliveryDate.HasValue ? document.DeliveryDate.Value : null;

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(long id) =>
        await Decide(id, (submission, userId) => _review.SaveCorrectionsAsync(submission, userId));

    public async Task<IActionResult> OnPostApproveAsync(long id) =>
        await Decide(id, (submission, userId) => _review.ApproveAsync(submission, userId), requireApprove: true);

    public async Task<IActionResult> OnPostRejectAsync(long id) =>
        await Decide(id, (submission, userId) => _review.RejectAsync(submission, userId), requireApprove: true);

    /// <summary>
    /// يعيد المستند إلى المراجعة. لا يمرّ بـ <see cref="Decide"/> عمداً: ذاك
    /// يحفظ ما في الشاشة، والشاشة هنا معروضةٌ للقراءة فحقولها فارغة — فحفظها
    /// يمحو البنود.
    /// </summary>
    public async Task<IActionResult> OnPostReopenAsync(long id, string? reason)
    {
        if (!(await AuthorizeApprove()).Succeeded) return Forbid();

        var result = await _review.ReopenAsync(id, reason, long.Parse(_users.GetUserId(User)!));

        Feedback = result.Message;
        FeedbackKind = result.Success ? "ok" : "bad";

        return RedirectToPage(new { id });
    }

    private async Task<IActionResult> Decide(
        long id,
        Func<ReviewSubmission, long, Task<ReviewResult>> action,
        bool requireApprove = false)
    {
        if (requireApprove && !(await AuthorizeApprove()).Succeeded) return Forbid();

        var userId = long.Parse(_users.GetUserId(User)!);
        Input.DocumentId = id;

        // يُعاد فحص العميل قبل الحفظ لا عند العرض وحده. الصفحة قد تكون فُتحت
        // قبل رفع كشف العملاء أو قبل ربط الفرع، والقرار يُبنى على حال اللحظة
        // التي يُتخذ فيها لا على حالٍ قديم في الشاشة.
        if (await _documents.GetWithDetailsAsync(id) is { } current)
        {
            await _gate.EvaluateAsync(current);
            await _verifier.VerifyAsync(current);
            await _documents.SaveAsync();
        }

        var result = await action(Input, userId);

        Feedback = result.Message;
        FeedbackKind = result.Success ? "ok" : "bad";

        return RedirectToPage(new { id });
    }

    /// <summary>
    /// ملف الصفحة المطلوبة. المستندات المحفوظة قبل دعم الصفحات لا سجلّ صفحات
    /// لها، فيُرجَع ملفها الأصلي — ولا تنكسر شاشتها.
    /// </summary>
    private static string FileOf(IntakeDocument document, int page)
    {
        var match = document.Pages.FirstOrDefault(p => p.PageNumber == page);
        return match?.StoredFileName ?? document.StoredFileName;
    }

    private static string KindOf(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "pdf",
            ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp" => "image",
            ".xlsx" or ".xlsm" or ".xls" => "sheet",
            _ => "other"
        };

    /// <summary>
    /// يقدّم الأصل للعرض داخل الصفحة لا للتنزيل. الفرق أن هذا بلا اسم ملف،
    /// فيعرضه المتصفح مكانه بدل أن يحفظه — والمراجع يحتاج أن يرى لا أن ينزّل.
    /// </summary>
    /// <summary>
    /// محتوى صفحةٍ من المستند: من القرص إن كان، وإلا من أودو.
    ///
    /// كل ما يعرض الأصل يمرّ من هنا — العارض والتنزيل وجدول الإكسل. وضعُ
    /// الرجوع إلى أودو في مسارٍ واحد وترْكُه في الباقي كان يجعل زرّ «الأصل»
    /// يعمل والعارض يخلو، وهو ما وقع فعلاً.
    /// </summary>
    private async Task<(byte[] Content, string Name)?> PageContentAsync(
        IntakeDocument document, int page, CancellationToken ct)
    {
        var stored = FileOf(document, page);
        var path = Path.Combine(_env.ContentRootPath, "Storage", stored);

        if (System.IO.File.Exists(path))
            return (await System.IO.File.ReadAllBytesAsync(path, ct), NameOf(document, page));

        // ولا يُطلب مرفقٌ من قاعدةٍ غير التي رُفع عليها: رقم المرفق لا معنى
        // له خارج قاعدته، وطلبه من غيرها يعرض ورقة عميلٍ آخر بلا أن يُنبَّه
        // أحد.
        if (!document.BelongsToDatabase(_publisher.Database)) return null;

        // مرفق الصفحة نفسها أولاً، ثم مرفق المستند حين يكون صفحةً واحدة.
        var attachment = document.Pages.FirstOrDefault(p => p.PageNumber == page)?.OdooAttachmentId
                         ?? (document.Pages.Count <= 1 ? document.OdooAttachmentId : null);

        if (attachment is not { } attachmentId) return null;

        var content = await _attachments.DownloadAsync(attachmentId, ct);
        return content is null ? null : (content, NameOf(document, page));
    }

    private static string NameOf(IntakeDocument document, int page) =>
        document.Pages.FirstOrDefault(p => p.PageNumber == page)?.SourceFileName
        ?? document.SourceFileName;

    public async Task<IActionResult> OnGetPreviewAsync(long id, int page = 1, CancellationToken ct = default)
    {
        var document = await _documents.GetWithDetailsAsync(id);
        if (document is null) return NotFound();

        var resolved = await PageContentAsync(document, page, ct);

        if (resolved is not { } file)
            return NotFound("تعذّر العثور على الأصل — لا على قرصنا ولا في أودو.");

        new FileExtensionContentTypeProvider().TryGetContentType(file.Name, out var contentType);
        return File(file.Content, contentType ?? "application/octet-stream");
    }

    /// <summary>يعرض جدول البيانات كجدول HTML — المتصفح لا يعرض xlsx بنفسه.</summary>
    public async Task<IActionResult> OnGetSheetAsync(long id, int page = 1, CancellationToken ct = default)
    {
        var document = await _documents.GetWithDetailsAsync(id);
        if (document is null) return NotFound();

        var resolved = await PageContentAsync(document, page, ct);

        if (resolved is not { } file)
            return Content("<p style=\"font-family:sans-serif;padding:16px;\">"
                         + "تعذّر العثور على الأصل — لا على قرصنا ولا في أودو.</p>",
                           "text/html; charset=utf-8");

        return Content(_sheets.ToHtml(file.Content), "text/html; charset=utf-8");
    }

    /// <summary>ينزّل الأصل كما رُفع — المراجع يحتاج أن يرى الورقة لا ما قُرئ منها فقط.</summary>
    public async Task<IActionResult> OnGetOriginalAsync(long id, CancellationToken ct)
    {
        var document = await _documents.GetWithDetailsAsync(id);
        if (document is null) return NotFound();

        var resolved = await PageContentAsync(document, CurrentPage < 1 ? 1 : CurrentPage, ct);

        if (resolved is not { } file)
            return NotFound("تعذّر العثور على الأصل — لا على قرصنا ولا في أودو.");

        new FileExtensionContentTypeProvider().TryGetContentType(file.Name, out var contentType);
        return File(file.Content, contentType ?? "application/octet-stream", file.Name);
    }

    /// <summary>
    /// يولّد ملف استيراد أودو. لا يُتاح إلا بعد الاعتماد: الملف هذا هو ما
    /// يدخل النظام المحاسبي، فلا يخرج من مستندٍ لم يبتّ فيه أحد.
    /// </summary>
    /// <summary>هل يُعرض زرّ الترحيل المباشر أصلاً؟</summary>
    public bool CanPublishToOdoo { get; private set; }

    /// <summary>
    /// المستند مُرحَّل على قاعدةٍ غير المتصل بها الآن.
    ///
    /// حالةٌ تُقال صراحةً: أصله قد يكون محفوظاً هناك وحده، فالعارض يخلو ولا
    /// عطل — والفرق بين «لا يوجد» و«ليس في هذه القاعدة» هو الفرق بين بحثٍ
    /// طويل وبديهةٍ في سطر.
    /// </summary>
    public bool FromAnotherDatabase { get; private set; }

    /// <summary>حال عميل المستند — أمعروفٌ في أودو أم ينتظر اختيار المراجع.</summary>
    public CustomerState Customer { get; private set; } = new(false, null, null, null);

    /// <summary>الأسماء المتاحة للاختيار من كشف أودو.</summary>
    public List<OdooCustomer> CustomerChoices { get; private set; } = new();

    /// <summary>الاسم الذي يقترحه رقم المحل، إن كفى للتمييز.</summary>
    public CustomerSuggestion? CustomerHint { get; private set; }

    /// <summary>حال مطابقة كل بند بمنتجٍ في أودو — للعرض قُدّام السطر.</summary>
    public LineMatchReport Matches { get; private set; } =
        new(false, new Dictionary<int, LineMatch>());



    public string OdooDatabase { get; private set; } = "";

    /// <summary>
    /// يرحّل المستند إلى أودو مباشرةً وينشئ عرض سعر.
    ///
    /// الطلب POST لا GET: الإنشاء ليس قراءةً تُعاد بتحديث الصفحة، ورابطٌ
    /// يُنشئ أوردراً بزيارته يُنشئ واحداً كلما فُتح من السجل.
    /// </summary>
    /// <summary>
    /// يسند المراجع عميلاً من كشف أودو إلى فرع المستند.
    ///
    /// هذا هو المفتاح الوحيد للملاحظة المانعة: لا يُعتمد مستندٌ عميله مجهول،
    /// ولا يُرفع المنع إلا باختيار اسمٍ قائم — لا بكتابة اسمٍ من عند المراجع.
    /// </summary>
    /// <summary>
    /// يبحث عن منتجٍ برمزه ويعيد اسمه — تناديه الشاشة والمراجع يكتب.
    ///
    /// الجواب JSON لا صفحة: هذا نداءٌ من داخل الشاشة لا انتقالٌ إليها.
    /// </summary>
    public async Task<IActionResult> OnGetLookupAsync(string? code, CancellationToken ct)
    {
        if (!(await AuthorizeApprove()).Succeeded) return Forbid();

        if (string.IsNullOrWhiteSpace(code))
            return new JsonResult(new { found = false, product = (string?)null, brokenCheckDigit = false,
                                        suggestions = Array.Empty<object>() });

        var match = await _verifier.LookupAsync(code, ct);

        return new JsonResult(new
        {
            found = match.Found,
            product = match.Product,
            brokenCheckDigit = match.CheckDigitBroken,
            suggestions = match.Suggestions.Select(s => new { code = s.Code, product = s.Product })
        });
    }

    public async Task<IActionResult> OnPostAssignCustomerAsync(long id, string? odooCustomer)
    {
        var approve = await AuthorizeApprove();
        if (!approve.Succeeded) return Forbid();

        var document = await _documents.GetWithDetailsAsync(id);
        if (document is null) return NotFound();

        if (string.IsNullOrWhiteSpace(odooCustomer))
        {
            Feedback = "اختر عميلاً من الكشف.";
            FeedbackKind = "bad";
            return RedirectToPage(new { id });
        }

        var state = await _gate.AssignAsync(document, odooCustomer);
        await _documents.SaveAsync();

        if (state.IsKnown)
        {
            Feedback = $"رُبط الفرع بالعميل «{state.OdooCustomer}». "
                     + "يُستعمل هذا الربط لكل مستند يحمل الفرع نفسه.";
            FeedbackKind = "ok";
        }
        else
        {
            Feedback = state.Reason ?? "تعذّر إسناد العميل.";
            FeedbackKind = "bad";
        }

        return RedirectToPage(new { id });
    }

    /// <summary>
    /// يؤرشف المستند من شاشته.
    ///
    /// الأرشفة من هنا لا من القائمة وحدها: من فتح المستند ورأى أنه مكرَّر أو
    /// تجربة هو أقدر من يقرّر، ولا يصحّ أن يعود إلى القائمة ليبحث عنه.
    /// </summary>
    public async Task<IActionResult> OnPostArchiveAsync(long id)
    {
        if (!(await AuthorizeApprove()).Succeeded) return Forbid();

        var userId = long.Parse(_users.GetUserId(User)!);
        var count = await _documents.ArchiveAsync(new[] { id }, userId);

        if (count == 0)
        {
            Feedback = "لم يُؤرشف — قد يكون مؤرشفاً بالفعل.";
            FeedbackKind = "bad";
            return RedirectToPage(new { id });
        }

        TempData["Feedback"] = $"أُرشف المستند #{id}. الملف الأصلي باقٍ، ويمكن إعادته من مرشِّح «المؤرشف».";
        TempData["FeedbackKind"] = "ok";

        return RedirectToPage("/Documents/Index");
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// يعيد محاولة رفع الأصل على أوردر أودو.
    ///
    /// مستقلٌّ عن الترحيل: الأوردر أُنشئ، والناقص هو المرفق وحده. وإعادةُ
    /// الترحيل هنا كانت ستُنشئ أوردراً ثانياً لطلبيةٍ واحدة.
    /// </summary>
    public async Task<IActionResult> OnPostAttachAsync(long id, CancellationToken ct)
    {
        if (!(await AuthorizeApprove()).Succeeded) return Forbid();

        var document = await _documents.GetWithDetailsAsync(id);
        if (document is null) return NotFound();

        if (!document.IsPublished)
        {
            Feedback = "لا أوردر في أودو لِيُرفع عليه الأصل — رحّل المستند أولاً.";
            FeedbackKind = "bad";
            return RedirectToPage(new { id });
        }

        var archived = await _archivist.ArchiveAsync(
            document, Path.Combine(_env.ContentRootPath, "Storage"), ct);

        document.AttachError = archived.Attached ? null : NullIfBlank(archived.Message);
        await _documents.SaveAsync();

        Feedback = archived.Attached
            ? archived.Message
            : $"لم يُرفع الأصل بعد. {archived.Message}";
        FeedbackKind = archived.Attached ? "ok" : "bad";

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostPublishAsync(long id, CancellationToken ct)
    {
        var approve = await AuthorizeApprove();
        if (!approve.Succeeded) return Forbid();

        var document = await _documents.GetWithDetailsAsync(id);
        if (document is null) return NotFound();

        var mapping = await _mappings.ResolveAsync(
            document.BranchLabel.HasValue ? document.BranchLabel.Value : null);

        // السعر يُفحص قبل الإنشاء لا بعده: الأوردر الخاطئ سعراً يُقبل في أودو
        // ويمضي إلى التسليم والفاتورة، ولا يُكتشف إلا في الحساب. وقد وقع —
        // أوردرٌ خرج بـ١١٨٫٨٠ وكان يجب أن يخرج بنحو ٥٩٠٠.
        if (!PricingAcknowledged && mapping is { IsComplete: true } && _preflight.IsAvailable)
        {
            PricingCheck pricing;

            try { pricing = await _preflight.CheckPricingAsync(document, mapping, ct); }
            catch (OdooException) { pricing = PricingCheck.NotChecked; }

            if (pricing.NeedsAttention)
            {
                PricingWarning = string.Join(" · ", pricing.Warnings);
                return RedirectToPage(new { id });
            }
        }

        var outcome = await _publisher.PublishAsync(document, mapping, ct);

        if (outcome.Success)
        {
            // الأثر يُحفظ أولاً: انقطاعٌ بعد الإنشاء وقبل الحفظ يترك أوردراً
            // في أودو لا يعرف النظام أنه أنشأه، فيُنشئ ثانياً في المحاولة
            // التالية.
            document.OdooOrderId = outcome.OrderId;
            document.OdooOrderName = outcome.OrderName;
            document.OdooDatabase = _publisher.Database;
            document.PublishedDate = DateTime.Now;
            document.PublishedByUserId = long.Parse(_users.GetUserId(User)!);
            document.PublishError = null;
            document.Status = IntakeStatus.Published;

            // التحذير يُطوى بعد الإرسال: بقاؤه في الجلسة يجعله يظهر على مستندٍ
            // آخر لا شأن له به.
            PricingWarning = null;

            // الأثر يُحفظ قبل رفع الأصل: لو تعثّر الرفع يبقى الأوردر مسجّلاً
            // عندنا، ولا يُنشأ ثانٍ في محاولةٍ تالية.
            await _documents.SaveAsync();

            var archived = await _archivist.ArchiveAsync(
                document, Path.Combine(_env.ContentRootPath, "Storage"), ct);

            // تعثّر رفع الأصل لا يُبطل الترحيل: الأوردر أُنشئ فعلاً. يُسجَّل
            // على حدة ليُعاد وحده، ولا يُخلط بنجاح الترحيل.
            document.AttachError = archived.Attached ? null : NullIfBlank(archived.Message);
            await _documents.SaveAsync();

            if (!archived.Attached && !string.IsNullOrWhiteSpace(archived.Message))
                ExportWarning = archived.Message;

            Feedback = archived.Attached
                ? $"{outcome.Message} {archived.Message}"
                : outcome.Message;
            FeedbackKind = "ok";

            // يُفتح الأوردر في شاشتنا لا في أودو: الدورة كلها في مكان واحد،
            // والمراجع يرى ما أُنشئ ويعدّله فوراً إن لزم.
            return RedirectToPage("/Quotations/Detail", new { id = outcome.OrderId });
        }
        else
        {
            // سبب الفشل يُحفظ على المستند لا في الجلسة وحدها: من يفتحه غداً
            // يحتاج أن يعرف لماذا لم يُرحَّل.
            document.PublishError = outcome.Reasons.Count > 0
                ? outcome.Message + " " + string.Join(" · ", outcome.Reasons)
                : outcome.Message;

            await _documents.SaveAsync();

            Feedback = document.PublishError;
            FeedbackKind = "bad";
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnGetOdooAsync(long id)
    {
        var document = await _documents.GetWithDetailsAsync(id);
        if (document is null) return NotFound();

        if (document.Status != IntakeStatus.Approved)
        {
            Feedback = "ملف الاستيراد لا يُولَّد إلا لمستند معتمد.";
            FeedbackKind = "bad";
            return RedirectToPage(new { id });
        }

        var mapping = await _mappings.ResolveAsync(
            document.BranchLabel.HasValue ? document.BranchLabel.Value : null);

        // الاسم يُفحص عند التوليد لا عند الحفظ وحده: ربطٌ حُفظ قبل رفع الكشف
        // — أو كُتب بيدٍ قبل وجود الفحص — يبقى خاطئاً في الجدول، ولا يُكتشف
        // إلا حين يرفض أودو الملف. الرفض هنا أرخص من الرفض هناك.
        if (mapping is { IsComplete: true })
        {
            var check = await _customers.CheckAsync(mapping.OdooCustomer!);

            if (!check.IsKnown)
            {
                Feedback = $"اسم العميل المربوط «{mapping.OdooCustomer}» لا يطابق أي عميل في كشف أودو، "
                         + "وأودو يطابق هذا العمود بالنص الحرفي فسيرفض الملف. صحّحه من جدول الربط.";
                FeedbackKind = "bad";
                return RedirectToPage(new { id });
            }
        }

        var result = _odoo.Export(document, mapping);

        if (!result.Success || result.Content is null)
        {
            Feedback = result.Message;
            FeedbackKind = "bad";
            return RedirectToPage(new { id });
        }

        // يُسأل أودو عمّا سيطابقه الملف قبل تنزيله. الرفض بعد الرفع لا يقول أي
        // سطرٍ سببه، وقد قِيس أن قاعدة الاختبار بلا باركود واحد على منتجاتها —
        // فملفٌ سليم الشكل تفشل كل بنوده فيها.
        if (mapping is { IsComplete: true } && _preflight.IsAvailable)
        {
            var flight = await _preflight.CheckAsync(document, mapping, HttpContext.RequestAborted);

            if (!flight.WasChecked)
            {
                ExportWarning = flight.Error;
            }
            else
            {
                var problems = new List<string>();

                if (!flight.CustomerFound)
                    problems.Add($"العميل «{mapping.OdooCustomer}» غير موجود في القاعدة المتصل بها");

                if (!flight.PricelistFound)
                    problems.Add($"قائمة الأسعار «{mapping.OdooPricelist}» غير موجودة");

                if (flight.MissingBarcodes.Count > 0)
                    problems.Add($"{flight.MissingBarcodes.Count} من {flight.CheckedBarcodes} باركود "
                               + $"بلا منتج مطابق ({string.Join("، ", flight.MissingBarcodes.Take(5))}"
                               + $"{(flight.MissingBarcodes.Count > 5 ? " وغيرها" : "")})");

                if (problems.Count > 0)
                    ExportWarning = "الملف نُزِّل، لكن أودو سيرفض ما لا يطابقه: "
                                  + string.Join(" · ", problems) + ".";

                // ويُقال حال السعر ولو طابق كل شيء: ملفٌ يُقبل كله بسعرٍ خاطئ
                // أسوأ من ملفٍ يُرفض، لأنه لا يُردّ على أحد.
                if (flight.Pricing.NeedsAttention)
                    PricingWarning = string.Join(" · ", flight.Pricing.Warnings);
            }
        }

        var po = document.CustomerPoNumber.HasValue ? document.CustomerPoNumber.Value : "unknown";

        return File(result.Content,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"OdooImport_{po}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
    }

    public async Task<IActionResult> OnGetExportAsync(long id)
    {
        var document = await _documents.GetWithDetailsAsync(id);
        if (document is null) return NotFound();

        var bytes = _exporter.Export(document.ToExtractedOrder());
        var kind = document.Kind == DocumentKind.DeliverySlip ? "DS" : "PO";
        var po = document.CustomerPoNumber.HasValue ? document.CustomerPoNumber.Value : "unknown";

        return File(bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"{kind}_{po}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
    }

    private Task<Microsoft.AspNetCore.Authorization.AuthorizationResult> AuthorizeApprove()
    {
        var authorization = HttpContext.RequestServices
            .GetRequiredService<IAuthorizationService>();

        return authorization.AuthorizeAsync(User, PermissionCodes.DocumentApprove);
    }

    /// <summary>
    /// ما يُعرض في الحقل. المصحَّح بشرياً تُعرض قيمته الجديدة، وما دونه يُعرض
    /// نصه الخام كما هو على الورقة — لأن المراجع يقارن بالأصل لا بالمطبَّع.
    /// </summary>
    public static string Shown(FieldValue<string> field)
    {
        if (field.Origin == ValueOrigin.HumanEntry)
            return field.HasValue ? field.Value! : "";

        return field.RawText ?? (field.HasValue ? field.Value! : "");
    }

    /// <summary>
    /// الكمية تُخزَّن decimal(18,4) فتعود "24.0000". تُعرض بلا أصفار زائدة،
    /// وإلا امتلأت الخانة الضيقة بأصفار وخرج الرقم الحقيقي عن حدّها.
    /// </summary>
    /// <summary>التاريخ بصيغة حقل الإدخال. الفارغ يعني غياباً لا صفراً.</summary>
    public static string Date(FieldValue<DateTime> field) =>
        field.HasValue ? field.Value.ToString("yyyy-MM-dd") : "";

    public static string Number(FieldValue<decimal> field) =>
        field.HasValue ? field.Value.ToString("0.####") : "";

    public static string OriginBadge(ValueOrigin origin) => origin switch
    {
        ValueOrigin.PdfTextLayer       => "نص",
        ValueOrigin.BarcodeDecode      => "باركود",
        ValueOrigin.OpticalRecognition => "ضوئي",
        ValueOrigin.HumanEntry         => "بشري",
        ValueOrigin.Derived            => "محسوب",
        ValueOrigin.ErpLookup          => "أودو",
        _ => ""
    };
}
