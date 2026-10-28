using ISLA.Models;
using ISLA.Security;
using Microsoft.EntityFrameworkCore;

namespace ISLA.Storage;

public sealed class DatabaseInstallation
{
    public int Id { get; set; } = 1;
    public int SchemaVersion { get; set; } = 1;
    public bool Initialized { get; set; }
}

// Shared relational model. Provider migrations live separately under Storage/Migrations.
public sealed class HouseholdDbContext(DbContextOptions<HouseholdDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<DatabaseInstallation>().ToTable("Installation", table => table.HasCheckConstraint("CK_Installation_Id", "\"Id\" = 1"));
        model.Entity<DatabaseInstallation>().HasKey(x => x.Id);
        model.Entity<Recipe>().ToTable("Recipes", table => table.HasCheckConstraint("CK_Recipe_Servings", "\"Servings\" > 0"));
        model.Entity<Recipe>().HasKey(x => x.Id);
        model.Entity<Good>().Property<string>("NormalizedName").IsRequired();
        model.Entity<Good>().HasAlternateKey("NormalizedName");
        model.Entity<Recipe>().OwnsMany(x => x.Ingredients, ingredients =>
        {
            ingredients.ToTable("RecipeIngredients");
            ingredients.WithOwner().HasForeignKey("RecipeId");
            ingredients.Property<int>("Position").ValueGeneratedNever();
            ingredients.HasKey("RecipeId", "Position");
            ingredients.Property<string>("NormalizedName").IsRequired();
            ingredients.HasOne<Good>().WithMany().HasForeignKey("NormalizedName").HasPrincipalKey("NormalizedName").OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<Good>().ToTable("Goods");
        model.Entity<Good>().HasKey(x => x.Id);
        model.Entity<Good>().Property<string>("NormalizedName").IsRequired();
        model.Entity<Good>().HasAlternateKey("NormalizedName");
        model.Entity<PlannedMeal>().ToTable("Meals");
        model.Entity<PlannedMeal>().HasKey(x => x.Id);
        model.Entity<PlannedMeal>().HasOne<Recipe>().WithMany().HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<PlannedMeal>().HasOne<PlannedMeal>().WithMany().HasForeignKey(x => x.LeftoversFromMealId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ExtraShoppingItem>().ToTable("ExtraItems");
        model.Entity<ExtraShoppingItem>().HasKey(x => x.Id);
        model.Entity<ExtraShoppingItem>().HasOne<Good>().WithMany().HasForeignKey(x => x.GoodId).OnDelete(DeleteBehavior.Restrict);
        NamedWeek<ShoppingOverride>(model, "ShoppingOverrides");
        NamedWeek<PantryEntry>(model, "Pantry");
        NamedWeek<Purchase>(model, "Purchases");
        model.Entity<GoodPrice>().ToTable("Prices");
        model.Entity<GoodPrice>().Property<string>("NormalizedName").IsRequired();
        model.Entity<GoodPrice>().HasKey("NormalizedName", "Unit");
        foreach (var type in new[] { typeof(Recipe), typeof(Good), typeof(PlannedMeal), typeof(ExtraShoppingItem), typeof(ShoppingOverride), typeof(PantryEntry), typeof(Purchase), typeof(GoodPrice) })
            model.Entity(type).Property<int>("Position");
        model.Entity<Account>().ToTable("Accounts");
        model.Entity<Account>().HasKey(x => x.Id);
        model.Entity<Account>().Property<string>("NormalizedName").IsRequired();
        model.Entity<Account>().HasIndex("NormalizedName").IsUnique();
        model.Entity<LoginSession>().ToTable("Sessions");
        model.Entity<LoginSession>().HasKey(x => x.Id);
        model.Entity<LoginSession>().HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        // SQLite's decimal TEXT mapping is exact. All arithmetic stays in the shared
        // domain layer; quantities/prices are never converted to floating point.
        foreach (var entity in model.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                if (property.ClrType == typeof(decimal) || property.ClrType == typeof(decimal?)) property.SetPrecision(28);
    }
    private static void NamedWeek<T>(ModelBuilder model, string table) where T : class
    {
        model.Entity<T>().ToTable(table);
        model.Entity<T>().Property<string>("NormalizedName").IsRequired();
        model.Entity<T>().HasKey("Week", "NormalizedName", "Unit");
    }
}
