using SkiaSharp;
using System.Globalization;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Enums;
using ZXing.SkiaSharp;

namespace YelgLens.Intake.BLL.Services.Extraction;

/// <summary>
/// مزوّد التعرّف الضوئي. مفصول عمداً عن باقي المعالجة، حتى يمكن تركيب
/// أي محرك لاحقاً (محلي أو خدمة) دون المساس ببقية المسار.
/// </summary>
public interface IOcrEngine
{
    bool IsAvailable { get; }

    /// <summary>وصفٌ للعرض: أي محرك، وهل يعمل، ولماذا لا يعمل.</summary>
    OcrStatus Status { get; }
    Task<IReadOnlyList<OcrLine>> ReadTableAsync(string imagePath, CancellationToken ct = default);

    /// <summary>
    /// كالسابقة، ومعها ما لم يُقرأ.
    ///
    /// وُضعت لأن عدد البنود وحده لا يقول إن شيئاً ضاع: جدولٌ بستة صفوف يخرج
    /// منه ثلاثة يبدو سليماً تماماً في الشاشة. أما صفٌّ حمل كميةً ولم يُقرأ
    /// له رمز فهو دليلٌ على بندٍ موجودٍ في الورقة وغائبٍ عندنا.
    /// </summary>
    Task<OcrTable> ReadTableWithGapsAsync(string imagePath, CancellationToken ct = default)
        => ReadTableAsync(imagePath, ct)
            .ContinueWith(t => new OcrTable(t.Result, 0), ct,
                TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

    /// <summary>
    /// خلايا ما فوق الجدول، بمواضعها.
    ///
    /// الرأس لا يُقرأ سطراً واحداً لأنه ليس سطوراً: هو حقولٌ متجاورة، عنوانٌ
    /// فوق قيمته أو بجوارها. والموضع هو ما يربط الاثنين، فيُعاد معه.
    /// </summary>
    Task<IReadOnlyList<OcrHeaderCell>> ReadHeaderAsync(string imagePath, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<OcrHeaderCell>>(Array.Empty<OcrHeaderCell>());
}

/// <summary>خليةٌ في رأس المستند: نصّها، وعمودها، وارتفاعها، وثقتها.</summary>
public sealed record OcrHeaderCell(string Text, int Column, double Top, double Confidence);

/// <summary>جدولٌ مقروء، ومعه عدد الصفوف التي بدت بنوداً ولم يُقرأ رمزها.</summary>
public sealed record OcrTable(IReadOnlyList<OcrLine> Lines, int RowsWithoutCode);

/// <summary>
/// حال محرك القراءة، ليُعرض في الشاشة لا في السجل وحده.
///
/// وُضع بعد أن ظلّ المحرك المحلي معطّلاً على الخادم أياماً دون أن يظهر ذلك:
/// كان يُعلن أنه متاح، ثم يسقط كل نداء، فيُحال كل مستند إلى السحابة بلا
/// إعلان. العطب الصامت يُدار بعرضه، لا بالبحث عنه في السجلات.
/// </summary>
public sealed record OcrStatus(string Name, bool Available, string? Reason, string? Detail)
{
    /// <summary>حال المحرك الاحتياطي، إن وُجد.</summary>
    public OcrStatus? Fallback { get; init; }
}

/// <summary>سطر مقروء ضوئياً — القيمة دائماً مصحوبة بدرجة ثقة.</summary>
public sealed record OcrLine(string Code, string Description, decimal? Quantity, double Confidence);

/// <summary>
/// المنفذ الافتراضي: لا يقرأ شيئاً ويعلن ذلك صراحةً.
/// وجوده يجعل المسار مكتملاً قبل اختيار المحرك، ويمنع المرور الصامت
/// حين لا يكون محرك مركّباً.
/// </summary>
public sealed class UnconfiguredOcrEngine : IOcrEngine
{
    public bool IsAvailable => false;

    public OcrStatus Status => new("لا محرك", false, "لم يُختر محرك قراءة في الإعدادات.", null);

    public Task<IReadOnlyList<OcrLine>> ReadTableAsync(string imagePath, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OcrLine>>(Array.Empty<OcrLine>());
}

/// <summary>
/// يعالج المستندات المصوّرة (نمط سعودي: أمر شراء ورقي يُصوَّر بالهاتف).
///
/// يُقرأ الباركود أولاً لأنه يقيني — فك الباركود يحمل تحققاً داخلياً، فإما
/// أن يُقرأ صحيحاً أو يفشل، ولا يعطي قراءة خاطئة صامتة. أما جدول الأصناف
/// فمطبوع نصاً ولا سبيل إليه إلا بالتعرّف الضوئي، وقيمه احتمالية بطبيعتها.
/// </summary>
public sealed class ImageExtractor
{
    private readonly IOcrEngine _ocr;

    public ImageExtractor(IOcrEngine ocr) => _ocr = ocr;

    public async Task<ExtractedOrder> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        var order = new ExtractedOrder
        {
            SourceFileName = Path.GetFileName(filePath),
            Kind = DocumentKind.PurchaseOrder,
            Strategy = ExtractionStrategy.ImageRecognition
        };

        ReadBarcode(order, filePath);
        await ReadHeaderAsync(order, filePath, ct);
        await ReadLinesAsync(order, filePath, ct);

        return order;
    }

    /// <summary>
    /// صيغ الباركود المعتمدة في مستندات الموردين — خطية صناعية.
    ///
    /// صيغ التجزئة (UPC/EAN) مستبعدة عمداً: رقم تحققها خانة واحدة، فتمرّ
    /// قراءات وهمية مولّدة من خطوط الجدول نفسها في الصور المصوّرة بالهاتف.
    /// استبعادها شرط لصحة المبدأ الذي يقوم عليه هذا الملف: الباركود إما
    /// يُقرأ صحيحاً أو يفشل، ولا يعطي قراءة خاطئة صامتة.
    /// </summary>
    private static readonly ZXing.BarcodeFormat[] DocumentFormats =
    {
        ZXing.BarcodeFormat.CODE_128,
        ZXing.BarcodeFormat.CODE_39,
        ZXing.BarcodeFormat.CODE_93,
        ZXing.BarcodeFormat.ITF,
        ZXing.BarcodeFormat.CODABAR
    };

    /// <summary>معاملات التكبير المجرّبة. التكبير يعين على باركود صغير، ولا يخلق معلومة غائبة.</summary>
    private static readonly int[] ScaleFactors = { 1, 2, 3 };

    /// <summary>أقل عدد قراءات متطابقة لقبول القيمة.</summary>
    private const int MinimumAgreeingReads = 2;

    /// <summary>سقف حجم الصورة بعد التكبير، منعاً لاستنزاف الذاكرة.</summary>
    private const long MaximumScaledPixels = 40_000_000;

    private static void ReadBarcode(ExtractedOrder order, string filePath)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(filePath);
            if (bitmap is null)
            {
                order.Issues.Add(new ValidationIssue
                {
                    Code = "IMAGE_UNREADABLE",
                    Message = "تعذّر فتح الصورة.",
                    Severity = IssueSeverity.Blocking
                });
                return;
            }

            var readings = CollectReadings(bitmap);

            // لا تُقبل قراءة إلا إذا تكررت عبر تكبيرات مستقلة. القراءة التي
            // تظهر عند معامل واحد دون غيره ليست قراءة، بل تخمين من ضوضاء الورق.
            var agreed = readings
                .Where(r => r.Value >= MinimumAgreeingReads)
                .OrderByDescending(r => r.Value)
                .ToList();

            if (agreed.Count == 1)
            {
                var text = agreed[0].Key;
                order.CustomerPoNumber = FieldValue<string>.Certain(
                    text, ValueOrigin.BarcodeDecode, text);
                return;
            }

            if (agreed.Count > 1)
            {
                order.Issues.Add(new ValidationIssue
                {
                    Code = "BARCODE_AMBIGUOUS",
                    Message = "أعطى الباركود قراءات متعارضة (" +
                              string.Join(" / ", agreed.Select(a => a.Key)) +
                              "). لا تُعتمد أي منها. أعد التصوير أقرب.",
                    Severity = IssueSeverity.Blocking
                });
                return;
            }

            order.Issues.Add(new ValidationIssue
            {
                Code = "BARCODE_NOT_FOUND",
                Message = "لم يُقرأ الباركود. الغالب أن دقة الصورة لا تكفي — أضيق " +
                          "شريطة في الباركود تحتاج ثلاث بكسلات على الأقل. صوّر رأس " +
                          "المستند وحده من مسافة قريبة وبإضاءة مستوية بلا ظل، أو " +
                          "أدخل رقم أمر الشراء يدوياً.",
                Severity = IssueSeverity.Blocking
            });
        }
        catch (Exception ex)
        {
            order.Issues.Add(new ValidationIssue
            {
                Code = "BARCODE_READ_FAILED",
                Message = $"فشل فك الباركود: {ex.Message}",
                Severity = IssueSeverity.Blocking
            });
        }
    }

