using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using YelgLens.Intake.BLL.Services.Odoo;
using YelgLens.Intake.BLL.Services.Tenancy;
using YelgLens.Intake.Model.Auth;
using YelgLens.Intake.Model.Settings;

namespace YelgLens.Intake.PL.Pages.Settings;

/// <summary>
/// شاشة أوصال أودو.
///
/// وُضعت ليكون الانتقال إلى القاعدة الحيّة قراراً يُتخذ ويُراجع ويُرجع عنه،
/// لا تعديلاً في ملفٍ على الخادم يتبعه إعادة تشغيل. ولذلك تُحفظ الأوصال
/// جنباً إلى جنب — اختبارٌ وإنتاج — ويُفعَّل واحد.
///
/// وثلاثة أشياء لا تُترك للحظة الحماس: المفتاح لا يُعرض بعد حفظه، والتفعيل
/// على قاعدة إنتاج لا يمرّ إلا بكتابة اسمها بيد المسؤول، والكتابة في أودو
/// تبقى مقفولة حتى تُفتح صراحةً.
/// </summary>
[Authorize(Policy = PermissionCodes.UserManage)]
public class OdooModel : PageModel
{
    private readonly IOdooConnectionStore _store;
    private readonly IClientContext _clients;
    private readonly IOdooConnectionTester _tester;
    private readonly IOptionsSnapshot<OdooSettings> _settings;
    private readonly UserManager<User> _users;

    public OdooModel(
        IOdooConnectionStore store,
        IClientContext clients,
        IOdooConnectionTester tester,
        IOptionsSnapshot<OdooSettings> settings,
        UserManager<User> users)
    {
        _store = store;
        _clients = clients;
        _tester = tester;
        _settings = settings;
        _users = users;
    }

    public List<OdooConnection> Connections { get; private set; } = new();

    /// <summary>العميل الذي تُدار أوصاله في هذه الشاشة.</summary>
    public YelgLens.Intake.Model.Settings.Client? Client { get; private set; }

    /// <summary>الوصل المعمول به فعلاً في هذه اللحظة، كما يراه بقية النظام.</summary>
    public OdooSettings Effective => _settings.Value;

    /// <summary>هل يعمل النظام بملف الإعدادات لأنه لا وصل مفعَّل؟</summary>
    public bool FromFile => Connections.All(c => !c.IsActive);

    [BindProperty] public InputModel Input { get; set; } = new();

    [TempData] public string? Feedback { get; set; }
    [TempData] public string? FeedbackKind { get; set; }

    public class InputModel
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string Database { get; set; } = "";
        public string ServiceUser { get; set; } = "";

        /// <summary>يُترك فارغاً إن لم يُرَد تغيير المفتاح المحفوظ.</summary>
        public string? ApiKey { get; set; }

