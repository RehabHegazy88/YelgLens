using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;
using Tesseract;

namespace YelgLens.Intake.BLL.Services.Extraction;

/// <summary>إعدادات التعرّف الضوئي. تُقرأ من قسم "Ocr" في ملف الإعدادات.</summary>
public sealed class OcrSettings
{
    /// <summary>المحرك المطلوب: none أو tesseract.</summary>
    public string Engine { get; set; } = "none";

    /// <summary>مجلد ملفات اللغة، نسبةً إلى مجلد التطبيق أو مساراً مطلقاً.</summary>
    public string TessDataPath { get; set; } = "tessdata";

    /// <summary>اللغات مفصولة بعلامة +، مثل eng أو eng+ara.</summary>
    public string Languages { get; set; } = "eng";

    /// <summary>معامل تكبير الصورة قبل القراءة.</summary>
    public int Upscale { get; set; } = 3;

    /// <summary>أقل ثقة تُقبل للكلمة الواحدة، من مئة.</summary>
    public double MinWordConfidence { get; set; } = 40;

    /// <summary>البديل السحابي الذي يُستدعى حين لا يخرج المحرك المحلي ببند.</summary>
    public ClaudeOcrSettings Claude { get; set; } = new();
}

/// <summary>
/// محرك تعرّف ضوئي محلي يعمل دون اتصال — لا تخرج مستندات الموردين من الجهاز.
///
/// لا يمرَّر المستند إلى المحرك كصورة واحدة. تحليل التخطيط الذي يجريه المحرك
/// بنفسه يفشل على هذه الجداول: عمود الكميات ضيق وملاصق لخط الجدول فيُقرأ
/// بقعةً واحدة معه ويُهمل. لذلك تُعزل الأعمدة أولاً ويُمرَّر كل عمود وحده،
/// ثم تُلَمّ الصفوف من إحداثياتها الرأسية — وهي الطريقة نفسها التي يقرأ بها
/// <see cref="PdfTableExtractor"/> جداول الـ PDF.
///
/// كل قيمة تخرج من هنا احتمالية بطبيعتها، ولذلك تُبنى بـ
/// <see cref="Domain.FieldValue{T}.Probable"/> لا بـ Certain.
/// </summary>
public sealed class TesseractOcrEngine : IOcrEngine
{
    private static readonly Regex BarcodeToken = new(@"^\d{12,14}$", RegexOptions.Compiled);
    /// <summary>
    /// الكمية: عددٌ صحيح أو بكسرٍ عشري.
    ///
    /// والكسر ليس ترفاً: أوراقهم تكتب الكمية «12.00» لا «12»، وقصرُ النمط على
    /// الصحيح كان يُسقطها كلها. والأسوأ أنه لم يكن يتركها فارغة فحسب — كان
    /// يلتقط أول رقمٍ آخر في السطر، فيقرأ «20» من «20 Sachet» بدل 24.00.
    /// كميةٌ خاطئة تمرّ، وكميةٌ غائبة تُرى.
    ///
    /// والفاصلة مرفوضة عمداً: «1,200» ألفٌ ومئتان لا واحدٌ وخُمسان، والخطأ
    /// فيها بمقدار ألف ضعف.
    /// </summary>
    private static readonly Regex QuantityToken =
        new(@"^\d{1,5}(\.\d{1,3})?$", RegexOptions.Compiled);

    /// <summary>ما يحمل كسراً عشرياً — وهو شكل الكمية في أوراقهم.</summary>
    private static readonly Regex DecimalToken =
        new(@"^\d{1,5}\.\d{1,3}$", RegexOptions.Compiled);
    /// <summary>ما هو أرقامٌ خالصة — يُستبعد من الوصف. والعشري منها كذلك.</summary>
    private static readonly Regex DigitsOnly = new(@"^[\d\s.]+$", RegexOptions.Compiled);

    private readonly OcrSettings _settings;
    private readonly ILogger<TesseractOcrEngine> _log;
    private readonly string _tessData;

    public TesseractOcrEngine(OcrSettings settings, ILogger<TesseractOcrEngine> log)
    {
        _settings = settings;
        _log = log;
        _tessData = Path.IsPathRooted(settings.TessDataPath)
            ? settings.TessDataPath
            : Path.Combine(AppContext.BaseDirectory, settings.TessDataPath);
    }

