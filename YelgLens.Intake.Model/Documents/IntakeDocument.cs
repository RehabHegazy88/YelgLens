using System.ComponentModel.DataAnnotations;
using YelgLens.Intake.Model.Auth;
using YelgLens.Intake.Model.Basic;
using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.Model.Documents;

/// <summary>
/// المستند كما حُفظ: أصله، وما استُخرج منه، وأين صار في دورة المراجعة.
///
/// هذا الكيان غير <see cref="ExtractedOrder"/>. ذاك ناتج استخراجٍ عابر لا هوية
/// له، وهذا سجلٌّ له عمر وحالة ومسؤول. الفصل بينهما يبقي طبقة الاستخراج نقيةً
/// من همّ التخزين.
/// </summary>
public class IntakeDocument : BaseEntity<long>
{
    [Required, StringLength(260)]
    public string SourceFileName { get; set; } = "";

    /// <summary>اسم الملف في مجلد الحفظ — الأصل يبقى ولا يُحذف.</summary>
    [Required, StringLength(120)]
    public string StoredFileName { get; set; } = "";

    /// <summary>بصمة الأصل. هي الدليل عند الخلاف، ومانع التكرار عند إعادة الرفع.</summary>
    [Required, StringLength(64)]
    public string SourceSha256 { get; set; } = "";

    public DocumentKind Kind { get; set; }

    public ExtractionStrategy Strategy { get; set; }

    public IntakeStatus Status { get; set; } = IntakeStatus.AwaitingReview;

    /// <summary>
    /// العميل الذي يخصّه هذا المستند — أي قاعدة أودو التي كان النظام موصولاً
    /// بها لحظة الرفع.
    ///
    /// يُكتب عند الرفع لا عند الترحيل. الفرق ليس شكلياً: مستندٌ رُفع ونحن على
    /// أودو عميلٍ، ثم بُدِّلت الوصلة قبل ترحيله، كان يُرحَّل على أودو عميلٍ آخر
    /// — ورقةُ زبونٍ تصير أوردراً في دفاتر شركةٍ لا تعرفه. وبهذا العمود يُعرف
    /// صاحبه من أول لحظة، فلا يُعرض لغيره ولا يُرحَّل إلا عنده.
    /// </summary>
    [StringLength(100)]
    public string? OwnerDatabase { get; set; }

    public FieldValue<string> CustomerPoNumber { get; set; } = FieldValue<string>.Missing();

    public FieldValue<string> CustomerName { get; set; } = FieldValue<string>.Missing();

    public FieldValue<string> BranchLabel { get; set; } = FieldValue<string>.Missing();

    /// <summary>
    /// التاريخ يحمل مصدره كبقية الحقول. الفرق بين تاريخٍ مكتوب في المستند
    /// وتاريخٍ كتبه المراجع من عنده فرقٌ يُسأل عنه عند الخلاف.
    /// </summary>
    public FieldValue<DateTime> OrderDate { get; set; } = FieldValue<DateTime>.Missing();

    public FieldValue<DateTime> DeliveryDate { get; set; } = FieldValue<DateTime>.Missing();

    // ---------- أثر المسؤولية ----------

    public long UploadedByUserId { get; set; }

    public DateTime UploadedDate { get; set; } = DateTime.Now;

    public long? ReviewedByUserId { get; set; }

    public DateTime? ReviewedDate { get; set; }

    /// <summary>ملاحظة المراجع — إلزامية عند الرفض.</summary>
    [StringLength(500)]
    public string? ReviewNote { get; set; }

    // ---------- إعادة الفتح ----------

    /// <summary>
    /// متى أُعيد المستند إلى المراجعة بعد أن بُتَّ فيه.
    ///
    /// القرار ليس بابًا مغلقاً: يُعتمد المستند ثم يُكتشف بندٌ ناقص قبل الترحيل.
    /// لكن الرجوع يُسجَّل ولا يمرّ صامتاً — من فتحه ومتى ولماذا — وإلا صار
    /// الاعتماد بلا معنى.
    /// </summary>
    public DateTime? ReopenedDate { get; set; }

    public long? ReopenedByUserId { get; set; }

