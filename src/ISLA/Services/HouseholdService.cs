using ISLA.Models;
using ISLA.Security;

namespace ISLA.Services;

public sealed class HouseholdService(RecipeStore store, AccessGuard access) : IHouseholdService
{
    public async Task<RecipeData> ReadAsync() { await access.RequireAsync(); return await store.ReadAsync(); }
    public async Task SaveAsync(Recipe recipe) { await access.RequireAsync(); await store.SaveAsync(recipe); }
    public async Task DeleteAsync(Guid id) { await access.RequireAsync(); await store.DeleteAsync(id); }
    public async Task AddMealAsync(DateOnly date, Guid recipeId, Guid? leftoversFromMealId = null) { await access.RequireAsync(); await store.AddMealAsync(date, recipeId, leftoversFromMealId); }
    public async Task RemoveMealAsync(Guid id) { await access.RequireAsync(); await store.RemoveMealAsync(id); }
    public async Task SaveGoodAsync(string name, bool isFood) { await access.RequireAsync(); await store.SaveGoodAsync(name, isFood); }
    public async Task AddExtraItemAsync(DateOnly week, Guid goodId, decimal quantity, string unit) { await access.RequireAsync(); await store.AddExtraItemAsync(week, goodId, quantity, unit); }
    public async Task CreateGoodAndAddExtraAsync(DateOnly week, string name, bool isFood, decimal quantity, string unit) { await access.RequireAsync(); await store.CreateGoodAndAddExtraAsync(week, name, isFood, quantity, unit); }
    public async Task SetShoppingQuantityAsync(DateOnly week, string name, string unit, decimal quantity) { await access.RequireAsync(); await store.SetShoppingQuantityAsync(week, name, unit, quantity); }
    public async Task SetInStockAsync(DateOnly week, string name, string unit, bool inStock) { await access.RequireAsync(); await store.SetInStockAsync(week, name, unit, inStock); }
    public async Task SetPurchasedAsync(DateOnly week, string name, string unit, bool purchased, decimal? totalPrice = null) { await access.RequireAsync(); await store.SetPurchasedAsync(week, name, unit, purchased, totalPrice); }
    public async Task ResetShoppingQuantitiesAsync(DateOnly week) { await access.RequireAsync(); await store.ResetShoppingQuantitiesAsync(week); }
    public async Task RemoveExtraItemAsync(Guid id) { await access.RequireAsync(); await store.RemoveExtraItemAsync(id); }
}
