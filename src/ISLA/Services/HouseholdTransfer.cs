using System.Text.Json;
using ISLA.Models;

namespace ISLA.Services;

public sealed class HouseholdExport
{
    [System.Text.Json.Serialization.JsonRequired]
    public int SchemaVersion { get; set; } = 1;
    [System.Text.Json.Serialization.JsonRequired]
    public RecipeData Household { get; set; } = new();
}
public sealed class HouseholdTransfer(IHouseholdPersistence persistence)
{
    public async Task<byte[]> ExportAsync()
    {
        return JsonSerializer.SerializeToUtf8Bytes(new HouseholdExport { Household = await persistence.ReadAsync() }, new JsonSerializerOptions { WriteIndented = true });
    }
    public async Task ImportAsync(Stream stream)
    {
        HouseholdExport export;
        try { export = await JsonSerializer.DeserializeAsync<HouseholdExport>(stream) ?? throw new ArgumentException("Empty import."); }
        catch (JsonException) { throw new ArgumentException("The file is not a valid ISLA household export."); }
        if (export.SchemaVersion != 1) throw new ArgumentException("Unsupported export version.");
        Validate(export.Household);
        await persistence.UpdateAsync(current =>
        {
            if (!Empty(current)) throw new ArgumentException("Import requires an empty household. Existing data was not changed.");
            return export.Household;
        });
    }
    public static bool Empty(RecipeData d) => d.Recipes.Count + d.Ingredients.Count + d.Goods.Count + d.Meals.Count + d.ExtraItems.Count + d.ShoppingOverrides.Count + d.Pantry.Count + d.Purchases.Count + d.Prices.Count == 0;
    public static void Validate(RecipeData d)
    {
        try
        {
            static void Check(bool valid) { if (!valid) throw new ArgumentException("The export contains invalid, duplicate or inconsistent household records."); }
            static bool Named(string s) => !string.IsNullOrWhiteSpace(s) && s.Length <= 200;
            static bool Quantity(decimal q, string u, bool zero = false) => (zero ? q >= 0 : q > 0) && CookingUnits.All.Contains(u) && (u != "item" || decimal.Truncate(q) == q);
            static void Unique<T>(IEnumerable<T> values) { var a = values.ToList(); Check(a.Distinct().Count() == a.Count); }
            Check(d is not null && d.Ingredients.Count == 0);
            Unique(d!.Recipes.Select(x => x.Id)); Unique(d.Goods.Select(x => x.Id)); Unique(d.Meals.Select(x => x.Id)); Unique(d.ExtraItems.Select(x => x.Id));
            Unique(d.Goods.Select(x => x.Name.ToUpperInvariant()));
            foreach (var g in d.Goods) Check(g.Id != Guid.Empty && Named(g.Name));
            foreach (var r in d.Recipes)
            {
                Check(r.Id != Guid.Empty && Named(r.Name) && r.Servings > 0 && r.Ingredients.Count > 0 && r.Instructions is not null);
                foreach (var i in r.Ingredients) Check(Named(i.Name) && Quantity(i.Quantity, i.Unit) && d.Goods.Any(g => g.IsFood && g.Name.Equals(i.Name, StringComparison.OrdinalIgnoreCase)));
            }
            foreach (var m in d.Meals)
            {
                Check(m.Id != Guid.Empty && d.Recipes.Any(r => r.Id == m.RecipeId));
                if (m.LeftoversFromMealId is { } id) Check(d.Meals.Any(x => x.Id == id && x.LeftoversFromMealId is null && x.RecipeId == m.RecipeId && x.Date >= MealPlanning.Monday(m.Date) && x.Date < m.Date));
            }
            foreach (var x in d.ExtraItems) Check(x.Id != Guid.Empty && d.Goods.Any(g => g.Id == x.GoodId) && Quantity(x.Quantity, x.Unit) && x.Week == MealPlanning.Monday(x.Week));
            foreach (var x in d.ShoppingOverrides) Check(Named(x.Name) && Quantity(x.Quantity, x.Unit, true) && x.Week == MealPlanning.Monday(x.Week));
            foreach (var x in d.Pantry) Check(Named(x.Name) && CookingUnits.All.Contains(x.Unit) && x.Week == MealPlanning.Monday(x.Week));
            foreach (var x in d.Purchases) Check(Named(x.Name) && Quantity(x.Quantity, x.Unit) && (x.TotalPrice is null || (x.TotalPrice >= 0 && decimal.Round(x.TotalPrice.Value, 2) == x.TotalPrice)) && x.Week == MealPlanning.Monday(x.Week));
            foreach (var x in d.Prices) Check(Named(x.Name) && Quantity(x.Quantity, x.Unit) && x.TotalPrice >= 0 && decimal.Round(x.TotalPrice, 2) == x.TotalPrice);
            Unique(d.ShoppingOverrides.Select(x => (x.Week, x.Name.ToUpperInvariant(), x.Unit)));
            Unique(d.Pantry.Select(x => (x.Week, x.Name.ToUpperInvariant(), x.Unit)));
            Unique(d.Purchases.Select(x => (x.Week, x.Name.ToUpperInvariant(), x.Unit)));
            Unique(d.Prices.Select(x => (x.Name.ToUpperInvariant(), x.Unit)));
        }
        catch (Exception ex) when (ex is NullReferenceException or ArgumentNullException) { throw new ArgumentException("The export contains missing records or fields."); }
    }
}
