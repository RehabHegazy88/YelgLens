using System.Data;
using Microsoft.EntityFrameworkCore;

namespace YelgLens.Intake.DAL.Data;

public sealed class EFUnitOfWork<TContext> : IUnitOfWork<TContext> where TContext : MainDbContext
{
    private readonly TContext _context;

    public EFUnitOfWork(TContext context) => _context = context;

    public ITransaction BeginTransaction(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted) =>
        new DbTransaction(_context.Database.BeginTransaction(isolationLevel));

    public void Add<T>(T obj) where T : class => _context.Set<T>().Add(obj);

    public void Update<T>(T obj) where T : class => _context.Set<T>().Update(obj);

    public void Remove<T>(T obj) where T : class => _context.Set<T>().Remove(obj);

    public void Attach<T>(T obj) where T : class => _context.Set<T>().Attach(obj);

    public IQueryable<T> Query<T>() where T : class => _context.Set<T>();

    public void Commit() => _context.SaveChanges();

    public Task CommitAsync() => _context.SaveChangesAsync();

    public void Dispose() => _context.Dispose();
}
