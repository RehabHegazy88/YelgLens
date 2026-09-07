using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YelgLens.Intake.BLL.Services.Tenancy;

namespace YelgLens.Intake.PL.Pages.Settings;

/// <summary>
/// ينقل المستخدم بين عملائه من شريط الصفحة.
///
/// ولا صفحة له: يُرجع من حيث جاء. تبديل العميل لا ينقل المستخدم من شاشته —
/// من كان في قائمة المراجعة يبقى فيها ويراها بعيني العميل الآخر.
/// </summary>
[Authorize]
public class SwitchClientModel : PageModel
{
    private readonly IClientContext _clients;

    public SwitchClientModel(IClientContext clients) => _clients = clients;

    public IActionResult OnGet() => RedirectToPage("/Dashboard");

    public async Task<IActionResult> OnPostAsync(long clientId, CancellationToken ct)
    {
        await _clients.SwitchAsync(clientId, ct);

        // العودة إلى المُحيل لا إلى صفحةٍ ثابتة، وبشرط أن يكون من موقعنا:
        // عنوانٌ خارجي في هذا الحقل يصير تحويلاً مفتوحاً.
        var back = Request.Headers.Referer.ToString();

        return Url.IsLocalUrl(back) ? Redirect(back) : RedirectToPage("/Dashboard");
    }
}
