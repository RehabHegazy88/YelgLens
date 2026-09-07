using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.BLL.Services.Extraction;

/// <summary>
/// يقرأ جدول البنود من ملفات PDF النصية (نمط طلبات: أمر الشراء وإذن التسليم).
///
/// الطريقة: تُجمع كلمات الصفحة في صفوف حسب الإحداثي الرأسي، ثم يُقرأ كل صف
/// من طرفيه — الباركود من اليسار، والأرقام من اليمين — لأن وصف المنتج في
/// المنتصف متغيّر الطول ويلتف على أكثر من سطر.
///
/// كل القيم هنا مصدرها طبقة النص، فدرجة ثقتها يقينية.
/// </summary>
public sealed class PdfTableExtractor
{
    /// <summary>فرق رأسي بالنقاط يُعتبر ضمنه أن الكلمتين في صف واحد.</summary>
    private const double RowBandTolerance = 3.0;

    private static readonly Regex BarcodeToken = new(@"^\d{12,14}$", RegexOptions.Compiled);
    private static readonly Regex NumericToken = new(@"^-?[\d,]+(\.\d+)?%?$", RegexOptions.Compiled);
    private static readonly Regex PoNumber = new(@"\bPO\d{5,}\b", RegexOptions.Compiled);
    private static readonly Regex DatePattern = new(@"\b(\d{2})/(\d{2})/(\d{4})\b", RegexOptions.Compiled);

    public ExtractedOrder Extract(string filePath, ClassificationResult classification)
    {
        var order = new ExtractedOrder
        {
            SourceFileName = Path.GetFileName(filePath),
            Kind = classification.Kind,
            Strategy = ExtractionStrategy.PdfTextLayer
        };

        using var pdf = PdfDocument.Open(filePath);
        var allWords = new List<Word>();
        foreach (var page in pdf.GetPages())
            allWords.AddRange(page.GetWords());

        var rows = GroupIntoRows(allWords);

        ReadHeader(order, classification.PdfText ?? "");
        ReadLines(order, rows);

        return order;
    }

    // ---------- تجميع الكلمات في صفوف ----------

