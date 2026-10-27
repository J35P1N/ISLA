namespace ISLA.Models;

public sealed class Recipe
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public int Servings { get; set; } = 2;
    public string Instructions { get; set; } = "";
    public List<RecipeIngredient> Ingredients { get; set; } = [];
}

public sealed class RecipeIngredient
{
    public string Name { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    public string Unit { get; set; } = "item";
}

public static class CookingUnits
{
    public static readonly string[] All = ["item", "g", "kg", "ml", "l", "tsp", "tbsp", "oz", "lb", "fl oz", "pint", "pinch"];
}

public sealed class RecipeData
{
    public List<Recipe> Recipes { get; set; } = [];
    public List<string> Ingredients { get; set; } = [];
    public List<PlannedMeal> Meals { get; set; } = [];
    public List<Good> Goods { get; set; } = [];
    public List<ExtraShoppingItem> ExtraItems { get; set; } = [];
    public List<ShoppingOverride> ShoppingOverrides { get; set; } = [];
    public List<PantryEntry> Pantry { get; set; } = [];
    public List<Purchase> Purchases { get; set; } = [];
    public List<GoodPrice> Prices { get; set; } = [];
}

public sealed class PlannedMeal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateOnly Date { get; set; }
    public Guid RecipeId { get; set; }
    public Guid? LeftoversFromMealId { get; set; }
}

public sealed record ShoppingItem(string Name, decimal Quantity, string Unit);

public sealed class Good
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool IsFood { get; set; } = true;
}

public sealed class ExtraShoppingItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateOnly Week { get; set; }
    public Guid GoodId { get; set; }
    public decimal Quantity { get; set; } = 1;
    public string Unit { get; set; } = "item";
}

public sealed class ShoppingOverride
{
    public DateOnly Week { get; set; }
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "item";
    // Zero removes a row from this week's list.
    public decimal Quantity { get; set; }
}

public sealed class PantryEntry
{
    public DateOnly Week { get; set; }
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "item";
}

public sealed class Purchase
{
    public DateOnly Week { get; set; }
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "item";
    public decimal Quantity { get; set; }
    public decimal? TotalPrice { get; set; }
}

public sealed class GoodPrice
{
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "item";
    public decimal Quantity { get; set; }
    public decimal TotalPrice { get; set; }
}

public sealed record ShoppingEstimate(decimal Total, int PricedItems, int TotalItems);
