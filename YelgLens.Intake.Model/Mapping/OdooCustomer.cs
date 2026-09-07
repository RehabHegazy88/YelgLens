using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using YelgLens.Intake.Model.Basic;

namespace YelgLens.Intake.Model.Mapping;

/// <summary>
/// عميلٌ كما يعرفه أودو، مستوردٌ من كشف <c>res.partner</c>.
///
/// وجودها هنا ليس تكراراً لأودو بل شرطُ الصحة: عمود <c>Customer</c> في ملف
/// الاستيراد يُطابَق بالنص الحرفي، وحرفٌ زائد في كتابته يجعل الاستيراد يفشل
/// أو — وهو أسوأ — ينسب الأوردر إلى فرعٍ آخر. فيُختار الاسم من قائمة معروفة
/// ولا يُكتب باليد.
///
/// والقائمة صورةٌ من أودو لا مصدرٌ له: تُحدَّث بالرفع، ولا يُنشأ فيها عميل.
/// </summary>
public class OdooCustomer : BaseEntity<long>
{
    /// <summary>اسم جهة الاتصال وحدها كما في حقل <c>name</c>.</summary>
    [Required, StringLength(300)]
    public string Name { get; set; } = "";

    /// <summary>
    /// الاسم المعروض في أودو — وهو وحده ما يُكتب في عمود <c>Customer</c>.
    ///
    /// أودو يعرض الفرع مسبوقاً بشركته الأم: <c>Talabat, Talabat  Al agooza DS-7</c>.
    /// وكتابة اسم الفرع وحده في ملف الاستيراد لا تطابق شيئاً، فيُرفض الملف.
    /// وهذه البادئة ليست في حقل الاسم بل في <c>parent_id</c>، فلا تُستنتج من
    /// الكشف إن لم يُصدَّر معه.
    /// </summary>
    [Required, StringLength(400)]
    public string DisplayName { get; set; } = "";

    /// <summary>الشركة الأم إن وردت في الكشف — مصدر بادئة الاسم المعروض.</summary>
    [StringLength(200)]
    public string? ParentName { get; set; }

    /// <summary>هل الاسم المعروض مبنيٌّ على شركة أم معروفة، أم هو الاسم وحده؟</summary>
    public bool HasParent => !string.IsNullOrWhiteSpace(ParentName);

    /// <summary>
    /// يبني الاسم المعروض بصيغة أودو: <c>الأم، الفرع</c>. وبلا أمٍّ يبقى الاسم
    /// كما هو — لا تُخترع بادئة، فبادئةٌ مخترَعة تُرسل الأوردر إلى غير صاحبه.
    /// </summary>
    public static string BuildDisplayName(string name, string? parent) =>
        string.IsNullOrWhiteSpace(parent) ? name.Trim() : $"{parent.Trim()}, {name.Trim()}";

    /// <summary>
    /// يقتطع جزء الفرع من اسمٍ معروض. الإنسان قد يكتب البادئة بيده حين يغيب
    /// عمود الأم من الكشف، فيُتحقَّق مما يمكن التحقق منه: أن الفرع معروف.
    /// </summary>
    public static string BranchPartOf(string displayName)
    {
        var separator = displayName.LastIndexOf(", ", StringComparison.Ordinal);
        return separator < 0 ? displayName.Trim() : displayName[(separator + 2)..].Trim();
    }

    /// <summary>
    /// صورة مبسّطة للبحث: بلا مسافات مكرّرة ولا رموز، وبحروف صغيرة. الكشف
    /// يكتب <c>Talabat  Tersa DS-11</c> بمسافتين، والمستند يكتبه بغيرهما.
    /// </summary>
    [Required, StringLength(300)]
    public string SearchKey { get; set; } = "";

    /// <summary>
    /// رقم المحل في اسم العميل إن وُجد — <c>DS-11</c> و<c>DS 11</c> و<c>DS11</c>
    /// كلها أحد عشر. وهو المفتاح الوحيد المشترك بين اسم أودو ونصّ الفرع في
    /// المستند، فعليه يقوم الاقتراح.
    /// </summary>
    public int? StoreNumber { get; set; }

    /// <summary>
    /// من أين جاء هذا العميل: اسم قاعدة أودو، أو <c>sheet</c> لكشفٍ مرفوع.
    ///
    /// وجوده ليس للتوثيق بل للسلامة. قاعدة الاختبار ليست نسخةً من الإنتاج —
    /// قِيس ذلك ولم يُفترض: في اختبارهم <c>Talabat Nasr City DS8</c> وفي
    /// إنتاجهم <c>Talabat  Nasr City (3)DS59</c>. ومزامنةٌ من إحداهما كانت
    /// تُعطّل عملاء الأخرى بلا كلمة، فتُبنى ملفات الاستيراد على قائمةٍ من
    /// قاعدةٍ غير التي سيُستورد إليها.
    /// </summary>
    [Required, StringLength(100)]
    public string Source { get; set; } = "";

    /// <summary>آخر كشفٍ ورد فيه هذا العميل.</summary>
    public DateTime LastSeenDate { get; set; }

    /// <summary>
    /// عميلٌ غاب عن آخر كشف. لا يُحذف: ربطٌ قائم قد يشير إليه، ومحوُه يقطع
    /// أثر أوردرات سابقة. يُخفى عن الاختيار الجديد ويبقى مقروءاً.
    /// </summary>
    public bool IsActive { get; set; } = true;

    private static readonly Regex StorePattern =
        new(@"DS\s*[-_ ]?\s*(\d{1,4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Noise = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    /// <summary>يستخرج رقم المحل من نصٍّ أياً كانت صورة كتابته.</summary>
    public static int? ReadStoreNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var match = StorePattern.Match(text);
        return match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : null;
    }

    public static string BuildSearchKey(string name) =>
        string.IsNullOrWhiteSpace(name) ? "" : Noise.Replace(name.ToLowerInvariant(), " ").Trim();
}
