using System.Text.RegularExpressions;

namespace YelgLens.Intake.BLL.Services.Extraction;

/// <summary>ما قُرئ من رأس المستند. كل حقلٍ قد يغيب.</summary>
public sealed record DocumentHeader(string? PoNumber, string? Branch, string? OrderDate)
{
    public static readonly DocumentHeader Empty = new(null, null, null);

    public bool IsEmpty => PoNumber is null && Branch is null && OrderDate is null;
}

/// <summary>
/// يقرأ حقول رأس المستند من خلايا الصورة.
///
/// كان مسار الصور يقرأ الجدول وحده ويترك الرأس كله — فكل ورقةٍ مصوّرة تصل
/// إلى المراجع بلا عميل ولا فرع ولا رقم أمر شراء، ويُدخلها بيده. والرأس
/// مقروءٌ في الصورة ومقروءٌ في المحرك، وكنّا نرميه.
///
/// والربط بالموضع لا بالسطر: هذه الشاشات تكتب العنوان فوق قيمته
/// (<c>WAREHOUSE</c> ثم <c>Hyde Park</c>)، والأوراق المطبوعة تكتبه بجواره
/// (<c>Ship To: ...</c>). فيُجرَّب الاثنان.
/// </summary>
public sealed class DocumentHeaderReader
{
    private readonly double _minConfidence;

    /// <param name="minConfidence">
    /// أقل ثقةٍ تُقبل للقيمة، من مئة.
    ///
    /// القيمة نصٌّ حرّ بلا تحقّق بنيوي — لا باركود ولا رقم — والثقة حارسها
    /// الوحيد، كما في وصف البند. وبدونها خرجت من صور ورقٍ مصوّرة فروعٌ اسمها
    /// «BESJUCEN»: قراءةٌ مشوّهة لعنوانٍ حقيقي، تُربَط بعميلٍ ما فيذهب الأوردر
    /// إلى غير صاحبه. وفرعٌ فارغ يُكتب بيد خيرٌ من فرعٍ مخترع يمرّ.
    /// </param>
    public DocumentHeaderReader(double minConfidence = 70) => _minConfidence = minConfidence;

    /// <summary>
    /// العناوين التي تُعرف قيمتها.
    ///
    /// و«المورّد» ليس منها عمداً: في هذه الأوامر المورّد هو فينيكس نفسها —
    /// أي نحن — لا العميل. وأخذُه عميلاً يربط كل مستند بالشركة صاحبة النظام.
    /// </summary>
    private static readonly (string[] Labels, Field Target)[] Known =
    {
        (new[] { "PO NUMBER", "PO NO", "PO #", "PURCHASE ORDER", "ORDER NUMBER",
                 "أمر الشراء", "رقم أمر الشراء" }, Field.Po),

        (new[] { "WAREHOUSE", "SHIP TO", "BRANCH", "STORE", "DELIVER TO",
                 "المخزن", "الفرع", "المستودع" }, Field.Branch),

        (new[] { "DATE", "PO DATE", "ORDER DATE", "التاريخ", "تاريخ الأمر" }, Field.Date)
    };

    private enum Field { Po, Branch, Date }

    /// <summary>
    /// شكل القيمة المقبولة لكل حقل.
    ///
    /// بدونه يفوز أول نصٍّ بعد العنوان مهما كان: قرأ المحرك «DATE K%» فأخذ
    /// «K%» تاريخاً، والتاريخ الصحيح تحته في العمود. والشكل هو ما يميّز
    /// القيمة من زخرفةٍ لصقها المحرك بالعنوان.
    /// </summary>
    private static bool Fits(Field field, string text) => field switch
    {
        Field.Po     => text.Count(char.IsDigit) >= 3,
        Field.Branch => text.Count(char.IsLetter) >= 3,
        Field.Date   => DateLike.IsMatch(text),
        _            => true
    };

    private static readonly Regex DateLike =
        new(@"^\d{1,4}[/\-.]\d{1,2}[/\-.]\d{2,4}$", RegexOptions.Compiled);

    /// <summary>ما يُستبعد كقيمة: عناوين أخرى، وكلماتٌ لا تحمل معنى.</summary>
    private static readonly Regex Noise = new(@"^[\W_]+$", RegexOptions.Compiled);

    public DocumentHeader Read(IReadOnlyList<OcrHeaderCell> cells)
    {
        if (cells.Count == 0) return DocumentHeader.Empty;

        var found = new Dictionary<Field, string>();

        foreach (var cell in cells)
        {
            var text = Collapse(cell.Text);
            if (text.Length == 0) continue;

            foreach (var (labels, target) in Known)
            {
                if (found.ContainsKey(target)) continue;

                var at = labels
                    .Select(l => (Label: l, At: IndexOfLabel(text, l)))
                    .Where(x => x.At >= 0)
                    .OrderBy(x => x.At)
                    .FirstOrDefault();

                if (at.Label is null) continue;

                // القيمة في الخلية نفسها بعد العنوان، وإلا فتحته في عموده —
                // وأيّهما يصلح شكلاً للحقل هو المأخوذ.
                var inline = cell.Confidence * 100 >= _minConfidence
                    ? Remainder(text, EndOfLabel(text, at.At, at.Label))
                    : null;

                var candidates = new[] { inline, Below(cells, cell, target) };

                var value = candidates.FirstOrDefault(c => IsValue(c) && Fits(target, c!));

                if (value is not null) found[target] = value;
                break;
            }
        }

        return new DocumentHeader(
            found.GetValueOrDefault(Field.Po),
            found.GetValueOrDefault(Field.Branch),
            found.GetValueOrDefault(Field.Date));
    }

