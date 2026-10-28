using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using ISLA.Services;
using ISLA.Security;
using ISLA.Storage;
using ISLA.Models;
using Xunit;

namespace ISLA.Tests;

public abstract class ShoppingPurchaseTests(bool sqlite) : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "isla-tests-" + Guid.NewGuid());
    private readonly DateOnly week = new(2026, 10, 26);

    private RecipeStore Open()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["DataDirectory"] = directory, ["Storage:Provider"] = sqlite ? "Sqlite" : "Json" }).Build();
        if (!sqlite) return new RecipeStore(config, new TestEnvironment());
        using var installation = new Installation(config, new TestEnvironment());
        var database = new SqliteStorage(new StorageConfiguration(installation, config));
        database.InitializeAsync().GetAwaiter().GetResult();
        return new RecipeStore(new SqliteHouseholdPersistence(database));
    }

    private async Task<(RecipeStore Store, Guid GoodId)> Setup(decimal quantity = 2, string unit = "item")
    {
        var store = Open();
        await store.SaveGoodAsync("Red pepper", true);
        var id = (await store.ReadAsync()).Goods.Single().Id;
        await store.AddExtraItemAsync(week, id, quantity, unit);
        return (store, id);
    }

    [Fact]
    public async Task PurchasedStateAndPoundsAndPencePersistWithoutDuplicatingRows()
    {
        var (store, _) = await Setup();
        await store.SetPurchasedAsync(week, "red PEPPER", "item", true, 2.40m);
        await store.SetPurchasedAsync(week, "Red pepper", "item", true, 2.40m);
        var data = await Open().ReadAsync();
        var purchase = Assert.Single(data.Purchases);
        Assert.Equal(2.40m, purchase.TotalPrice);
        Assert.Equal(2m, purchase.Quantity);
        Assert.Single(data.Prices);
        Assert.NotNull(MealPlanning.Purchased(data, week, Assert.Single(MealPlanning.ShoppingList(data, week))));
        Assert.Null(MealPlanning.Purchased(data, week.AddDays(7), new("Red pepper", 2, "item")));
    }

    [Fact]
    public async Task OptionalPriceDoesNotPreventCheckingAnItem()
    {
        var (store, _) = await Setup();
        await store.SetPurchasedAsync(week, "Red pepper", "item", true);
        var data = await Open().ReadAsync();
        Assert.Null(Assert.Single(data.Purchases).TotalPrice);
        Assert.Empty(data.Prices);
        Assert.Equal(new(0, 0, 1), MealPlanning.Estimate(data, week));
    }

    [Fact]
    public async Task FutureEstimateScalesSavedPriceAndMarksMissingPricesAsPartial()
    {
        var (store, id) = await Setup();
        await store.SetPurchasedAsync(week, "Red pepper", "item", true, 2.40m);
        await store.AddExtraItemAsync(week.AddDays(7), id, 3, "item");
        await store.SaveGoodAsync("Bleach", false);
        var bleach = (await store.ReadAsync()).Goods.Single(g => g.Name == "Bleach");
        await store.AddExtraItemAsync(week.AddDays(7), bleach.Id, 1, "item");
        var estimate = MealPlanning.Estimate(await Open().ReadAsync(), week.AddDays(7));
        Assert.Equal(new(3.60m, 1, 2), estimate);
    }

    [Fact]
    public async Task PantryIsExcludedAndCannotBePurchased()
    {
        var (store, id) = await Setup();
        await store.SetPurchasedAsync(week, "Red pepper", "item", true, 2.40m);
        await store.AddExtraItemAsync(week.AddDays(7), id, 3, "item");
        await store.SetInStockAsync(week.AddDays(7), "Red pepper", "item", true);
        Assert.Equal(new(0, 0, 0), MealPlanning.Estimate(await Open().ReadAsync(), week.AddDays(7)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetPurchasedAsync(week.AddDays(7), "Red pepper", "item", true, 1m));
        await store.SetInStockAsync(week, "Red pepper", "item", true);
        Assert.Empty((await Open().ReadAsync()).Purchases);
    }

    [Fact]
    public async Task UntickingRestoresItemAndRetainsPriceHistory()
    {
        var (store, _) = await Setup();
        await store.SetPurchasedAsync(week, "Red pepper", "item", true, 2.40m);
        await store.SetPurchasedAsync(week, "Red pepper", "item", false);
        var data = await Open().ReadAsync();
        Assert.Empty(data.Purchases);
        Assert.Single(data.Prices);
        Assert.Equal(new(2.40m, 1, 1), MealPlanning.Estimate(data, week));
    }

    [Fact]
    public async Task MetricQuantitiesSharePricingBasis()
    {
        var (store, id) = await Setup(1, "kg");
        await store.SetPurchasedAsync(week, "Red pepper", "g", true, 4m);
        await store.AddExtraItemAsync(week.AddDays(7), id, 250, "g");
        Assert.Equal(new(1m, 1, 1), MealPlanning.Estimate(await Open().ReadAsync(), week.AddDays(7)));
    }

    [Fact]
    public async Task EarlierActualPriceSurvivesLaterPriceChanges()
    {
        var (store, id) = await Setup();
        await store.SetPurchasedAsync(week, "Red pepper", "item", true, 2.40m);
        await store.AddExtraItemAsync(week.AddDays(7), id, 2, "item");
        await store.SetPurchasedAsync(week.AddDays(7), "Red pepper", "item", true, 3m);
        var data = await Open().ReadAsync();
        Assert.Equal(2.40m, MealPlanning.Estimate(data, week).Total);
        Assert.Equal(3m, MealPlanning.Estimate(data, week.AddDays(7)).Total);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1.999)]
    public async Task InvalidPriceDoesNotSavePurchase(decimal price)
    {
        var (store, _) = await Setup();
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetPurchasedAsync(week, "Red pepper", "item", true, price));
        Assert.Empty((await Open().ReadAsync()).Purchases);
    }

    [Fact]
    public async Task ZeroPriceIsKnownRatherThanMissing()
    {
        var (store, _) = await Setup();
        await store.SetPurchasedAsync(week, "Red pepper", "item", true, 0);
        Assert.Equal(new(0, 1, 1), MealPlanning.Estimate(await Open().ReadAsync(), week));
    }

    [Fact]
    public async Task PriceFieldsRestoreSavedPricesForUnpurchasedRowsAfterReopening()
    {
        var (store, id) = await Setup();
        await store.SetPurchasedAsync(week, "Red pepper", "item", true, 2.40m);
        await store.AddExtraItemAsync(week.AddDays(7), id, 3, "item");
        var reopened = await Open().ReadAsync();
        var nextWeekItem = Assert.Single(MealPlanning.ShoppingList(reopened, week.AddDays(7)));
        Assert.Equal(3.60m, MealPlanning.PriceFor(reopened, week.AddDays(7), nextWeekItem));
        Assert.Equal(MealPlanning.Estimate(reopened, week.AddDays(7)).Total, MealPlanning.PriceFor(reopened, week.AddDays(7), nextWeekItem));
        var purchasedItem = Assert.Single(MealPlanning.ShoppingList(reopened, week));
        Assert.Equal(2.40m, MealPlanning.PriceFor(reopened, week, purchasedItem));
    }

    [Fact]
    public async Task PriceFieldsKeepUnknownPricesBlankAndRestoreZeroPrices()
    {
        var (store, _) = await Setup();
        var data = await Open().ReadAsync();
        var item = Assert.Single(MealPlanning.ShoppingList(data, week));
        Assert.Null(MealPlanning.PriceFor(data, week, item));
        await store.SetPurchasedAsync(week, item.Name, item.Unit, true, 0);
        Assert.Equal(0m, MealPlanning.PriceFor(await Open().ReadAsync(), week, item));
    }

    [Fact]
    public async Task RecipesLeftoversCatalogueAndShoppingMutationsHaveProviderParity()
    {
        var store = Open();
        var recipe = new Recipe { Name = "Stew", Ingredients = [new() { Name = "Potato", Quantity = 2 }, new() { Name = "Carrot", Quantity = 3 }] };
        await store.SaveAsync(recipe);
        recipe.Instructions = "Simmer";
        recipe.Ingredients.Reverse();
        await store.SaveAsync(recipe);
        var saved = Assert.Single((await Open().ReadAsync()).Recipes);
        Assert.Equal("Carrot", saved.Ingredients[0].Name);
        Assert.Equal("Simmer", saved.Instructions);
        await store.SaveGoodAsync("POTATO", true);
        Assert.Equal(2, (await store.ReadAsync()).Goods.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveGoodAsync("Potato", false));
        await store.AddMealAsync(week, recipe.Id);
        var cook = Assert.Single((await store.ReadAsync()).Meals);
        await store.AddMealAsync(week.AddDays(1), recipe.Id, cook.Id);
        await store.AddMealAsync(week.AddDays(2), recipe.Id, cook.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddMealAsync(week, recipe.Id));
        await store.RemoveMealAsync(cook.Id);
        var meals = (await store.ReadAsync()).Meals.OrderBy(x => x.Date).ToArray();
        Assert.Null(meals[0].LeftoversFromMealId);
        Assert.Equal(meals[0].Id, meals[1].LeftoversFromMealId);
        await store.SetShoppingQuantityAsync(week, "Potato", "item", 4);
        Assert.Single((await store.ReadAsync()).ShoppingOverrides);
        await store.ResetShoppingQuantitiesAsync(week);
        Assert.Empty((await store.ReadAsync()).ShoppingOverrides);
        await store.CreateGoodAndAddExtraAsync(week, "Sponge", false, 1, "item");
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateGoodAndAddExtraAsync(week, "SPONGE", false, 1, "item"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddExtraItemAsync(week, Guid.NewGuid(), 1, "item"));
        var extra = Assert.Single((await store.ReadAsync()).ExtraItems);
        await store.RemoveExtraItemAsync(extra.Id);
        Assert.Empty((await Open().ReadAsync()).ExtraItems);
        await store.DeleteAsync(recipe.Id);
        var final = await Open().ReadAsync();
        Assert.Empty(final.Recipes);
        Assert.Empty(final.Meals);
        Assert.Equal(3, final.Goods.Count);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public sealed class JsonShoppingPurchaseTests() : ShoppingPurchaseTests(false);
public sealed class SqliteShoppingPurchaseTests() : ShoppingPurchaseTests(true);
