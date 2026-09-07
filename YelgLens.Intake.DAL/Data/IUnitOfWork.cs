using System.Data;

namespace YelgLens.Intake.DAL.Data;

public interface IUnitOfWork<TContext> : IDisposable where TContext : MainDbContext
{
    ITransaction BeginTransaction(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted);

    void Add<T>(T obj) where T : class;
    void Update<T>(T obj) where T : class;
    void Remove<T>(T obj) where T : class;
    void Attach<T>(T obj) where T : class;

    IQueryable<T> Query<T>() where T : class;

    void Commit();
    Task CommitAsync();
}
