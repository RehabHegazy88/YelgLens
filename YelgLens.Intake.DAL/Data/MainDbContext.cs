using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using YelgLens.Intake.Model.Auth;
using YelgLens.Intake.Model.Documents;
using YelgLens.Intake.Model.Mapping;

namespace YelgLens.Intake.DAL.Data;

public class MainDbContext
    : IdentityDbContext<User, Role, long, UserClaim, UserRole, UserLogin, RoleClaim, UserToken>
{
    public MainDbContext(DbContextOptions<MainDbContext> options) : base(options) { }

    public DbSet<Permission> Permissions { get; set; } = null!;
    public DbSet<RolePermission> RolePermissions { get; set; } = null!;
    public DbSet<RefreshToken> RefreshTokens { get; set; } = null!;

    public DbSet<IntakeDocument> IntakeDocuments { get; set; } = null!;
    public DbSet<IntakeLine> IntakeLines { get; set; } = null!;
    public DbSet<IntakeIssue> IntakeIssues { get; set; } = null!;
    public DbSet<IntakePage> IntakePages { get; set; } = null!;

    public DbSet<BranchMapping> BranchMappings { get; set; } = null!;
    public DbSet<OdooCustomer> OdooCustomers { get; set; } = null!;

    public DbSet<YelgLens.Intake.Model.Settings.OdooConnection> OdooConnections { get; set; } = null!;

    public DbSet<YelgLens.Intake.Model.Settings.Client> Clients { get; set; } = null!;
    public DbSet<YelgLens.Intake.Model.Settings.ClientUser> ClientUsers { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        foreach (var mapping in MappingsHelper.GetMainMappings())
            mapping.Visit(modelBuilder);
    }
}
