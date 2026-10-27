using ISLA.Models;

namespace ISLA.Services;

// Household rules are independent of the configured persistence provider.
public sealed class RecipeStore
{
    private readonly IHouseholdPersistence persistence;

    public RecipeStore(IConfiguration configuration, IWebHostEnvironment environment)
        : this(new JsonHouseholdPersistence(configuration, environment)) { }

    public RecipeStore(IHouseholdPersistence persistence) => this.persistence = persistence;

    public Task<RecipeData> ReadAsync() => persistence.ReadAsync();

    public async Task SaveAsync(Recipe recipe)
    {
        recipe.Name = recipe.Name.Trim();
        if (recipe.Name.Length == 0 || recipe.Servings < 1 || recipe.Ingredients.Count == 0)
            throw new ArgumentException("Enter a recipe name, at least one serving, and at least one ingredient.");
        foreach (var ingredient in recipe.Ingredients)
        {
            ingredient.Name = ingredient.Name.Trim();
            if (ingredient.Name.Length == 0 || ingredient.Quantity <= 0 || !CookingUnits.All.Contains(ingredient.Unit))
                throw new ArgumentException("Each ingredient needs a name, a positive quantity, and a supported measurement.");
            if (ingredient.Unit == "item" && ingredient.Quantity != decimal.Truncate(ingredient.Quantity))
                throw new ArgumentException("Individual items must have whole-number quantities.");
        }
        await persistence.UpdateAsync(data =>
        {
            foreach (var ingredient in recipe.Ingredients)
            {
                var existing = data.Goods.Find(x => x.Name.Equals(ingredient.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is { IsFood: false })
                    throw new ArgumentException($"{existing.Name} is a non-food good and cannot be used in recipes.");
                if (existing is null) data.Goods.Add(new Good { Name = ingredient.Name });
                else ingredient.Name = existing.Name;
            }
            data.Recipes.RemoveAll(x => x.Id == recipe.Id);
            data.Recipes.Add(recipe);
            return data;
        });
    }

    public async Task DeleteAsync(Guid id)
    {
        await persistence.UpdateAsync(data =>
        {
            data.Recipes.RemoveAll(x => x.Id == id);
            data.Meals.RemoveAll(x => x.RecipeId == id);
            return data;
        });
    }

    public async Task AddMealAsync(DateOnly date, Guid recipeId, Guid? leftoversFromMealId = null)
    {
        await persistence.UpdateAsync(data =>
        {
            if (!data.Recipes.Any(x => x.Id == recipeId))
                throw new ArgumentException("This recipe is no longer available. Refresh the page and choose another recipe.");
            if (data.Meals.Any(x => x.Date == date))
                throw new ArgumentException("An evening meal is already planned for this day. Refresh the planner and remove it before choosing another.");
            if (leftoversFromMealId is not null && !data.Meals.Any(x => x.Id == leftoversFromMealId && x.RecipeId == recipeId && x.LeftoversFromMealId is null && x.Date >= MealPlanning.Monday(date) && x.Date < date))
                throw new ArgumentException("Leftovers need a fresh cook of the same recipe earlier in this week. Refresh the planner and try again.");
            data.Meals.Add(new PlannedMeal { Date = date, RecipeId = recipeId, LeftoversFromMealId = leftoversFromMealId });
            return data;
        });
    }

    public async Task RemoveMealAsync(Guid id)
    {
        await persistence.UpdateAsync(data =>
        {
            data.Meals.RemoveAll(x => x.Id == id);
            // If the original cook is removed, the first remaining meal must supply the ingredients.
            var dependents = data.Meals.Where(x => x.LeftoversFromMealId == id).OrderBy(x => x.Date).ToList();
            if (dependents.Count > 0)
            {
                dependents[0].LeftoversFromMealId = null;
                foreach (var meal in dependents.Skip(1)) meal.LeftoversFromMealId = dependents[0].Id;
            }
            return data;
        });
    }

    public async Task SaveGoodAsync(string name, bool isFood)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 200) throw new ArgumentException("Enter a goods name of up to 200 characters.");
        await persistence.UpdateAsync(data =>
        {
            var good = data.Goods.Find(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (!isFood && data.Recipes.Any(r => r.Ingredients.Any(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase))))
                throw new ArgumentException("This good is used in a recipe. Remove it from those recipes before marking it as non-food.");
            if (good is null) data.Goods.Add(new Good { Name = name, IsFood = isFood });
            else good.IsFood = isFood;
            return data;
        });
    }

    public async Task AddExtraItemAsync(DateOnly week, Guid goodId, decimal quantity, string unit)
    {
        if (quantity <= 0 || !CookingUnits.All.Contains(unit) || (unit == "item" && decimal.Truncate(quantity) != quantity))
            throw new ArgumentException("Enter a positive quantity; item counts must be whole numbers.");
        await persistence.UpdateAsync(data =>
        {
            var good = data.Goods.Find(x => x.Id == goodId) ?? throw new ArgumentException("Choose a good from the catalogue.");
            data.ShoppingOverrides.RemoveAll(x => x.Week == MealPlanning.Monday(week) && x.Name.Equals(good.Name, StringComparison.OrdinalIgnoreCase) && x.Unit == MealPlanning.BaseUnit(unit));
            data.ExtraItems.Add(new ExtraShoppingItem { Week = MealPlanning.Monday(week), GoodId = goodId, Quantity = quantity, Unit = unit });
            return data;
        });
    }

    public async Task CreateGoodAndAddExtraAsync(DateOnly week, string name, bool isFood, decimal quantity, string unit)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 200)
            throw new ArgumentException("Enter a goods name of up to 200 characters.");
        if (quantity <= 0 || !CookingUnits.All.Contains(unit) || (unit == "item" && decimal.Truncate(quantity) != quantity))
            throw new ArgumentException("Enter a positive quantity; item counts must be whole numbers.");
        await persistence.UpdateAsync(data =>
        {
            if (data.Goods.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("This good already exists. Choose it from the existing goods list.");
            var good = new Good { Name = name, IsFood = isFood };
            data.Goods.Add(good);
            data.ExtraItems.Add(new ExtraShoppingItem { Week = MealPlanning.Monday(week), GoodId = good.Id, Quantity = quantity, Unit = unit });
            return data;
        });
    }

    public async Task SetShoppingQuantityAsync(DateOnly week, string name, string unit, decimal quantity)
    {
        week = MealPlanning.Monday(week);
        if (quantity < 0 || !CookingUnits.All.Contains(unit) || (unit == "item" && quantity != decimal.Truncate(quantity)))
            throw new ArgumentException("Enter a positive quantity; item counts must be whole numbers.");
        await persistence.UpdateAsync(data =>
        {
            if (!MealPlanning.ShoppingList(data, week, applyOverrides: false, includePantry: true).Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.Unit == unit))
                throw new ArgumentException("This item is no longer on the list. Refresh the page and try again.");
            data.ShoppingOverrides.RemoveAll(x => x.Week == week && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.Unit == unit);
            data.ShoppingOverrides.Add(new ShoppingOverride { Week = week, Name = name, Unit = unit, Quantity = quantity });
            return data;
        });
    }

    public async Task SetInStockAsync(DateOnly week, string name, string unit, bool inStock)
    {
        week = MealPlanning.Monday(week);
        await persistence.UpdateAsync(data =>
        {
            var item = MealPlanning.ShoppingList(data, week, includePantry: true)
                .FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.Unit == unit);
            if (item is null) throw new ArgumentException("This item is no longer on the list. Refresh the page and try again.");
            data.Pantry.RemoveAll(x => x.Week == week && x.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase) && x.Unit == unit);
            if (inStock) data.Purchases.RemoveAll(x => x.Week == week && x.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase) && x.Unit == unit);
            if (inStock) data.Pantry.Add(new PantryEntry { Week = week, Name = item.Name, Unit = unit });
            return data;
        });
    }

    public async Task SetPurchasedAsync(DateOnly week, string name, string unit, bool purchased, decimal? totalPrice = null)
    {
        week = MealPlanning.Monday(week);
        if (totalPrice is < 0 || (totalPrice is { } value && decimal.Round(value, 2) != value))
            throw new ArgumentException("Enter a price in pounds and pence, with no more than two decimal places.");
        await persistence.UpdateAsync(data =>
        {
            var item = MealPlanning.ShoppingList(data, week).FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.Unit == unit);
            if (item is null) throw new ArgumentException("This item is no longer on the shopping list. Refresh and try again.");
            data.Purchases.RemoveAll(x => x.Week == week && x.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase) && x.Unit == unit);
            if (purchased)
            {
                data.Purchases.Add(new Purchase { Week = week, Name = item.Name, Unit = unit, Quantity = item.Quantity, TotalPrice = totalPrice });
                if (totalPrice is { } price)
                {
                    data.Prices.RemoveAll(x => x.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase) && x.Unit == unit);
                    data.Prices.Add(new GoodPrice { Name = item.Name, Unit = unit, Quantity = item.Quantity, TotalPrice = price });
                }
            }
            return data;
        });
    }

    public async Task ResetShoppingQuantitiesAsync(DateOnly week)
    {
        await persistence.UpdateAsync(data =>
        {
            data.ShoppingOverrides.RemoveAll(x => x.Week == MealPlanning.Monday(week));
            return data;
        });
    }

    public async Task RemoveExtraItemAsync(Guid id)
    {
        await persistence.UpdateAsync(data =>
        {
            data.ExtraItems.RemoveAll(x => x.Id == id);
            return data;
        });
    }

}
