namespace YelgLens.Intake.Model.Enums;

/// <summary>
/// من أين جاءت القيمة. المبدأ الحاكم: كل رقم في المخرجات له مصدر معروف.
/// </summary>
public enum ValueOrigin
{
    /// <summary>مستخرجة حرفياً من طبقة نص الـ PDF — يقينية.</summary>
    PdfTextLayer,

    /// <summary>مفكوكة من باركود — يقينية، ومعها تحقق داخلي (checksum).</summary>
    BarcodeDecode,

    /// <summary>ناتجة عن تعرّف ضوئي أو موديل رؤية — احتمالية.</summary>
    OpticalRecognition,

    /// <summary>محسوبة من قيم أخرى داخل المستند.</summary>
    Derived,

    /// <summary>أدخلها أو عدّلها مستخدم.</summary>
    HumanEntry,

    /// <summary>ستأتي لاحقاً من أودو (سعر، وحدة، ضريبة) — غير مأخوذة من المستند.</summary>
    ErpLookup,

    /// <summary>مقروءة من خلية جدول بيانات — يقينية كطبقة النص، بلا تعرّف ضوئي.</summary>
    SpreadsheetCell
}

public enum DocumentKind
{
    Unknown,
    /// <summary>أمر شراء وارد من العميل — يصبح أمر بيع عندنا.</summary>
    PurchaseOrder,
    /// <summary>إذن تسليم/استلام — يحمل الكمية المستلمة فعلاً.</summary>
    DeliverySlip
}

public enum ExtractionStrategy
{
    None,
    /// <summary>PDF يحتوي طبقة نص — يُقرأ مباشرة، بلا تعرّف ضوئي.</summary>
    PdfTextLayer,
    /// <summary>صورة أو PDF ممسوح — يحتاج تعرّفاً ضوئياً.</summary>
    ImageRecognition,

    /// <summary>ملف جدول بيانات — تُقرأ خلاياه مباشرة، وهي مصدر يقيني ثالث.</summary>
    Spreadsheet
}

public enum IssueSeverity
{
    /// <summary>معلومة — لا تمنع شيئاً.</summary>
    Info,
    /// <summary>تحتاج نظر بشري قبل الترحيل لأودو.</summary>
    Review,
    /// <summary>تمنع الترحيل نهائياً حتى تُحل.</summary>
    Blocking
}

/// <summary>
/// حال المستند في دورة المراجعة. الفصل بين "مرفوع" و"معتمد" هو جوهر النظام:
/// ما التقطه المندوب لا يمر إلى أودو حتى تمر عليه عين المراجع.
/// </summary>
public enum IntakeStatus
{
    /// <summary>رُفع واستُخرج، ينتظر مراجعاً.</summary>
    AwaitingReview,

    /// <summary>فتحه مراجع ولم يبتّ فيه بعد.</summary>
    UnderReview,

    /// <summary>اعتمده المراجع — صار مؤهلاً للترحيل.</summary>
    Approved,

    /// <summary>رفضه المراجع، ومعه سبب الرفض.</summary>
    Rejected,

    /// <summary>رُحّل إلى أودو.</summary>
    Published
}
