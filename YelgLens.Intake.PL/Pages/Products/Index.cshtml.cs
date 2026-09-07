using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YelgLens.Intake.BLL.Services.Odoo;
using YelgLens.Intake.BLL.ViewModel;
using YelgLens.Intake.Model.Auth;

namespace YelgLens.Intake.PL.Pages.Products;

[Authorize(Policy = PermissionCodes.DocumentReview)]
public class IndexModel : PageModel
{
    private readonly IOdooProductReader _products;

    public IndexModel(IOdooProductReader products) => _products = products;

    public PagedResult<OdooProduct> Result { get; private set; } =
        new(Array.Empty<OdooProduct>(), 0, 1, PageSizes[0]);

    public List<OdooPricelist> Pricelists { get; private set; } = new();

    public OdooPricelist? SelectedPricelist { get; private set; }

    public bool IsAvailable { get; private set; }

    public string Database { get; private set; } = "";

    public string? Error { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    /// <summary>قائمة الأسعار التي تُعرض أسعارها بجانب سعر القائمة العامة.</summary>
    [BindProperty(SupportsGet = true, Name = "pl")]
    public long? PricelistId { get; set; }

    /// <summary>لا يُعرض إلا ما له سعرٌ في القائمة المختارة.</summary>
    [BindProperty(SupportsGet = true, Name = "priced")]
    public bool PricedOnly { get; set; }

    /// <summary>يشمل غير القابل للبيع — يُطلب حين يُبحث عن صنفٍ لا يظهر.</summary>
    [BindProperty(SupportsGet = true, Name = "all")]
    public bool IncludeNotSaleable { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true, Name = "size")]
    public int PageSize { get; set; } = 25;

    public static readonly int[] PageSizes = { 25, 50, 100 };

    public bool HasFilter =>
        !string.IsNullOrWhiteSpace(Q) || PricelistId is not null || PricedOnly || IncludeNotSaleable;

    public async Task OnGetAsync(CancellationToken ct)
    {
        IsAvailable = _products.IsAvailable;
        Database = _products.Database;

        if (!IsAvailable)
        {
            Error = "الاتصال بأودو غير مضبوط، فلا يمكن الاستعلام عن المنتجات.";
            return;
        }

        if (!PageSizes.Contains(PageSize)) PageSize = PageSizes[0];
        if (PageNumber < 1) PageNumber = 1;

        try
        {
            Pricelists = await _products.GetPricelistsAsync(ct);
            SelectedPricelist = Pricelists.FirstOrDefault(p => p.Id == PricelistId);

            // القصر على المسعَّر بلا قائمةٍ مختارة لا معنى له، فيُهمَل بدل أن
            // يُرجع فراغاً يُقرأ كأن لا منتجات.
            var priced = PricedOnly && PricelistId is not null;

            Result = await _products.SearchAsync(new ProductQuery
            {
                Text = Q,
                PricelistId = PricelistId,
                PricedOnly = priced,
                SaleableOnly = !IncludeNotSaleable,
                Page = PageNumber,
                PageSize = PageSize
            }, ct);
        }
        catch (OdooException ex)
        {
            Error = ex.Message;
        }
    }

    public Dictionary<string, string?> Link(int? page = null)
    {
        var route = new Dictionary<string, string?>();

        if (!string.IsNullOrWhiteSpace(Q)) route["q"] = Q;
        if (PricelistId is { } pricelist) route["pl"] = pricelist.ToString();
        if (PricedOnly) route["priced"] = "true";
        if (IncludeNotSaleable) route["all"] = "true";
        if (PageSize != PageSizes[0]) route["size"] = PageSize.ToString();

        var effectivePage = page ?? 1;
        if (effectivePage > 1) route["p"] = effectivePage.ToString();

        return route;
    }
}
