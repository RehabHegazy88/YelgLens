using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.Model.Documents;

/// <summary>
/// قيمة مصحوبة بمصدرها ودرجة الثقة فيها.
/// درجة الثقة 1.0 تعني يقين (نص أو باركود)؛ وما دونها يعني أن الحقل قابل للمراجعة.
/// </summary>
public sealed class FieldValue<T>
{
    public T? Value { get; init; }
    public ValueOrigin Origin { get; init; }
    public double Confidence { get; init; }

    /// <summary>النص الخام كما ظهر في المستند قبل أي تطبيع — للمراجعة والتدقيق.</summary>
    public string? RawText { get; init; }

    /// <summary>
    /// هل حُمِّل هذا الحقل بقيمة؟ لا تكفي مقارنة القيمة بالعدم: المعامل العام
    /// غير المقيَّد لا يحمل قابلية العدم لأنواع القيمة، فـ decimal الغائبة
    /// تساوي صفراً لا عدماً، وكانت المقارنة ترجع "موجود" دائماً لكل حقل رقمي.
    /// </summary>
    public bool HasValue { get; init; }

    public static FieldValue<T> Certain(T value, ValueOrigin origin, string? raw = null) =>
        new() { Value = value, Origin = origin, Confidence = 1.0, RawText = raw, HasValue = true };

    public static FieldValue<T> Probable(T? value, double confidence, string? raw = null) =>
        new() { Value = value, Origin = ValueOrigin.OpticalRecognition, Confidence = confidence, RawText = raw, HasValue = value is not null };

    public static FieldValue<T> Missing() =>
        new() { Value = default, Origin = ValueOrigin.OpticalRecognition, Confidence = 0.0, HasValue = false };

    public override string ToString() => Value?.ToString() ?? "";
}
