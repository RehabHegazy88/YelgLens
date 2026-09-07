using System.Text;
using System.Text.RegularExpressions;

namespace YelgLens.Intake.BLL.Infrastructure.Normalization;

/// <summary>
/// توحيد صور الكتابة العربية قبل أي مقارنة نصية.
///
/// لا حاجة إليه اليوم لأن العميلين الحاليين يرسلان الباركود، والمطابقة به
/// يقينية. لكنه يُطبَّق من الآن على الأوصاف لأن تكلفته لا تُذكر، ولأن
/// المطابقة بالاسم — حين تُضاف لعملاء لا يرسلون أكواداً — ستقوم عليه.
/// </summary>
public static class ArabicTextNormalizer
{
    private static readonly Regex Diacritics = new(@"[\u064B-\u0652\u0670\u0640]", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private const string ArabicIndicDigits = "٠١٢٣٤٥٦٧٨٩";
    private const string ExtendedArabicIndicDigits = "۰۱۲۳۴۵۶۷۸۹";

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";

        var sb = new StringBuilder(input.Length);

        foreach (var c in input)
        {
            var mapped = c switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' => 'ي',
                'ة' => 'ه',
                'ؤ' => 'و',
                'ئ' => 'ي',
                _ => c
            };

            var arabicIndex = ArabicIndicDigits.IndexOf(mapped);
            if (arabicIndex >= 0) { sb.Append((char)('0' + arabicIndex)); continue; }

            var extendedIndex = ExtendedArabicIndicDigits.IndexOf(mapped);
            if (extendedIndex >= 0) { sb.Append((char)('0' + extendedIndex)); continue; }

            sb.Append(mapped);
        }

        var text = Diacritics.Replace(sb.ToString(), "");
        text = NormalizeUnits(text);
        return Whitespace.Replace(text, " ").Trim().ToLowerInvariant();
    }

    /// <summary>توحيد كتابة وحدات الوزن: جم / جرام / g / gm تصير رمزاً واحداً.</summary>
    private static string NormalizeUnits(string text)
    {
        text = Regex.Replace(text, @"(\d)\s*(جرام|جم|gm|g)\b", "$1g", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(\d)\s*(كجم|كيلو|kg)\b", "$1kg", RegexOptions.IgnoreCase);
        return text;
    }

    /// <summary>مفتاح مقارنة مجرّد من كل شيء عدا الحروف والأرقام.</summary>
    public static string ComparisonKey(string? input) =>
        Regex.Replace(Normalize(input), @"[^\p{L}\p{N}]", "");
}
