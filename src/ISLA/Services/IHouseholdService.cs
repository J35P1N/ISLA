using ISLA.Models;

namespace ISLA.Services;

public interface IHouseholdService
{
    Task<RecipeData> ReadAsync();
    Task SaveAsync(Recipe recipe);
    Task DeleteAsync(Guid id);
    Task AddMealAsync(DateOnly date, Guid recipeId, Guid? leftoversFromMealId = null);
    Task RemoveMealAsync(Guid id);
    Task SaveGoodAsync(string name, bool isFood);
    Task AddExtraItemAsync(DateOnly week, Guid goodId, decimal quantity, string unit);
    Task CreateGoodAndAddExtraAsync(DateOnly week, string name, bool isFood, decimal quantity, string unit);
    Task SetShoppingQuantityAsync(DateOnly week, string name, string unit, decimal quantity);
    Task SetInStockAsync(DateOnly week, string name, string unit, bool inStock);
    Task SetPurchasedAsync(DateOnly week, string name, string unit, bool purchased, decimal? totalPrice = null);
    Task ResetShoppingQuantitiesAsync(DateOnly week);
    Task RemoveExtraItemAsync(Guid id);
}
