using YelgLens.Intake.BLL.IRepository.Core;
using YelgLens.Intake.BLL.Services.Mapping;
using YelgLens.Intake.BLL.ViewModel;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Enums;

namespace YelgLens.Intake.BLL.Services.Review;

public interface IIntakeReviewService
{
    /// <summary>يحفظ مستنداً من صفحة واحدة أو أكثر، مدموجةً في سجل واحد.</summary>
    Task<IntakeDocument> StoreAsync(
        IReadOnlyList<PageInput> pages, long uploadedByUserId, long? replacesDocumentId = null);
    Task<IntakeDocument?> FindDuplicateAsync(string sha256);
    Task<ReviewResult> SaveCorrectionsAsync(ReviewSubmission submission, long reviewerId);
    Task<ReviewResult> ApproveAsync(ReviewSubmission submission, long reviewerId);
    Task<ReviewResult> RejectAsync(ReviewSubmission submission, long reviewerId);

    /// <summary>يعيد مستنداً مبتوتاً فيه إلى المراجعة ليُعدَّل.</summary>
    Task<ReviewResult> ReopenAsync(long documentId, string? reason, long reviewerId);
}

/// <summary>
/// قواعد دورة المراجعة. موضعها هنا لا في الصفحة، لأن الترحيل إلى أودو سيمرّ
/// بالقواعد نفسها حين يأتي من تطبيق المندوب لا من المتصفح.
/// </summary>
public sealed class IntakeReviewService : IIntakeReviewService
{
    private readonly IIntakeDocumentRepository _documents;
    private readonly IBranchMappingService _mappings;
    private readonly ILogger<IntakeReviewService> _log;

    public IntakeReviewService(
        IIntakeDocumentRepository documents,
        IBranchMappingService mappings,
        ILogger<IntakeReviewService> log)
    {
        _documents = documents;
        _mappings = mappings;
        _log = log;
    }

    public Task<IntakeDocument?> FindDuplicateAsync(string sha256) =>
        _documents.FindByHashAsync(sha256);

    // ---------- الحفظ بعد الاستخراج ----------

    public async Task<IntakeDocument> StoreAsync(
        IReadOnlyList<PageInput> pages, long uploadedByUserId, long? replacesDocumentId = null)
    {
        if (pages.Count == 0) throw new ArgumentException("لا صفحات.", nameof(pages));

        var first = pages[0];

        var document = new IntakeDocument
        {
            SourceFileName = Trim(first.SourceFileName, 260),
            StoredFileName = first.StoredFileName,
            SourceSha256 = first.Sha256,
            Status = IntakeStatus.AwaitingReview,
            UploadedByUserId = uploadedByUserId,
            UploadedDate = DateTime.Now,
            ReplacesDocumentId = replacesDocumentId,

            // صاحب المستند يُثبَّت هنا، في أول لحظةٍ يُعرف فيها. تأجيلُه إلى
            // الترحيل يجعل الوصلة القائمة وقتَ الترحيل هي التي تقرّر، وهي قد
            // تكون بُدِّلت بعد الرفع.
            OwnerDatabase = NullIfBlank(_documents.OwnerDatabase)
        };

        // النوع وطريقة القراءة من أول صفحة عرفتهما: الصفحة الثانية من أمر شراء
        // غالباً جدول بنود عارٍ لا يُعرف منه نوع المستند.
        document.Kind = pages.Select(p => p.Order.Kind).FirstOrDefault(k => k != DocumentKind.Unknown);
        document.Strategy = pages.Select(p => p.Order.Strategy).FirstOrDefault(t => t != ExtractionStrategy.None);

        // حقول الرأس تؤخذ من أول صفحة تحملها. الصفحات التالية لا تحمل رأساً
        // عادةً، وأخذُ الفارغ منها يمحو ما قرأته الأولى.
        document.CustomerPoNumber = FirstWith(pages, o => o.CustomerPoNumber);
        document.CustomerName     = FirstWith(pages, o => o.CustomerName);
        document.BranchLabel      = FirstWith(pages, o => o.BranchLabel);
        document.OrderDate        = FirstDate(pages, o => o.OrderDate);
        document.DeliveryDate     = FirstDate(pages, o => o.DeliveryDate);

        var sequence = 0;
        var pageNumber = 0;

        foreach (var page in pages)
        {
            pageNumber++;

            foreach (var line in page.Order.Lines)
                document.Lines.Add(new IntakeLine
                {
                    Sequence = ++sequence,
                    Barcode = line.Barcode,
                    SupplierSku = line.SupplierSku,
                    Description = line.Description,
                    OrderedQty = line.OrderedQty,
                    ReceivedQty = line.ReceivedQty ?? FieldValue<decimal>.Missing(),
                    DocumentUnitPrice = line.DocumentUnitPrice ?? FieldValue<decimal>.Missing(),
                    VatPercent = line.VatPercent ?? FieldValue<decimal>.Missing()
                });

            document.Pages.Add(new IntakePage
            {
                PageNumber = pageNumber,
                SourceFileName = Trim(page.SourceFileName, 260),
                StoredFileName = page.StoredFileName,
                Sha256 = page.Sha256,
                Strategy = page.Order.Strategy,
                LineCount = page.Order.Lines.Count
            });
        }

        AddIssues(document, pages);

        await _documents.AddAsync(document);
        await _documents.SaveAsync();

        _log.LogInformation(
            "حُفظ المستند {Id} من {Pages} صفحة بـ {Lines} بند و{Issues} ملاحظة.",
            document.Id, document.Pages.Count, document.Lines.Count, document.Issues.Count);

        await _mappings.CaptureAsync(
            document.BranchLabel.HasValue ? document.BranchLabel.Value : null, document.Id);

        return document;
    }

