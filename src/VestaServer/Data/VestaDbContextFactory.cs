using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VestaServer.Data;

/// <summary>
/// Design-time factory for EF Core migrations.
/// Used by "dotnet ef migrations add" without needing a running host.
/// </summary>
public sealed class VestaDbContextFactory : IDesignTimeDbContextFactory<VestaDbContext>
{
    public VestaDbContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<VestaDbContext> optionsBuilder = new();
        optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=vesta;Username=postgres;Password=vesta");
        return new VestaDbContext(optionsBuilder.Options);
    }
}
