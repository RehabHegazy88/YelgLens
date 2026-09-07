using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YelgLens.Intake.BLL.IRepository.Core;
using YelgLens.Intake.BLL.Services.Pipeline;
using YelgLens.Intake.BLL.Services.Review;
using YelgLens.Intake.BLL.Services.Storage;
using YelgLens.Intake.BLL.Services.Tenancy;
using YelgLens.Intake.BLL.ViewModel;
using YelgLens.Intake.Model.Auth;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.PL.Pages;

public class IndexModel : PageModel
{
    private readonly IntakePipeline _pipeline;
    private readonly IIntakeReviewService _review;
    private readonly ICustomerGate _gate;
    private readonly IIntakeDocumentRepository _documents;
    private readonly UserManager<User> _users;
    private readonly IImageCompressor _compressor;
    private readonly IClientContext _clients;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<IndexModel> _log;

    public IndexModel(
        IntakePipeline pipeline,
        IIntakeReviewService review,
        ICustomerGate gate,
        IIntakeDocumentRepository documents,
        UserManager<User> users,
        IImageCompressor compressor,
        IClientContext clients,
        IWebHostEnvironment env,
        ILogger<IndexModel> log)
    {
        _compressor = compressor;
        _clients = clients;
        _pipeline = pipeline;
        _review = review;
        _gate = gate;
        _documents = documents;
        _users = users;
        _env = env;
        _log = log;
    }

    /// <summary>صفحات المستند الواحد. الورقة قد تكون ورقتين أو ثلاثاً.</summary>
    [BindProperty]
    public List<IFormFile> Uploads { get; set; } = new();

    /// <summary>المستند المرفوض الذي يحل هذا الرفع محله، إن وُجد.</summary>
    [BindProperty(SupportsGet = true)]
    public long? Replaces { get; set; }

    public IntakeDocument? Rejected { get; private set; }

    public string? ErrorMessage { get; private set; }

    public long? StoredDocumentId { get; private set; }

    public int StoredPageCount { get; private set; }

    /// <summary>ما وفّره الضغط في هذا الرفع، بالبايت.</summary>
    public long SavedBytes { get; private set; }

    /// <summary>عملاء هذا المندوب — يختار منهم قبل أن يرفع.</summary>
    public List<YelgLens.Intake.Model.Settings.Client> MyClients { get; private set; } = new();

    /// <summary>العميل الذي سيُنسب إليه المرفوع.</summary>
    public YelgLens.Intake.Model.Settings.Client? CurrentClient { get; private set; }

    /// <summary>قاعدة أودو المعمول بها عنده. بلا وصلةٍ لا يُرفع شيء.</summary>
    public string? CurrentDatabase { get; private set; }

    /// <summary>
    /// العميل الذي فُتحت الشاشة عليه، يُرسَل مع النموذج.
    ///
    /// ليس لاختيار الوجهة — الوجهة هي العميل المعمول عليه — بل للتحقق منها:
    /// نافذةٌ فُتحت على عميل ثم بُدِّل العميل في نافذةٍ أخرى تُرسل ورقةً يظنّ
    /// صاحبها أنها ذاهبة إلى الأول وهي ذاهبة إلى الثاني.
    /// </summary>
    [BindProperty]
    public long? ClientId { get; set; }

    public bool CanUpload => CurrentClient is not null && !string.IsNullOrWhiteSpace(CurrentDatabase);

    public bool WasDuplicate { get; private set; }

    private static readonly string[] Allowed =
        { ".pdf", ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".webp", ".xlsx", ".xlsm", ".xls" };

    public async Task OnGetAsync()
    {
        await LoadClientsAsync();
        await LoadReplacedAsync();
    }

    private async Task LoadClientsAsync()
    {
        MyClients = await _clients.AvailableAsync();
        CurrentClient = await _clients.CurrentAsync();
        CurrentDatabase = (await _clients.ActiveConnectionAsync())?.Database;
        ClientId ??= CurrentClient?.Id;
    }