    /// <summary>
    /// تُجمع ملاحظات الصفحات، وتُسقط منها ما عالجته صفحةٌ أخرى.
    ///
    /// الصفحة الثانية من أمر شراء لا تحمل رقمه، فتشكو غيابه. وشكواها لا معنى
    /// لها ما دامت الأولى قد حملته — وإبقاؤها يمنع الاعتماد بلا سبب.
    /// </summary>
    private static void AddIssues(IntakeDocument document, IReadOnlyList<PageInput> pages)
    {
        var solvedByOthers = new HashSet<string>(StringComparer.Ordinal);

        if (document.CustomerPoNumber.HasValue)
        {
            solvedByOthers.Add("PO_NUMBER_MISSING");
            solvedByOthers.Add("BARCODE_NOT_FOUND");
            solvedByOthers.Add("BARCODE_AMBIGUOUS");
        }

        if (document.Lines.Count > 0)
        {
            solvedByOthers.Add("NO_LINES_FOUND");
            solvedByOthers.Add("NO_LINES_RECOGNIZED");
            solvedByOthers.Add("OCR_NOT_CONFIGURED");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var page in pages)
            foreach (var issue in page.Order.Issues)
            {
                if (solvedByOthers.Contains(issue.Code)) continue;

                // الملاحظة نفسها من صفحتين تُذكر مرة.
                var key = issue.Code + "|" + issue.Message + "|" + issue.LineSequence;
                if (!seen.Add(key)) continue;

                document.Issues.Add(new IntakeIssue
                {
                    Code = Trim(issue.Code, 60),
                    Message = Trim(issue.Message, 600),
                    Severity = issue.Severity,
                    LineSequence = issue.LineSequence
                });
            }
    }

    private static FieldValue<string> FirstWith(
        IReadOnlyList<PageInput> pages, Func<ExtractedOrder, FieldValue<string>> pick)
    {
        foreach (var page in pages)
        {
            var value = pick(page.Order);
            if (value.HasValue) return value;
        }

        return FieldValue<string>.Missing();
    }

    private static FieldValue<DateTime> FirstDate(
        IReadOnlyList<PageInput> pages, Func<ExtractedOrder, FieldValue<DateTime>?> pick)
    {
        foreach (var page in pages)
        {
            var value = pick(page.Order);
            if (value is { HasValue: true }) return value;
        }

        return FieldValue<DateTime>.Missing();
    }

