using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;

namespace YelgLens.Intake.Model.Auth;

[Table("IdentityRoles")]
public class Role : IdentityRole<long>
{
    public Role() { }

    public Role(string name) : base(name) { }

    [Required]
    public RoleType Type { get; set; }

    [StringLength(255)]
    public string? Description { get; set; }

    public virtual ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

    public virtual ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}
