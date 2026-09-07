using System.Security.Cryptography;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Enums;
using YelgLens.Intake.BLL.Services.Extraction;
using YelgLens.Intake.BLL.Infrastructure.Normalization;
using YelgLens.Intake.BLL.Services.Validation;

namespace YelgLens.Intake.BLL.Services.Pipeline;

/// <summary>
/// المسار الذي يمر به كل مستند، أياً كان مصدره:
///
///   تصنيف ← استخراج ← تطبيع ← تحقق
///
/// الاختلاف بين العملاء محصور في خطوة الاستخراج وحدها. ما بعدها موحّد،
/// فإضافة عميل جديد لا تمس بقية المسار.
/// </summary>
public sealed class IntakePipeline
{
    private readonly DocumentClassifier _classifier;
    private readonly PdfTableExtractor _pdfExtractor;
    private readonly ExcelTableExtractor _excelExtractor;
    private readonly ImageExtractor _imageExtractor;
    private readonly OrderValidator _validator;
    private readonly ILogger<IntakePipeline> _log;

    public IntakePipeline(
        DocumentClassifier classifier,
        PdfTableExtractor pdfExtractor,
        ExcelTableExtractor excelExtractor,
        ImageExtractor imageExtractor,
        OrderValidator validator,
        ILogger<IntakePipeline> log)
    {
        _classifier = classifier;
        _pdfExtractor = pdfExtractor;
        _excelExtractor = excelExtractor;
        _imageExtractor = imageExtractor;
        _validator = validator;
        _log = log;
    }

    public async Task<ExtractedOrder> ProcessAsync(string filePath, CancellationToken ct = default)
    {
        var classification = _classifier.Classify(filePath);
        _log.LogInformation("تصنيف {File}: {Strategy} — {Reason}",
            Path.GetFileName(filePath), classification.Strategy, classification.Reason);

        ExtractedOrder order = classification.Strategy switch
        {
            ExtractionStrategy.PdfTextLayer => _pdfExtractor.Extract(filePath, classification),
            ExtractionStrategy.Spreadsheet => _excelExtractor.Extract(filePath, classification),
            ExtractionStrategy.ImageRecognition => await _imageExtractor.ExtractAsync(filePath, ct),
            _ => Unsupported(filePath, classification.Reason)
        };

        order.SourceSha256 = ComputeHash(filePath);
        NormalizeDescriptions(order);
        _validator.Validate(order);

        return order;
    }

    private static ExtractedOrder Unsupported(string filePath, string reason) => new()
    {
        SourceFileName = Path.GetFileName(filePath),
        Issues =
        {
            new ValidationIssue
            {
                Code = "UNSUPPORTED_DOCUMENT",
                Message = reason,
                Severity = IssueSeverity.Blocking
            }
        }
    };

    private static void NormalizeDescriptions(ExtractedOrder order)
    {
        foreach (var line in order.Lines.Where(l => l.Description.HasValue))
        {
            var normalized = ArabicTextNormalizer.Normalize(line.Description.Value);
            line.Description = new FieldValue<string>
            {
                Value = normalized,
                Origin = line.Description.Origin,
                Confidence = line.Description.Confidence,
                HasValue = true,
                // النص الخام يبقى محفوظاً — التطبيع للمقارنة لا للعرض النهائي.
                RawText = line.Description.RawText ?? line.Description.Value
            };
        }
    }

    private static string ComputeHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
