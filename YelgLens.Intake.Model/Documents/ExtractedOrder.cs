using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.Model.Documents;

public sealed class ExtractedOrder
{
    public DocumentKind Kind { get; set; } = DocumentKind.Unknown;
    public ExtractionStrategy Strategy { get; set; } = ExtractionStrategy.None;

    /// <summary>اسم الملف الأصلي — يُحفظ مع المخرجات لتتبع المصدر.</summary>
    public string SourceFileName { get; set; } = "";

    /// <summary>بصمة الملف الأصلي — تمنع المعالجة المكررة وتثبت أن الملف لم يتغير.</summary>
    public string SourceSha256 { get; set; } = "";

    /// <summary>رقم أمر الشراء لدى العميل — يجب أن يُحفظ لاحقاً في client_order_ref بأودو.</summary>
    public FieldValue<string> CustomerPoNumber { get; set; } = FieldValue<string>.Missing();

    public FieldValue<string> CustomerName { get; set; } = FieldValue<string>.Missing();
    public FieldValue<string> BranchLabel { get; set; } = FieldValue<string>.Missing();
    public FieldValue<DateTime>? OrderDate { get; set; }
    public FieldValue<DateTime>? DeliveryDate { get; set; }

    public List<ExtractedLine> Lines { get; set; } = new();
    public List<ValidationIssue> Issues { get; set; } = new();

    /// <summary>سطور خام لم يُتعرف عليها — تُصدَّر للتشخيص وضبط قواعد الاستخراج.</summary>
    public List<string> UnparsedRows { get; set; } = new();

    public DateTime ProcessedAtUtc { get; set; } = DateTime.UtcNow;

    public bool NeedsReview => Issues.Any(i => i.Severity != IssueSeverity.Info);
}
