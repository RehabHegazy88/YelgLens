using UglyToad.PdfPig;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.BLL.Services.Extraction;

public sealed record ClassificationResult(
    ExtractionStrategy Strategy,
    DocumentKind Kind,
    string Reason,
    string? PdfText = null);

/// <summary>
/// يقرر بماذا نقرأ المستند قبل أن نقرأه.
///
/// القاعدة: لا يُستخدم التعرّف الضوئي على ملف يحمل طبقة نص. استخدامه هناك
/// يحوّل بيانات يقينية إلى بيانات احتمالية بلا داعٍ.
/// </summary>
public sealed class DocumentClassifier
{
    /// <summary>الحد الأدنى من الكلمات ليُعتبر الـ PDF نصياً لا ممسوحاً.</summary>
    private const int MinimumWordsForTextLayer = 40;

    private static readonly string[] ImageExtensions =
        { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".webp" };

    private static readonly string[] SpreadsheetExtensions = { ".xlsx", ".xlsm", ".xls" };

    public ClassificationResult Classify(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        // جدول البيانات مصدر يقيني: خلاياه قيمٌ مكتوبة لا صورة تُقرأ.
        if (SpreadsheetExtensions.Contains(ext))
            return new ClassificationResult(
                ExtractionStrategy.Spreadsheet,
                DocumentKind.Unknown,
                "ملف جدول بيانات — تُقرأ خلاياه مباشرة.");

        if (ImageExtensions.Contains(ext))
            return new ClassificationResult(
                ExtractionStrategy.ImageRecognition,
                DocumentKind.Unknown,
                "ملف صورة — لا توجد طبقة نص.");

        if (ext != ".pdf")
            return new ClassificationResult(
                ExtractionStrategy.None,
                DocumentKind.Unknown,
                $"امتداد غير مدعوم: {ext}");

        string text;
        int wordCount;
        try
        {
            using var pdf = PdfDocument.Open(filePath);
            var words = pdf.GetPages().SelectMany(p => p.GetWords()).ToList();
            wordCount = words.Count;
            text = string.Join(" ", words.Select(w => w.Text));
        }
        catch (Exception ex)
        {
            return new ClassificationResult(
                ExtractionStrategy.None,
                DocumentKind.Unknown,
                $"تعذّر فتح الملف: {ex.Message}");
        }

        if (wordCount < MinimumWordsForTextLayer)
            return new ClassificationResult(
                ExtractionStrategy.ImageRecognition,
                DocumentKind.Unknown,
                $"PDF ممسوح — عدد الكلمات {wordCount} أقل من الحد ({MinimumWordsForTextLayer}).");

        var kind = DetectKind(text);

        return new ClassificationResult(
            ExtractionStrategy.PdfTextLayer,
            kind,
            $"PDF نصي — {wordCount} كلمة، يُقرأ مباشرة بلا تعرّف ضوئي.",
            text);
    }

    /// <summary>
    /// التفرقة بين أمر الشراء وإذن التسليم تكون من عنوان المستند نفسه،
    /// لا من اسم الملف. الملفان في حالتنا يحملان اسماً شبه متطابق ويختلف
    /// أحدهما بلاحقة النسخة فقط، فالاعتماد على الاسم يوقع في الخطأ.
    /// </summary>
    private static DocumentKind DetectKind(string text)
    {
        if (text.Contains("Delivery Slip", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Received date", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Rcvd", StringComparison.OrdinalIgnoreCase))
            return DocumentKind.DeliverySlip;

        if (text.Contains("Purchase Order", StringComparison.OrdinalIgnoreCase))
            return DocumentKind.PurchaseOrder;

        return DocumentKind.Unknown;
    }
}
