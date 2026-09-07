namespace YelgLens.Intake.BLL.Services.Validation;

/// <summary>
/// يتحقق من رقم التحقق في الباركود.
///
/// الباركود ليس رقماً اعتباطياً: خانته الأخيرة محسوبة من التي قبلها، فخطأ خانةٍ
/// واحدة في القراءة الضوئية يكسر الحساب ويُكتشف فوراً — بلا سؤال أودو وبلا
/// انتظار رفض ملف الاستيراد.
///
/// وهذا يكشف أغلب أخطاء القراءة لا كلها: تبديل خانتين متجاورتين قد يبقي الحساب
/// صحيحاً. فهو دليلٌ على الخطأ حين يفشل، لا شهادةٌ بالصحة حين ينجح.
/// </summary>
public static class BarcodeCheckDigit
{
    /// <summary>نتيجة الفحص: صحيح، أو خاطئ، أو لا ينطبق على هذا الطول.</summary>
    public enum Result { Valid, Invalid, NotApplicable }

    /// <summary>
    /// الأطوال المعيارية: <c>UPC-A</c> اثنتا عشرة خانة — وهو ما يستعمله موردو
    /// هذه المستندات — و<c>EAN-13</c> ثلاث عشرة، و<c>EAN-8</c> ثمان.
    /// </summary>
    public static Result Check(string? barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return Result.NotApplicable;

        var code = barcode.Trim();
        if (!code.All(char.IsDigit)) return Result.NotApplicable;
        if (code.Length is not (8 or 12 or 13 or 14)) return Result.NotApplicable;

        // الأوزان تُحسب من اليمين: الخانة التي تسبق رقم التحقق وزنها ثلاثة،
        // والتي قبلها واحد، وهكذا بالتناوب. وهذه القاعدة واحدة لكل الأطوال،
        // بخلاف العدّ من اليسار الذي يختلف بين الزوجي والفردي.
        var digits = code[..^1];
        var expected = code[^1] - '0';

        var sum = 0;
        var weight = 3;

        for (var i = digits.Length - 1; i >= 0; i--)
        {
            sum += (digits[i] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }

        var computed = (10 - sum % 10) % 10;
        return computed == expected ? Result.Valid : Result.Invalid;
    }

    public static bool IsInvalid(string? barcode) => Check(barcode) == Result.Invalid;

    /// <summary>
    /// يصحّح خانة التحقق ويعيد الرمز كما كان ينبغي أن يكون.
    ///
    /// حين تُقرأ خانةٌ خطأً يكسر الحساب، وأرجح الاحتمالات أن الخطأ في خانة
    /// التحقق نفسها — فهي آخر ما يُطبع وأصغره. فيُجرَّب تصحيحها أولاً قبل
    /// عرض بدائل أبعد.
    /// </summary>
    public static string? WithCorrectedCheckDigit(string? barcode)
    {
        if (Check(barcode) != Result.Invalid) return null;

        var code = barcode!.Trim();
        var digits = code[..^1];

        var sum = 0;
        var weight = 3;

        for (var i = digits.Length - 1; i >= 0; i--)
        {
            sum += (digits[i] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }

        return digits + (char)('0' + (10 - sum % 10) % 10);
    }

    /// <summary>عدد الخانات المختلفة بين رمزين متساويي الطول.</summary>
    public static int Distance(string a, string b)
    {
        if (a.Length != b.Length) return int.MaxValue;

        var differences = 0;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) differences++;

        return differences;
    }
}