    /// <summary>سبب إعادة الفتح — إلزامي.</summary>
    [StringLength(500)]
    public string? ReopenNote { get; set; }

    /// <summary>كم مرة أُعيد فتحه. تكرارٌ كثير علامةٌ على خللٍ في المراجعة نفسها.</summary>
    public int ReopenCount { get; set; }

    /// <summary>
    /// المستند الذي جاء هذا بديلاً عنه بعد رفضه.
    ///
    /// الرفض ليس نهاية: المندوب يعيد التصوير ويرفع. وربط الجديد بالقديم يبقي
    /// المحاولتين مقترنتين، فيرى المراجع أنه سبق أن ردّ هذه الورقة ولماذا.
    /// </summary>
    public long? ReplacesDocumentId { get; set; }

    /// <summary>
    /// عميل أودو المسنَد إلى هذا المستند وحده.
    ///
    /// الطريق الأصل أن يُربط الفرع بعميل فيُستعمل لكل ورقةٍ من الفرع نفسه.
    /// لكن الورقة قد تصل بلا فرعٍ أصلاً — صورةٌ مقصوصة على الجدول بلا رأس —
    /// فلا يبقى ما يُربط. واشتراط الفرع حينئذٍ يقفل الباب بلا مخرج: الفرع
    /// مفتاحُ إعادة استعمال، لا شرطٌ لمعرفة العميل.
    ///
    /// فيُسنَد العميل هنا لهذا المستند وحده، ولا يُعمَّم على غيره.
    /// </summary>
    [StringLength(300)]
    public string? OdooCustomerOverride { get; set; }

    // ---------- أثر الترحيل إلى أودو ----------

    /// <summary>
    /// معرّف أمر البيع في أودو بعد الترحيل.
    ///
    /// وجوده هو ما يمنع الترحيل مرتين: الضغطة الثانية على زرٍّ بطيء لا يجوز
    /// أن تصنع أوردراً ثانياً لنفس الورقة. ويُحفظ قبل أي شيء آخر، فحتى لو
    /// انقطع ما بعده بقي أثرُ ما أُنشئ.
    /// </summary>
    public long? OdooOrderId { get; set; }

    /// <summary>رقم الأوردر كما يعرضه أودو — <c>S02845</c>.</summary>
    [StringLength(60)]
    public string? OdooOrderName { get; set; }

    /// <summary>القاعدة التي أُنشئ فيها — اختبارٌ أم إنتاج.</summary>
    [StringLength(100)]
    public string? OdooDatabase { get; set; }

    /// <summary>
    /// هل معرّفات هذا المستند صالحةٌ في القاعدة المتصل بها الآن؟
    ///
    /// معرّف الأوردر ومعرّف المرفق أرقامٌ لا معنى لها خارج قاعدتها: الرقم
    /// ١٦٥٦ في قاعدة الاختبار مرفقُ ورقةٍ بعينها، وفي القاعدة الحيّة مرفقُ
    /// شيءٍ آخر تماماً أو لا شيء. فاستعمالها بلا هذا الفحص بعد تبديل القاعدة
    /// يعرض ورقة عميلٍ مكان ورقة عميلٍ آخر، ولا يُكتشف.
    ///
    /// والقديم الذي لا قاعدة مسجّلة عليه يُعامل معاملة الموافق: سابقٌ لوجود
    /// هذا العمود، ومنعه يكسر شاشاتٍ تعمل.
    /// </summary>
    public bool BelongsToDatabase(string? database)
    {
        if (string.IsNullOrWhiteSpace(database)) return true;

        // صاحبُ المستند أولاً — يُكتب عند الرفع فيشمل ما لم يُرحَّل بعد.
        // وأثرُ الترحيل بعده، للمستندات السابقة لهذا العمود.
        var owner = OwnerDatabase ?? OdooDatabase;

        return string.IsNullOrWhiteSpace(owner)
            || string.Equals(owner, database, StringComparison.OrdinalIgnoreCase);
    }

    public DateTime? PublishedDate { get; set; }

    public long? PublishedByUserId { get; set; }

    public virtual User? PublishedBy { get; set; }

