using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ALAgent;

/// <summary>A tool takes its arguments (all normalized to strings) and returns a string result.</summary>
public delegate string ToolFunc(Dictionary<string, string> args);

public interface IToolbox
{
    IReadOnlySet<string> Risky { get; }

    JsonArray Specs();

    Dictionary<string, ToolFunc> AsFuncs();
}

/// <summary>The real tool implementations.</summary>
public sealed class Toolbox : IToolbox
{
    private const int MaxToolOutput = 4000;
    private const int CommandTimeoutMax = 60;

    private readonly HttpClient _http = new();

    public IReadOnlySet<string> Risky { get; } = new HashSet<string>
        { "write_file", "edit_file", "delete_file", "run_command" };

    public Dictionary<string, ToolFunc> AsFuncs() => new()
    {
        ["web_search"] = a => WebSearch(Arg(a, "query"), IntArg(a, "max_results", 5)),
        ["write_file"] = a => WriteFile(Arg(a, "path"), Arg(a, "content")),
        ["read_file"] = a => ReadFile(Arg(a, "path")),
        ["edit_file"] = a => EditFile(Arg(a, "path"), Arg(a, "old_string"), Arg(a, "new_string")),
        ["list_files"] = a => ListFiles(Arg(a, "dir", ".")),
        ["delete_file"] = a => DeleteFile(Arg(a, "path")),
        ["run_command"] = a => RunCommand(Arg(a, "cmd"), IntArg(a, "timeout", 30)),
    };

    private static string Arg(Dictionary<string, string> a, string key, string fallback = "")
        => a.TryGetValue(key, out var v) && v != null ? v : fallback;

    private static int IntArg(Dictionary<string, string> a, string key, int fallback)
        => int.TryParse(a.GetValueOrDefault(key), out var v) && v > 0 ? Math.Min(v, CommandTimeoutMax) : fallback;

    private static string Truncate(string text, int limit = MaxToolOutput)
        => text.Length <= limit ? text : text[..limit] + $"\n... [truncated, {text.Length - limit} chars omitted]";

    // ------------------------------------------------------------- web search

