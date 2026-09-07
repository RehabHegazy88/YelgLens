using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace YelgLens.Intake.Model.Auth;

/// <summary>
/// صلاحية دقيقة مستقلة عن الدور. الدور يجمع صلاحيات، والصلاحية هي ما
/// يُفحص عند الفعل — فتغيير سياسة لا يستلزم تغيير أدوار المستخدمين.
/// </summary>
[Table("Permissions")]
public class Permission
{
    [Key]
    public long Id { get; set; }

    [Required, StringLength(100)]
    public string Code { get; set; } = "";

    [Required, StringLength(100)]
    public string Name { get; set; } = "";

    [StringLength(255)]
    public string? Description { get; set; }

    public virtual ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}

/// <summary>رموز الصلاحيات المعروفة — تُقارن كنص واحد لا تُكتب متفرقة.</summary>
public static class PermissionCodes
{
    public const string DocumentUpload  = "document.upload";
    public const string DocumentReview  = "document.review";
    public const string DocumentApprove = "document.approve";
    public const string DocumentExport  = "document.export";
    public const string OdooPublish     = "odoo.publish";
    public const string UserManage      = "user.manage";
}