    private static async Task<byte[]> ReadAsync(IFormFile file, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        await LoadClientsAsync();
        await LoadReplacedAsync();

        if (!CanUpload)
        {
            ErrorMessage = CurrentClient is null
                ? "لست مُسنَداً إلى أي عميل، فلا موضع يُحفظ فيه المرفوع. راجع مسؤول النظام."
                : $"العميل «{CurrentClient.Display}» بلا وصلة أودو مفعَّلة، فلا يُرفع له مستند بعد.";
            return Page();
        }

        // ما اختاره في الشاشة يجب أن يكون ما يعمل عليه الآن. اختلافُهما يعني
        // أن العميل بُدِّل بعد فتح الشاشة، وحفظُ الورقة حينئذٍ يضعها عند عميلٍ
        // لم يقصده الرافع — وهو خطأٌ لا يُكتشف لأن الورقة تبدو سليمة.
        if (ClientId is { } chosen && chosen != CurrentClient!.Id)
        {
            ErrorMessage = $"اخترت عميلاً غير الذي تعمل عليه الآن («{CurrentClient.Display}»). "
                         + "بُدِّل العميل بعد فتح هذه الشاشة. راجع الاختيار ثم ارفع من جديد.";
            return Page();
        }

        var files = Uploads.Where(f => f.Length > 0).ToList();
        if (files.Count == 0)
        {
            ErrorMessage = "اختر ملفاً أولاً.";
            return Page();
        }

        var bad = files.FirstOrDefault(f => !Allowed.Contains(Extension(f)));
        if (bad is not null)
        {
            ErrorMessage = $"الامتداد {Extension(bad)} غير مدعوم. المقبول: PDF أو صورة أو جدول Excel.";
            return Page();
        }

        var storage = Path.Combine(_env.ContentRootPath, "Storage");
        Directory.CreateDirectory(storage);

        var pages = new List<PageInput>();

        try
        {
            foreach (var file in files)
            {
                // يُحفظ الأصل باسم فريد ولا يُحذف بعد المعالجة — هو الدليل عند
                // الخلاف، ويُرفق لاحقاً على الأوردر في أودو.
                var storedName = $"{Guid.NewGuid():N}{Extension(file)}";

                // الصورة تُضغط قبل الحفظ لا بعده. ما يُحفظ هو ما تُقرأ منه
                // البنود وما تُحسب عليه البصمة وما يُرفع على أودو — نسخةٌ
                // واحدة لا ثلاث. وقِيس على مستنداتهم أن جودة ٨٥ دون تصغير
                // الأبعاد توفّر سبعين بالمئة وتُبقي أرقام الباركود حرفاً بحرف.
                var uploaded = await ReadAsync(file, ct);
                var compressed = _compressor.Compress(uploaded, storedName);

                if (compressed.Compressed)
                {
                    storedName = compressed.FileName;
                    SavedBytes += compressed.Saved;
                }

                var storedPath = Path.Combine(storage, storedName);
                await System.IO.File.WriteAllBytesAsync(storedPath, compressed.Content, ct);

                var order = await _pipeline.ProcessAsync(storedPath, ct);
                order.SourceFileName = file.FileName;

                // يكفي أن تكون صفحة واحدة مرفوعة من قبل ليكون المستند مكرراً:
                // الورقة نفسها لا تصير طلبيتين.
                var existing = await _review.FindDuplicateAsync(order.SourceSha256);
                if (existing is not null)
                {
                    _log.LogInformation("صفحة مكررة ببصمة {Hash} — المستند القائم {Id}.",
                        order.SourceSha256[..12], existing.Id);

                    WasDuplicate = true;
                    StoredDocumentId = existing.Id;
                    return await Route(existing.Id);
                }

                pages.Add(new PageInput(order, file.FileName, storedName, order.SourceSha256));
            }

            var userId = long.Parse(_users.GetUserId(User)!);
            var document = await _review.StoreAsync(pages, userId, Replaces);

            // العميل يُفحص عند الوصول لا عند الترحيل: مستندٌ بفرعٍ مجهول يجب
            // أن يصل إلى المراجع موسوماً، لا أن يُعتمد ثم يُرفض في أودو.
            await _gate.EvaluateAsync(document);
            await _documents.SaveAsync();

            StoredDocumentId = document.Id;
            StoredPageCount = document.Pages.Count;

            return await Route(document.Id);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "تعذّرت معالجة الرفع ({Count} ملف).", files.Count);
            ErrorMessage = $"تعذّرت معالجة الملف: {ex.Message}";
            return Page();
        }
    }

    private async Task LoadReplacedAsync()
    {
        if (Replaces is not { } id) return;

        var document = await _documents.GetAsync(id);

        // لا يُعاد رفع مستند إلا إن كان مرفوضاً وصاحبه هو الرافع.
        var userId = long.Parse(_users.GetUserId(User)!);
        Rejected = document is { Status: IntakeStatus.Rejected } && document.UploadedByUserId == userId
            ? document
            : null;

        if (Rejected is null) Replaces = null;
    }

    private static string Extension(IFormFile file) =>
        Path.GetExtension(file.FileName).ToLowerInvariant();

    /// <summary>
    /// من يملك المراجعة يُنقل إلى الشاشة فوراً، والمندوب يبقى هنا ويُطمأن أن
    /// مستنده وصل — لا يرى شاشة اعتماد ليس له أن يبتّ فيها.
    /// </summary>
    private async Task<IActionResult> Route(long documentId)
    {
        var authorization = HttpContext.RequestServices.GetRequiredService<IAuthorizationService>();
        var canReview = await authorization.AuthorizeAsync(User, PermissionCodes.DocumentReview);

        return canReview.Succeeded
            ? RedirectToPage("/Documents/Review", new { id = documentId })
            : Page();
    }
}