    /// <summary>
    /// المحرك متاح فقط إذا وُجدت ملفات كل لغة مطلوبة. الادعاء بالتوفر ثم
    /// الفشل عند القراءة يحوّل عطباً ظاهراً إلى عطب صامت.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            if (!string.Equals(_settings.Engine, "tesseract", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!Directory.Exists(_tessData)) return false;

            return Languages().All(lang =>
                File.Exists(Path.Combine(_tessData, $"{lang}.traineddata")));
        }
    }

    /// <summary>اللغات التي تعذّر العثور على ملفاتها — للتشخيص في الرسائل.</summary>
    public IReadOnlyList<string> MissingLanguages() =>
        Languages()
            .Where(l => !File.Exists(Path.Combine(_tessData, $"{l}.traineddata")))
            .ToList();

    private string[] Languages() =>
        _settings.Languages.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public Task<IReadOnlyList<OcrLine>> ReadTableAsync(string imagePath, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<OcrLine>>(() => ReadTable(imagePath, ct), ct);

    private IReadOnlyList<OcrLine> ReadTable(string imagePath, CancellationToken ct)
    {
        using var page = ImagePreprocessor.Prepare(imagePath, _settings.Upscale);
        _log.LogInformation("تجهيز {File}: {W}x{H}، {Columns} عمود.",
            Path.GetFileName(imagePath), page.Image.Width, page.Image.Height, page.Columns.Count);

        var cells = new List<Cell>();
        foreach (var column in page.Columns)
        {
            ct.ThrowIfCancellationRequested();
            cells.AddRange(ReadColumn(page.Image, column));
        }

        return AssembleRows(cells);
    }

    /// <summary>
    /// يقرأ عموداً واحداً مرتين عند اللزوم: قراءة حرة أولاً لمعرفة طبيعته،
    /// ثم قراءة مقيّدة بالأرقام إن تبيّن أنه عمود رقمي. التقييد يمنع الخلط
    /// المعروف بين الصفر والحرف O وبين الواحد والحرف l.
    /// </summary>
    private List<Cell> ReadColumn(SKBitmap source, ColumnBand column)
    {
        var free = Recognize(source, column, digitsOnly: false);
        if (free.Count == 0) return free;

        var numeric = free.Count(c => DigitsOnly.IsMatch(c.Text)) * 2 >= free.Count;
        if (!numeric) return free;

        var strict = Recognize(source, column, digitsOnly: true);
        return strict.Count >= free.Count ? strict : free;
    }

    private List<Cell> Recognize(SKBitmap source, ColumnBand column, bool digitsOnly)
    {
        var rect = SKRectI.Create(column.Left, 0, column.Width, source.Height);
        using var slice = new SKBitmap(column.Width, source.Height);
        if (!source.ExtractSubset(slice, rect)) return new List<Cell>();

        using var copy = slice.Copy();
        using var image = SKImage.FromBitmap(copy);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        using var engine = new TesseractEngine(_tessData, _settings.Languages, EngineMode.Default);
        // والنقطة معها، وإلا حُذفت الفاصلة العشرية من الرقم فصار «12.00»
        // «1200» — مئة ضعف. والقراءة الحرة كانت تقرؤها صحيحة، ثم تُطرح
        // لصالح القراءة «الأدق» فتُفسدها. خطأٌ بهذا الحجم يمرّ صامتاً: الرقم
        // سليم الشكل، ولا شيء في الشاشة يقول إنه أُضيف إليه صفران.
        if (digitsOnly) engine.SetVariable("tessedit_char_whitelist", "0123456789.");

        using var pix = Pix.LoadFromMemory(encoded.ToArray());

        // SingleBlock: العمود المعزول كتلة نص واحدة، فلا حاجة لتحليل تخطيط.
        using var result = engine.Process(pix, PageSegMode.SingleBlock);
        using var iterator = result.GetIterator();
        iterator.Begin();

        var words = new List<Word>();
        do
        {
            var text = iterator.GetText(PageIteratorLevel.Word)?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var box)) continue;

            words.Add(new Word(
                Text: Collapse(text),
                Confidence: Math.Clamp(iterator.GetConfidence(PageIteratorLevel.Word) / 100.0, 0, 1),
                Center: (box.Y1 + box.Y2) / 2.0));
        }
        while (iterator.Next(PageIteratorLevel.Word));

        return GroupWords(words, column.Left);
    }

    /// <summary>
    /// كلمات العمود الواحد المتقاربة رأسياً خليةٌ واحدة. التجميع يتم من
    /// الكلمات لا من السطور، لأن ثقة السطر التي يعلنها المحرك تعود صفراً
    /// أحياناً لنصٍّ مقروء صحيحاً، فتُسقط قراءةً سليمة.
    /// </summary>
    private static List<Cell> GroupWords(List<Word> words, int column)
    {
        if (words.Count == 0) return new List<Cell>();

        var tolerance = Tolerance(words.Select(w => w.Center));
        var groups = new List<List<Word>>();

        foreach (var word in words.OrderBy(w => w.Center))
        {
            var group = groups.FirstOrDefault(g =>
                Math.Abs(g.Average(w => w.Center) - word.Center) <= tolerance);

            if (group is null) groups.Add(new List<Word> { word });
            else group.Add(word);
        }

        return groups
            .Select(g => new Cell(
                Text: string.Join(" ", g.Select(w => w.Text)),
                Confidence: g.Min(w => w.Confidence),
                Center: g.Average(w => w.Center),
                Column: column))
            .ToList();
    }

    private static string Collapse(string text) =>
        Regex.Replace(text.Replace('\n', ' '), @"\s+", " ").Trim();

    /// <summary>
    /// تُلَمّ الخلايا في صفوف من تقارب مراكزها الرأسية. سعة التقارب تُشتق من
    /// تباعد أكثر الأعمدة سطوراً — لا تُثبَّت رقماً، لأن ارتفاع السطر يختلف
    /// باختلاف المستند ودقة التصوير.
    /// </summary>
    private List<OcrLine> AssembleRows(List<Cell> cells)
    {
        if (cells.Count == 0) return new List<OcrLine>();

        var tolerance = RowTolerance(cells);
        var rows = new List<List<Cell>>();

        foreach (var cell in cells.OrderBy(c => c.Center))
        {
            var row = rows.FirstOrDefault(r =>
                Math.Abs(r.Average(c => c.Center) - cell.Center) <= tolerance);

            if (row is null) rows.Add(new List<Cell> { cell });
            else row.Add(cell);
        }

        // أيّ عمودٍ هو عمود الكمية؟ يُقرَّر من المستند كله لا من صفٍّ واحد،
        // ثم تُؤخذ الكمية منه وحده.
        var quantityColumn = FindQuantityColumn(rows);

        var lines = new List<OcrLine>();

        foreach (var row in rows)
        {
            // الرمز والكمية يتحققان من شكلهما، فلا تُسقطهما ثقةٌ منخفضة وحدها.
            // أما الوصف فنصٌّ حر بلا تحقق بنيوي، والثقة حارسه الوحيد: النص
            // المشوّه أسوأ من غيابه، لأنه يُطبَّع ويُطابَق به فيورّث خطأه.
            var trusted = row
                .Where(c => c.Confidence * 100 >= _settings.MinWordConfidence)
                .ToList();

            var tokens = row.SelectMany(c => c.Text.Split(' ')).ToList();

            // الباركود هو مرساة الصف. الصف الذي لا يحمله رأسٌ أو تذييل لا بند.
            //
            // ويُطهَّر من النقاط قبل الفحص: السماح بالنقطة في القراءة الرقمية
            // — وهو لازمٌ للكسر العشري في الكمية — يجعل المحرك يدسّها أحياناً
            // داخل الباركود نفسه، فيصير «725.765711120» ولا يطابق شيئاً،
            // فيسقط البند كله لا كميته وحدها.
            var code = tokens.Select(Digits).FirstOrDefault(t => BarcodeToken.IsMatch(t));
            if (code is null) continue;

            // المكتوب بكسرٍ عشري يُقدَّم على الصحيح: عمود الكمية في أوراقهم
            // مُنسَّق بمنزلتين، والأرقام الصحيحة الأخرى في السطر جزءٌ من
            // الوصف غالباً — «20 Sachet» و«180g» و«285g».
            // الكمية من عمودها إن عُرف. وهذا هو الأصل: الصف يحمل أرقاماً
            // كثيرة — رقم السطر ورقم الأمر والوزن في الوصف — وأخذُ أولها
            // يأخذ رقم السطر ويسمّيه كمية. وقد قِيس ذلك على أوامر شرائهم:
            // عمود «LINE NUM» يُقرأ كمياتٍ ١ و٢ و٣ والكمية الحقيقية ٢٤.
            var quantity = quantityColumn is { } band
                ? Number(Clean(row.FirstOrDefault(c => c.Column == band).Text))
                : null;

            // وإلا فالتخمين من السطر كله، والمكتوب بكسرٍ عشري أولى: عمود
            // الكمية مُنسَّق بمنزلتين، وما عداه من الأرقام وصفٌ غالباً.
            if (quantity is null)
            {
                var candidates = tokens
                    .Select(t => t.Trim('.'))
                    .Where(t => Digits(t) != code && QuantityToken.IsMatch(t))
                    .ToList();

                quantity = Number(candidates.FirstOrDefault(DecimalToken.IsMatch))
                        ?? Number(candidates.FirstOrDefault());
            }

            var description = string.Join(" ",
                    trusted.Select(c => c.Text).Where(t => !DigitsOnly.IsMatch(t)))
                .Trim();

            lines.Add(new OcrLine(
                Code: code,
                Description: description,
                Quantity: quantity,
                Confidence: trusted.Count > 0
                    ? trusted.Min(c => c.Confidence)
                    : row.Max(c => c.Confidence)));
        }

        return lines;
    }

    /// <summary>
    /// عمود الكمية، إن أمكن تمييزه.
    ///
    /// يُستبعد عمود الباركود، ويُستبعد عمود ترقيم السطور — وهو يُعرف بأن
    /// قيمه متتاليةٌ صاعدة تبدأ من واحد. وما بقي عمودٌ رقميٌّ واحد فهو
    /// الكمية؛ وإن بقي أكثر من واحد فلا يُخمَّن، ويُترك الأمر للاستدلال من
    /// السطر — تخمينُ عمودٍ خطأً يعمّ كل البنود، والخطأ العام أسوأ.
    /// </summary>
    private static int? FindQuantityColumn(List<List<Cell>> rows)
    {
        var withCode = rows
            .Where(r => r.Any(c => BarcodeToken.IsMatch(Digits(c.Text))))
            .ToList();

        if (withCode.Count < 2) return null;

        var codeColumns = withCode
            .SelectMany(r => r.Where(c => BarcodeToken.IsMatch(Digits(c.Text))).Select(c => c.Column))
            .ToHashSet();

        var columns = withCode.SelectMany(r => r).Select(c => c.Column).Distinct().ToList();
        var candidates = new List<int>();

        foreach (var column in columns.Where(c => !codeColumns.Contains(c)))
        {
            var values = withCode
                .Select(r => Number(Clean(r.FirstOrDefault(c => c.Column == column).Text)))
                .ToList();

            // العمود يصلح كمية إن حمل رقماً في كل صفٍّ تقريباً.
            if (values.Count(v => v.HasValue) * 4 < values.Count * 3) continue;

            var numbers = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();

            // ترقيم السطور: ١، ٢، ٣… لا يتكرر ولا يتجاوز عدد الصفوف.
            var counter = numbers.Count > 1
                && numbers.SequenceEqual(numbers.OrderBy(v => v))
                && numbers.Distinct().Count() == numbers.Count
                && numbers.All(v => v == Math.Floor(v) && v >= 1 && v <= rows.Count + 1);

            if (counter) continue;

            candidates.Add(column);
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>نصّ الخلية مهيَّأً للتحليل رقماً.</summary>
    private static string? Clean(string? text) => text?.Trim().Trim('.');

    /// <summary>الرقم بلا نقاط — لمطابقة الباركود مهما دُسّت فيه.</summary>
    private static string Digits(string token) => token.Replace(".", "");

    /// <summary>يقرأ الرقم بفاصلةٍ عشرية إنجليزية أياً كانت لغة الجهاز.</summary>
    private static decimal? Number(string? token) =>
        token is not null && decimal.TryParse(token, NumberStyles.Number,
            CultureInfo.InvariantCulture, out var value) ? value : null;

    private static double RowTolerance(List<Cell> cells) =>
        Tolerance(cells.Select(c => c.Center));

    /// <summary>سعة التقارب تُشتق من وسيط التباعد، فتتبع مقاس المستند لا رقماً ثابتاً.</summary>
    private static double Tolerance(IEnumerable<double> centers)
    {
        var ordered = centers.OrderBy(v => v).ToList();
        if (ordered.Count < 2) return 20;

        var gaps = ordered.Zip(ordered.Skip(1), (a, b) => b - a)
            .Where(g => g > 1)
            .OrderBy(g => g)
            .ToList();

        if (gaps.Count == 0) return 20;

        return Math.Max(8, gaps[gaps.Count / 2] * 0.45);
    }

    private readonly record struct Word(string Text, double Confidence, double Center);
    /// <summary>خليةٌ مقروءة، ومعها العمود الذي جاءت منه.</summary>
    private readonly record struct Cell(string Text, double Confidence, double Center, int Column);
}