    // ---------- التصحيح والبتّ ----------

    public async Task<ReviewResult> SaveCorrectionsAsync(ReviewSubmission submission, long reviewerId)
    {
        var document = await Load(submission.DocumentId);
        if (document is null) return new ReviewResult(false, "المستند غير موجود.");
        if (document.IsDecided) return new ReviewResult(false, "بُتَّ في هذا المستند ولا يقبل التعديل.");

        Apply(document, submission);

        if (document.Status == IntakeStatus.AwaitingReview)
            document.Status = IntakeStatus.UnderReview;

        document.ReviewedByUserId = reviewerId;
        document.LastModifiedDate = DateTime.Now;

        await _documents.SaveAsync();
        return new ReviewResult(true, "حُفظت التعديلات.");
    }

    /// <summary>
    /// يعيد المستند إلى المراجعة بعد أن بُتَّ فيه.
    ///
    /// والمُرحَّل لا يُعاد فتحه: الأوردر صار موجوداً في أودو، وتعديل نسختنا
    /// بعده لا يغيّر شيئاً هناك ويجعل الاثنين يقولان قولين مختلفين عن ورقةٍ
    /// واحدة. تعديل المُرحَّل موضعه شاشة الأوردر، حيث يُكتب في أودو مباشرةً.
    ///
    /// والسبب إلزامي: قرارٌ يُنقض بلا سببٍ مكتوب يجعل الاعتماد إجراءً شكلياً.
    /// </summary>
    public async Task<ReviewResult> ReopenAsync(long documentId, string? reason, long reviewerId)
    {
        var document = await Load(documentId);
        if (document is null) return new ReviewResult(false, "المستند غير موجود.");

        if (document.Status == IntakeStatus.Published)
            return new ReviewResult(false,
                "هذا المستند مُرحَّل إلى أودو ولا يُعاد فتحه. "
                + "عدّل الأوردر نفسه من شاشة «أوردرات أودو» ليُكتب التعديل هناك.");

        if (!document.IsDecided)
            return new ReviewResult(false, "المستند مفتوح للمراجعة بالفعل.");

        var note = Trim(reason, 500);

        if (string.IsNullOrWhiteSpace(note))
            return new ReviewResult(false, "اكتب سبب إعادة الفتح — القرار لا يُنقض بلا سبب.");

        var was = document.Status;

        document.Status = IntakeStatus.UnderReview;
        document.ReviewedDate = null;
        document.ReviewedByUserId = reviewerId;
        document.ReopenedDate = DateTime.Now;
        document.ReopenedByUserId = reviewerId;
        document.ReopenNote = note;
        document.ReopenCount++;
        document.LastModifiedDate = DateTime.Now;

        await _documents.SaveAsync();

        _log.LogInformation("أُعيد فتح المستند {Id} من حالة {Was} بواسطة {User}: {Reason}",
            document.Id, was, reviewerId, note);

        return new ReviewResult(true,
            $"أُعيد المستند إلى المراجعة (كان {(was == IntakeStatus.Approved ? "معتمداً" : "مرفوضاً")}). "
            + "عدّل ما تحتاجه ثم اعتمده من جديد.");
    }

