using YelgLens.Intake.DAL.Maps;

namespace YelgLens.Intake.DAL.Data;

public static class MappingsHelper
{
    public static IEnumerable<IEntityMap> GetMainMappings() => new IEntityMap[]
    {
        new UserMap(),
        new RoleMap(),
        new PermissionMap(),
        new RolePermissionMap(),
        new IdentityJoinsMap(),
        new RefreshTokenMap(),

        new IntakeDocumentMap(),
        new IntakeLineMap(),
        new IntakePageMap(),
        new IntakeIssueMap(),

        new BranchMappingMap(),
        new ClientMap(),
        new ClientUserMap(),
        new OdooConnectionMap(),
        new OdooCustomerMap()
    };
}