    /// <summary>Search the web (DuckDuckGo HTML endpoint) and return titled results.</summary>
    public string WebSearch(string query, int maxResults = 5)
    {
        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["q"] = query });
            var request = new HttpRequestMessage(HttpMethod.Post, "https://html.duckduckgo.com/html/")
            {
                Content = content,
            };
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (agent-cli/1.0)");
            using var response = _http.Send(request);
            var page = response.Content.ReadAsStringAsync().Result;
            return ParseDdgResults(page, query, maxResults);
        }
        catch (Exception e)
        {
            return $"Error: web_search failed: {e.Message}";
        }
    }

    private static string ParseDdgResults(string page, string query, int maxResults)
    {
        // Result anchors appear with attributes in either order.
        var anchors = Regex.Matches(
            page,
            @"<a[^>]*class=""result__a""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>",
            RegexOptions.Singleline).ToList();
        if (anchors.Count == 0)
        {
            anchors = Regex.Matches(
                page,
                @"<a[^>]*href=""([^""]+)""[^>]*class=""result__a""[^>]*>(.*?)</a>",
                RegexOptions.Singleline).ToList();
        }
        var snippets = Regex.Matches(
            page, @"class=""result__snippet""[^>]*>(.*?)</a>", RegexOptions.Singleline);

        if (anchors.Count == 0)
            return $"No results found for \"{query}\".";

        var lines = new List<string>();
        for (var i = 0; i < Math.Min(anchors.Count, maxResults); i++)
        {
            var url = CleanDdgUrl(anchors[i].Groups[1].Value);
            var title = StripHtml(anchors[i].Groups[2].Value);
            var entry = $"{i + 1}. {title}\n   {url}";
            if (i < snippets.Count)
                entry += $"\n   {StripHtml(snippets[i].Groups[1].Value)}";
            lines.Add(entry);
        }
        return Truncate(string.Join("\n", lines));
    }

    /// <summary>DuckDuckGo wraps result URLs: //duckduckgo.com/l/?uddg=&lt;real url&gt;&amp;...</summary>
    private static string CleanDdgUrl(string href)
    {
        if (href.Contains("uddg="))
        {
            var query = href.StartsWith("//")
                ? new Uri("https:" + href).Query
                : new Uri(href, UriKind.RelativeOrAbsolute).Query;
            foreach (var part in query.TrimStart('?').Split('&'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0] == "uddg")
                    return WebUtility.UrlDecode(kv[1]);
            }
        }
        return href.StartsWith("//") ? "https:" + href : href;
    }

    private static string StripHtml(string fragment)
        => Regex.Replace(WebUtility.HtmlDecode(fragment), @"\s+", " ").Trim();

    // ------------------------------------------------------------ file tools

    public string WriteFile(string path, string content)
    {
        var p = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content, new UTF8Encoding(false));
        return $"Wrote {content.Length} chars to {p}.";
    }

    public string ReadFile(string path)
    {
        var p = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (Directory.Exists(p))
            return $"Error: {p} is a directory, not a file.";
        if (!File.Exists(p))
            return $"Error: {p} does not exist.";
        string text;
        try
        {
            text = File.ReadAllText(p, new UTF8Encoding(false, throwOnInvalidBytes: true));
        }
        catch (Exception e) when (e is DecoderFallbackException or IOException)
        {
            return $"Error: {p} is not valid UTF-8 text. ({e.Message})";
        }
        return Truncate(string.IsNullOrWhiteSpace(text) ? "(empty file)" : text);
    }

    /// <summary>Replace old_string with new_string; it must match exactly once.</summary>
    public string EditFile(string path, string oldString, string newString)
    {
        var p = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (!File.Exists(p))
            return $"Error: {p} does not exist. Use write_file to create it.";
        var text = File.ReadAllText(p);
        var count = CountOccurrences(text, oldString);
        if (count == 0)
            return $"Error: old_string not found in {p}. read_file the file first and copy the exact text.";
        if (count > 1)
            return $"Error: old_string appears {count} times in {p}. Include more surrounding lines so it is unique.";
        var index = text.IndexOf(oldString, StringComparison.Ordinal);
        File.WriteAllText(p, text[..index] + newString + text[(index + oldString.Length)..],
            new UTF8Encoding(false));
        return $"Edited {p}: replaced {oldString.Length} chars with {newString.Length} chars.";
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    public string ListFiles(string dir = ".")
    {
        var d = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir));
        if (!Directory.Exists(d))
            return Directory.Exists(d) ? $"Error: {d} is not a directory." : $"Error: {d} does not exist.";
        var entries = Directory.EnumerateFileSystemEntries(d)
            .OrderBy(e => Path.GetFileName(e), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (entries.Count == 0)
            return $"{d} is empty.";
        var lines = entries.Select(e =>
            Directory.Exists(e)
                ? Path.GetFileName(e) + "/"
                : $"{Path.GetFileName(e)} ({new FileInfo(e).Length} bytes)");
        return Truncate(string.Join("\n", lines));
    }

    public string DeleteFile(string path)
    {
        var p = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (Directory.Exists(p) && !File.Exists(p))
            return $"Error: {p} is a directory; delete_file only removes files.";
        if (!File.Exists(p))
            return $"Error: {p} does not exist.";
        File.Delete(p);
        return $"Deleted {p}.";
    }

    public string RunCommand(string cmd, int timeout = 30)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
                Arguments = OperatingSystem.IsWindows() ? $"/c {cmd}" : $"-c {cmd}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return $"Error: could not start command: {cmd}";
            if (!proc.WaitForExit(timeout * 1000))
            {
                proc.Kill(entireProcessTree: true);
                return $"Error: command timed out after {timeout}s: {cmd}";
            }
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(stdout))
                parts.Add(stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr))
                parts.Add("[stderr]\n" + stderr.TrimEnd());
            parts.Add($"[exit code {proc.ExitCode}]");
            return Truncate(string.Join("\n", parts));
        }
        catch (Exception e)
        {
            return $"Error: run_command failed: {e.Message}";
        }
    }

    // ------------------------------------------------------- tool specs (API)

    /// <summary>The tools array sent to the model API.</summary>
    public JsonArray Specs()
    {
        var json = """
        [
          {"type":"function","function":{"name":"web_search","description":"Search the web. Use for current or external facts (latest versions, news, prices) that you may not know.","parameters":{"type":"object","properties":{"query":{"type":"string","description":"The search query."},"max_results":{"type":"integer","description":"Number of results to return (default 5)."}},"required":["query"]}}},
          {"type":"function","function":{"name":"write_file","description":"Create or overwrite a file with the given content.","parameters":{"type":"object","properties":{"path":{"type":"string","description":"File path."},"content":{"type":"string","description":"Full file content."}},"required":["path","content"]}}},
          {"type":"function","function":{"name":"read_file","description":"Read a file's contents.","parameters":{"type":"object","properties":{"path":{"type":"string","description":"File path."}},"required":["path"]}}},
          {"type":"function","function":{"name":"edit_file","description":"Edit an existing file by replacing old_string with new_string. old_string must match the file exactly once.","parameters":{"type":"object","properties":{"path":{"type":"string","description":"File path."},"old_string":{"type":"string","description":"Exact text to replace."},"new_string":{"type":"string","description":"Replacement text."}},"required":["path","old_string","new_string"]}}},
          {"type":"function","function":{"name":"list_files","description":"List the entries of a directory.","parameters":{"type":"object","properties":{"dir":{"type":"string","description":"Directory path (default: current directory)."}}}}},
          {"type":"function","function":{"name":"delete_file","description":"Delete a file (not a directory).","parameters":{"type":"object","properties":{"path":{"type":"string","description":"File path."}},"required":["path"]}}},
          {"type":"function","function":{"name":"run_command","description":"Run a shell command and return stdout, stderr and exit code.","parameters":{"type":"object","properties":{"cmd":{"type":"string","description":"The command to run."},"timeout":{"type":"integer","description":"Timeout in seconds (default 30, max 60)."}},"required":["cmd"]}}}
        ]
        """;
        return (JsonArray)JsonNode.Parse(json)!;
    }
}
