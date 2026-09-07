using YelgLens.Intake.BLL.Services.Odoo;
using YelgLens.Intake.BLL.Services.Validation;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.BLL.Services.Review;

/// <summary>حال مطابقة بندٍ بمنتجٍ في أودو، كما تُعرض للمراجع.</summary>
public sealed record LineMatch(
    bool Found,
    string? Product,
    bool CheckDigitBroken,
    IReadOnlyList<(string Code, string Product)> Suggestions)
{
    public static readonly LineMatch Unknown =
        new(false, null, false, Array.Empty<(string, string)>());
}

public interface ILineVerifier
{
    /// <summary>رمز الملاحظة حين يكسر الباركود رقم التحقق الخاص به.</summary>
    const string CheckDigitCode = "BARCODE_CHECK_DIGIT";

    /// <summary>رمز الملاحظة حين لا يقابل الباركود منتجاً في أودو.</summary>
    const string NoProductCode = "BARCODE_NO_PRODUCT";

    /// <summary>
    /// يفحص بنود المستند ويضع ملاحظاتها أو يرفعها.
    /// </summary>
    Task VerifyAsync(IntakeDocument document, CancellationToken ct = default);

    /// <summary>
    /// حال كل بند: أوُجد منتجه في أودو، وما اسمه، وما أقرب البدائل إن لم يوجد.
    ///
    /// هذا للعرض لا للحفظ: المراجع يحتاج أن يرى قُدّام كل سطرٍ ما طابقه، لا أن
    /// يعرف أن ثمة خطأً في مكانٍ ما من المستند.
    /// </summary>
    Task<LineMatchReport> MatchAsync(IntakeDocument document, CancellationToken ct = default);

    /// <summary>يبحث عن رمزٍ واحد — لِما يُكتب في الشاشة قبل أن يُحفظ.</summary>
    Task<LineMatch> LookupAsync(string barcode, CancellationToken ct = default);
}

/// <summary>
/// حال البنود، ومعها هل جاوب أودو أصلاً.
///
/// التفريق لازم: «لا منتج بهذا الرمز» حكمٌ لا يصح إلا بعد سؤالٍ أُجيب. وتعذّر
/// السؤال — وخادمهم يتقلّب — ليس جواباً بالنفي.
/// </summary>
public sealed record LineMatchReport(bool Answered, IReadOnlyDictionary<int, LineMatch> Lines)
{
    public LineMatch For(int sequence) =>
        Lines.TryGetValue(sequence, out var match) ? match : LineMatch.Unknown;
}

/// <summary>
/// يتحقق من بنود المستند وقت المراجعة لا وقت التصدير.
///
/// كان الفحص يجري عند توليد ملف الاستيراد، فيُكتشف الباركود الخاطئ بعد أن صار
/// المستند «معتمداً» — وبعد أن مرّ على عين المراجع دون أن يُنبَّه. والقراءة
/// الضوئية تخطئ خانةً فتصير <c>725765711120</c> رقماً آخر يبدو سليماً.
///
/// فيُفحص على مرحلتين: رقم التحقق أولاً — محليٌّ وفوريّ ولا يحتاج شبكة — ثم
/// وجود المنتج في أودو إن كان الاتصال مضبوطاً.
/// </summary>
public sealed class LineVerifier : ILineVerifier
{
    private readonly IOdooClient _odoo;

    public LineVerifier(IOdooClient odoo) => _odoo = odoo;

