using Microsoft.EntityFrameworkCore;
using YelgLens.Intake.Model.Auth;

namespace YelgLens.Intake.DAL.Maps;

public sealed class UserMap : IEntityMap
{
    public void Visit(ModelBuilder builder)
    {
        var e = builder.Entity<User>();
        e.ToTable("IdentityUsers");
        e.HasIndex(u => u.Email).IsUnique();
        e.Property(u => u.FirstName).HasMaxLength(100);
        e.Property(u => u.LastName).HasMaxLength(100);
        e.Property(u => u.BranchCode).HasMaxLength(100);

        e.HasMany(u => u.UserRoles).WithOne(r => r.User)
         .HasForeignKey(r => r.UserId).IsRequired();

        e.HasMany(u => u.RefreshTokens).WithOne(t => t.User)
         .HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class RoleMap : IEntityMap
{
    public void Visit(ModelBuilder builder)
    {
        var e = builder.Entity<Role>();
        e.ToTable("IdentityRoles");
        e.Property(r => r.Description).HasMaxLength(255);

        e.HasMany(r => r.UserRoles).WithOne(ur => ur.Role)
         .HasForeignKey(ur => ur.RoleId).IsRequired();
    }
}

public sealed class PermissionMap : IEntityMap
{
    public void Visit(ModelBuilder builder)
    {
        var e = builder.Entity<Permission>();
        e.HasIndex(p => p.Code).IsUnique();
        e.Property(p => p.Code).HasMaxLength(100).IsRequired();
        e.Property(p => p.Name).HasMaxLength(100).IsRequired();
        e.Property(p => p.Description).HasMaxLength(255);
    }
}

public sealed class RolePermissionMap : IEntityMap
{
    public void Visit(ModelBuilder builder)
    {
        var e = builder.Entity<RolePermission>();
        e.HasKey(rp => new { rp.RoleId, rp.PermissionId });

        e.HasOne(rp => rp.Role).WithMany(r => r.RolePermissions)
         .HasForeignKey(rp => rp.RoleId).OnDelete(DeleteBehavior.Cascade);

        e.HasOne(rp => rp.Permission).WithMany(p => p.RolePermissions)
         .HasForeignKey(rp => rp.PermissionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class IdentityJoinsMap : IEntityMap
{
    // اصطلاح Identity يسمّي الجداول AspNet*، وسمة [Table] لا تغلبه.
    // تُثبَّت هنا لتوافق تسمية بقية مشاريعنا.
    public void Visit(ModelBuilder builder)
    {
        builder.Entity<UserRole>().ToTable("IdentityUserRoles");
        builder.Entity<UserClaim>().ToTable("IdentityUserClaims");
        builder.Entity<UserLogin>().ToTable("IdentityUserLogins");
        builder.Entity<RoleClaim>().ToTable("IdentityRoleClaims");
        builder.Entity<UserToken>().ToTable("IdentityUserTokens");
    }
}

public sealed class RefreshTokenMap : IEntityMap
{
    public void Visit(ModelBuilder builder)
    {
        var e = builder.Entity<RefreshToken>();
        e.HasIndex(t => t.Token).IsUnique();
        e.Property(t => t.Token).HasMaxLength(200).IsRequired();
        e.Property(t => t.RemoteIpAddress).HasMaxLength(64);
    }
}
