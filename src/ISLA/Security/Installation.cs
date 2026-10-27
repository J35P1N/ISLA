using System.Security.Cryptography;
using System.Text;
using ISLA.Services;

namespace ISLA.Security;

// Held for the lifetime of the process, including operator recovery commands.
public sealed class Installation : IDisposable
{
    private readonly FileStream lease;
    public string DirectoryPath { get; }
    public string KeysPath => Path.Combine(DirectoryPath, "keys");
    private string? token;
    public Installation(IConfiguration config, IWebHostEnvironment environment)
    {
        DirectoryPath = Path.GetFullPath(config["DataDirectory"] ?? Path.Combine(environment.ContentRootPath, "App_Data"));
        var webRoot = Path.GetFullPath(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"));
        if (DirectoryPath == webRoot || DirectoryPath.StartsWith(webRoot + Path.DirectorySeparatorChar))
            throw new InvalidOperationException("DataDirectory must be outside the web root.");
        Directory.CreateDirectory(DirectoryPath);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try { lease = new FileStream(Path.Combine(DirectoryPath, ".instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new DataDirectoryInUseException(ex); }
        token = config["Setup:Token"];
        if (string.IsNullOrEmpty(token) && config["Setup:TokenFile"] is { } file) token = File.ReadAllText(file).Trim();
    }
    public void EnsureSetupToken(TextWriter console)
    {
        if (HasToken) return;
        token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        console.WriteLine();
        console.WriteLine("ISLA first-run setup token: " + token);
        console.WriteLine("Enter this token on the Set up ISLA page to confirm you operate this server.");
        console.WriteLine("This token is valid for this run only and stops working when setup completes.");
        console.WriteLine();
    }
    public bool TokenMatches(string supplied) => !string.IsNullOrWhiteSpace(token) && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
    public bool HasToken => !string.IsNullOrWhiteSpace(token);
    public void Dispose() => lease.Dispose();
}

public sealed class DataDirectoryInUseException(IOException innerException) : InvalidOperationException(
    "Simple JSON mode requires exclusive access to DataDirectory. Stop the other ISLA process first.", innerException);
