using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YelgLens.Intake.Model.Auth;

namespace YelgLens.Intake.PL.Pages.Account;

public class LogoutModel : PageModel
{
    private readonly SignInManager<User> _signIn;

    public LogoutModel(SignInManager<User> signIn) => _signIn = signIn;

    // الخروج فعلٌ يغيّر الحالة، فلا يُنفَّذ بطلب GET — رابطٌ في بريد أو صورة
    // كان يكفي لإخراج المستخدم.
    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        await _signIn.SignOutAsync();
        return RedirectToPage("/Account/Login");
    }
}