    public async Task<ReviewResult> ApproveAsync(ReviewSubmission submission, long reviewerId)
    {
        var document = await Load(submission.DocumentId);
        if (document is null) return new ReviewResult(false, "المستند غير موجود.");
        if (document.IsDecided) return new ReviewResult(false, "بُتَّ في هذا المستند من قبل.");

        Apply(document, submission);

        if (document.Lines.Count == 0)
            return new ReviewResult(false, "لا يُعتمد مستند بلا بنود.");

        // البند المانع يوقف الاعتماد. المراجع يعالجه أو يعلّمه محلولاً، ولا
        // يمرّ من فوقه — وإلا فقدت الملاحظة معناها.
        var blocking = document.Issues.Where(i => i.Severity == IssueSeverity.Blocking && !i.Resolved).ToList();
        if (blocking.Count > 0)
            return new ReviewResult(false,
                "لا يمكن الاعتماد وفيه ملاحظات مانعة لم تُعالَج: " +
                string.Join("، ", blocking.Select(i => i.Code)));

        if (!document.CustomerPoNumber.HasValue)
            return new ReviewResult(false, "رقم أمر الشراء إلزامي — هو رابط الأوردر بمستند العميل.");

        document.Status = IntakeStatus.Approved;
        document.ReviewedByUserId = reviewerId;
        document.ReviewedDate = DateTime.Now;
        document.ReviewNote = Trim(submission.Note, 500);
        document.LastModifiedDate = DateTime.Now;

        await _documents.SaveAsync();
        _log.LogInformation("اعتُمد المستند {Id} بواسطة {User}.", document.Id, reviewerId);

        return new ReviewResult(true, "اعتُمد المستند.");
    }

    public async Task<ReviewResult> RejectAsync(ReviewSubmission submission, long reviewerId)
    {
        var document = await Load(submission.DocumentId);
        if (document is null) return new ReviewResult(false, "المستند غير موجود.");
        if (document.IsDecided) return new ReviewResult(false, "بُتَّ في هذا المستند من قبل.");

        // الرفض بلا سبب لا يفيد المندوب في شيء — لن يعرف ما يصلحه.
        if (string.IsNullOrWhiteSpace(submission.Note))
            return new ReviewResult(false, "اذكر سبب الرفض ليعرف المندوب ما يعيده.");

        document.Status = IntakeStatus.Rejected;
        document.ReviewedByUserId = reviewerId;
        document.ReviewedDate = DateTime.Now;
        document.ReviewNote = Trim(submission.Note, 500);
        document.LastModifiedDate = DateTime.Now;

        await _documents.SaveAsync();
        _log.LogInformation("رُفض المستند {Id} بواسطة {User}.", document.Id, reviewerId);

        return new ReviewResult(true, "رُفض المستند.");
    }

    // ---------- تطبيق التصحيحات ----------

    private static void Apply(IntakeDocument document, ReviewSubmission submission)
    {
        if (!string.IsNullOrWhiteSpace(submission.CustomerPoNumber))
            document.CustomerPoNumber = Correct(document.CustomerPoNumber, submission.CustomerPoNumber.Trim());

        if (!string.IsNullOrWhiteSpace(submission.CustomerName))
            document.CustomerName = Correct(document.CustomerName, submission.CustomerName.Trim());

        if (!string.IsNullOrWhiteSpace(submission.BranchLabel))
            document.BranchLabel = Correct(document.BranchLabel, submission.BranchLabel.Trim());

        if (submission.OrderDate is { } orderDate)
            document.OrderDate = Correct(document.OrderDate, orderDate);

        if (submission.DeliveryDate is { } deliveryDate)
            document.DeliveryDate = Correct(document.DeliveryDate, deliveryDate);

        foreach (var correction in submission.Lines)
        {
            // معرّف صفر يعني بنداً أضافه المراجع بيده. الاستخراج يسقط بنوداً
            // أحياناً — صفٌّ باهت أو صورة قُصّت — فلا بد من باب لإضافته.
            if (correction.LineId == 0)
            {
                AddManualLine(document, correction);
                continue;
            }

            var line = document.Lines.FirstOrDefault(l => l.Id == correction.LineId);
            if (line is null) continue;

            if (correction.Remove)
            {
                document.Lines.Remove(line);
                continue;
            }

            if (correction.Barcode is not null)
                line.Barcode = Correct(line.Barcode, correction.Barcode.Trim());

            if (correction.Description is not null)
                line.Description = Correct(line.Description, correction.Description.Trim());

            if (correction.OrderedQty.HasValue)
                line.OrderedQty = Correct(line.OrderedQty, correction.OrderedQty.Value);

            if (correction.ReceivedQty.HasValue)
                line.ReceivedQty = Correct(line.ReceivedQty, correction.ReceivedQty.Value);

            line.LastModifiedDate = DateTime.Now;
        }

        Revalidate(document);

        // الترتيب يُعاد بعد الحذف حتى لا تبقى فجوات في أرقام البنود.
        var order = 1;
        foreach (var line in document.Lines.OrderBy(l => l.Sequence))
            line.Sequence = order++;
    }