    private static List<List<Word>> GroupIntoRows(IEnumerable<Word> words)
    {
        var rows = new List<List<Word>>();

        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Bottom))
        {
            var band = rows.FirstOrDefault(r =>
                Math.Abs(r[0].BoundingBox.Bottom - word.BoundingBox.Bottom) <= RowBandTolerance);

            if (band is null)
                rows.Add(new List<Word> { word });
            else
                band.Add(word);
        }

        foreach (var row in rows)
            row.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));

        return rows;
    }

    // ---------- رأس المستند ----------

    private static void ReadHeader(ExtractedOrder order, string text)
    {
        var po = PoNumber.Match(text);
        if (po.Success)
            order.CustomerPoNumber = FieldValue<string>.Certain(
                po.Value, ValueOrigin.PdfTextLayer, po.Value);
        else
            order.Issues.Add(new ValidationIssue
            {
                Code = "PO_NUMBER_MISSING",
                Message = "لم يُعثر على رقم أمر الشراء. هذا الرقم إلزامي لربط الأوردر بمستند العميل.",
                Severity = IssueSeverity.Blocking
            });

        var store = Regex.Match(text, @"Store Information:\s*(.+?)\s+Order Number", RegexOptions.Singleline);
        if (store.Success)
        {
            var label = store.Groups[1].Value.Trim();
            order.BranchLabel = FieldValue<string>.Certain(label, ValueOrigin.PdfTextLayer, label);
        }

        var supplier = Regex.Match(text, @"Supplier Information:\s*(.+?)\s+Store Information", RegexOptions.Singleline);
        if (supplier.Success)
            order.CustomerName = FieldValue<string>.Certain(
                supplier.Groups[1].Value.Trim(), ValueOrigin.PdfTextLayer);

        // التواريخ في هذه المستندات بصيغة dd/MM/yyyy. لا تُترك للتحويل التلقائي:
        // 01/09/2026 تعني ١ سبتمبر هنا، بينما يكتبها أودو 09/02/2026 بصيغة أخرى.
        var dates = DatePattern.Matches(text)
            .Select(m => TryParseDayFirst(m.Value))
            .Where(d => d.HasValue)
            .Select(d => d!.Value)
            .ToList();

        if (dates.Count > 0)
            order.OrderDate = FieldValue<DateTime>.Certain(dates[0], ValueOrigin.PdfTextLayer);
        if (dates.Count > 1)
            order.DeliveryDate = FieldValue<DateTime>.Certain(dates[1], ValueOrigin.PdfTextLayer);
    }

    private static DateTime? TryParseDayFirst(string value) =>
        DateTime.TryParseExact(value, "dd/MM/yyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d)
            ? d
            : null;

    // ---------- بنود الجدول ----------

    /// <summary>صفٌّ قُبل بنداً، ومعه موضعه الرأسي لإسناد شظايا الوصف إليه.</summary>
    private sealed record Placed(double Y, ExtractedLine Line, List<ValidationIssue> Issues);

    private static void ReadLines(ExtractedOrder order, List<List<Word>> rows)
    {
        var rejected = new List<string>();
        var placed = new List<Placed>();
        var fragments = new List<(double Y, string Text)>();

        foreach (var row in rows)
        {
            var tokens = row.Select(w => w.Text).ToList();
            if (tokens.Count == 0) continue;

            var y = row.Average(w => w.BoundingBox.Bottom);
            var barcodeIndex = FindBarcodeIndex(tokens);

            if (barcodeIndex < 0)
            {
                // صف بلا باركود: شظية وصف محتملة. لا يُقرَّر صاحبها الآن.
                if (LooksLikeDescriptionFragment(tokens))
                    fragments.Add((y, string.Join(" ", tokens)));
                continue;
            }

            var pending = new List<ValidationIssue>();
            var candidate = BuildLine(tokens, barcodeIndex, pending);

            // الكمية هي ما يجعل الصف بنداً. هذه المستندات تعيد كتابة الباركود في
            // عمود كود المورد، وتكسره على سطرين حين يضيق العمود، فينشأ صفٌّ يحمل
            // رمزاً صالح الشكل بلا كمية ولا سعر — وليس بنداً في الطلب.
            if (!candidate.OrderedQty.HasValue)
            {
                rejected.Add(candidate.Barcode.Value ?? "");

                // الصف المرفوض قد يحمل شظية باركود واسم المنتج التالي في سطر
                // واحد. إسقاطه كله يضيع الاسم، فيُنقذ ما فيه من كلمات.
                var words = tokens.Where(IsDescriptionToken).ToList();
                if (words.Count > 0 && LooksLikeDescriptionFragment(words))
                    fragments.Add((y, string.Join(" ", words)));

                continue;
            }

            placed.Add(new Placed(y, candidate, pending));
        }

        placed = placed.OrderByDescending(p => p.Y).ToList();

        AttachFragments(placed, fragments);

        var sequence = 0;
        foreach (var item in placed)
        {
            item.Line.Sequence = ++sequence;

            // ترتيب البند لا يُعرف قبل قبوله، والملاحظة ثابتة لا تُعدَّل بعد
            // إنشائها، فتُنسخ بالترتيب بدل أن تُغيَّر.
            foreach (var issue in item.Issues)
                order.Issues.Add(new ValidationIssue
                {
                    Code = issue.Code,
                    Message = issue.Message,
                    Severity = issue.Severity,
                    LineSequence = sequence
                });

            order.Lines.Add(item.Line);
        }

        ReportRejected(order, rejected);

        if (order.Lines.Count == 0)
            order.Issues.Add(new ValidationIssue
            {
                Code = "NO_LINES_FOUND",
                Message = "لم يُتعرف على أي بند. قد يكون تخطيط الملف مختلفاً عن المتوقع.",
                Severity = IssueSeverity.Blocking
            });
    }

    /// <summary>
    /// تُسنَد كل شظية إلى أقرب بندٍ رأسياً، لا إلى آخر بندٍ مرّ.
    ///
    /// اسم المنتج في هذه المستندات يلتف حول صف أرقامه: أوله فوقه وآخره تحته.
    /// والإسناد إلى "آخر بند مرّ" كان يعطي أولَ اسمِ المنتج التالي للبند السابق،
    /// ويلحق تذييل الصفحة كله بآخر بند في الجدول.
    ///
    /// والشظية البعيدة عن كل بند لا تُسنَد أصلاً: ما بَعُد عن الجدول ليس وصفاً
    /// فيه، بل رأسٌ أو تذييل أو مجموع.
    /// </summary>
    private static void AttachFragments(List<Placed> placed, List<(double Y, string Text)> fragments)
    {
        if (placed.Count == 0 || fragments.Count == 0) return;

        var reach = FragmentReach(placed);

        var above = placed.ToDictionary(p => p.Line, _ => new List<(double Y, string Text)>());
        var below = placed.ToDictionary(p => p.Line, _ => new List<(double Y, string Text)>());

        foreach (var fragment in fragments)
        {
            var nearest = placed.MinBy(p => Math.Abs(p.Y - fragment.Y))!;
            if (Math.Abs(nearest.Y - fragment.Y) > reach) continue;

            // الإحداثي الرأسي يكبر كلما ارتفعنا في الصفحة.
            (fragment.Y > nearest.Y ? above : below)[nearest.Line].Add(fragment);
        }

        foreach (var item in placed)
        {
            var parts = new List<string>();

            parts.AddRange(above[item.Line].OrderByDescending(f => f.Y).Select(f => f.Text));

            var existing = item.Line.Description.RawText ?? item.Line.Description.Value;
            if (!string.IsNullOrWhiteSpace(existing)) parts.Add(existing);

            parts.AddRange(below[item.Line].OrderByDescending(f => f.Y).Select(f => f.Text));

            if (parts.Count == 0) continue;

            var text = CleanDescription(string.Join(" ", parts));
            if (text.Length == 0) continue;
            item.Line.Description = FieldValue<string>.Certain(text, ValueOrigin.PdfTextLayer, text);
        }
    }

    /// <summary>
    /// يزيل الأرقام الشاردة من الوصف.
    ///
    /// كود المورد في هذه المستندات ينكسر على سطرين، فتبقى منه خانة أو اثنتان
    /// وحدها بين كلمات الاسم: "Goodhands Oat 7 Flour". والرقم المجرّد ليس من
    /// اسم المنتج، بخلاف "180g" و"70%" فلهما لاحقة تدل عليهما.
    /// </summary>
    private static string CleanDescription(string text)
    {
        var kept = text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !StrayNumber.IsMatch(t));

        return string.Join(" ", kept).Trim();
    }

    private static readonly Regex StrayNumber = new(@"^\d{1,3}$", RegexOptions.Compiled);

    /// <summary>
    /// يميّز ما هو من اسم المنتج فيما تبقّى من صفٍّ مرفوض.
    ///
    /// الشرط الأول كان "يبدأ بحرف"، فأسقط <c>180g</c> و<c>70%</c> وهما من الاسم.
    /// والمعيار الصحيح أن يحمل الرمز حرفاً في أي موضع، أو أن يكون نسبةً قصيرة
    /// كنسبة الكاكاو — أما الباركود وكود المورد المكسور فأرقام محضة لا اسم فيها.
    /// </summary>
    private static bool IsDescriptionToken(string token)
    {
        if (BarcodeToken.IsMatch(token)) return false;
        if (ShortPercent.IsMatch(token)) return true;

        return token.Length > 2 && token.Any(char.IsLetter);
    }

    private static readonly Regex ShortPercent = new(@"^\d{1,3}%$", RegexOptions.Compiled);

    /// <summary>
    /// أقصى بُعدٍ تُقبل عنده الشظية: مسافة ما بين بندين. الشظية أبعد من ذلك
    /// تخصّ شيئاً خارج الجدول.
    /// </summary>
    private static double FragmentReach(List<Placed> placed)
    {
        if (placed.Count < 2) return 60;

        var gaps = placed.Zip(placed.Skip(1), (a, b) => Math.Abs(a.Y - b.Y))
            .Where(g => g > 1)
            .OrderBy(g => g)
            .ToList();

        return gaps.Count == 0 ? 60 : gaps[gaps.Count / 2];
    }

    /// <summary>
    /// الباركود هو آخر رمز رقمي من 12–14 خانة قبل أن يبدأ وصف المنتج.
    /// يلزم أخذ الأخير لا الأول، لأن كود المورد قد يكون هو نفسه رقماً من 13 خانة
    /// ويسبق الباركود مباشرة.
    /// </summary>
    private static int FindBarcodeIndex(List<string> tokens)
    {
        var last = -1;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (BarcodeToken.IsMatch(tokens[i])) last = i;
            else if (StartsDescription(tokens[i]) && last >= 0) break;
        }
        return last;
    }

    private static bool StartsDescription(string token) =>
        token.Length > 1 && char.IsLetter(token[0]);

    /// <summary>عبارات التذييل والمجاميع — لا تكون اسم منتج قط.</summary>
    private static readonly string[] FooterPhrases =
    {
        "total", "subtotal", "vat amount", "discount", "balance",
        "signature", "الإجمالي", "إجمالي", "المجموع", "الضريبة", "الخصم", "التوقيع"
    };

    /// <summary>
    /// كلمات عناوين الأعمدة. وجود كلمة منها وحدها لا يكفي — قد يحمل اسم منتج
    /// كلمة "unit" — لكن اجتماع ثلاث منها في سطر واحد لا يكون إلا صف عناوين.
    /// </summary>
    private static readonly HashSet<string> HeaderWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "no", "sku", "barcode", "product", "qty", "quantity", "unit", "cost",
        "price", "disc", "amt", "amount", "vat", "excl", "incl", "supplier",
        "الكود", "الباركود", "الصنف", "الكمية", "السعر", "البيان", "مسلسل"
    };

    private const int HeaderWordThreshold = 3;

    private static bool LooksLikeDescriptionFragment(List<string> tokens)
    {
        if (!tokens.Any(t => t.Length > 2 && char.IsLetter(t[0]))) return false;

        var text = string.Join(" ", tokens);

        if (FooterPhrases.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)))
            return false;

        var headings = tokens.Count(t => HeaderWords.Contains(t.Trim('.', ':', '%')));

        // ثلاث كلمات عناوين لا تجتمع إلا في صف عناوين. وسطرٌ كله عناوين مرفوض
        // مهما قلّ عدده، لأن صف العناوين قد يلتف على سطرين فيبقى منه "VAT VAT".
        if (headings >= HeaderWordThreshold || headings == tokens.Count) return false;

        // سطر أغلبه أرقام ليس وصفاً بل بقية جدول أو مجموع.
        var letters = text.Count(char.IsLetter);
        var digits = text.Count(char.IsDigit);
        return letters > digits;
    }

    private static void AppendDescription(ExtractedLine line, string fragment)
    {
        var existing = line.Description.Value ?? "";
        var merged = string.IsNullOrWhiteSpace(existing) ? fragment : $"{existing} {fragment}";
        line.Description = FieldValue<string>.Certain(merged, ValueOrigin.PdfTextLayer, merged);
    }

    private static ExtractedLine BuildLine(
        List<string> tokens, int barcodeIndex, List<ValidationIssue> issues)
    {
        var line = new ExtractedLine();

        var barcode = tokens[barcodeIndex];
        line.Barcode = FieldValue<string>.Certain(barcode, ValueOrigin.PdfTextLayer, barcode);

        // ما قبل الباركود: الترتيب، ثم كود العميل، ثم كود المورد (إن وُجد).
        if (barcodeIndex >= 3)
        {
            var supplierSku = string.Concat(tokens.Skip(2).Take(barcodeIndex - 2));
            line.SupplierSku = FieldValue<string>.Certain(
                supplierSku, ValueOrigin.PdfTextLayer, supplierSku);
        }

        // ما بعد الباركود: وصف ثم ذيل رقمي.
        var rest = tokens.Skip(barcodeIndex + 1).ToList();
        var tailStart = FindNumericTailStart(rest);

        if (tailStart > 0)
        {
            var description = string.Join(" ", rest.Take(tailStart));
            line.Description = FieldValue<string>.Certain(
                description, ValueOrigin.PdfTextLayer, description);
        }

        if (tailStart < 0)
        {
            issues.Add(new ValidationIssue
            {
                Code = "LINE_NUMERICS_MISSING",
                Message = "لم يُعثر على الأرقام في هذا البند.",
                Severity = IssueSeverity.Review
            });
            return line;
        }

        ReadNumericTail(line, rest.Skip(tailStart).ToList(), issues);
        return line;
    }

    private static int FindNumericTailStart(List<string> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!NumericToken.IsMatch(tokens[i])) continue;
            if (tokens.Skip(i).All(t => NumericToken.IsMatch(t))) return i;
        }
        return -1;
    }

    /// <summary>
    /// يُقرأ الذيل الرقمي من موضع نسبة الضريبة، لا من أوله.
    /// نسبة الضريبة هي الرمز الوحيد الذي ينتهي بعلامة ٪، فتصلح مرساة ثابتة
    /// تجعل القراءة تعمل على أمر الشراء وإذن التسليم رغم اختلاف عدد الأعمدة
    /// بينهما (إذن التسليم يضيف عمود الكمية المستلمة).
    /// </summary>
    private static void ReadNumericTail(ExtractedLine line, List<string> tail, List<ValidationIssue> issues)
    {
        var vatIndex = tail.FindIndex(t => t.EndsWith('%'));
        if (vatIndex < 0)
        {
            issues.Add(new ValidationIssue
            {
                Code = "VAT_ANCHOR_MISSING",
                Message = "لم يُعثر على نسبة الضريبة في البند، فتعذّر تحديد باقي الأعمدة.",
                Severity = IssueSeverity.Review
            });
            return;
        }

        if (TryDecimal(tail[vatIndex].TrimEnd('%'), out var vat))
            line.VatPercent = FieldValue<decimal>.Certain(vat, ValueOrigin.PdfTextLayer, tail[vatIndex]);

        // قبل نسبة الضريبة مباشرة: سعر الوحدة، الخصم، القيمة قبل الضريبة.
        var head = tail.Take(vatIndex).ToList();
        if (head.Count >= 4 && TryDecimal(head[^3], out var unitCost))
            line.DocumentUnitPrice = FieldValue<decimal>.Certain(
                unitCost, ValueOrigin.PdfTextLayer, head[^3]);

        // ما تبقى في المقدمة هو الكميات: واحدة في أمر الشراء، واثنتان في إذن التسليم.
        var qtyTokens = head.Take(Math.Max(0, head.Count - 3)).ToList();

        if (qtyTokens.Count >= 1 && TryDecimal(qtyTokens[0], out var ordered))
            line.OrderedQty = FieldValue<decimal>.Certain(
                ordered, ValueOrigin.PdfTextLayer, qtyTokens[0]);

        if (qtyTokens.Count >= 2 && TryDecimal(qtyTokens[1], out var received))
            line.ReceivedQty = FieldValue<decimal>.Certain(
                received, ValueOrigin.PdfTextLayer, qtyTokens[1]);
    }

    /// <summary>
    /// الصف المرفوض قد يكون تكراراً لباركود بندٍ مقروء أو شظيةً منه انكسرت على
    /// سطرين — وهذا متوقع في هذه المستندات فلا يُبلَّغ عنه. أما رمزٌ لا يمتّ
    /// لأي بند مقروء فقد يكون بنداً ضاع، والسكوت عنه يُخفي نقصاً في الطلب.
    /// </summary>
    private static void ReportRejected(ExtractedOrder order, List<string> rejected)
    {
        var accepted = order.Lines
            .Select(l => l.Barcode.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToList();

        var orphans = rejected
            .Where(r => !string.IsNullOrEmpty(r))
            .Where(r => !accepted.Any(a =>
                a.StartsWith(r, StringComparison.Ordinal) ||
                r.StartsWith(a, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (orphans.Count == 0) return;

        order.Issues.Add(new ValidationIssue
        {
            Code = "UNMATCHED_CODE_ROW",
            Message = $"صفوف تحمل رموزاً ({string.Join("، ", orphans)}) بلا كمية، " +
                      "ولا تطابق أي بند مقروء. لم تُحتسب بنوداً — راجعها في الأصل.",
            Severity = IssueSeverity.Review
        });
    }

    private static bool TryDecimal(string token, out decimal value) =>
        decimal.TryParse(token.Replace(",", ""), NumberStyles.Any,
            CultureInfo.InvariantCulture, out value);
}
