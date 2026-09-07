using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.BLL.Services.Review;

/// <summary>
/// يعيد المستند المحفوظ إلى صيغة الاستخراج. التصدير والترحيل كلاهما يعمل على
/// <see cref="ExtractedOrder"/>، فبهذا التحويل يعملان على المستند بعد تصحيح
/// المراجع لا على ما خرج من الآلة أول مرة.
/// </summary>
public static class IntakeDocumentMapper
{
    public static ExtractedOrder ToExtractedOrder(this IntakeDocument document)
    {
        var order = new ExtractedOrder
        {
            SourceFileName = document.SourceFileName,
            SourceSha256 = document.SourceSha256,
            Kind = document.Kind,
            Strategy = document.Strategy,
            CustomerPoNumber = document.CustomerPoNumber,
            CustomerName = document.CustomerName,
            BranchLabel = document.BranchLabel,
            OrderDate = document.OrderDate.HasValue ? document.OrderDate : null,
            DeliveryDate = document.DeliveryDate.HasValue ? document.DeliveryDate : null
        };

        foreach (var line in document.Lines.OrderBy(l => l.Sequence))
            order.Lines.Add(new ExtractedLine
            {
                Sequence = line.Sequence,
                Barcode = line.Barcode,
                SupplierSku = line.SupplierSku,
                Description = line.Description,
                OrderedQty = line.OrderedQty,
                ReceivedQty = line.ReceivedQty.HasValue ? line.ReceivedQty : null,
                DocumentUnitPrice = line.DocumentUnitPrice.HasValue ? line.DocumentUnitPrice : null,
                VatPercent = line.VatPercent.HasValue ? line.VatPercent : null
            });

        foreach (var issue in document.Issues.Where(i => !i.Resolved))
            order.Issues.Add(new ValidationIssue
            {
                Code = issue.Code,
                Message = issue.Message,
                Severity = issue.Severity,
                LineSequence = issue.LineSequence
            });

        return order;
    }
}
