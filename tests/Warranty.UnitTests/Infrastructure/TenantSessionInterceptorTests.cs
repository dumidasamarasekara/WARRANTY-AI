using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Warranty.Infrastructure.Persistence;

namespace Warranty.UnitTests.Infrastructure;

/// <summary>Session settings the interceptor issues on open and close, against a recording connection.</summary>
public sealed class TenantSessionInterceptorTests
{
    private static readonly Guid Aurora = Guid.Parse("11111111-1111-7111-8111-111111111111");

    [Fact]
    public void Opening_sets_the_tenant_and_knowledge_namespace_from_the_tenant_context()
    {
        using var connection = new RecordingConnection();

        new TenantSessionInterceptor(new FakeTenantContext(Aurora)).ConnectionOpened(connection, null!);

        var executed = connection.Executed.ShouldHaveSingleItem();
        executed.Sql.ShouldBe(TenantSessionInterceptor.ApplySql);
        executed.Parameters["tenant"].ShouldBe(Aurora.ToString());
        executed.Parameters["ns"].ShouldBe("tenant-aurora");
    }

    [Fact]
    public async Task Opening_asynchronously_sets_the_tenant_and_knowledge_namespace()
    {
        await using var connection = new RecordingConnection();

        await new TenantSessionInterceptor(new FakeTenantContext(Aurora))
            .ConnectionOpenedAsync(connection, null!, TestContext.Current.CancellationToken);

        var executed = connection.Executed.ShouldHaveSingleItem();
        executed.Sql.ShouldBe(TenantSessionInterceptor.ApplySql);
        executed.Parameters["tenant"].ShouldBe(Aurora.ToString());
    }

    [Fact]
    public async Task Opening_without_a_tenant_context_throws_and_runs_nothing()
    {
        await using var connection = new RecordingConnection();
        var interceptor = new TenantSessionInterceptor(new FakeTenantContext(null));

        Should.Throw<InvalidOperationException>(() => interceptor.ConnectionOpened(connection, null!));
        await Should.ThrowAsync<InvalidOperationException>(
            () => interceptor.ConnectionOpenedAsync(connection, null!, TestContext.Current.CancellationToken));

        connection.Executed.ShouldBeEmpty();
    }

    [Fact]
    public void A_no_tenant_scope_connection_opens_with_cleared_settings_and_the_exemption_ends_on_dispose()
    {
        using var connection = new RecordingConnection();
        var interceptor = new TenantSessionInterceptor(new FakeTenantContext(null));

        using (NoTenantScope.Begin())
        {
            interceptor.ConnectionOpened(connection, null!);
        }

        connection.Executed.ShouldHaveSingleItem().Sql.ShouldBe(TenantSessionInterceptor.ResetSql);
        NoTenantScope.IsActive.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => interceptor.ConnectionOpened(connection, null!));
    }

    [Fact]
    public void A_no_tenant_scope_ignores_a_resolved_tenant()
    {
        using var connection = new RecordingConnection();

        using (NoTenantScope.Begin())
        {
            new TenantSessionInterceptor(new FakeTenantContext(Aurora)).ConnectionOpened(connection, null!);
        }

        connection.Executed.ShouldHaveSingleItem().Sql.ShouldBe(TenantSessionInterceptor.ResetSql);
    }

    [Fact]
    public async Task Closing_clears_both_settings()
    {
        await using var connection = new RecordingConnection();
        var interceptor = new TenantSessionInterceptor(new FakeTenantContext(Aurora));

        interceptor.ConnectionClosing(connection, null!, default);
        await interceptor.ConnectionClosingAsync(connection, null!, default);

        connection.Executed.Select(e => e.Sql).ShouldBe([TenantSessionInterceptor.ResetSql, TenantSessionInterceptor.ResetSql]);
    }

    [Fact]
    public async Task Closing_a_connection_that_is_not_open_or_cannot_reset_does_not_throw()
    {
        await using var closed = new RecordingConnection { CurrentState = ConnectionState.Closed };
        await using var broken = new RecordingConnection { FailExecution = true };
        var interceptor = new TenantSessionInterceptor(new FakeTenantContext(Aurora));

        interceptor.ConnectionClosing(closed, null!, default);
        interceptor.ConnectionClosing(broken, null!, default);
        await interceptor.ConnectionClosingAsync(broken, null!, default);

        closed.Executed.ShouldBeEmpty();
    }

    private sealed record ExecutedCommand(string Sql, IReadOnlyDictionary<string, object?> Parameters);

    private sealed class RecordingConnection : DbConnection
    {
        public List<ExecutedCommand> Executed { get; } = [];

        public ConnectionState CurrentState { get; set; } = ConnectionState.Open;

        public bool FailExecution { get; init; }

        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => "warranty";

        public override string DataSource => "test";

        public override string ServerVersion => "17";

        public override ConnectionState State => CurrentState;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close() => CurrentState = ConnectionState.Closed;

        public override void Open() => CurrentState = ConnectionState.Open;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => new RecordingCommand(this);
    }

    private sealed class RecordingCommand(RecordingConnection connection) : DbCommand
    {
        private readonly NpgsqlCommand _parameterHolder = new();

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection => _parameterHolder.Parameters;

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery()
        {
            if (connection.FailExecution)
            {
                throw new FakeDbException();
            }

            connection.Executed.Add(new ExecutedCommand(
                CommandText,
                Parameters.Cast<DbParameter>().ToDictionary(p => p.ParameterName, p => p.Value)));
            return -1;
        }

        public override object ExecuteScalar() => throw new NotSupportedException();

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => new NpgsqlParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _parameterHolder.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FakeDbException() : DbException("current transaction is aborted");
}