        public bool AllowWrites { get; set; }
        public int TimeoutSeconds { get; set; } = 180;
        public bool AttachSourceOnPublish { get; set; } = true;
        public bool DeleteLocalAfterAttach { get; set; }
        public bool IsProduction { get; set; }
        public string? Note { get; set; }
    }

    public async Task OnGetAsync(long? edit, CancellationToken ct)
    {
        Client = await _clients.CurrentAsync(ct);
        Connections = Client is null ? new() : await _store.ListAsync(Client.Id, ct);

        if (edit is { } id && Connections.FirstOrDefault(c => c.Id == id) is { } chosen)
            Input = new InputModel
            {
                Id = chosen.Id,
                Name = chosen.Name,
                Url = chosen.Url,
                Database = chosen.Database,
                ServiceUser = chosen.ServiceUser,
                AllowWrites = chosen.AllowWrites,
                TimeoutSeconds = chosen.TimeoutSeconds,
                AttachSourceOnPublish = chosen.AttachSourceOnPublish,
                DeleteLocalAfterAttach = chosen.DeleteLocalAfterAttach,
                IsProduction = chosen.IsProduction,
                Note = chosen.Note
            };
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Input.Name) || string.IsNullOrWhiteSpace(Input.Url)
            || string.IsNullOrWhiteSpace(Input.Database) || string.IsNullOrWhiteSpace(Input.ServiceUser))
        {
            Feedback = "الاسم والعنوان والقاعدة والمستخدم كلها إلزامية.";
            FeedbackKind = "bad";
            return RedirectToPage(new { edit = Input.Id > 0 ? Input.Id : (long?)null });
        }

        // وصلٌ جديد بلا مفتاح لا يعمل، ويُقال ذلك عند الحفظ لا حين يفشل نداء
        // في أودو بعد أسبوع.
        if (Input.Id == 0 && string.IsNullOrWhiteSpace(Input.ApiKey))
        {
            Feedback = "اكتب مفتاح الوصول — وصلٌ بلا مفتاح لا يتصل.";
            FeedbackKind = "bad";
            return RedirectToPage();
        }

        if (await _clients.CurrentAsync(ct) is not { } client)
        {
            Feedback = "لا عميل مختار. أنشئ عميلاً أولاً من شاشة العملاء.";
            FeedbackKind = "bad";
            return RedirectToPage();
        }

        var id = await _store.SaveAsync(new OdooConnection
        {
            Id = Input.Id,
            ClientId = client.Id,
            Name = Input.Name,
            Url = Input.Url,
            Database = Input.Database,
            ServiceUser = Input.ServiceUser,
            AllowWrites = Input.AllowWrites,
            TimeoutSeconds = Input.TimeoutSeconds,
            AttachSourceOnPublish = Input.AttachSourceOnPublish,
            // الحذف المحلي لا يُفتح إلا مع الرفع: بدون رفعٍ يعني محو الدليل.
            DeleteLocalAfterAttach = Input.AttachSourceOnPublish && Input.DeleteLocalAfterAttach,
            IsProduction = Input.IsProduction,
            Note = Input.Note
        }, Input.ApiKey, UserId(), ct);

        Feedback = "حُفظ الوصل. جرّب الاتصال قبل تفعيله.";
        FeedbackKind = "ok";

        return RedirectToPage(new { edit = id });
    }

    /// <summary>
    /// يجرّب الوصل قبل تفعيله.
    ///
    /// والتجربة على الوصل المطلوب لا على المفعَّل: الغرض أن يُعرف أن الجديد
    /// يعمل قبل أن يُنتقل إليه، لا بعد أن يتوقف كل شيء.
    /// </summary>
    public async Task<IActionResult> OnPostTestAsync(long id, CancellationToken ct)
    {
        var connection = await _store.FindAsync(id, ct);

        if (connection is null)
        {
            Feedback = "لم يُعثر على الوصل.";
            FeedbackKind = "bad";
            return RedirectToPage();
        }

        var key = _store.RevealKey(connection);

        if (string.IsNullOrWhiteSpace(key))
        {
            var missing = "لا مفتاح صالح لهذا الوصل — أعِد كتابته.";
            await _store.RecordTestAsync(id, missing, ct);

            Feedback = missing;
            FeedbackKind = "bad";
            return RedirectToPage(new { edit = id });
        }

        var (success, message) = await _tester.TestAsync(connection, key, ct);
        var result = success ? $"نجح الاتصال — {message}" : $"فشل الاتصال: {message}";

        await _store.RecordTestAsync(id, result, ct);

        Feedback = result;
        FeedbackKind = success ? "ok" : "bad";

        return RedirectToPage(new { edit = id });
    }

    /// <summary>
    /// يفعّل وصلاً. والانتقال إلى قاعدة إنتاج يُطلب معه كتابة اسم القاعدة.
    ///
    /// ضغطةٌ واحدة تكفي للاختبار ولا تكفي للإنتاج: بعدها يُنشئ النظام أوردرات
    /// في دفاتر شركة، وكتابة الاسم هي الفارق بين قرارٍ ونقرةٍ في غير موضعها.
    /// </summary>
    public async Task<IActionResult> OnPostActivateAsync(long id, string? confirm, CancellationToken ct)
    {
        var connection = await _store.FindAsync(id, ct);

        if (connection is null)
        {
            Feedback = "لم يُعثر على الوصل.";
            FeedbackKind = "bad";
            return RedirectToPage();
        }

        if (connection.IsProduction
            && !string.Equals(confirm?.Trim(), connection.Database, StringComparison.OrdinalIgnoreCase))
        {
            Feedback = $"هذه قاعدة إنتاج. اكتب اسمها «{connection.Database}» في خانة التأكيد لتفعيلها.";
            FeedbackKind = "bad";
            return RedirectToPage();
        }

        if (!connection.HasKey)
        {
            Feedback = "لا مفتاح محفوظ لهذا الوصل. اكتب المفتاح أولاً.";
            FeedbackKind = "bad";
            return RedirectToPage(new { edit = id });
        }

        await _store.ActivateAsync(id, UserId(), ct);

        Feedback = $"صار «{connection.Name}» ({connection.Database}) هو الوصل المعمول به"
                 + (connection.AllowWrites ? " والكتابة مفتوحة." : " والكتابة مقفولة.");
        FeedbackKind = "ok";

        return RedirectToPage();
    }

    /// <summary>
    /// يفتح الكتابة أو يقفلها بضغطة من الجدول.
    ///
    /// كانت الخانة داخل نموذج التعديل وحده، فمن أراد فتحها لزمه أن يفتح
    /// النموذج ويعرف أن «المفتاح الفارغ يعني لا تغيّره» — وهذا كثيرٌ على
    /// إعدادٍ يُفتح ويُقفل كثيراً، وهو أخطر إعدادٍ في الشاشة.
    /// </summary>
    public async Task<IActionResult> OnPostToggleWritesAsync(long id, bool allow, CancellationToken ct)
    {
        var connection = await _store.FindAsync(id, ct);

        if (connection is null)
        {
            Feedback = "لم يُعثر على الوصلة.";
            FeedbackKind = "bad";
            return RedirectToPage();
        }

        // فتحُ الكتابة على قاعدة إنتاج يُطلب معه اسمها، كما في التفعيل: بعدها
        // تُنشأ أوردرات في دفاتر شركة، والضغطة وحدها لا تكفي لذلك.
        if (allow && connection.IsProduction)
        {
            Feedback = $"«{connection.Name}» موسومةٌ إنتاجاً. افتح الكتابة من نموذج التعديل "
                     + "لتقرأ ما تفتحه قبل أن تفتحه.";
            FeedbackKind = "bad";
            return RedirectToPage(new { edit = id });
        }

        await _store.SetWritesAsync(id, allow, UserId(), ct);

        Feedback = allow
            ? $"فُتحت الكتابة على «{connection.Name}» ({connection.Database}). "
              + "النظام صار قادراً على إنشاء أوردرات فيها."
            : $"أُقفلت الكتابة على «{connection.Name}». القراءة وحدها تعمل.";
        FeedbackKind = "ok";

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(long id, CancellationToken ct)
    {
        var connection = await _store.FindAsync(id, ct);

        if (connection is { IsActive: true })
        {
            Feedback = "لا يُحذف الوصل المعمول به. فعّل غيره أولاً.";
            FeedbackKind = "bad";
            return RedirectToPage();
        }

        await _store.RemoveAsync(id, UserId(), ct);

        Feedback = "حُذف الوصل.";
        FeedbackKind = "ok";

        return RedirectToPage();
    }

    private long UserId() => long.Parse(_users.GetUserId(User)!);
}
