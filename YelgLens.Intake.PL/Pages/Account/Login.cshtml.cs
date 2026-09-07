using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YelgLens.Intake.Model.Auth;

namespace YelgLens.Intake.PL.Pages.Account;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly SignInManager<User> _signIn;
    private readonly UserManager<User> _users;
    private readonly ILogger<LoginModel> _log;

    public LoginModel(SignInManager<User> signIn, UserManager<User> users, ILogger<LoginModel> log)
    {
        _signIn = signIn;
        _users = users;
        _log = log;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ErrorMessage { get; private set; }

    public class InputModel
    {
        [Required(ErrorMessage = "أدخل البريد الإلكتروني.")]
        [EmailAddress(ErrorMessage = "صيغة البريد غير صحيحة.")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "أدخل كلمة المرور.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = "";

        public bool RememberMe { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return LocalRedirect(returnUrl ?? await HomeAsync(User));

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        if (!ModelState.IsValid) return Page();

        var user = await _users.FindByEmailAsync(Input.Email);

        // رسالة واحدة للحالتين. تمييز "بريد غير موجود" عن "كلمة مرور خاطئة"
        // يكشف للمهاجم أي الحسابات قائمة.
        if (user is null || !user.IsActive || user.Deleted)
        {
            ErrorMessage = "بيانات الدخول غير صحيحة.";
            return Page();
        }

        var result = await _signIn.PasswordSignInAsync(
            user, Input.Password, Input.RememberMe, lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            _log.LogWarning("حساب موقوف مؤقتاً بعد محاولات فاشلة: {Email}", Input.Email);
            ErrorMessage = "أُوقف الحساب مؤقتاً بعد محاولات فاشلة متكررة. حاول بعد قليل.";
            return Page();
        }

        if (!result.Succeeded)
        {
            ErrorMessage = "بيانات الدخول غير صحيحة.";
            return Page();
        }

        user.LastLoginDate = DateTime.Now;
        await _users.UpdateAsync(user);

        _log.LogInformation("دخول ناجح: {Email}", Input.Email);

        // هوية هذا الطلب ما زالت مجهولة: الدخول يكتب كعكة الاستجابة ولا يغيّر
        // HttpContext.User قبل الطلب التالي. ففحص الصلاحية على User هنا يرجع
        // "لا يملك" دائماً، وتُبنى الهوية من المستخدم نفسه.
        var principal = await _signIn.CreateUserPrincipalAsync(user);
        return LocalRedirect(returnUrl ?? await HomeAsync(principal));
    }

    /// <summary>
    /// أول شاشة يراها الداخل هي عمله لا قائمةُ كل شيء.
    ///
    /// المراجع يفتح على طابور المستندات لأنه ما ينتظره، والمندوب يفتح على
    /// شاشة الرفع لأنه واقف أمام الورقة. وإرسال المراجع إلى شاشة رفعٍ لا
    /// يرفع منها يكلّفه ضغطةً في كل مرة يفتح فيها النظام.
    /// </summary>
    private async Task<string> HomeAsync(ClaimsPrincipal principal)
    {
        var authorization = HttpContext.RequestServices.GetRequiredService<IAuthorizationService>();
        var canReview = await authorization.AuthorizeAsync(principal, PermissionCodes.DocumentReview);

        return canReview.Succeeded ? "/Dashboard" : "/";
    }
}
