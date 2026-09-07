using System.ComponentModel.DataAnnotations;
using YelgLens.Intake.Model.Basic;

namespace YelgLens.Intake.Model.Mapping;

/// <summary>
/// يربط ما يكتبه العميل في مستنده بما يعرفه أودو.
///
/// المستند يقول <c>EG_Shrouk (3)_DS_73</c> وأودو يقول
/// <c>Talabat, Talabat El Shrouk DS73</c>. لا سبيل إلى الجسر بينهما إلا جدولٌ
/// يُملأ مرة ويُستعمل دائماً — والتخمين بالتشابه اللفظي يربط الفرع بفرعٍ آخر
/// فيذهب الأوردر إلى غير صاحبه.
/// </summary>
public class BranchMapping : MainBaseEntity
{
    /// <summary>النص كما يظهر في مستند العميل.</summary>
    [Required, StringLength(300)]
    public string SourceLabel { get; set; } = "";

    /// <summary>
    /// صورة مبسّطة من <see cref="SourceLabel"/> للمطابقة: بلا مسافات ولا رموز
    /// ولا اختلاف همزات. المستند نفسه يصل بصور شتى، والمقارنة الحرفية تفشل.
    /// </summary>
    [Required, StringLength(300)]
    public string MatchKey { get; set; } = "";

    /// <summary>
    /// قاعدة أودو التي يخصّها هذا الربط.
    ///
    /// لكل عميلٍ من عملائنا أودو خاص بعملائه هو، و«فرع المعادي» عند أحدهم
    /// غير «فرع المعادي» عند الآخر. فربطٌ بلا قاعدةٍ يخصّه يظهر لمن لا يعنيه،
    /// ويرسل أوردراً إلى عميلٍ في شركةٍ أخرى — وهو خطأ لا يُكتشف من الشاشة.
    /// </summary>
    [StringLength(100)]
    public string? OdooDatabase { get; set; }

    /// <summary>
    /// اسم العميل في أودو كما يظهر — عمود Customer في ملف الاستيراد.
    ///
    /// يقبل الفراغ عمداً: النظام يلتقط الفرع الجديد وحده حين يرد في مستند،
    /// لكنه لا يخترع له اسماً في أودو. السجل يوجد ناقصاً حتى يكمله إنسان،
    /// وهذا أنفع من ألّا يوجد — فالفرع الملتقط لا يُنسى.
    /// </summary>
    [StringLength(300)]
    public string? OdooCustomer { get; set; }

    /// <summary>هل اكتمل الربط؟ الناقص يُلتقط ولا يُصدَّر به.</summary>
    public bool IsComplete => !string.IsNullOrWhiteSpace(OdooCustomer);

    /// <summary>التُقط تلقائياً من مستند، لا أدخله إنسان.</summary>
    public bool AutoCaptured { get; set; }

    /// <summary>أول مستند ورد فيه هذا الفرع — مرجعٌ لمن يكمل الربط.</summary>
    public long? FirstSeenDocumentId { get; set; }

    [StringLength(300)]
    public string? OdooInvoiceAddress { get; set; }

    [StringLength(300)]
    public string? OdooDeliveryAddress { get; set; }

    [StringLength(150)]
    public string? OdooPricelist { get; set; }

    public bool IsActive { get; set; } = true;

    [StringLength(400)]
    public string? Note { get; set; }

    /// <summary>عنوان الفوترة والتسليم يساويان العميل ما لم يُذكر غيرهما.</summary>
    public string InvoiceAddressOrCustomer =>
        string.IsNullOrWhiteSpace(OdooInvoiceAddress) ? OdooCustomer ?? "" : OdooInvoiceAddress;

    public string DeliveryAddressOrCustomer =>
        string.IsNullOrWhiteSpace(OdooDeliveryAddress) ? OdooCustomer ?? "" : OdooDeliveryAddress;
}
