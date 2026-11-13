using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MeowGram.Core.Data;

/// <summary>Используется только инструментом dotnet-ef при создании миграций.</summary>
internal sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={AppPaths.Default().DatabasePath}")
            .Options;

        return new AppDbContext(options);
    }
}
