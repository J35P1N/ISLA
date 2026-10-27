using ISLA.Models;

namespace ISLA.Services;

public static class MealPlanning
{
    public static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Europe/London").DateTime);

    public static DateOnly Monday(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static DateOnly Week(string? value) => Monday(DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ? date : Today);

    public static string Key(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static string BaseUnit(string unit) => unit switch { "kg" => "g", "l" => "ml", _ => unit };

    public static PlannedMeal? PreviousCook(RecipeData data, DateOnly date, Guid recipeId) => data.Meals
        .Where(x => x.RecipeId == recipeId && x.Date >= Monday(date) && x.Date < date && x.LeftoversFromMealId is null)
        .OrderByDescending(x => x.Date).FirstOrDefault();

    public static bool InStock(RecipeData data, DateOnly week, ShoppingItem item) => data.Pantry.Any(x =>
        x.Week == week && x.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase) && x.Unit == item.Unit);

    public static Purchase? Purchased(RecipeData data, DateOnly week, ShoppingItem item) => data.Purchases.Find(x =>
        x.Week == week && x.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase) && x.Unit == item.Unit);

    public static decimal? PriceFor(RecipeData data, DateOnly week, ShoppingItem item)
    {
        var actual = Purchased(data, week, item)?.TotalPrice;
        if (actual is not null) return actual;
        var saved = data.Prices.Find(x => x.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase) && x.Unit == item.Unit);
        return saved is { Quantity: > 0 }
            ? decimal.Round(saved.TotalPrice * item.Quantity / saved.Quantity, 2, MidpointRounding.AwayFromZero)
            : null;
    }

    public static ShoppingEstimate Estimate(RecipeData data, DateOnly week)
    {
        var items = ShoppingList(data, week);
        decimal total = 0;
        var priced = 0;
        foreach (var item in items)
        {
            var amount = PriceFor(data, week, item);
            if (amount is not null)
            {
                total += decimal.Round(amount.Value, 2, MidpointRounding.AwayFromZero);
                priced++;
            }
        }
        return new ShoppingEstimate(total, priced, items.Count);
    }

    public static IReadOnlyList<ShoppingItem> ShoppingList(RecipeData data, DateOnly week, bool applyOverrides = true, bool includePantry = false)
    {
        var recipes = data.Recipes.ToDictionary(x => x.Id);
        var ingredients = data.Meals.Where(x => x.Date >= week && x.Date < week.AddDays(7) && x.LeftoversFromMealId is null && recipes.ContainsKey(x.RecipeId))
            .SelectMany(x => recipes[x.RecipeId].Ingredients)
            .Select(x => new ShoppingItem(x.Name, x.Quantity, x.Unit));
        var extras = data.ExtraItems.Where(x => x.Week == week)
            .Join(data.Goods, x => x.GoodId, g => g.Id, (x, g) => new ShoppingItem(g.Name, x.Quantity, x.Unit));
        return ingredients.Concat(extras)
            .Select(x => new ShoppingItem(x.Name.Trim(), x.Quantity * (x.Unit is "kg" or "l" ? 1000m : 1m), BaseUnit(x.Unit)))
            .GroupBy(x => (Name: x.Name.ToUpperInvariant(), x.Unit))
            .Select(x => new ShoppingItem(x.First().Name, x.Sum(i => i.Quantity), x.Key.Unit))
            .Select(x =>
            {
                var adjustment = applyOverrides ? data.ShoppingOverrides.Find(o => o.Week == week && o.Name.Equals(x.Name, StringComparison.OrdinalIgnoreCase) && o.Unit == x.Unit) : null;
                return adjustment is null ? x : x with { Quantity = adjustment.Quantity };
            })
            .Where(x => x.Quantity > 0 && (includePantry || !InStock(data, week, x)))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Unit).ToList();
    }
}
