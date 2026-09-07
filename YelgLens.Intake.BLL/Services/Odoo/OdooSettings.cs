namespace YelgLens.Intake.BLL.Services.Odoo;

/// <summary>
/// بيانات الوصول إلى أودو.
///
/// المفتاح لا يُكتب في ملف الإعدادات: ملفات الإعدادات تُنسخ وتُرفع على
/// المستودعات، ومفتاحٌ في أحدها مفتاحٌ في يد كل من قرأه. يُقرأ من متغيّر
/// البيئة <c>ODOO_API_KEY</c> أو من أسرار المستخدم.
/// </summary>
public sealed class OdooSettings
{
    public const string SectionName = "Odoo";

    /// <summary>أصل الخادم بلا مسار: <c>https://example.odoo.com</c>.</summary>
    public string Url { get; set; } = "";

    /// <summary>اسم قاعدة البيانات — للخادم الواحد قواعد: إنتاج واختبار وغيرهما.</summary>
    public string Database { get; set; } = "";

    public string ServiceUser { get; set; } = "";

    public string ApiKey { get; set; } = "";

    /// <summary>
    /// هل يُسمح لهذا النظام بالكتابة في أودو؟
    ///
    /// الافتراض المنع. القراءة تُخطئ فتُعرض قائمةٌ قديمة، والكتابة تُخطئ فيُنشأ
    /// أوردر في دفاتر شركة. ولا يُفتح هذا إلا بقرارٍ مكتوب في الإعدادات، وعلى
    /// قاعدة اختبارٍ أولاً.
    /// </summary>
    public bool AllowWrites { get; set; }

    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>عدد المحاولات لكل نداء — خادمٌ متقلّب يفشل مرة وينجح التالية.</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>يُرفع أصل المستند على أمر البيع في أودو بعد إنشائه.</summary>
    public bool AttachSourceOnPublish { get; set; }

    /// <summary>
    /// يُحذف الأصل من قرصنا بعد التأكد من وصوله إلى أودو.
    ///
    /// مغلقٌ بالافتراض، ولا يعمل إلا مع <see cref="AttachSourceOnPublish"/>.
    /// الحذف لا رجعة فيه، وبعده تصير نسخة أودو هي النسخة الوحيدة — فإن حُذف
    /// المرفق هناك ضاع الدليل. يُفتح بقرارٍ مكتوب، لا بالافتراض.
    /// </summary>
    public bool DeleteLocalAfterAttach { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Url)
        && !string.IsNullOrWhiteSpace(Database)
        && !string.IsNullOrWhiteSpace(ServiceUser)
        && !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>وصفٌ آمن للعرض — لا يحمل المفتاح.</summary>
    public string Describe() =>
        IsConfigured ? $"{ServiceUser}@{Database} على {Url}" : "غير مضبوط";
}
