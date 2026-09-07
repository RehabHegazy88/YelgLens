using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.BLL.Services.Validation;

/// <summary>
/// القواعد التي يمر عليها كل مستند بعد الاستخراج.
///
/// المبدأ: عند فشل قاعدة يُسجَّل استثناء ولا يُنشأ سجل ناقص. البند المانع
/// يوقف الترحيل إلى أودو تماماً؛ وبند المراجعة يمرّ بعد نظر بشري.
/// </summary>
public sealed class OrderValidator
{
    /// <summary>الحد الذي تحته يُعرض الحقل على مراجع بشري.</summary>
    public const double ReviewThreshold = 0.90;

    public void Validate(ExtractedOrder order)
    {
        if (!order.CustomerPoNumber.HasValue &&
            !order.Issues.Any(i => i.Code == "PO_NUMBER_MISSING"))
        {
            order.Issues.Add(new ValidationIssue
            {
                Code = "PO_NUMBER_MISSING",
                Message = "رقم أمر الشراء غائب. هو الرابط الوحيد بين مستند العميل والأوردر عندنا، " +
                          "وبدونه لا يمكن الرجوع لاحقاً من الفاتورة إلى مصدرها.",
                Severity = IssueSeverity.Blocking
            });
        }

        foreach (var line in order.Lines)
            ValidateLine(order, line);

        CheckDuplicateBarcodes(order);
        CheckShortfalls(order);
    }

    private static void ValidateLine(ExtractedOrder order, ExtractedLine line)
    {
        if (!line.Barcode.HasValue)
        {
            order.Issues.Add(new ValidationIssue
            {
                Code = "BARCODE_MISSING",
                Message = "بند بلا كود صنف — لا يمكن مطابقته بمنتج.",
                Severity = IssueSeverity.Blocking,
                LineSequence = line.Sequence
            });
        }

        if (!line.OrderedQty.HasValue || line.OrderedQty.Value <= 0)
        {
            order.Issues.Add(new ValidationIssue
            {
                Code = "QUANTITY_INVALID",
                Message = "الكمية غائبة أو غير موجبة.",
                Severity = IssueSeverity.Blocking,
                LineSequence = line.Sequence
            });
        }

        if (line.MinConfidence < ReviewThreshold)
        {
            order.Issues.Add(new ValidationIssue
            {
                Code = "LOW_CONFIDENCE",
                Message = $"درجة الثقة {line.MinConfidence:P0} أقل من الحد المقرر — يحتاج البند مراجعة بشرية.",
                Severity = IssueSeverity.Review,
                LineSequence = line.Sequence
            });
        }

        if (line.DocumentUnitPrice is null)
        {
            order.Issues.Add(new ValidationIssue
            {
                Code = "PRICE_NOT_IN_DOCUMENT",
                Message = "المستند لا يحمل سعراً. يؤخذ السعر من قائمة أسعار العميل في أودو عند الترحيل.",
                Severity = IssueSeverity.Info,
                LineSequence = line.Sequence
            });
        }
    }

    private static void CheckDuplicateBarcodes(ExtractedOrder order)
    {
        var duplicates = order.Lines
            .Where(l => l.Barcode.HasValue)
            .GroupBy(l => l.Barcode.Value!)
            .Where(g => g.Count() > 1);

        foreach (var group in duplicates)
        {
            order.Issues.Add(new ValidationIssue
            {
                Code = "DUPLICATE_BARCODE",
                Message = $"الكود {group.Key} مكرر في {group.Count()} بنود. " +
                          "قد يكون خطأ قراءة، وقد يكون تكراراً مقصوداً في المستند.",
                Severity = IssueSeverity.Review
            });
        }
    }

    /// <summary>
    /// فرق الكمية لا يُطرح ثم يُنسى. يُسجَّل ويُطلب له سبب، لأن الرقم وحده
    /// لا يفيد في التحليل بعد شهر.
    /// </summary>
    private static void CheckShortfalls(ExtractedOrder order)
    {
        if (order.Kind != DocumentKind.DeliverySlip) return;

        foreach (var line in order.Lines.Where(l => l.ShortfallQty is > 0))
        {
            order.Issues.Add(new ValidationIssue
            {
                Code = "DELIVERY_SHORTFALL",
                Message = $"فرق تسليم قدره {line.ShortfallQty} — يلزم تحديد السبب قبل الترحيل.",
                Severity = IssueSeverity.Review,
                LineSequence = line.Sequence
            });
        }
    }
}
