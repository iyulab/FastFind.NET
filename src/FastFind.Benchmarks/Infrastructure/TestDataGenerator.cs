namespace FastFind.Benchmarks.Infrastructure;

/// <summary>
/// Generates test data for benchmarks
/// </summary>
public static class TestDataGenerator
{
    private static readonly string[] Extensions = [".txt", ".cs", ".json", ".xml", ".md", ".log", ".dll", ".exe", ".pdf", ".doc"];
    private static readonly string[] Directories = ["Documents", "Projects", "Source", "Data", "Temp", "Cache", "Build", "Output", "Logs", "Config"];
    private static readonly string[] FileNames = ["readme", "config", "settings", "data", "output", "log", "main", "app", "service", "handler"];

    /// <summary>
    /// Generates random file paths for benchmarking
    /// </summary>
    /// <param name="count">Number of paths to generate</param>
    /// <returns>Array of file paths</returns>
    public static string[] GenerateFilePaths(int count)
    {
        var random = new Random(42); // Fixed seed for reproducibility
        var paths = new string[count];

        for (int i = 0; i < count; i++)
        {
            var depth = random.Next(1, 6);
            var parts = new List<string> { "C:" };

            for (int d = 0; d < depth; d++)
            {
                parts.Add(Directories[random.Next(Directories.Length)]);
            }

            var fileName = FileNames[random.Next(FileNames.Length)] + "_" + i;
            var extension = Extensions[random.Next(Extensions.Length)];
            parts.Add(fileName + extension);

            paths[i] = string.Join(Path.DirectorySeparatorChar, parts);
        }

        return paths;
    }

    /// <summary>
    /// Generates random search patterns
    /// </summary>
    /// <param name="count">Number of patterns to generate</param>
    /// <returns>Array of search patterns</returns>
    public static string[] GenerateSearchPatterns(int count)
    {
        var patterns = new string[count];
        var random = new Random(42);

        for (int i = 0; i < count; i++)
        {
            var type = random.Next(4);
            patterns[i] = type switch
            {
                0 => FileNames[random.Next(FileNames.Length)],
                1 => "*" + Extensions[random.Next(Extensions.Length)],
                2 => FileNames[random.Next(FileNames.Length)] + "*",
                3 => "*" + FileNames[random.Next(FileNames.Length)].Substring(0, 3) + "*",
                _ => "test"
            };
        }

        return patterns;
    }

    /// <summary>
    /// Generates random strings for string matching benchmarks
    /// </summary>
    /// <param name="count">Number of strings to generate</param>
    /// <param name="minLength">Minimum string length</param>
    /// <param name="maxLength">Maximum string length</param>
    /// <returns>Array of random strings</returns>
    public static string[] GenerateRandomStrings(int count, int minLength = 10, int maxLength = 100)
    {
        var random = new Random(42);
        var strings = new string[count];
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-.";

        for (int i = 0; i < count; i++)
        {
            var length = random.Next(minLength, maxLength + 1);
            var charArray = new char[length];

            for (int j = 0; j < length; j++)
            {
                charArray[j] = chars[random.Next(chars.Length)];
            }

            strings[i] = new string(charArray);
        }

        return strings;
    }

    /// <summary>
    /// Gets a real test directory if available
    /// </summary>
    /// <returns>Path to test directory</returns>
    public static string GetTestDirectory()
    {
        // Try common directories
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            @"C:\Windows\System32",
            @"C:\Program Files"
        };

        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir))
            {
                return dir;
            }
        }

        return Environment.CurrentDirectory;
    }
}