    /// <summary>
    /// تُعلَّم الملاحظة محلولةً حين يزول سببها.
    ///
    /// بدون هذا كانت الملاحظة المانعة سجناً بلا مفتاح: المراجع يُدخل رقم أمر
    /// الشراء فتبقى ملاحظة غيابه قائمة، ويظل الاعتماد مرفوضاً إلى الأبد.
    /// السبب هو ما يُفحص، لا وجود الملاحظة.
    /// </summary>
    private static void Revalidate(IntakeDocument document)
    {
        var hasPo = document.CustomerPoNumber.HasValue;
        var hasLines = document.Lines.Count > 0;
        var allQuantities = hasLines && document.Lines.All(l => l.OrderedQty.HasValue);

        foreach (var issue in document.Issues.Where(i => !i.Resolved))
            issue.Resolved = issue.Code switch
            {
                "PO_NUMBER_MISSING"      => hasPo,
                "BARCODE_NOT_FOUND"      => hasPo,
                "BARCODE_AMBIGUOUS"      => hasPo,
                "QUANTITY_NOT_NUMERIC"   => allQuantities,
                "QUANTITY_INVALID"       => allQuantities,
                "NO_LINES_FOUND"         => hasLines,
                "NO_LINES_RECOGNIZED"    => hasLines,
                "OCR_NOT_CONFIGURED"     => hasLines,
                _ => false
            };
    }

    /// <summary>
    /// البند المضاف يدوياً كل حقوله مصدرها إنسان. الباركود شرط إضافته: هو
    /// مرساة البند ومفتاح مطابقته بمنتج في أودو، وبند بلا كود لا يُطابَق.
    /// </summary>
    private static void AddManualLine(IntakeDocument document, LineCorrection correction)
    {
        if (correction.Remove) return;
        if (string.IsNullOrWhiteSpace(correction.Barcode)) return;

        document.Lines.Add(new IntakeLine
        {
            Sequence = document.Lines.Count == 0 ? 1 : document.Lines.Max(l => l.Sequence) + 1,

            Barcode = FieldValue<string>.Certain(correction.Barcode.Trim(), ValueOrigin.HumanEntry),

            Description = string.IsNullOrWhiteSpace(correction.Description)
                ? FieldValue<string>.Missing()
                : FieldValue<string>.Certain(correction.Description.Trim(), ValueOrigin.HumanEntry),

            OrderedQty = correction.OrderedQty.HasValue
                ? FieldValue<decimal>.Certain(correction.OrderedQty.Value, ValueOrigin.HumanEntry)
                : FieldValue<decimal>.Missing(),

            ReceivedQty = correction.ReceivedQty.HasValue
                ? FieldValue<decimal>.Certain(correction.ReceivedQty.Value, ValueOrigin.HumanEntry)
                : FieldValue<decimal>.Missing(),

            SupplierSku = FieldValue<string>.Missing(),
            DocumentUnitPrice = FieldValue<decimal>.Missing(),
            VatPercent = FieldValue<decimal>.Missing()
        });
    }

    /// <summary>
    /// القيمة المصحَّحة يقينية ومصدرها إنسان، ويُحفظ فيها ما كان قبلها نصاً
    /// خاماً. هكذا يبقى أثر ما قرأه النظام وما غيّره المراجع في السجل نفسه.
    /// </summary>
    private static FieldValue<T> Correct<T>(FieldValue<T> current, T corrected)
    {
        if (current.HasValue && Equals(current.Value, corrected)) return current;

        var previous = current.RawText ?? current.Value?.ToString();
        return FieldValue<T>.Certain(corrected, ValueOrigin.HumanEntry, previous);
    }

    private Task<IntakeDocument?> Load(long id) => _documents.GetWithDetailsAsync(id);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Trim(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];
}
