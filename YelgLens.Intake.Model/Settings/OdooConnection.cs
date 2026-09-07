using System.ComponentModel.DataAnnotations;
using YelgLens.Intake.Model.Basic;

namespace YelgLens.Intake.Model.Settings;

/// <summary>
/// بيانات وصلٍ محفوظة بأودو — قاعدةٌ واحدة بمفتاحها وصلاحياتها.
///
/// تُحفظ أكثر من واحدة ويُفعَّل منها واحدة: اختبارٌ يُجرَّب عليه، وإنتاجٌ
/// يُنتقل إليه حين تنجح التجربة. والانتقال حينئذٍ ضغطةٌ في الشاشة لا تعديلٌ
/// في ملف الإعدادات ثم إعادة تشغيل — وهذا ما يجعله قابلاً للرجوع عنه.
///
/// والمفتاح لا يُحفظ كما هو: يُعمّى بمفاتيح حماية البيانات في التطبيق، ولا
/// يُعاد إلى الشاشة أبداً بعد حفظه.
/// </summary>
public class OdooConnection : MainBaseEntity
{
    /// <summary>العميل الذي تخصّه هذه الوصلة.</summary>
    public long? ClientId { get; set; }

    public virtual Client? Client { get; set; }

    /// <summary>اسمٌ يميّزها للعين: «اختبار» أو «إنتاج».</summary>
    [Required, StringLength(80)]
    public string Name { get; set; } = "";

    [Required, StringLength(300)]
    public string Url { get; set; } = "";

    [Required, StringLength(100)]
    public string Database { get; set; } = "";

    [Required, StringLength(150)]
    public string ServiceUser { get; set; } = "";

    /// <summary>المفتاح معمّى. لا يُقرأ إلا في الخادم ولا يُعرض في الشاشة.</summary>
    [StringLength(800)]
    public string? ApiKeyProtected { get; set; }

    /// <summary>هل يُسمح لهذا النظام بالكتابة في هذه القاعدة؟</summary>
    public bool AllowWrites { get; set; }

    public int TimeoutSeconds { get; set; } = 180;

    public bool AttachSourceOnPublish { get; set; } = true;

    public bool DeleteLocalAfterAttach { get; set; }

    /// <summary>
    /// الوصلة المعمول بها لهذا العميل. واحدةٌ لكل عميل لا واحدةٌ للنظام.
    ///
    /// الفرق جوهري بعد أن صار للنظام عملاء: تفعيلٌ عامٌّ واحد كان يعني أن من
    /// ينتقل بعميلٍ إلى الإنتاج ينقل الجميع معه.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// أهذه قاعدة إنتاج؟
    ///
    /// تُكتب بيد المسؤول لا تُستنتج من الاسم: قاعدةٌ اسمها test قد تكون هي
    /// الحيّة. ووسمها هنا هو ما يُشدّد التأكيد عند التفعيل ويُظهر التنبيه في
    /// كل شاشة.
    /// </summary>
    public bool IsProduction { get; set; }

    [StringLength(400)]
    public string? Note { get; set; }

    /// <summary>متى جُرّب الاتصال آخر مرة وبماذا خرج.</summary>
    public DateTime? LastTestedDate { get; set; }

    [StringLength(500)]
    public string? LastTestResult { get; set; }

    public bool HasKey => !string.IsNullOrWhiteSpace(ApiKeyProtected);

    /// <summary>وصفٌ آمن للعرض — لا يحمل المفتاح.</summary>
    public string Describe() => $"{ServiceUser}@{Database} على {Url}";
}
