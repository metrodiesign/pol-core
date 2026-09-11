using System.Data;
using System.Data.Common;
using Platform.Application.Migration;

namespace BuildingBlocks.Infrastructure.Persistence.MigrationReadiness;

/// <summary>Cross-process maintenance writer lease backed by SQL Server session application locks.</summary>
public sealed class SqlMigrationMaintenanceLease(DbConnection connection)
{
    private readonly DbConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<SqlMigrationWriterLease> AcquireAsync(
        Guid runId,
        string writerId,
        CancellationToken cancellationToken)
    {
        if (runId == Guid.Empty)
            throw new ArgumentException("RunId is required.", nameof(runId));
        ArgumentException.ThrowIfNullOrWhiteSpace(writerId);
        if (_connection.State != ConnectionState.Open)
            await _connection.OpenAsync(cancellationToken);

        var resource = $"pol-core:migration:{_connection.Database}";
        var transaction = await _connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = @resource,
                    @LockMode = N'Exclusive',
                    @LockOwner = N'Transaction',
                    @LockTimeout = 0;
                SELECT @result;
                """;
            Add(command, "@resource", resource);
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            if (result < 0)
                throw new MigrationMaintenanceException("maintenance_writer_active", "Another SQL session owns the migration writer lease.");
            return new(_connection, transaction, runId, writerId.Trim(), resource);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

public sealed class SqlMigrationWriterLease : IAsyncDisposable
{
    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;
    private readonly string _resource;
    private bool _completed;

    internal SqlMigrationWriterLease(
        DbConnection connection,
        DbTransaction transaction,
        Guid runId,
        string writerId,
        string resource)
    {
        _connection = connection;
        _transaction = transaction;
        RunId = runId;
        WriterId = writerId;
        _resource = resource;
    }

    public Guid RunId { get; }
    public string WriterId { get; }

    internal DbTransaction Transaction => _transaction;

    internal async Task EnsureOwnedAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (_completed || !ReferenceEquals(connection, _connection) || _connection.State != ConnectionState.Open)
            throw new MigrationMaintenanceException("maintenance_lease_lost", "The migration writer lease is no longer valid.");
        try
        {
            await using var command = _connection.CreateCommand();
            command.Transaction = _transaction;
            command.CommandText = "SELECT APPLOCK_MODE(N'public', @resource, N'Transaction');";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@resource";
            parameter.Value = _resource;
            command.Parameters.Add(parameter);
            var mode = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(mode, "Exclusive", StringComparison.OrdinalIgnoreCase))
                throw new MigrationMaintenanceException("maintenance_lease_lost", "The SQL transaction no longer owns the migration writer lease.");
        }
        catch (MigrationMaintenanceException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException)
        {
            throw new MigrationMaintenanceException("maintenance_lease_lost", "The SQL transaction no longer owns the migration writer lease.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_completed)
            return;
        _completed = true;
        if (_connection.State != ConnectionState.Open)
            return;
        try
        {
            await _transaction.RollbackAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException)
        {
            // Closing the connection already abandoned the transaction and released the applock.
        }
    }

    internal async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (_completed)
            throw new MigrationMaintenanceException("maintenance_lease_lost", "The migration writer lease is no longer valid.");
        await _transaction.CommitAsync(cancellationToken);
        _completed = true;
    }

    internal async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (_completed)
            return;
        await _transaction.RollbackAsync(cancellationToken);
        _completed = true;
    }
}
