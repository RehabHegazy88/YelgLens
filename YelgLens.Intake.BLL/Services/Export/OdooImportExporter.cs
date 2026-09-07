using ClosedXML.Excel;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.BLL.Services.Mapping;
using YelgLens.Intake.Model.Mapping;

namespace YelgLens.Intake.BLL.Services.Export;

/// <summary>نتيجة محاولة توليد ملف الاستيراد.</summary>
public sealed record OdooExportResult(bool Success, string Message, byte[]? Content = null);

/// <summary>
/// يولّد ملفاً بأعمدة استيراد أودو لأمر البيع.
///
/// الأعمدة مأخوذة من قالب الاستيراد الذي تنزّله أودو نفسها، لا من تخمين:
/// Customer و Invoice Address و Delivery Address و Pricelist وسطور الأمر.
///
/// البنود تُكتب بالطريقة التي يفهمها الاستيراد: أول صفٍّ يحمل حقول الأمر مع
/// أول بند، وما بعده يترك حقول الأمر فارغةً ويحمل البند وحده — فيلحقه أودو
/// بالسجل الذي فوقه.
/// </summary>
public sealed class OdooImportExporter
{
    private const string Customer = "Customer";
    private const string InvoiceAddress = "Invoice Address";
    private const string DeliveryAddress = "Delivery Address";
    private const string Pricelist = "Pricelist";
    private const string CustomerReference = "Customer Reference";
    private const string LineProduct = "Order Lines/Product";
    private const string LineQuantity = "Order Lines/Quantity";

    public OdooExportResult Export(IntakeDocument document, BranchMapping? mapping)
    {
        // العميل المسنَد للمستند وحده يكفي: الورقة بلا فرع لا تُربط، والملف
        // يحتاج اسم العميل لا مصدره.
        var direct = document.OdooCustomerOverride;

        if (!string.IsNullOrWhiteSpace(direct))
            return Write(document, direct, mapping?.OdooPricelist,
                mapping?.OdooInvoiceAddress ?? direct, mapping?.OdooDeliveryAddress ?? direct);

        // بلا ربط لا يُعرف العميل في أودو، وملفٌ بعمود عميل فارغ يُرفض عند
        // الاستيراد أو — أسوأ — يُستورد إلى غير صاحبه.
        if (mapping is { IsComplete: false })
            return new OdooExportResult(false,
                $"الفرع «{mapping.SourceLabel}» ملتقَط في جدول الربط لكن اسم العميل في أودو ناقص. " +
                "أكمله ثم أعد توليد الملف.");

        if (mapping is null)
        {
            var branch = document.BranchLabel.HasValue ? document.BranchLabel.Value : null;

            return new OdooExportResult(false,
                string.IsNullOrWhiteSpace(branch)
                    ? "المستند بلا فرع، ولا يُعرف عميله في أودو. أدخل الفرع ثم اربطه."
                    : $"الفرع «{BranchCodeHint.Suggest(branch)}» غير مربوط بعميل في أودو. " +
                      "اربطه من جدول الربط ثم أعد توليد الملف.");
        }

        return Write(document, mapping.OdooCustomer!, mapping.OdooPricelist,
            mapping.InvoiceAddressOrCustomer, mapping.DeliveryAddressOrCustomer);
    }

    private OdooExportResult Write(
        IntakeDocument document, string customer, string? pricelist,
        string invoiceAddress, string deliveryAddress)
    {
        var lines = document.Lines
            .OrderBy(l => l.Sequence)
            .Where(l => l.Barcode.HasValue && l.OrderedQty.HasValue)
            .ToList();

        if (lines.Count == 0)
            return new OdooExportResult(false, "لا توجد بنود مكتملة (كود وكمية) لتصديرها.");

        var incomplete = document.Lines.Count - lines.Count;

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Sheet1");

        var columns = new[]
        {
            Customer, InvoiceAddress, DeliveryAddress, Pricelist,
            CustomerReference, LineProduct, LineQuantity
        };

        for (var c = 0; c < columns.Length; c++)
            sheet.Cell(1, c + 1).Value = columns[c];

        var row = 2;
        var first = true;

        foreach (var line in lines)
        {
            if (first)
            {
                sheet.Cell(row, 1).Value = customer;
                sheet.Cell(row, 2).Value = invoiceAddress;
                sheet.Cell(row, 3).Value = deliveryAddress;
                sheet.Cell(row, 4).Value = pricelist ?? "";
                sheet.Cell(row, 5).Value = document.CustomerPoNumber.HasValue
                    ? document.CustomerPoNumber.Value
                    : "";
                first = false;
            }

            // الكود يُكتب نصاً لا رقماً: أودو يطابق المنتج بنصّ الباركود،
            // وتحويله إلى عدد يقصّ الأصفار البادئة ويكسر المطابقة.
            var product = sheet.Cell(row, 6);
            product.Value = line.Barcode.Value!;
            product.Style.NumberFormat.Format = "@";

            sheet.Cell(row, 7).Value = line.OrderedQty.Value;

            row++;
        }

        sheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        var message = incomplete == 0
            ? $"جاهز: {lines.Count} بند."
            : $"جاهز: {lines.Count} بند. استُبعد {incomplete} بلا كود أو كمية.";

        return new OdooExportResult(true, message, stream.ToArray());
    }
}
