using Microsoft.EntityFrameworkCore.Storage;

namespace YelgLens.Intake.DAL.Data;

public sealed class DbTransaction : ITransaction
{
    private readonly IDbContextTransaction _transaction;

    public DbTransaction(IDbContextTransaction transaction) => _transaction = transaction;

    public void Commit() => _transaction.Commit();

    public void Rollback() => _transaction.Rollback();

    public void Dispose() => _transaction.Dispose();
}
