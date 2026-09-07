using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.Model.Documents;

public sealed class ExtractedLine
{
    public int Sequence { get; set; }
    public FieldValue<string> Barcode { get; set; } = FieldValue<string>.Missing();
    public FieldValue<string> SupplierSku { get; set; } = FieldValue<string>.Missing();
    public FieldValue<string> Description { get; set; } = FieldValue<string>.Missing();
    public FieldValue<decimal> OrderedQty { get; set; } = FieldValue<decimal>.Missing();

    /// <summary>الكمية المستلمة — لا تظهر إلا في إذن التسليم.</summary>
    public FieldValue<decimal>? ReceivedQty { get; set; }

    /// <summary>سعر الوحدة كما ورد في المستند. قد يكون غائباً (حالة سعودي).</summary>
    public FieldValue<decimal>? DocumentUnitPrice { get; set; }

    public FieldValue<decimal>? VatPercent { get; set; }

    /// <summary>الفرق بين المطلوب والمستلم — يُحسب ولا يُستخرج.</summary>
    public decimal? ShortfallQty =>
        ReceivedQty is { HasValue: true } received && OrderedQty.HasValue
            ? OrderedQty.Value - received.Value
            : null;

    /// <summary>أدنى درجة ثقة بين حقول هذا السطر — تحدد إن كان يحتاج مراجعة.</summary>
    public double MinConfidence
    {
        get
        {
            // الحقل الغائب لا ثقة له تُقاس، فلا يُحسب. إقحامه بصفر كان يهبط
            // بثقة البند كله إلى الصفر ولو كانت بقية حقوله مقروءة يقيناً.
            var scores = new List<double>();
            if (Barcode.HasValue) scores.Add(Barcode.Confidence);
            if (OrderedQty.HasValue) scores.Add(OrderedQty.Confidence);
            if (Description.HasValue) scores.Add(Description.Confidence);
            if (ReceivedQty is { HasValue: true }) scores.Add(ReceivedQty.Confidence);
            if (DocumentUnitPrice is { HasValue: true }) scores.Add(DocumentUnitPrice.Confidence);
            return scores.Count == 0 ? 0.0 : scores.Min();
        }
    }
}