    /// <summary>
    /// أقرب خليةٍ تحت هذه في عمودها.
    ///
    /// ويُشترط القرب: العنوان وقيمته متلاصقان، أما خليةٌ بعيدة تحته فهي حقلٌ
    /// آخر — وأخذها يجعل «المخزن» يساوي أول كلمةٍ في الجدول.
    /// </summary>
    private string? Below(IReadOnlyList<OcrHeaderCell> cells, OcrHeaderCell label, Field target)
    {
        var gaps = cells.Select(c => c.Top).Distinct().OrderBy(t => t).ToList();

        // سعة القرب تُشتق من تباعد الأسطر في المستند نفسه لا من رقمٍ ثابت،
        // فتصلح للصورة الكبيرة والصغيرة معاً.
        var spacing = gaps.Count > 2
            ? gaps.Zip(gaps.Skip(1), (a, b) => b - a).Where(g => g > 1).DefaultIfEmpty(40).Median()
            : 40;

        return cells
            .Where(c => c.Column == label.Column
                     && c.Top > label.Top
                     && c.Top - label.Top <= spacing * 3
                     && c.Confidence * 100 >= _minConfidence)
            .OrderBy(c => c.Top)
            .Select(c => Collapse(c.Text))
            .FirstOrDefault(t => IsValue(t) && Fits(target, t));
    }

    /// <summary>
    /// موضع العنوان في النصّ، أو سالب واحد.
    ///
    /// ولا يُشترط أن يبدأه: المحرك يقرأ أيقونة الحقل حرفاً فيخرج
    /// «M WAREHOUSE» و«8 VENDOR NAME». واشتراطُ البداية كان يُسقط الفرع
    /// وحده من بين كل الحقول.
    /// </summary>
    private static int IndexOfLabel(string text, string label)
    {
        var at = text.IndexOf(label, StringComparison.OrdinalIgnoreCase);

        // العنوان مبتوراً: يقرأ المحرك «WAREHOUS» بلا آخر حرف، فيسقط الحقل
        // كله. ويُقبل البتر إن كان ما قُرئ بدايةً حقيقية للعنوان وطوله يكفي
        // لتمييزه — «WAREHOUS» لا تلتبس بغيرها، و«PO» تلتبس بكل شيء.
        if (at < 0) at = IndexOfTruncated(text, label);

        if (at < 0) return -1;

        // على حدّ كلمة: «DATED» ليست «DATE»، و«NO PO» ليست «PO».
        var before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
        var after = at + label.Length >= text.Length
                    || !char.IsLetterOrDigit(text[at + label.Length]);

        return before && after ? at : -1;
    }

    /// <summary>موضع صيغةٍ مبتورة من العنوان، أو سالب واحد.</summary>
    private static int IndexOfTruncated(string text, string label)
    {
        if (label.Length < 7) return -1;

        var at = 0;

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var bare = new string(word.Where(char.IsLetter).ToArray());

            if (bare.Length >= 6 && bare.Length < label.Length
                && label.StartsWith(bare, StringComparison.OrdinalIgnoreCase))
                return text.IndexOf(word, at, StringComparison.Ordinal);

            at = text.IndexOf(word, at, StringComparison.Ordinal) + word.Length;
        }

        return -1;
    }

    private static bool StartsWithLabel(string text, string label) =>
        IndexOfLabel(text, label) >= 0;

    /// <summary>نهاية العنوان في النصّ، سواء كُتب كاملاً أو مبتوراً.</summary>
    private static int EndOfLabel(string text, int at, string label)
    {
        var rest = text[at..];
        var word = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        return rest.StartsWith(label, StringComparison.OrdinalIgnoreCase)
            ? at + label.Length
            : at + word.Length;
    }

    private static string Remainder(string text, int from) =>
        from >= text.Length ? "" : Collapse(text[from..].TrimStart(':', '-', '.', ' '));

    /// <summary>هل هذا نصٌّ يصلح قيمةً، أم زخرفةٌ أو عنوانٌ آخر؟</summary>
    private static bool IsValue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return false;
        if (Noise.IsMatch(text)) return false;

        // عنوانٌ آخر ليس قيمةً لسابقه.
        return !Known.Any(k => k.Labels.Any(l => StartsWithLabel(text, l)));
    }

    /// <summary>يُزيل الأيقونات والمسافات المكرّرة التي يتركها المحرك.</summary>
    private static string Collapse(string text) =>
        Regex.Replace(text.Replace('\n', ' '), @"\s+", " ")
             .Trim()
             .TrimStart('#', '@', '*', '|', '_', '·', ' ')
             .Trim();
}

internal static class MedianExtension
{
    public static double Median(this IEnumerable<double> values)
    {
        var ordered = values.OrderBy(v => v).ToList();
        return ordered.Count == 0 ? 40 : ordered[ordered.Count / 2];
    }
}
