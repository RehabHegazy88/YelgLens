using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YelgLens.Intake.BLL.Services.Mapping;
using YelgLens.Intake.BLL.Services.Odoo;
using YelgLens.Intake.BLL.ViewModel;
using YelgLens.Intake.Model.Auth;
using YelgLens.Intake.Model.Mapping;

namespace YelgLens.Intake.PL.Pages.Branches;

/// <summary>
/// دليل الفروع: كل عملاء أودو في مكانٍ واحد، ومعهم حال ربطهم وعدد أوردراتهم.
///
/// الشاشة تجمع ثلاثة أطراف كانت متفرقة: كشف أودو (من يوجد هناك)، وجدول الربط
/// (من نعرف كيف نصله بمستنداتنا)، وأوامر البيع (ما وصله فعلاً). ورؤيتها معاً
/// هي ما يُظهر الفرع الموجود ولم يُربط، والفرع المربوط بلا أوردر واحد.
/// </summary>
[Authorize(Policy = PermissionCodes.DocumentReview)]
public class IndexModel : PageModel
{
    private readonly IOdooCustomerService _customers;
    private readonly IBranchMappingService _mappings;
    private readonly IOdooQuotationReader _quotations;

    public IndexModel(
        IOdooCustomerService customers,
        IBranchMappingService mappings,
        IOdooQuotationReader quotations)
    {
        _customers = customers;
        _mappings = mappings;
        _quotations = quotations;
    }

    public sealed record Row(OdooCustomer Customer, BranchMapping? Mapping, long PartnerId, int Orders);

    public PagedResult<Row> Result { get; private set; } = new(Array.Empty<Row>(), 0, 1, PageSizes[0]);

    public string Source { get; private set; } = "";

    /// <summary>من أين تُؤخذ الأسماء وكم فيها — يُعرض ليُفهم الفراغ.</summary>
    public CustomerSourceStatus? CustomerStatus { get; private set; }
    public int TotalCustomers { get; private set; }
    public int MappedCount { get; private set; }
    public List<string> Parents { get; private set; } = new();
    public string? CountError { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    /// <summary>الشركة الأم — لعرض سلسلةٍ بعينها: كل فروع طلبات مثلاً.</summary>
    [BindProperty(SupportsGet = true, Name = "parent")]
    public string? Parent { get; set; }

    /// <summary>الترشيح بحال الربط: <c>mapped</c> أو <c>unmapped</c>.</summary>
    [BindProperty(SupportsGet = true, Name = "link")]
    public string? Link { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true, Name = "size")]
    public int PageSize { get; set; } = 50;

    public static readonly int[] PageSizes = { 25, 50, 100 };

    public bool HasFilter =>
        !string.IsNullOrWhiteSpace(Q) || !string.IsNullOrWhiteSpace(Parent) || !string.IsNullOrWhiteSpace(Link);

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (!PageSizes.Contains(PageSize)) PageSize = PageSizes[1];
        if (PageNumber < 1) PageNumber = 1;

        Source = await _customers.ActiveSourceAsync();
        CustomerStatus = await _customers.StatusAsync();

        var all = await _customers.GetActiveAsync();
        TotalCustomers = all.Count;

        Parents = all.Where(c => c.ParentName is not null)
                     .Select(c => c.ParentName!)
                     .Distinct().OrderBy(n => n).ToList();

        // الربط يُقرأ مرةً ويُفهرس: استعلامٌ لكل صفٍّ على ثلاثمئة صف يجعل
        // الشاشة تنتظر بلا سبب.
        var mappings = await _mappings.GetAllAsync();
        var byCustomer = mappings.Where(m => m.IsComplete)
                                 .GroupBy(m => m.OdooCustomer!)
                                 .ToDictionary(g => g.Key, g => g.First());

        MappedCount = all.Count(c => byCustomer.ContainsKey(c.DisplayName));

        var filtered = all.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(Parent))
            filtered = filtered.Where(c => c.ParentName == Parent);

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var text = Q.Trim();
            filtered = filtered.Where(c =>
                c.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || c.StoreNumber?.ToString() == text);
        }

        filtered = Link switch
        {
            "mapped"   => filtered.Where(c => byCustomer.ContainsKey(c.DisplayName)),
            "unmapped" => filtered.Where(c => !byCustomer.ContainsKey(c.DisplayName)),
            _ => filtered
        };

        // السلاسل أولاً ثم الفروع المفردة: من يفتح الدليل يبحث عن طلبات
        // وكارفور، لا عن «عميل نقدي».
        var ordered = filtered
            .OrderBy(c => c.ParentName is null)
            .ThenBy(c => c.ParentName ?? "")
            .ThenBy(c => c.StoreNumber ?? int.MaxValue)
            .ThenBy(c => c.DisplayName)
            .ToList();

        var pageItems = ordered.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList();

        // عدّ الأوردرات يُطلب للصفحة المعروضة وحدها، وفشلُه لا يُفرغ الشاشة:
        // الدليل يظل نافعاً بلا عدّادات.
        var summary = new Dictionary<string, (long PartnerId, int Orders)>();
        if (_quotations.IsAvailable && pageItems.Count > 0)
        {
            try
            {
                summary = await _quotations.SummarizeByCustomerAsync(
                    pageItems.Select(c => c.DisplayName).ToList(), ct);
            }
            catch (OdooException ex)
            {
                CountError = $"تعذّر جلب عدد الأوردرات من أودو: {ex.Message}";
            }
        }

        var rows = pageItems.Select(c =>
        {
            summary.TryGetValue(c.DisplayName, out var s);
            return new Row(c, byCustomer.GetValueOrDefault(c.DisplayName), s.PartnerId, s.Orders);
        }).ToList();

        Result = new PagedResult<Row>(rows, ordered.Count, PageNumber, PageSize);
    }

    public Dictionary<string, string?> LinkTo(int? page = null, string? parent = null, bool keepParent = true)
    {
        var route = new Dictionary<string, string?>();

        var effectiveParent = keepParent ? parent ?? Parent : parent;
        if (!string.IsNullOrWhiteSpace(effectiveParent)) route["parent"] = effectiveParent;

        if (!string.IsNullOrWhiteSpace(Q)) route["q"] = Q;
        if (!string.IsNullOrWhiteSpace(Link)) route["link"] = Link;
        if (PageSize != PageSizes[1]) route["size"] = PageSize.ToString();

        var effectivePage = page ?? 1;
        if (effectivePage > 1) route["p"] = effectivePage.ToString();

        return route;
    }
}
