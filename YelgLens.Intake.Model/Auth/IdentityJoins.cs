using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;

namespace YelgLens.Intake.Model.Auth;

[Table("IdentityUserRoles")]
public class UserRole : IdentityUserRole<long>
{
    public virtual User User { get; set; } = null!;
    public virtual Role Role { get; set; } = null!;
}

[Table("IdentityUserClaims")]
public class UserClaim : IdentityUserClaim<long> { }

[Table("IdentityUserLogins")]
public class UserLogin : IdentityUserLogin<long> { }

[Table("IdentityRoleClaims")]
public class RoleClaim : IdentityRoleClaim<long> { }

[Table("IdentityUserTokens")]
public class UserToken : IdentityUserToken<long> { }