    /// <summary>معرّف المرفق في أودو بعد رفع الأصل عليه.</summary>
    public long? OdooAttachmentId { get; set; }

    /// <summary>حجم الملف الأصلي بالبايت — يبقى في السجل ولو حُذف الملف.</summary>
    public long? SourceSizeBytes { get; set; }

    /// <summary>
    /// متى حُذف الملف الأصلي من قرصنا بعد رفعه على أودو.
    ///
    /// وجود تاريخٍ هنا يعني أن نسختنا لم تعد موجودة، وأن العرض يأتي من أودو.
    /// والبصمة والاسم والحجم تبقى — هي السجل الذي يُثبت أي ملفٍ كان.
    /// </summary>
    public DateTime? FileRemovedDate { get; set; }

    /// <summary>هل بقي الأصل عندنا؟</summary>
    public bool HasLocalFile => FileRemovedDate is null;

    /// <summary>
    /// سبب آخر محاولة فاشلة لرفع الأصل على أودو.
    ///
    /// يُفصل عن خطأ الترحيل عمداً: الأوردر قد يكون أُنشئ بنجاح والأصل وحده لم
    /// يصل. وخلطُهما يجعل المراجع يعيد ترحيلاً تمّ، أو يظنّ أن الطلبية لم تصل
    /// وهي واصلة.
    /// </summary>
    [StringLength(1000)]
    public string? AttachError { get; set; }

    /// <summary>
    /// أوردرٌ في أودو وأصلٌ لم يُرفع بعد.
    ///
    /// هذه الحال هي ما يُعرض بزرّ إعادة المحاولة: العمل تمّ ناقصاً، والنقص
    /// معروفٌ ويُعالج بضغطة.
    /// </summary>
    public bool SourceNotSent => IsPublished && OdooAttachmentId is null;

    /// <summary>سبب آخر محاولة فاشلة — يُعرض للمراجع ولا يُبتلع.</summary>
    [StringLength(1000)]
    public string? PublishError { get; set; }

    /// <summary>
    /// من أرشف المستند ومتى.
    ///
    /// الأرشفة إخفاءٌ من كل الشاشات، وإخفاءٌ بلا اسمٍ خلفه ثغرةٌ في سجل
    /// المسؤولية — وهو ما يقوم عليه هذا النظام كله.
    /// </summary>
    public long? ArchivedByUserId { get; set; }

    public virtual User? ArchivedBy { get; set; }

    /// <summary>هل رُحِّل فعلاً؟ الحالة وحدها لا تكفي: الرقم هو الدليل.</summary>
    public bool IsPublished => OdooOrderId is > 0;

    public virtual IntakeDocument? Replaces { get; set; }

    public virtual User? UploadedBy { get; set; }

    public virtual User? ReviewedBy { get; set; }

    public virtual User? ReopenedBy { get; set; }

    public virtual ICollection<IntakeLine> Lines { get; set; } = new List<IntakeLine>();

    public virtual ICollection<IntakeIssue> Issues { get; set; } = new List<IntakeIssue>();

    /// <summary>صفحات المستند مرتبةً. أمر الشراء الورقي يقع في أكثر من ورقة.</summary>
    public virtual ICollection<IntakePage> Pages { get; set; } = new List<IntakePage>();

    /// <summary>
    /// هل فيه ما يمنع الترحيل؟
    ///
    /// المحلولة لا تُعدّ. الملاحظة تُعلَّم ولا تُحذف حتى يبقى أثرها، وعدُّها
    /// مانعةً بعد حلّها يجعل كل تصحيحٍ بلا أثر — وهو ما كان يقع هنا: المراجع
    /// يُدخل رقم أمر الشراء فيزول سبب المنع وتبقى الملاحظة، فيُرفض الترحيل.
    /// </summary>
    public bool HasBlockingIssue => Issues.Any(i => i.Severity == IssueSeverity.Blocking && !i.Resolved);

    public bool IsDecided => Status is IntakeStatus.Approved or IntakeStatus.Rejected or IntakeStatus.Published;
}

