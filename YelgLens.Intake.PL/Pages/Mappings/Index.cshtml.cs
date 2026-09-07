using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YelgLens.Intake.BLL.IRepository.Core;
using YelgLens.Intake.BLL.Services.Mapping;
using YelgLens.Intake.BLL.Services.Odoo;
using YelgLens.Intake.Model.Auth;
using YelgLens.Intake.Model.Mapping;

namespace YelgLens.Intake.PL.Pages.Mappings;

[Authorize(Policy = PermissionCodes.DocumentApprove)]
public class IndexModel : PageModel
{
    private readonly IBranchMappingService _mappings;
    private readonly IIntakeDocumentRepository _documents;
    private readonly IOdooCustomerService _customers;
    private readonly IOdooClient _odooClient;
    private readonly IMappingAudit _audit;

    public IndexModel(
        IBranchMappingService mappings,
        IIntakeDocumentRepository documents,
        IOdooCustomerService customers,
        IOdooClient odooClient,
        IMappingAudit audit)
    {
        _audit = audit;
        _mappings = mappings;
        _documents = documents;
        _customers = customers;
        _odooClient = odooClient;
    }

    public List<BranchMapping> Items { get; private set; } = new();

    /// <summary>روابط التُقطت من مستندات وينقصها اسم العميل في أودو.</summary>
    public List<BranchMapping> Pending => Items.Where(m => !m.IsComplete).ToList();

    public List<BranchMapping> Complete => Items.Where(m => m.IsComplete).ToList();

    /// <summary>كشف عملاء أودو المتاح للاختيار.</summary>
    public List<OdooCustomer> Customers { get; private set; } = new();

    public DateTime? CustomersImportedOn { get; private set; }

    /// <summary>هل يحمل الكشف المرفوع أسماء أودو المعروضة كاملةً بشركاتها الأم؟</summary>
    public bool CustomersHaveDisplayNames { get; private set; }

    /// <summary>هل الاتصال بأودو مضبوط، فيمكن السحب منه بدل رفع كشف؟</summary>
    public bool CanSyncFromOdoo { get; private set; }

    /// <summary>القاعدة أو الكشف الذي تُؤخذ منه الأسماء المعروضة الآن.</summary>
    public string CustomerSource { get; private set; } = "";

    /// <summary>من أين تُؤخذ الأسماء وكم فيها — يُعرض ليُفهم الفراغ.</summary>
    public CustomerSourceStatus? CustomerStatus { get; private set; }

    /// <summary>حصيلة فحص كل الربطات، إن طُلب.</summary>
    public MappingAuditReport? Audit { get; private set; }

    /// <summary>ربطاتٌ محفوظة لقواعد أخرى — تُعدّ ولا تُعرض، لئلا يُظنّ أنها ضاعت.</summary>
    public int MappingsElsewhere { get; private set; }

    /// <summary>القاعدة المضبوطة للاتصال — قد تختلف عن مصدر الأسماء.</summary>
    public string ConfiguredDatabase { get; private set; } = "";

    /// <summary>اقتراحُ عميلٍ لكل فرعٍ ناقص، حين يكفي رقم المحل للتمييز.</summary>
    public Dictionary<long, CustomerSuggestion> Suggestions { get; private set; } = new();

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public IFormFile? CustomerSheet { get; set; }

    [TempData] public string? Feedback { get; set; }
    [TempData] public string? FeedbackKind { get; set; }

    public class InputModel
    {
        public long Id { get; set; }

        [Required(ErrorMessage = "أدخل نص الفرع كما يظهر في المستند.")]
        public string SourceLabel { get; set; } = "";

        [Required(ErrorMessage = "اختر اسم العميل في أودو.")]
        public string OdooCustomer { get; set; } = "";