    /// <summary>
    /// يجمع حال كل بند: المنتج المطابق، أو أقرب ما يشبهه.
    ///
    /// الاقتراح يقوم على أن خطأ القراءة يقع في الخانات الأخيرة غالباً — الأولى
    /// تحمل بادئة الشركة وتتكرر عبر أصنافها، والأخيرة تميّز الصنف وهي الأدقّ
    /// طباعةً والأسهل خطأً. فيُبحث ببادئة الرمز عن إخوته.
    /// </summary>
    public async Task<LineMatchReport> MatchAsync(
        IntakeDocument document, CancellationToken ct = default)
    {
        var lines = document.Lines.Where(l => l.Barcode.HasValue).ToList();
        var result = new Dictionary<int, LineMatch>();

        foreach (var line in lines)
            result[line.Sequence] = LineMatch.Unknown with
            {
                CheckDigitBroken = BarcodeCheckDigit.IsInvalid(line.Barcode.Value)
            };

        if (!_odoo.IsConfigured || lines.Count == 0) return new LineMatchReport(false, result);

        var codes = lines.Select(l => l.Barcode.Value!).Distinct().ToList();

        try
        {
            var found = await _odoo.SearchReadAsync("product.product",
                new object[]
                {
                    "|",
                    new object[] { "barcode", "in", codes },
                    new object[] { "default_code", "in", codes }
                },
                new[] { "display_name", "barcode", "default_code" }, ct: ct);

            var byCode = new Dictionary<string, string>();
            foreach (var product in found)
            {
                var name = OdooValue.Text(product["display_name"]) ?? "";
                foreach (var code in new[] { OdooValue.Text(product["barcode"]), OdooValue.Text(product["default_code"]) })
                    if (code is not null && codes.Contains(code)) byCode[code] = Clean(name);
            }

            var unmatched = codes.Where(c => !byCode.ContainsKey(c)).ToList();
            var suggestions = await SuggestAsync(unmatched, ct);

            foreach (var line in lines)
            {
                var code = line.Barcode.Value!;
                var broken = BarcodeCheckDigit.IsInvalid(code);

                result[line.Sequence] = byCode.TryGetValue(code, out var product)
                    ? new LineMatch(true, product, broken, Array.Empty<(string, string)>())
                    : new LineMatch(false, null, broken,
                        suggestions.TryGetValue(code, out var near)
                            ? near
                            : Array.Empty<(string, string)>());
            }
        }
        catch (OdooException)
        {
            // تعذّر السؤال ليس جواباً: تُترك الحال مجهولةً بدل أن تُعرض
            // «غير موجود» عن منتجٍ موجود.
            return new LineMatchReport(false, result);
        }

        return new LineMatchReport(true, result);
    }

    /// <summary>
    /// يبحث عن رمزٍ واحد كما يُكتب.
    ///
    /// المراجع يكتب الباركود من الورقة ولا يعرف إن كان صحيحاً حتى يحفظ ويعيد
    /// الفتح. فيُسأل أودو وهو يكتب، ويُعرض اسم المنتج فوراً — أو أقربُ ما
    /// يشبهه إن لم يوجد.
    /// </summary>
    public async Task<LineMatch> LookupAsync(string barcode, CancellationToken ct = default)
    {
        var code = barcode?.Trim() ?? "";
        var broken = BarcodeCheckDigit.IsInvalid(code);

        if (code.Length < 6 || !_odoo.IsConfigured)
            return LineMatch.Unknown with { CheckDigitBroken = broken };

        try
        {
            var rows = await _odoo.SearchReadAsync("product.product",
                new object[]
                {
                    "|",
                    new object[] { "barcode", "=", code },
                    new object[] { "default_code", "=", code }
                },
                new[] { "display_name" }, limit: 1, ct: ct);

            if (rows.Count > 0)
                return new LineMatch(true, Clean(OdooValue.Text(rows[0]["display_name"]) ?? ""),
                    broken, Array.Empty<(string, string)>());

            var near = await SuggestAsync(new List<string> { code }, ct);

            return new LineMatch(false, null, broken,
                near.TryGetValue(code, out var list) ? list : Array.Empty<(string, string)>());
        }
        catch (OdooException)
        {
            return LineMatch.Unknown with { CheckDigitBroken = broken };
        }
    }

    /// <summary>يبحث بالبادئة عن أقرب الرموز لكل رمزٍ لم يُطابَق.</summary>
    private async Task<Dictionary<string, List<(string Code, string Product)>>> SuggestAsync(
        List<string> codes, CancellationToken ct)
    {
        var suggestions = new Dictionary<string, List<(string, string)>>();
        if (codes.Count == 0) return suggestions;

        foreach (var code in codes)
        {
            if (code.Length < 8) continue;

            // ثلاث خانات من الآخر: تكفي لجمع إخوة الصنف، ولا تتسع حتى تجمع
            // كتالوج الشركة كله.
            var prefix = code[..^3];

            var rows = await _odoo.SearchReadAsync("product.product",
                new object[]
                {
                    "|",
                    new object[] { "barcode", "=like", prefix + "%" },
                    new object[] { "default_code", "=like", prefix + "%" }
                },
                new[] { "display_name", "barcode", "default_code" }, limit: 30, ct: ct);

            // الترتيب بالقرب لا بترتيب أودو. والرمز الذي يصحّحه إصلاحُ خانة
            // التحقق يتصدّر: هو أرجح ما قُصد، وتركُه واحداً من ستة يجعل
            // المراجع يبحث فيما نبحث نحن عنه.
            var corrected = BarcodeCheckDigit.WithCorrectedCheckDigit(code);

            var found = rows.Select(r => (
                    Code: OdooValue.Text(r["default_code"]) ?? OdooValue.Text(r["barcode"]) ?? "",
                    Product: Clean(OdooValue.Text(r["display_name"]) ?? "")))
                .Where(x => x.Code.Length > 0 && x.Code != code)
                .OrderBy(x => x.Code == corrected ? 0 : 1)
                .ThenBy(x => BarcodeCheckDigit.Distance(code, x.Code))
                .ThenBy(x => x.Code, StringComparer.Ordinal)
                .Take(5)
                .ToList();

            if (found.Count > 0) suggestions[code] = found;
        }

        return suggestions;
    }

