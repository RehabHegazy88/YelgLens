namespace YelgLens.Intake.DAL.Data;

public interface ITransaction : IDisposable
{
    void Commit();
    void Rollback();
}
