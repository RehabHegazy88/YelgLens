namespace YelgLens.Intake.BLL.ViewModel;

/// <summary>تصحيح المراجع لبند واحد. القيمة الفارغة تعني "لا تغيير".</summary>
public sealed class LineCorrection
{
    public long LineId { get; set; }

    public string? Barcode { get; set; }

    public string? Description { get; set; }

    public decimal? OrderedQty { get; set; }

    public decimal? ReceivedQty { get; set; }

    /// <summary>حذف البند — للشظايا والتكرارات التي ينتجها الاستخراج أحياناً.</summary>
    public bool Remove { get; set; }
}

/// <summary>ما يرسله المراجع عند البتّ في المستند.</summary>
public sealed class ReviewSubmission
{
    public long DocumentId { get; set; }

    public string? CustomerPoNumber { get; set; }

    public string? CustomerName { get; set; }

    public string? BranchLabel { get; set; }

    public DateTime? OrderDate { get; set; }

    public DateTime? DeliveryDate { get; set; }

    public string? Note { get; set; }

    public List<LineCorrection> Lines { get; set; } = new();
}

/// <summary>نتيجة محاولة الاعتماد أو الرفض.</summary>
public sealed record ReviewResult(bool Success, string Message);

/// <summary>صفحة واحدة بعد استخراجها، قبل دمجها مع أخواتها في مستند.</summary>
public sealed record PageInput(
    YelgLens.Intake.Model.Documents.ExtractedOrder Order,
    string SourceFileName,
    string StoredFileName,
    string Sha256);
