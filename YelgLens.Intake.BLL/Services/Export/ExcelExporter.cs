using ClosedXML.Excel;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.BLL.Services.Export;

/// <summary>
/// يُصدِّر نتيجة الاستخراج إلى ملف Excel.
///
/// الملف ثلاث أوراق: البنود، والملاحظات، وورقة مصدر تبيّن من أين جاء كل حقل.
/// ورقة المصدر ليست زينة — هي التي تسمح بالرجوع لاحقاً ومعرفة أي رقم قُرئ
/// من المستند وأيّها بقي منتظراً أودو.
/// </summary>
public sealed class ExcelExporter
{
    private static readonly XLColor Navy = XLColor.FromHtml("#1F3864");
    private static readonly XLColor Cyan = XLColor.FromHtml("#0891B2");
    private static readonly XLColor ReviewTint = XLColor.FromHtml("#FEF3C7");
    private static readonly XLColor BlockTint = XLColor.FromHtml("#FEE2E2");

    public byte[] Export(ExtractedOrder order)
    {
        using var workbook = new XLWorkbook();

        BuildLinesSheet(workbook, order);
        BuildIssuesSheet(workbook, order);
        BuildProvenanceSheet(workbook, order);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void BuildLinesSheet(XLWorkbook wb, ExtractedOrder order)
    {
        var ws = wb.Worksheets.Add("البنود");
        ws.RightToLeft = true;

        var isDelivery = order.Kind == DocumentKind.DeliverySlip;

        var headers = new List<string> { "م", "الكود", "كود المورد", "البيان", "الكمية المطلوبة" };
        if (isDelivery) { headers.Add("الكمية المستلمة"); headers.Add("الفرق"); }
        headers.Add("سعر المستند");
        headers.Add("نسبة الضريبة");
        headers.Add("أدنى ثقة");

        for (var c = 0; c < headers.Count; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = headers[c];
            cell.Style.Fill.BackgroundColor = Navy;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Font.Bold = true;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        var row = 2;
        foreach (var line in order.Lines)
        {
            var col = 1;
            ws.Cell(row, col++).Value = line.Sequence;

            // الأكواد تُكتب نصاً لا رقماً، وإلا حوّلها Excel إلى صيغة أسّية وضاعت.
            var codeCell = ws.Cell(row, col++);
            codeCell.Value = line.Barcode.Value ?? "";
            codeCell.Style.NumberFormat.Format = "@";

            var supplierCell = ws.Cell(row, col++);
            supplierCell.Value = line.SupplierSku.Value ?? "";
            supplierCell.Style.NumberFormat.Format = "@";

            ws.Cell(row, col++).Value = line.Description.Value ?? "";

            // الخلية تُترك فارغة عند غياب القيمة. كتابة صفر تجعل الغياب يبدو
            // كمية مطلوبة صفراً، وهي حالة مختلفة تماماً في أمر شراء.
            var qtyCell = ws.Cell(row, col++);
            if (line.OrderedQty.HasValue) qtyCell.Value = line.OrderedQty.Value;

            if (isDelivery)
            {
                var receivedCell = ws.Cell(row, col++);
                if (line.ReceivedQty is { HasValue: true } received)
                    receivedCell.Value = received.Value;

                var gapCell = ws.Cell(row, col++);
                gapCell.Value = line.ShortfallQty;
                if (line.ShortfallQty is > 0)
                    gapCell.Style.Fill.BackgroundColor = ReviewTint;
            }

            var priceCell = ws.Cell(row, col++);
            if (line.DocumentUnitPrice is { HasValue: true } price)
                priceCell.Value = price.Value;

            var vatCell = ws.Cell(row, col++);
            if (line.VatPercent is { HasValue: true } vat)
                vatCell.Value = vat.Value;

            var confidenceCell = ws.Cell(row, col);
            confidenceCell.Value = line.MinConfidence;
            confidenceCell.Style.NumberFormat.Format = "0%";
            if (line.MinConfidence < 0.90)
                confidenceCell.Style.Fill.BackgroundColor = ReviewTint;

            row++;
        }

        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();
    }

    private static void BuildIssuesSheet(XLWorkbook wb, ExtractedOrder order)
    {
        var ws = wb.Worksheets.Add("الملاحظات");
        ws.RightToLeft = true;

        var headers = new[] { "البند", "الرمز", "الدرجة", "الوصف" };
        for (var c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = headers[c];
            cell.Style.Fill.BackgroundColor = Navy;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Font.Bold = true;
        }

        if (order.Issues.Count == 0)
        {
            ws.Cell(2, 1).Value = "لا ملاحظات — مرّ المستند على كل القواعد.";
            ws.Columns().AdjustToContents();
            return;
        }

        var row = 2;
        foreach (var issue in order.Issues.OrderByDescending(i => i.Severity))
        {
            ws.Cell(row, 1).Value = issue.LineSequence?.ToString() ?? "—";
            ws.Cell(row, 2).Value = issue.Code;
            ws.Cell(row, 3).Value = issue.Severity switch
            {
                IssueSeverity.Blocking => "مانع",
                IssueSeverity.Review => "مراجعة",
                _ => "معلومة"
            };
            ws.Cell(row, 4).Value = issue.Message;

            if (issue.Severity == IssueSeverity.Blocking)
                ws.Range(row, 1, row, 4).Style.Fill.BackgroundColor = BlockTint;
            else if (issue.Severity == IssueSeverity.Review)
                ws.Range(row, 1, row, 4).Style.Fill.BackgroundColor = ReviewTint;

            row++;
        }

        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();
        ws.Column(4).Width = 70;
        ws.Column(4).Style.Alignment.WrapText = true;
    }

    private static void BuildProvenanceSheet(XLWorkbook wb, ExtractedOrder order)
    {
        var ws = wb.Worksheets.Add("المصدر");
        ws.RightToLeft = true;

        var row = 1;
        void Pair(string label, string value)
        {
            ws.Cell(row, 1).Value = label;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontColor = Navy;
            ws.Cell(row, 2).Value = value;
            row++;
        }

        Pair("الملف الأصلي", order.SourceFileName);
        Pair("بصمة الملف", order.SourceSha256);
        Pair("نوع المستند", order.Kind switch
        {
            DocumentKind.PurchaseOrder => "أمر شراء",
            DocumentKind.DeliverySlip => "إذن تسليم",
            _ => "غير محدد"
        });
        Pair("طريقة القراءة", order.Strategy switch
        {
            ExtractionStrategy.PdfTextLayer => "طبقة نص PDF — يقينية",
            ExtractionStrategy.ImageRecognition => "تعرّف ضوئي — احتمالية",
            _ => "لم تُحدد"
        });
        Pair("رقم أمر الشراء", order.CustomerPoNumber.Value ?? "غائب");
        Pair("مصدر رقم أمر الشراء", Describe(order.CustomerPoNumber.Origin));
        Pair("الفرع", order.BranchLabel.Value ?? "غير محدد");
        Pair("تاريخ الأمر", order.OrderDate?.Value.ToString("yyyy-MM-dd") ?? "غائب");
        Pair("وقت المعالجة", order.ProcessedAtUtc.ToString("yyyy-MM-dd HH:mm 'UTC'"));
        Pair("عدد البنود", order.Lines.Count.ToString());

        row++;
        ws.Cell(row, 1).Value = "حقول لم تُؤخذ من المستند وتُستكمل من أودو عند الترحيل:";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 1).Style.Font.FontColor = Cyan;
        row++;

        foreach (var pending in new[]
                 {
                     "سعر الوحدة — من قائمة أسعار العميل",
                     "وحدة القياس — من إعداد المنتج",
                     "نسبة الضريبة — من إعداد المنتج",
                     "المنتج — بمطابقة الكود مع product.product"
                 })
        {
            ws.Cell(row++, 1).Value = $"— {pending}";
        }

        if (order.UnparsedRows.Count > 0)
        {
            row += 1;
            ws.Cell(row, 1).Value = "صفوف لم يُتعرف عليها (للتشخيص):";
            ws.Cell(row, 1).Style.Font.Bold = true;
            row++;
            foreach (var raw in order.UnparsedRows.Take(50))
                ws.Cell(row++, 1).Value = raw;
        }

        ws.Column(1).Width = 45;
        ws.Column(2).Width = 55;
    }

    private static string Describe(ValueOrigin origin) => origin switch
    {
        ValueOrigin.PdfTextLayer => "طبقة نص PDF — يقيني",
        ValueOrigin.BarcodeDecode => "فك باركود — يقيني",
        ValueOrigin.OpticalRecognition => "تعرّف ضوئي — احتمالي",
        ValueOrigin.Derived => "محسوب",
        ValueOrigin.HumanEntry => "إدخال بشري",
        ValueOrigin.ErpLookup => "من أودو",
        _ => "غير معروف"
    };
}
