using System.Text.Json;
using ISLA.Models;

namespace ISLA.Services;

public interface IHouseholdPersistence
{
    Task<RecipeData> ReadAsync();
    // Providers must commit the callback atomically against the latest household state.
    Task UpdateAsync(Func<RecipeData, RecipeData> update);
}

public sealed class JsonHouseholdPersistence : IHouseholdPersistence
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path;
    public JsonHouseholdPersistence(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var directory = configuration["DataDirectory"] ?? Path.Combine(environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "recipes.json");
    }
    public async Task<RecipeData> ReadAsync()
    {
        await gate.WaitAsync();
        try { return await ReadFileAsync(); }
        finally { gate.Release(); }
    }
    public async Task UpdateAsync(Func<RecipeData, RecipeData> update)
    {
        await gate.WaitAsync();
        try { await PrivateFiles.WriteAsync(path, update(await ReadFileAsync())); }
        finally { gate.Release(); }
    }
    private async Task<RecipeData> ReadFileAsync()
    {
        if (!File.Exists(path)) return new();
        await using var stream = File.OpenRead(path);
        var data = await JsonSerializer.DeserializeAsync<RecipeData>(stream) ?? throw new InvalidDataException("Household data is invalid.");
        if (data.Recipes is null || data.Ingredients is null || data.Meals is null || data.Goods is null || data.ExtraItems is null || data.ShoppingOverrides is null || data.Pantry is null || data.Purchases is null || data.Prices is null || data.Recipes.Any(r => r is null || r.Ingredients is null) || data.Goods.Any(g => g is null || g.Name is null))
            throw new InvalidDataException("Household data contains missing collections or records.");
        foreach (var name in data.Ingredients.Concat(data.Recipes.SelectMany(r => r.Ingredients).Select(i => i.Name)))
            if (!data.Goods.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) data.Goods.Add(new Good { Name = name });
        data.Ingredients.Clear();
        return data;
    }
}

public static class PrivateFiles
{
    public static async Task WriteAsync<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, true);
    }
}