/// <summary>
/// صفحة واحدة من المستند: صورتها أو ملفها الأصلي وبصمته.
///
/// الورقة الواحدة قد تكون صفحتين أو ثلاثاً، ورفعها مستنداتٍ منفصلة يجعل ما بعد
/// الأولى بلا رقم أمر شراء ولا عميل — فتُرفض بلا ذنب. جمعها في مستند واحد يبقي
/// الطلبية طلبيةً واحدة.
/// </summary>
public class IntakePage : BaseEntity<long>
{
    public long IntakeDocumentId { get; set; }

    public int PageNumber { get; set; }

    [Required, StringLength(260)]
    public string SourceFileName { get; set; } = "";

    [Required, StringLength(120)]
    public string StoredFileName { get; set; } = "";

    [Required, StringLength(64)]
    public string Sha256 { get; set; } = "";

    public ExtractionStrategy Strategy { get; set; }

    /// <summary>عدد البنود التي جاءت من هذه الصفحة — للتشخيص عند الخلل.</summary>
    public int LineCount { get; set; }

    /// <summary>
    /// مرفق هذه الصفحة في أودو.
    ///
    /// لكل صفحةٍ مرفقها: المستند ثلاث ورقات، وجلبُ الأولى مكان الثالثة يعرض
    /// دليلاً غير الذي طُلب — وهو خطأٌ لا يُكتشف بالنظر لأن الصورة تبدو سليمة.
    /// </summary>
    public long? OdooAttachmentId { get; set; }

    public virtual IntakeDocument IntakeDocument { get; set; } = null!;
}

/// <summary>بند محفوظ. قيمه قابلة لتصحيح المراجع، وكل قيمة تحمل مصدرها.</summary>
public class IntakeLine : BaseEntity<long>
{
    public long IntakeDocumentId { get; set; }

    public int Sequence { get; set; }

    public FieldValue<string> Barcode { get; set; } = FieldValue<string>.Missing();

    public FieldValue<string> SupplierSku { get; set; } = FieldValue<string>.Missing();

    public FieldValue<string> Description { get; set; } = FieldValue<string>.Missing();

    public FieldValue<decimal> OrderedQty { get; set; } = FieldValue<decimal>.Missing();

    public FieldValue<decimal> ReceivedQty { get; set; } = FieldValue<decimal>.Missing();

    public FieldValue<decimal> DocumentUnitPrice { get; set; } = FieldValue<decimal>.Missing();

    public FieldValue<decimal> VatPercent { get; set; } = FieldValue<decimal>.Missing();

    public virtual IntakeDocument IntakeDocument { get; set; } = null!;

    /// <summary>أدنى ثقة بين الحقول الحاضرة — تحدد إن كان البند يحتاج نظراً.</summary>
    public double MinConfidence
    {
        get
        {
            var scores = new List<double>();
            if (Barcode.HasValue) scores.Add(Barcode.Confidence);
            if (OrderedQty.HasValue) scores.Add(OrderedQty.Confidence);
            if (Description.HasValue) scores.Add(Description.Confidence);
            if (ReceivedQty.HasValue) scores.Add(ReceivedQty.Confidence);
            if (DocumentUnitPrice.HasValue) scores.Add(DocumentUnitPrice.Confidence);
            return scores.Count == 0 ? 0.0 : scores.Min();
        }
    }

    public decimal? ShortfallQty =>
        ReceivedQty.HasValue && OrderedQty.HasValue
            ? OrderedQty.Value - ReceivedQty.Value
            : null;
}

/// <summary>ملاحظة معالجة محفوظة مع المستند.</summary>
public class IntakeIssue : BaseEntity<long>
{
    public long IntakeDocumentId { get; set; }

    [Required, StringLength(60)]
    public string Code { get; set; } = "";

    [Required, StringLength(600)]
    public string Message { get; set; } = "";

    public IssueSeverity Severity { get; set; }

    public int? LineSequence { get; set; }

    /// <summary>الملاحظة التي عالجها المراجع لا تُحذف بل تُعلَّم — الأثر يبقى.</summary>
    public bool Resolved { get; set; }

    public virtual IntakeDocument IntakeDocument { get; set; } = null!;
}
