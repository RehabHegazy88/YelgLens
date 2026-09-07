using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;

namespace YelgLens.Intake.Model.Auth;

[Table("IdentityUsers")]
public class User : IdentityUser<long>
{
    [StringLength(100)]
    public string? FirstName { get; set; }

    [StringLength(100)]
    public string? LastName { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();

    /// <summary>المندوب قد يُنسب إلى فرع أو منطقة — يُستعمل لاحقاً في تصفية مستنداته.</summary>
    [StringLength(100)]
    public string? BranchCode { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// العميل الذي يعمل عليه هذا المستخدم الآن.
    ///
    /// يُحفظ على المستخدم لا في الجلسة: من يفتح النظام غداً يجد نفسه حيث
    /// تركه، ولا يبدأ عمله على عميلٍ غير الذي يظنّ.
    /// </summary>
    public long? CurrentClientId { get; set; }

    public DateTime AddedDate { get; set; } = DateTime.Now;

    public DateTime? LastLoginDate { get; set; }

    public bool Deleted { get; set; }

    public DateTime? DeleteDate { get; set; }

    public virtual ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

    public virtual ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}