        public string? OdooInvoiceAddress { get; set; }
        public string? OdooDeliveryAddress { get; set; }
        public string? OdooPricelist { get; set; }
        public string? Note { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public async Task OnGetAsync(long? edit, string? prefill)
    {
        await LoadAsync();

        // النقر على فرعٍ بلا ربط يملأ النص كما ورد حرفياً، فلا يُعاد نسخه بيد.
        if (!string.IsNullOrWhiteSpace(prefill))
            Input.SourceLabel = prefill;

        if (edit is { } id && Items.FirstOrDefault(m => m.Id == id) is { } target)
        {
            Input = new InputModel
            {
                Id = target.Id,
                SourceLabel = target.SourceLabel,
                OdooCustomer = target.OdooCustomer ?? "",
                OdooInvoiceAddress = target.OdooInvoiceAddress,
                OdooDeliveryAddress = target.OdooDeliveryAddress,
                OdooPricelist = target.OdooPricelist,
                Note = target.Note,
                IsActive = target.IsActive
            };

            // الفرع الناقص يُفتح ومعه اقتراحه مملوءاً — يُراجَع ويُحفظ، ولا
            // يُكتب وحده. الاقتراح يوفّر البحث، والحفظ يبقى فعل إنسان.
            if (!target.IsComplete && Suggestions.TryGetValue(target.Id, out var suggestion))
                Input.OdooCustomer = suggestion.Customer.DisplayName;
        }
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        var customer = Input.OdooCustomer.Trim();

        // الاسم يُفحص قبل الحفظ لا بعد فشل الاستيراد: عمود Customer في أودو
        // يُطابَق بالنص الحرفي، وحرفٌ زائد يجعل الملف يُرفض — أو يُقبل وينسب
        // الأوردر إلى فرعٍ آخر، وهو الأسوأ لأنه لا يُكتشف.
        var check = await _customers.CheckAsync(customer);
        if (!check.IsKnown)
        {
            await LoadAsync();
            ModelState.AddModelError("Input.OdooCustomer", check.Message!);
            return Page();
        }

        var mapping = Input.Id == 0
            ? new BranchMapping()
            : await _mappings.GetAsync(Input.Id) ?? new BranchMapping();

        mapping.SourceLabel = Input.SourceLabel.Trim();
        mapping.OdooCustomer = customer;
        mapping.OdooInvoiceAddress = Blank(Input.OdooInvoiceAddress);
        mapping.OdooDeliveryAddress = Blank(Input.OdooDeliveryAddress);
        mapping.OdooPricelist = Blank(Input.OdooPricelist);
        mapping.Note = Blank(Input.Note);
        mapping.IsActive = Input.IsActive;

        try
        {
            await _mappings.SaveAsync(mapping);

            // البادئة غير المحقَّقة تُقال ولا تُبتلع: الحفظ نجح، والمراجع يعرف
            // أي جزءٍ من الاسم تحقّق النظام منه وأيّه على مسؤوليته.
            Feedback = check.PrefixUnverified
                ? $"حُفظ الربط. {check.Message}"
                : "حُفظ الربط.";
            FeedbackKind = "ok";
        }
        catch (Exception ex) when (ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
                                || ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)
        {
            // المفتاح مفرد عمداً: ربط النص الواحد بفرعين يعيد الغموض نفسه.
            Feedback = "هذا النص مربوط بالفعل. عدّل الربط القائم بدل إضافة ثانٍ.";
            FeedbackKind = "bad";
        }

        return RedirectToPage();
    }

    /// <summary>
    /// يستورد كشف العملاء المصدَّر من أودو.
    ///
    /// الكشف صورةٌ تُحدَّث، لا مصدرٌ يُنشأ فيه عميل: من غاب عنه يُعطَّل ولا
    /// يُمحى، لأن ربطاً قائماً قد يشير إليه.
    /// </summary>
    public async Task<IActionResult> OnPostImportCustomersAsync(CancellationToken ct)
    {
        if (CustomerSheet is null || CustomerSheet.Length == 0)
        {
            Feedback = "اختر ملف الكشف أولاً.";
            FeedbackKind = "bad";
            return RedirectToPage();
        }

        var extension = Path.GetExtension(CustomerSheet.FileName).ToLowerInvariant();
        if (extension is not (".xlsx" or ".xlsm"))
        {
            Feedback = $"الامتداد {extension} غير مدعوم. صدّر الكشف من أودو بصيغة xlsx.";
            FeedbackKind = "bad";
            return RedirectToPage();
        }

        try
        {
            // الملف يُقرأ في الذاكرة: ClosedXML يطلب تدفقاً قابلاً للتموضع،
            // وتدفق الرفع ليس كذلك دائماً.
            await using var upload = CustomerSheet.OpenReadStream();
            using var buffer = new MemoryStream();
            await upload.CopyToAsync(buffer, ct);
            buffer.Position = 0;

            var result = await _customers.ImportAsync(buffer, ct);

            if (result.IsEmpty)
            {
                Feedback = "لم يُقرأ أي اسم من الملف. تأكد أن فيه عمود Name.";
                FeedbackKind = "bad";
                return RedirectToPage();
            }

            var parts = new List<string> { $"قُرئ {result.Read} اسماً" };
            if (result.Added > 0) parts.Add($"أُضيف {result.Added}");
            if (result.Restored > 0) parts.Add($"عاد {result.Restored}");
            if (result.Retired > 0) parts.Add($"عُطِّل {result.Retired} لغيابه عن الكشف");
            parts.Add($"المتاح الآن {result.Total}");

            Feedback = string.Join(" · ", parts) + ".";
            FeedbackKind = "ok";

            // الكشف بلا شركة أم يعطي أسماء ناقصة البادئة، وملفُ الاستيراد
            // المبنيّ عليها يُرفض. يُقال الآن لا بعد أن يُرفض الملف.
            if (!result.HasDisplayNames)
                Feedback += " لكن الكشف بلا عمود الشركة الأم، فالأسماء ناقصة البادئة "
                          + "التي يعرضها أودو (مثل «Talabat, …»). صدّره ثانيةً ومعه "
                          + "حقل Display Name أو Related Company.";
        }
        catch (Exception ex)
        {
            Feedback = $"تعذّرت قراءة الكشف: {ex.Message}";
            FeedbackKind = "bad";
        }

        return RedirectToPage();
    }

    /// <summary>
    /// يسحب العملاء من أودو مباشرةً — وهو الطريق الصحيح.
    ///
    /// الاسم المعروض يأتي كما يبنيه أودو، فلا بادئةَ تُستنتج ولا تُكتب بيد.
    /// والسحب قراءةٌ محضة: لا يُنشأ في أودو شيء ولا يُعدَّل.
    /// </summary>
    public async Task<IActionResult> OnPostSyncOdooAsync(CancellationToken ct)
    {
        if (!_customers.CanSync)
        {
            Feedback = "الاتصال بأودو غير مضبوط. اضبط العنوان وقاعدة البيانات والمستخدم، "
                     + "وضع المفتاح في متغيّر البيئة ODOO_API_KEY.";
            FeedbackKind = "bad";
            return RedirectToPage();
        }

        try
        {
            var result = await _customers.SyncFromOdooAsync(ct);

            if (result.IsEmpty)
            {
                Feedback = "لم يردّ أودو بأي عميل. تأكد أن للحساب صلاحية القراءة على res.partner.";
                FeedbackKind = "bad";
                return RedirectToPage();
            }

            var parts = new List<string> { $"سُحب {result.Read} عميلاً من أودو" };
            if (result.Added > 0) parts.Add($"أُضيف {result.Added}");
            if (result.Restored > 0) parts.Add($"عاد {result.Restored}");
            if (result.Retired > 0) parts.Add($"عُطِّل {result.Retired} لغيابه");
            parts.Add($"المتاح الآن {result.Total}");

            Feedback = string.Join(" · ", parts) + ".";
            FeedbackKind = "ok";
        }
        catch (OdooException ex)
        {
            Feedback = $"تعذّر السحب من أودو: {ex.Message}";
            FeedbackKind = "bad";
        }

        return RedirectToPage();
    }

    /// <summary>
    /// يمرّ على المستندات المحفوظة ويلتقط ما لم يُلتقط من فروعها.
    ///
    /// الالتقاط يجري عند حفظ المستند، فما حُفظ قبل وجود هذه الميزة يبقى خارج
    /// الجدول إلى الأبد. هذا الزر يسدّ تلك الفجوة، ويُعاد تشغيله بلا ضرر —
    /// الموجود لا يُنشأ ثانيةً.
    /// </summary>
    /// <summary>
    /// يفحص الربطات كلها مقابل أودو المتصل به.
    ///
    /// يُطلب صراحةً لا عند كل فتح: النداءات تُثقل خادمهم البطيء، والحاجة إليه
    /// تأتي عند الانتقال إلى أودو آخر لا في اليوم العادي.
    /// </summary>
    public async Task<IActionResult> OnPostAuditAsync(CancellationToken ct)
    {
        await LoadAsync();

        Audit = await _audit.RunAsync(ct);

        if (Audit.Error is not null)
        {
            Feedback = Audit.Error;
            FeedbackKind = "bad";
        }
        else if (Audit.Total == 0)
        {
            Feedback = "لا ربطات في هذه القاعدة لتُفحص.";
            FeedbackKind = "bad";
        }

        return Page();
    }

    public async Task<IActionResult> OnPostCaptureAllAsync()
    {
        var documents = _documents.Get()
            .Where(d => d.BranchLabel.HasValue)
            .Select(d => new { d.Id, Label = d.BranchLabel.Value })
            .ToList();

        var captured = 0;
        foreach (var d in documents)
            if (await _mappings.CaptureAsync(d.Label, d.Id) is { AutoCaptured: true })
                captured++;

        Feedback = captured == 0
            ? "لا فروع جديدة — كل ما في المستندات ملتقَط بالفعل."
            : $"التُقط {captured} فرعاً جديداً. أكمل أسماء أودو لها.";
        FeedbackKind = "ok";

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        await _mappings.DeleteAsync(id);
        Feedback = "أُزيل الربط.";
        FeedbackKind = "ok";
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Items = await _mappings.GetAllAsync();
        Customers = await _customers.GetActiveAsync();
        CustomersImportedOn = await _customers.LastImportDateAsync();
        CustomersHaveDisplayNames = await _customers.HasDisplayNamesAsync();
        CanSyncFromOdoo = _customers.CanSync;
        CustomerSource = await _customers.ActiveSourceAsync();
        CustomerStatus = await _customers.StatusAsync();
        ConfiguredDatabase = _odooClient.Database;

        // ربطات القواعد الأخرى تُعدّ ولا تُعرض: من بدّل القاعدة ورأى الجدول
        // فارغاً يظنّ أن عمله ضاع، والعدّاد يقول إنه محفوظ في مكانه.
        MappingsElsewhere = await _mappings.GetEverywhere()
            .CountAsync(m => m.OdooDatabase != null && m.OdooDatabase != ConfiguredDatabase);

        Suggestions = new Dictionary<long, CustomerSuggestion>();
        foreach (var mapping in Items.Where(m => !m.IsComplete))
            if (await _customers.SuggestAsync(mapping.SourceLabel) is { } suggestion)
                Suggestions[mapping.Id] = suggestion;
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
