using Microsoft.EntityFrameworkCore;

namespace YelgLens.Intake.DAL.Maps;

/// <summary>تهيئة كيان واحد. تُجمع كلها في MappingsHelper ويزورها السياق.</summary>
public interface IEntityMap
{
    void Visit(ModelBuilder builder);
}
