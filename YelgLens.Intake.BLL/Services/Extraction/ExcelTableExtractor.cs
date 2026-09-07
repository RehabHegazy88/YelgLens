using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.BLL.Services.Extraction;

/// <summary>
/// يقرأ جدول البنود من ملف جدول بيانات.
///
/// هذا المصدر الثالث بعد طبقة نص الـ PDF والتعرّف الضوئي، وهو يقيني كأولهما:
/// الخلية قيمةٌ مكتوبة لا صورةٌ مقروءة، فلا مجال فيها لاحتمال.
///
/// الأعمدة تُعرف من صف العناوين لا من موضعها، لأن ترتيبها يختلف بين المورّدين
/// بينما تسمياتها متقاربة. وحيث لا عناوين تُعرف، يُبلَّغ عن ذلك صراحةً بدل
/// التخمين بالمواضع.
/// </summary>
public sealed class ExcelTableExtractor
{
    private static readonly Regex BarcodeToken = new(@"^\d{8,14}$", RegexOptions.Compiled);
    private static readonly Regex PoNumber = new(@"\bPO\s*-?\s*(\d{5,})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>أقصى عدد صفوف يُفتَّش فيها عن العناوين قبل الاستسلام.</summary>
    private const int HeaderSearchDepth = 25;

    private static readonly string[] BarcodeWords =
        { "barcode", "bar code", "ean", "upc", "code", "باركود", "كود", "رمز" };

    private static readonly string[] SkuWords =
        { "sku", "supplier sku", "item code", "كود المورد", "كود الصنف", "رقم الصنف" };

    private static readonly string[] DescriptionWords =
        { "item", "description", "product", "desc", "name", "بيان", "وصف", "صنف", "منتج", "اسم" };

    /// <summary>
    /// "case" عنوانٌ شائع في هذه الملفات ويحمل ما يُطلب من الصنف. أُدرج مع
    /// ألفاظ الكمية لأن الصف بلا كمية لا يصير بنداً.
    /// </summary>
    private static readonly string[] QuantityWords =
        { "qty", "quantity", "ordered", "case", "cases", "كمية", "مطلوب", "عدد", "كرتون" };

    private static readonly string[] ReceivedWords =
        { "received", "rcvd", "delivered", "مستلم" };

    private static readonly string[] PriceWords =
        { "price", "unit price", "unit cost", "cost", "سعر" };

    public ExtractedOrder Extract(string filePath, ClassificationResult classification)
    {
        var order = new ExtractedOrder
        {
            SourceFileName = Path.GetFileName(filePath),
            Kind = classification.Kind,
            Strategy = ExtractionStrategy.Spreadsheet
        };

        using var workbook = new XLWorkbook(filePath);

        var sheet = workbook.Worksheets.FirstOrDefault(w => w.RowsUsed().Any());
        if (sheet is null)
        {
            order.Issues.Add(Issue("SHEET_EMPTY", "الملف لا يحتوي أي بيانات.", IssueSeverity.Blocking));
            return order;
        }

        var rows = sheet.RowsUsed().ToList();

        ReadPoNumber(order, rows);

        var map = FindColumns(rows);
        if (map is null)
        {
            order.Issues.Add(Issue("HEADER_ROW_NOT_FOUND",
                "لم يُعثر على صف عناوين يحدد الأعمدة. يلزم أن يحمل الملف عنواناً " +
                "لعمود الكود وآخر للكمية حتى تُقرأ البنود.", IssueSeverity.Blocking));
            return order;
        }

        ReadLines(order, rows, map);
        return order;
    }

    // ---------- رأس المستند ----------

    private static void ReadPoNumber(ExtractedOrder order, List<IXLRow> rows)
    {
        // الرقم قد يكون في أي خلية فوق الجدول، فيُفتَّش عنه بالنمط لا بالموضع.
        foreach (var row in rows.Take(HeaderSearchDepth))
            foreach (var cell in row.CellsUsed())
            {
                var text = cell.GetString();
                if (string.IsNullOrWhiteSpace(text)) continue;

                var match = PoNumber.Match(text);
                if (!match.Success) continue;

                order.CustomerPoNumber = FieldValue<string>.Certain(
                    "PO" + match.Groups[1].Value, ValueOrigin.SpreadsheetCell, text.Trim());
                return;
            }

        // كثير من هذه الملفات قوائم أصناف لفرع بلا رقم أمر شراء أصلاً. لا
        // يمنع ذلك القراءة، لكنه يمنع الاعتماد حتى يُدخله المراجع.
        order.Issues.Add(Issue("PO_NUMBER_MISSING",
            "لا يحمل الملف رقم أمر شراء. أدخله في شاشة المراجعة — هو الرابط " +
            "الوحيد بين مستند العميل والأوردر عندنا.", IssueSeverity.Blocking));
    }

    // ---------- تحديد الأعمدة ----------

    private sealed record ColumnMap(
        int HeaderRow, int Barcode, int Sku, int Description, int Quantity, int Received, int Price);

    private static ColumnMap? FindColumns(List<IXLRow> rows)
    {
        foreach (var row in rows.Take(HeaderSearchDepth))
        {
            int barcode = 0, sku = 0, description = 0, quantity = 0, received = 0, price = 0;

            foreach (var cell in row.CellsUsed())
            {
                var text = Normalize(cell.GetString());
                if (text.Length == 0) continue;

                var column = cell.Address.ColumnNumber;

                // الترتيب مقصود: الأخص قبل الأعم، وإلا ابتلع "item" عمود
                // "item code" وابتلعت الكمية عمود "المستلم".
                if (sku == 0 && Matches(text, SkuWords)) { sku = column; continue; }
                if (barcode == 0 && Matches(text, BarcodeWords)) { barcode = column; continue; }
                if (received == 0 && Matches(text, ReceivedWords)) { received = column; continue; }
                if (quantity == 0 && Matches(text, QuantityWords)) { quantity = column; continue; }
                if (price == 0 && Matches(text, PriceWords)) { price = column; continue; }

                // عمود الوصف قد يتكرر (عربي وإنجليزي). يُؤخذ أولهما، وهو
                // العربي في ملفاتنا، لأنه لغة كتالوج الأصناف عندنا.
                if (description == 0 && Matches(text, DescriptionWords)) { description = column; continue; }
            }

            if (barcode != 0 && quantity != 0)
                return new ColumnMap(row.RowNumber(), barcode, sku, description, quantity, received, price);
        }

        return null;
    }

    private static bool Matches(string text, string[] words) =>
        words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>يوحّد النص قبل المقارنة: الهمزات والتاء المربوطة تُكتب بصور شتى.</summary>
    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant()
             .Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا')
             .Replace('ة', 'ه').Replace('ى', 'ي');

    // ---------- البنود ----------

    private static void ReadLines(ExtractedOrder order, List<IXLRow> rows, ColumnMap map)
    {
        var sequence = 0;
        var wordy = new List<string>();

        foreach (var row in rows.Where(r => r.RowNumber() > map.HeaderRow))
        {
            var barcode = Text(row, map.Barcode);

            // الكود مرساة البند. الصف بلا كود إما تذييل أو مجموع أو فراغ.
            if (string.IsNullOrWhiteSpace(barcode) || !BarcodeToken.IsMatch(barcode)) continue;

            var line = new ExtractedLine
            {
                Sequence = ++sequence,
                Barcode = FieldValue<string>.Certain(barcode, ValueOrigin.SpreadsheetCell, barcode)
            };

            var raw = Text(row, map.Quantity);

            if (Number(row, map.Quantity) is { } quantity)
            {
                line.OrderedQty = FieldValue<decimal>.Certain(quantity, ValueOrigin.SpreadsheetCell, raw);
            }
            else if (!string.IsNullOrWhiteSpace(raw))
            {
                // خلية الكمية نصٌّ لا رقم: "كرتونة"، "علبتين". تحويلها إلى عدد
                // يقتضي معرفة ما في الكرتونة، وهي ليست في الملف. تُترك فارغة
                // ويُحفظ النص كما هو ليقرره المراجع — والتخمين هنا يكتب رقماً
                // خاطئاً في أمر بيع.
                line.OrderedQty = new FieldValue<decimal>
                {
                    Value = default,
                    Origin = ValueOrigin.SpreadsheetCell,
                    Confidence = 0,
                    RawText = raw,
                    HasValue = false
                };
                wordy.Add($"{barcode}: {raw}");
            }

            var sku = Text(row, map.Sku);
            if (!string.IsNullOrWhiteSpace(sku))
                line.SupplierSku = FieldValue<string>.Certain(sku, ValueOrigin.SpreadsheetCell, sku);

            var description = Text(row, map.Description);
            if (!string.IsNullOrWhiteSpace(description))
                line.Description = FieldValue<string>.Certain(description, ValueOrigin.SpreadsheetCell, description);

            if (Number(row, map.Received) is { } received)
                line.ReceivedQty = FieldValue<decimal>.Certain(received, ValueOrigin.SpreadsheetCell);

            if (Number(row, map.Price) is { } price)
                line.DocumentUnitPrice = FieldValue<decimal>.Certain(price, ValueOrigin.SpreadsheetCell);

            order.Lines.Add(line);
        }

        if (order.Lines.Count == 0)
        {
            order.Issues.Add(Issue("NO_LINES_FOUND",
                "عُثر على صف العناوين ولم يُقرأ أي بند تحته. تحقق أن عمود الكود " +
                "يحمل أرقاماً من ثماني خانات فأكثر.", IssueSeverity.Blocking));
            return;
        }

        if (wordy.Count > 0)
            order.Issues.Add(Issue("QUANTITY_NOT_NUMERIC",
                $"كمية {wordy.Count} بند مكتوبة نصاً لا رقماً ({string.Join("، ", wordy.Take(5))}" +
                (wordy.Count > 5 ? " …" : "") + "). أدخل العدد في شاشة المراجعة.",
                IssueSeverity.Blocking));

        // الباركود المكرر يعني صنفين بمعرّف واحد — لا يُطابَق أحدهما دون الآخر.
        var duplicates = order.Lines
            .Where(l => l.Barcode.HasValue)
            .GroupBy(l => l.Barcode.Value!)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
            order.Issues.Add(Issue("DUPLICATE_BARCODE_IN_FILE",
                $"تكرر الكود في الملف: {string.Join("، ", duplicates)}. راجع أيهما المقصود.",
                IssueSeverity.Review));
    }

    private static string Text(IXLRow row, int column) =>
        column == 0 ? "" : row.Cell(column).GetString().Trim();

    private static decimal? Number(IXLRow row, int column)
    {
        if (column == 0) return null;

        var cell = row.Cell(column);
        if (cell.IsEmpty()) return null;

        if (cell.DataType == XLDataType.Number) return (decimal)cell.GetDouble();

        var text = cell.GetString().Replace(",", "").Trim();
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static ValidationIssue Issue(string code, string message, IssueSeverity severity) =>
        new() { Code = code, Message = message, Severity = severity };
}
