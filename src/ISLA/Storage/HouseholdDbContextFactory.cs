using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ISLA.Storage;

// Design-time tooling never starts the web application or opens deployment data.
public sealed class HouseholdDbContextFactory : IDesignTimeDbContextFactory<HouseholdDbContext>
{
    public HouseholdDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<HouseholdDbContext>()
        .UseSqlite("Data Source=isla-design.db").Options);
}
