using Admins.Domain.Users;
using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;

namespace Architecture.Tests;

/// <summary><c>admin.Users.UpdatedAt</c> is stamped by <see cref="UserUpdatedAtInterceptor"/> on every
/// Modified row at save time and left NULL on insert — the one seam every admin mutation path funnels through.</summary>
public sealed class UserUpdatedAtInterceptorTests : IDisposable
{
    private static readonly DateTime Created = new(2026, 8, 30, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = Created.AddHours(3);

    private readonly SqliteConnection _connection;
    private readonly FixedClock _clock = new(Created);

    public UserUpdatedAtInterceptorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
    }

    private ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new UserUpdatedAtInterceptor(_clock))
                .Options,
            FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Insert_leaves_UpdatedAt_null_and_update_stamps_clock(bool useAsync)
    {
        var admin = User.CreateScoped("scoped@viriyah.co.th", Created);
        await using (var insert = NewContext())
        {
            insert.Users.Add(admin);
            await insert.SaveChangesAsync();
        }

        await using (var read = NewContext())
            Assert.Null((await read.Users.SingleAsync(x => x.Id == admin.Id)).UpdatedAt);

        _clock.UtcNow = Later;
        await using (var update = NewContext())
        {
            var loaded = await update.Users.SingleAsync(x => x.Id == admin.Id);
            loaded.Suspend(actingAdminId: Guid.NewGuid());
            if (useAsync) await update.SaveChangesAsync(); else update.SaveChanges();
        }

        await using (var read = NewContext())
            Assert.Equal(Later, (await read.Users.SingleAsync(x => x.Id == admin.Id)).UpdatedAt);
    }

    [Fact]
    public async Task Unchanged_row_is_not_stamped()
    {
        var admin = User.CreateScoped("idle@viriyah.co.th", Created);
        await using (var insert = NewContext())
        {
            insert.Users.Add(admin);
            await insert.SaveChangesAsync();
        }

        _clock.UtcNow = Later;
        await using (var touch = NewContext())
        {
            _ = await touch.Users.SingleAsync(x => x.Id == admin.Id);
            await touch.SaveChangesAsync();
        }

        await using (var read = NewContext())
            Assert.Null((await read.Users.SingleAsync(x => x.Id == admin.Id)).UpdatedAt);
    }

    public void Dispose() => _connection.Dispose();

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; set; } = utcNow;
    }
}
