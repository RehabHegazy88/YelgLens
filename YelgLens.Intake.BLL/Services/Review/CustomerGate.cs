using YelgLens.Intake.BLL.Services.Mapping;
using YelgLens.Intake.Model;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.BLL.Services.Review;

/// <summary>
/// حال العميل في مستندٍ بعينه — أهو معروف في أودو أم لا، وما اسمه إن كان.
/// </summary>
public sealed record CustomerState(
    bool IsKnown, string? OdooCustomer, string? BranchLabel, string? Reason)
{
    /// <summary>
    /// الاختيار متاحٌ دائماً.
    ///
    /// كان مشروطاً بوجود فرعٍ في المستند، فكان المستند بلا فرع يُمنع بلا مخرج:
    /// الشاشة تقول «اختر عميلاً» ولا تعرض ما يُختار منه. والفرع مفتاحُ إعادة
    /// استعمال لا شرطٌ لمعرفة العميل.
    /// </summary>
    public bool CanChoose => true;

    /// <summary>هل أُسنِد العميل لهذا المستند وحده، لا لفرعٍ يُعاد استعماله؟</summary>
    public bool IsPerDocument { get; init; }

    /// <summary>رقم المحل في نصّ الفرع، إن كان فيه رقم.</summary>
    public int? BranchStore { get; init; }

    /// <summary>رقم المحل في اسم العميل المختار.</summary>
    public int? CustomerStore { get; init; }

    /// <summary>
    /// هل رقما المحل موجودان ومختلفان؟
    ///
    /// هذا أقوى ما يمكن قوله عن صحة الربط: الرقم هو المعرّف المشترك الوحيد بين
    /// نصّ الورقة واسم أودو. واختلافه يعني — على الأرجح — أن الأوردر سيذهب إلى
    /// فرعٍ غير الذي طلبه.
    /// </summary>
    public bool StoreMismatch =>
        BranchStore is { } branch && CustomerStore is { } customer && branch != customer;
}

public interface ICustomerGate
{
    /// <summary>رمز الملاحظة المانعة حين لا يُعرف عميل المستند في أودو.</summary>
    const string IssueCode = "CUSTOMER_NOT_IN_ODOO";

    /// <summary>رمز التحذير حين يختلف رقم محل الفرع عن رقم محل العميل المربوط.</summary>
    const string MismatchCode = "CUSTOMER_STORE_MISMATCH";

    /// <summary>يفحص عميل المستند ويضع الملاحظة المانعة أو يرفعها.</summary>
    Task<CustomerState> EvaluateAsync(IntakeDocument document);

    /// <summary>
    /// يسند عميلاً اختاره المراجع إلى فرع المستند، فيُحفظ الربط ويُستعمل لكل
    /// مستندٍ يحمل الفرع نفسه بعده.
    /// </summary>
    Task<CustomerState> AssignAsync(IntakeDocument document, string odooCustomer);
}

/// <summary>
/// يمنع اعتماد مستندٍ لا يُعرف عميله في أودو.
///
/// المستند يصل من الميدان بنصِّ فرعٍ لا يعرفه أحد، والنظام لا يخترع له عميلاً.
/// وقبولُه بلا عميل يؤجّل المشكلة إلى لحظة الترحيل — حيث يُرفض الملف بخطأٍ لا
/// يقول أي مستندٍ سببه، وبعد أن صار «معتمداً» في أعين الجميع.
///
/// فيُفحص عند الرفع وعند كل فتحٍ للمراجعة: إن لم يُعرف العميل وُضعت ملاحظة
/// مانعة، ولا تُرفع إلا باختيار المراجع اسماً من كشف أودو.
/// </summary>
public sealed class CustomerGate : ICustomerGate
{
    private readonly IBranchMappingService _mappings;
    private readonly IOdooCustomerService _customers;

    public CustomerGate(IBranchMappingService mappings, IOdooCustomerService customers)
    {
        _mappings = mappings;
        _customers = customers;
    }

    public async Task<CustomerState> EvaluateAsync(IntakeDocument document)
    {
        var state = await InspectAsync(document);
        Apply(document, state);
        return state;
    }

    public async Task<CustomerState> AssignAsync(IntakeDocument document, string odooCustomer)
    {
        var branch = document.BranchLabel.HasValue ? document.BranchLabel.Value : null;
        var name = odooCustomer.Trim();

        // يُفحص الاسم قبل حفظه: اختيارٌ من قائمةٍ لا يمنع لصق نصٍّ في الخانة،
        // واسمٌ لا وجود له في أودو يعيد المشكلة التي وُضعت هذه البوابة لرفعها.
        var check = await _customers.CheckAsync(name);
        if (!check.IsKnown)
        {
            var state = new CustomerState(false, null, branch, check.Message);
            Apply(document, state);
            return state;
        }

        // بلا فرعٍ يُسنَد العميل للمستند وحده: لا شيء يُربط، ولا يصح تعميم
        // اختيارٍ على أوراقٍ أخرى بناءً على ورقةٍ بلا معرّف.
        if (string.IsNullOrWhiteSpace(branch))
        {
            document.OdooCustomerOverride = name;
            return await EvaluateAsync(document);
        }

        // ومع الفرع يُحفظ الربط عليه: الورقة التالية من الفرع نفسه تجد عميلها
        // جاهزاً، ولا يُسأل المراجع عنه مرتين.
        var mapping = await _mappings.ResolveAsync(branch) ?? await _mappings.CaptureAsync(branch, document.Id);

        if (mapping is null)
        {
            document.OdooCustomerOverride = name;
            return await EvaluateAsync(document);
        }

        mapping.OdooCustomer = name;
        mapping.AutoCaptured = false;
        await _mappings.SaveAsync(mapping);

        return await EvaluateAsync(document);
    }

