using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YelgLens.Intake.BLL.IRepository.Core;
using YelgLens.Intake.BLL.Services.Odoo;
using YelgLens.Intake.BLL.ViewModel;
using YelgLens.Intake.Model.Auth;

namespace YelgLens.Intake.PL.Pages.Quotations;

[Authorize(Policy = PermissionCodes.DocumentReview)]
public class IndexModel : PageModel
{
    private readonly IOdooQuotationReader _quotations;
    private readonly IIntakeDocumentRepository _documents;

    public IndexModel(IOdooQuotationReader quotations, IIntakeDocumentRepository documents)
    {
        _quotations = quotations;
        _documents = documents;
    }

    public PagedResult<OdooQuotation> Result { get; private set; } =
        new(Array.Empty<OdooQuotation>(), 0, 1, 25);

    public bool IsAvailable { get; private set; }

    public string Database { get; private set; } = "";

    public string? Error { get; private set; }

    /// <summary>
    /// المستند الذي وُلِّد عنه كل أوردر، إن كان من عندنا.
    ///
    /// أودو لا يعرف مستنداتنا، ونحن نعرف أي أوردر أنشأناه. فيُوصل الطرفان هنا
    /// ليُفتح المستند من الأوردر ويُقارن بأصله — وهو الغرض من الشاشة كلها.
    /// </summary>
    public Dictionary<long, long> DocumentByOrderId { get; private set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true, Name = "state")]
    public string? State { get; set; }

    /// <summary>عميلٌ بعينه — يأتي من دليل الفروع لعرض أوردرات فرعٍ واحد.</summary>
    [BindProperty(SupportsGet = true, Name = "partner")]
    public long? PartnerId { get; set; }

    /// <summary>اسم العميل المرشَّح به، ليُعرض في الشاشة بدل رقمه.</summary>
    public string? PartnerName { get; private set; }

    [BindProperty(SupportsGet = true, Name = "from")]
    public DateTime? From { get; set; }

    [BindProperty(SupportsGet = true, Name = "to")]
    public DateTime? To { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true, Name = "size")]
    public int PageSize { get; set; } = 25;

    public static readonly int[] PageSizes = { 25, 50, 100 };

    /// <summary>حالات أودو المعروضة في المرشِّح — بنصّها الذي يفهمه أودو.</summary>
    public static readonly (string Value, string Label)[] States =
    {
        ("", "كل الحالات"),
        ("draft", "عرض سعر"),
        ("sent", "عرض مُرسَل"),
        ("sale", "أمر بيع مؤكَّد"),
        ("done", "مُقفَل"),
        ("cancel", "ملغى")
    };

    public bool HasFilter =>
        !string.IsNullOrWhiteSpace(Q) || !string.IsNullOrWhiteSpace(State)
        || From is not null || To is not null || PartnerId is not null;

    public async Task OnGetAsync(CancellationToken ct)
    {
        IsAvailable = _quotations.IsAvailable;
        Database = _quotations.Database;

        if (!IsAvailable)
        {
            Error = "الاتصال بأودو غير مضبوط، فلا يمكن الاستعلام عن الأوردرات.";
            return;
        }

        if (!PageSizes.Contains(PageSize)) PageSize = PageSizes[0];
        if (PageNumber < 1) PageNumber = 1;

        try
        {
            Result = await _quotations.SearchAsync(new QuotationQuery
            {
                Text = Q,
                State = string.IsNullOrWhiteSpace(State) ? null : State,
                From = From,
                To = To,
                PartnerId = PartnerId,
                Page = PageNumber,
                PageSize = PageSize
            }, ct);

            // اسم العميل يُقرأ من أول نتيجة: الترشيح برقمٍ لا يُقرأ، والشاشة
            // يجب أن تقول أي فرعٍ تعرض.
            if (PartnerId is not null && Result.Items.Count > 0)
                PartnerName = Result.Items[0].Customer;

            // الوصل يُبنى من عندنا لا من أودو: أرقام الأوردرات المعروضة تُبحث
            // في مستنداتنا دفعةً واحدة، لا استعلامٌ لكل صف.
            var ids = Result.Items.Select(q => q.Id).ToList();

            // وتُقيَّد بالقاعدة: رقم الأوردر يتكرر بين القواعد، فوصلٌ بلا
            // قيدٍ يربط أوردراً حيّاً بمستندٍ من الاختبار.
            var database = _quotations.Database;

            DocumentByOrderId = _documents.Get()
                .Where(d => d.OdooOrderId != null && ids.Contains(d.OdooOrderId!.Value)
                            && (d.OdooDatabase == null || d.OdooDatabase == database))
                .ToDictionary(d => d.OdooOrderId!.Value, d => d.Id);
        }
        catch (OdooException ex)
        {
            // خادمهم يتقلّب، والشاشة تقول ما جرى بدل أن تُعرض فارغةً كأن لا
            // أوردرات هناك.
            Error = ex.Message;
        }
    }

    public Dictionary<string, string?> Link(int? page = null, string? state = null, bool keepState = true)
    {
        var route = new Dictionary<string, string?>();

        var effectiveState = keepState ? state ?? State : state;
        if (!string.IsNullOrWhiteSpace(effectiveState)) route["state"] = effectiveState;

        if (!string.IsNullOrWhiteSpace(Q)) route["q"] = Q;
        if (From is not null) route["from"] = From.Value.ToString("yyyy-MM-dd");
        if (To is not null) route["to"] = To.Value.ToString("yyyy-MM-dd");
        if (PartnerId is { } partner) route["partner"] = partner.ToString();
        if (PageSize != PageSizes[0]) route["size"] = PageSize.ToString();

        var effectivePage = page ?? 1;
        if (effectivePage > 1) route["p"] = effectivePage.ToString();

        return route;
    }
}