    /// <summary>
    /// يفك الباركود بعدة معاملات تكبير ويعدّ القراءات المتطابقة.
    /// التكرار عبر تكبيرات مستقلة هو ما يفرّق القراءة الحقيقية عن الوهمية:
    /// الحقيقية ثابتة مهما تغيّر المعامل، والوهمية تتبدل معه.
    /// </summary>
    private static Dictionary<string, int> CollectReadings(SKBitmap bitmap)
    {
        var votes = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var factor in ScaleFactors)
        {
            SKBitmap? scaled = null;
            try
            {
                scaled = Rescale(bitmap, factor);
                if (scaled is null) continue;

                var result = CreateReader().Decode(scaled);
                if (result is null || string.IsNullOrWhiteSpace(result.Text)) continue;

                votes[result.Text] = votes.GetValueOrDefault(result.Text) + 1;
            }
            finally
            {
                if (!ReferenceEquals(scaled, bitmap)) scaled?.Dispose();
            }
        }

        return votes;
    }

    private static SKBitmap? Rescale(SKBitmap source, int factor)
    {
        if (factor == 1) return source;

        var width = source.Width * factor;
        var height = source.Height * factor;
        if ((long)width * height > MaximumScaledPixels) return null;

        return source.Resize(new SKImageInfo(width, height), SKFilterQuality.High);
    }

    private static BarcodeReader CreateReader() => new()
    {
        AutoRotate = true,
        Options = new ZXing.Common.DecodingOptions
        {
            TryHarder = true,
            TryInverted = true,
            PossibleFormats = DocumentFormats
        }
    };

    /// <summary>
    /// يقرأ رأس الورقة: رقم أمر الشراء والفرع والتاريخ.
    ///
    /// وبثقةٍ دون اليقين: هذه قراءةٌ ضوئية لا نصٌّ مضمون كما في PDF، فتُعرض
    /// في الشاشة مع مصدرها ليؤكّدها المراجع أو يصحّحها. وأن تصل الورقة
    /// بفرعٍ مقروءٍ يُراجَع خيرٌ من أن تصل فارغةً يُكتب كل حقلٍ فيها بيد.
    ///
    /// وما قُرئ من الباركود لا يُمسّ: الباركود يقينٌ والقراءة الضوئية ظنّ.
    /// </summary>
    private async Task ReadHeaderAsync(ExtractedOrder order, string filePath, CancellationToken ct)
    {
        if (!_ocr.IsAvailable) return;

        IReadOnlyList<OcrHeaderCell> cells;

        try { cells = await _ocr.ReadHeaderAsync(filePath, ct); }
        catch (Exception) { return; }   // الرأس تحسينٌ لا شرط: تعثّره لا يُفشل الاستخراج

        var header = new DocumentHeaderReader().Read(cells);
        if (header.IsEmpty) return;

        if (header.PoNumber is { } po && !order.CustomerPoNumber.HasValue)
            order.CustomerPoNumber = FieldValue<string>.Probable(po, 0.6, po);

        if (header.Branch is { } branch)
            order.BranchLabel = FieldValue<string>.Probable(branch, 0.6, branch);

        if (header.OrderDate is { } raw && TryDate(raw) is { } date)
            order.OrderDate = FieldValue<DateTime>.Probable(date, 0.6, raw);
    }

    /// <summary>
    /// التاريخ بصيغة يوم/شهر/سنة أولاً.
    ///
    /// أوراقهم مصريّة، و<c>08/09/2026</c> فيها ثامن سبتمبر لا الثامن من
    /// سبتمبر عند الأمريكيين. والخطأ هنا يزحزح تاريخ التسليم شهراً.
    /// </summary>
    private static DateTime? TryDate(string raw)
    {
        string[] formats = { "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "yyyy-MM-dd" };

        return DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var value) ? value : null;
    }

    private async Task ReadLinesAsync(ExtractedOrder order, string filePath, CancellationToken ct)
    {
        if (!_ocr.IsAvailable)
        {
            // السبب يُقال بعينه لا وصفاً عاماً: «غير مهيّأ» تصف ثلاث حالات
            // مختلفة — إعدادٌ مغلق، أو ملف لغةٍ ناقص، أو مكتبةٌ أصلية لا
            // تُحمَّل — ولكلٍّ علاجٌ آخر. والوصف العام يُحيل من يقرؤه إلى
            // البحث، وقد كلّف ذلك أياماً على الخادم.
            var status = _ocr.Status;

            order.Issues.Add(new ValidationIssue
            {
                Code = "OCR_NOT_CONFIGURED",
                Message = $"محرك القراءة «{status.Name}» لا يعمل، فلم تُقرأ بنود الجدول. "
                        + (status.Reason ?? "لم يُذكر سبب.")
                        + (status.Detail is { } detail ? $" ({detail})" : "")
                        + (status.Fallback is { } fallback && !fallback.Available
                            ? $" والبديل «{fallback.Name}» مقفول كذلك: {fallback.Reason}"
                            : ""),
                Severity = IssueSeverity.Blocking
            });
            return;
        }

        var table = await _ocr.ReadTableWithGapsAsync(filePath, ct);
        var lines = table.Lines;

        // صفوفٌ حملت كميةً ولم يُقرأ رمزها: بنودٌ في الورقة سقطت من القراءة.
        // تُقال مانعةً لا تنبيهاً: أوردرٌ ناقص بنداً يُقبل ويُسلَّم ناقصاً،
        // ولا يُكتشف إلا عند العميل. والمراجع يضيف البند أو يعلّم الملاحظة
        // محلولةً بعد أن يقابل الورقة بالشاشة.
        if (table.RowsWithoutCode > 0)
            order.Issues.Add(new ValidationIssue
            {
                Code = "LINES_MISSING",
                // «على الأقل» ليست تلطّفاً: الصفّ لا يُعدّ ناقصاً إلا إذا
                // قُرئت كميته، والصفّ الذي ضاع كله لا يُعدّ. فالرقم أرضيةٌ
                // لا حصر، والادعاء بأنه حصرٌ يطمئن المراجع في غير موضعه.
                Message = $"قُرئ {lines.Count} بند، وفي الجدول {table.RowsWithoutCode} صفٍّ على الأقل "
                        + "يحمل كميةً ولم يُقرأ رمزه. قابِل الورقة بالشاشة سطراً سطراً "
                        + "وأضف الناقص — قد يكون الناقص أكثر.",
                Severity = IssueSeverity.Blocking
            });

        // المحرك عمل ولم يجد شيئاً: حالة مختلفة عن غيابه، ولها سبب مختلف.
        // السكوت عنها يترك المستخدم أمام جدول فارغ بلا تفسير.
        if (lines.Count == 0)
        {
            order.Issues.Add(new ValidationIssue
            {
                Code = "NO_LINES_RECOGNIZED",
                Message = "عمل التعرّف الضوئي ولم يتعرّف على أي بند. الغالب أن " +
                          "الصورة صفحة كاملة مصوّرة من بعيد، فالحروف فيها أصغر " +
                          "من أن تُقرأ. صوّر جدول الأصناف وحده من مسافة قريبة، " +
                          "أو فعّل البديل السحابي فهو أقدر على الصور الصعبة.",
                Severity = IssueSeverity.Blocking
            });
            return;
        }

        var sequence = 0;

        foreach (var l in lines)
        {
            order.Lines.Add(new ExtractedLine
            {
                Sequence = ++sequence,
                Barcode = FieldValue<string>.Probable(l.Code, l.Confidence, l.Code),
                // الوصف الفارغ غياب لا قيمة، فلا يُحسب في ثقة البند.
                Description = string.IsNullOrWhiteSpace(l.Description)
                    ? FieldValue<string>.Missing()
                    : FieldValue<string>.Probable(l.Description, l.Confidence, l.Description),
                OrderedQty = l.Quantity.HasValue
                    ? FieldValue<decimal>.Probable(l.Quantity.Value, l.Confidence)
                    : FieldValue<decimal>.Missing()
            });
        }
    }
}