    private async Task<CustomerState> InspectAsync(IntakeDocument document)
    {
        var branch = document.BranchLabel.HasValue ? document.BranchLabel.Value : null;

        // العميل المسنَد للمستند وحده يسبق الفرع: من أسنده رآه بعينه، والفرع
        // قاعدةٌ عامة قد لا تنطبق على ورقةٍ بعينها.
        if (!string.IsNullOrWhiteSpace(document.OdooCustomerOverride))
        {
            var direct = await _customers.CheckAsync(document.OdooCustomerOverride);

            if (direct.IsKnown)
                return new CustomerState(true, document.OdooCustomerOverride, branch, null)
                {
                    IsPerDocument = true,
                    BranchStore = Model.Mapping.OdooCustomer.ReadStoreNumber(branch),
                    CustomerStore = Model.Mapping.OdooCustomer.ReadStoreNumber(document.OdooCustomerOverride)
                };

            return new CustomerState(false, document.OdooCustomerOverride, branch,
                $"العميل المسنَد «{document.OdooCustomerOverride}» لم يعد في كشف أودو. اختر بديلاً.")
                { IsPerDocument = true };
        }

        if (string.IsNullOrWhiteSpace(branch))
            return new CustomerState(false, null, null,
                "هذا المستند بلا فرع مقروء — صورةٌ بلا رأس الورقة غالباً. "
                + "اختر عميل أودو له مباشرةً، أو أدخل الفرع ليُربط ويُستعمل لكل ورقةٍ منه.");

        var mapping = await _mappings.ResolveAsync(branch);

        if (mapping is null || !mapping.IsComplete)
            return new CustomerState(false, null, branch,
                "فرع هذا المستند غير مربوط بعميل في أودو. اختر العميل من الكشف.");

        var check = await _customers.CheckAsync(mapping.OdooCustomer!);

        var stores = new
        {
            Branch = Model.Mapping.OdooCustomer.ReadStoreNumber(branch),
            Customer = Model.Mapping.OdooCustomer.ReadStoreNumber(mapping.OdooCustomer)
        };

        return check.IsKnown
            ? new CustomerState(true, mapping.OdooCustomer, branch, null)
                { BranchStore = stores.Branch, CustomerStore = stores.Customer }
            : new CustomerState(false, mapping.OdooCustomer, branch,
                $"العميل المربوط «{mapping.OdooCustomer}» لم يعد في كشف أودو. اختر بديلاً.")
                { BranchStore = stores.Branch, CustomerStore = stores.Customer };
    }

    /// <summary>
    /// تُوضع الملاحظة أو تُعلَّم محلولةً. ولا تُحذف: أثرُ أن المستند وصل بلا
    /// عميلٍ معروف، ومن أكمله، جزءٌ من سجل المسؤولية.
    /// </summary>
    private static void Apply(IntakeDocument document, CustomerState state)
    {
        ApplyMismatch(document, state);

        var existing = document.Issues.FirstOrDefault(i => i.Code == ICustomerGate.IssueCode);

        if (state.IsKnown)
        {
            if (existing is not null) existing.Resolved = true;
            return;
        }

        if (existing is not null)
        {
            existing.Resolved = false;
            existing.Message = state.Reason ?? existing.Message;
            return;
        }

        document.Issues.Add(new IntakeIssue
        {
            Code = ICustomerGate.IssueCode,
            Message = state.Reason ?? "عميل هذا المستند غير معروف في أودو.",
            Severity = IssueSeverity.Blocking
        });
    }

    /// <summary>
    /// يُحذّر حين يختلف رقم محل الفرع عن رقم محل العميل.
    ///
    /// ولا يمنع: قد لا يكون في أودو عميلٌ بذلك الرقم أصلاً، فيربطه المراجع
    /// بأقرب ما يعرفه وهو أدرى. لكن السكوت عنه غير جائز — الاسم يبدو صحيحاً،
    /// ولا شيء في الشاشة يقول إن الأوردر ذاهبٌ إلى فرعٍ آخر.
    /// </summary>
    private static void ApplyMismatch(IntakeDocument document, CustomerState state)
    {
        var existing = document.Issues.FirstOrDefault(i => i.Code == ICustomerGate.MismatchCode);

        if (!state.StoreMismatch)
        {
            if (existing is not null) existing.Resolved = true;
            return;
        }

        var message = $"الفرع في المستند رقمه DS {state.BranchStore}، والعميل المربوط "
                    + $"«{state.OdooCustomer}» رقمه DS {state.CustomerStore}. "
                    + "راجع الربط: الأوردر سيذهب إلى الفرع الذي يخصّ اسم العميل.";

        if (existing is not null)
        {
            existing.Resolved = false;
            existing.Message = message;
            return;
        }

        document.Issues.Add(new IntakeIssue
        {
            Code = ICustomerGate.MismatchCode,
            Message = message,
            Severity = IssueSeverity.Review
        });
    }
}
