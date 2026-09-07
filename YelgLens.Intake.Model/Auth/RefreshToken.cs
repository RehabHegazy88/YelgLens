using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using YelgLens.Intake.Model.Basic;

namespace YelgLens.Intake.Model.Auth;

/// <summary>
/// رمز تجديد الجلسة لتطبيق المندوب. الويب يستعمل الكوكي، أما التطبيق
/// فيحتاج رمزاً يُجدَّد دون إعادة إدخال كلمة المرور في الميدان.
/// </summary>
[Table("RefreshTokens")]
public class RefreshToken : BaseEntity<long>
{
    [Required, StringLength(200)]
    public string Token { get; set; } = "";

    public DateTime Expires { get; set; }

    public long UserId { get; set; }

    [StringLength(64)]
    public string? RemoteIpAddress { get; set; }

    public DateTime? RevokedDate { get; set; }

    [NotMapped]
    public bool Active => RevokedDate is null && DateTime.UtcNow <= Expires;

    public virtual User User { get; set; } = null!;
}
