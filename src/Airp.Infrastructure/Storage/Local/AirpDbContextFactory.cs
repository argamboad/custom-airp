using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Airp.Infrastructure.Storage.Local;

/// <summary>
/// Builds a context for <c>dotnet ef</c> at design time.
/// </summary>
/// <remarks>
/// Exists only so migrations can be generated from a library that has no host to ask for its
/// configuration. The connection string here is a scratch file and is never the one the
/// application runs against — migrations describe the schema, not where it lives.
/// </remarks>
internal sealed class AirpDbContextFactory : IDesignTimeDbContextFactory<AirpDbContext>
{
    /// <inheritdoc />
    public AirpDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AirpDbContext>()
            .UseSqlite("Data Source=airp-design-time.db")
            .Options;

        return new AirpDbContext(options);
    }
}
