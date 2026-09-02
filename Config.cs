namespace AIAgent;

/// <summary>Configuration, loaded from environment variables or a .env file.</summary>
public sealed class AgentConfig
{

    public const string DefaultUserAgent = "agent-cli/1.0.0";

    public string ApiKey { get; private set; } = "";
    public string BaseUrl { get; private set; } 
    public string Model { get; private set; } 
    public string UserAgent { get; private set; } = DefaultUserAgent;

    public static AgentConfig Load()
    {
        LoadDotEnv();
        return new AgentConfig
        {
            ApiKey = Environment.GetEnvironmentVariable("AGENT_API_KEY") ?? "",
            BaseUrl = (Environment.GetEnvironmentVariable("AGENT_BASE_URL")! ).TrimEnd('/'),
            Model = Environment.GetEnvironmentVariable("AGENT_MODEL")! ,
            UserAgent = Environment.GetEnvironmentVariable("AGENT_UA") ?? DefaultUserAgent,
        };
    }

    // .env is the base, .env.{AGENT_ENV} (default: development) overrides it;
    // values already set in the real environment are never overwritten.
    private static void LoadDotEnv()
    {
        foreach (var pass in new[] { ".env", $".env.{Environment.GetEnvironmentVariable("AGENT_ENV") ?? "development"}" })
            foreach (var dir in CandidateDirectories())
            {
                var path = Path.Combine(dir, pass);
                if (!File.Exists(path))
                    continue;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#') || !line.Contains('='))
                        continue;
                    var eq = line.IndexOf('=');
                    var key = line[..eq].Trim();
                    var value = line[(eq + 1)..].Trim().Trim('\'', '"');
                    if (Environment.GetEnvironmentVariable(key) is null)
                        Environment.SetEnvironmentVariable(key, value);
                }
            }
    }

    // current directory plus the binary's ancestors, to cover `dotnet run`
    // from any path and the bin/<config>/<tfm> layout
    private static IEnumerable<string> CandidateDirectories()
    {
        yield return Environment.CurrentDirectory;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            yield return dir.FullName;
    }
}