    /// <summary>أودو يسبق الاسم برمزه بين قوسين معقوفين، والرمز معروضٌ في عموده.</summary>
    private static string Clean(string displayName)
    {
        if (!displayName.StartsWith('[')) return displayName;
        var close = displayName.IndexOf(']');
        return close > 0 ? displayName[(close + 1)..].Trim() : displayName;
    }

    public async Task VerifyAsync(IntakeDocument document, CancellationToken ct = default)
    {
        var lines = document.Lines.Where(l => l.Barcode.HasValue).ToList();

        // رقم التحقق: يُفحص دائماً، فهو حسابٌ لا نداء.
        var broken = lines
            .Where(l => BarcodeCheckDigit.IsInvalid(l.Barcode.Value))
            .ToList();

        ApplyPerLine(document, ILineVerifier.CheckDigitCode, IssueSeverity.Review,
            broken.ToDictionary(
                l => l.Sequence,
                l => $"الباركود {l.Barcode.Value} يكسر رقم التحقق الخاص به — "
                   + "أرجح أن خانةً قُرئت خطأً. راجعه على الورقة."));

        // وجود المنتج: نداءٌ واحد لكل المستند، ويُتخطّى بصمتٍ إن كان أودو
        // غير مضبوط — الفحص يُضيف يقيناً ولا يُشترط لعمل الشاشة.
        if (!_odoo.IsConfigured || lines.Count == 0) return;

        var codes = lines.Select(l => l.Barcode.Value!).Distinct().ToList();

        Dictionary<int, string> missing;
        try
        {
            var found = await _odoo.SearchReadAsync("product.product",
                new object[]
                {
                    "|",
                    new object[] { "barcode", "in", codes },
                    new object[] { "default_code", "in", codes }
                },
                new[] { "barcode", "default_code" }, ct: ct);

            var present = found
                .SelectMany(p => new[] { OdooValue.Text(p["barcode"]), OdooValue.Text(p["default_code"]) })
                .Where(c => c is not null)
                .ToHashSet()!;

            missing = lines
                .Where(l => !present.Contains(l.Barcode.Value!))
                .ToDictionary(
                    l => l.Sequence,
                    l => $"لا منتج في أودو بالرمز {l.Barcode.Value}. "
                       + "راجع الباركود، أو أضف المنتج في أودو إن كان جديداً.");
        }
        catch (OdooException)
        {
            // خادمهم يتقلّب. تعذّر الفحص ليس دليلاً على أن الباركود خطأ،
            // فتُترك الملاحظات على حالها بدل أن تُوضع بلا أساس.
            return;
        }

        ApplyPerLine(document, ILineVerifier.NoProductCode, IssueSeverity.Review, missing);
    }

    /// <summary>
    /// يوائم ملاحظات رمزٍ واحد مع الواقع: تُوضع لما ظهر، وتُعلَّم محلولةً لما
    /// زال. ولا تُحذف — أثرُ أن البند وصل خطأً ومن صحّحه جزءٌ من السجل.
    /// </summary>
    private static void ApplyPerLine(
        IntakeDocument document, string code, IssueSeverity severity, Dictionary<int, string> problems)
    {
        foreach (var issue in document.Issues.Where(i => i.Code == code && i.LineSequence is not null))
        {
            if (problems.TryGetValue(issue.LineSequence!.Value, out var message))
            {
                issue.Resolved = false;
                issue.Message = message;
                problems.Remove(issue.LineSequence.Value);
            }
            else
            {
                issue.Resolved = true;
            }
        }

        foreach (var (sequence, message) in problems)
            document.Issues.Add(new IntakeIssue
            {
                Code = code,
                Message = message,
                Severity = severity,
                LineSequence = sequence
            });
    }
}
