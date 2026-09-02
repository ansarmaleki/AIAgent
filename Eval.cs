using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIAgent;

/// <summary>Canned versions of every tool. Records each call; the in-memory
/// filesystem makes write/read/edit behave consistently across turns.</summary>
public sealed class MockToolbox : IToolbox
{
    private readonly Toolbox _real = new();
    private readonly Dictionary<string, string> _commandResults;

    public Dictionary<string, string> Files { get; } = new();
    public List<(string Name, Dictionary<string, string> Args)> Calls { get; } = new();

    public const string Denied = "User denied this action.";

    public MockToolbox(Dictionary<string, string>? files = null,
        Dictionary<string, string>? commandResults = null)
    {
        if (files is not null)
            foreach (var (k, v) in files) Files[k] = v;
        _commandResults = commandResults ?? new();
    }

    public IReadOnlySet<string> Risky => _real.Risky;

    public JsonArray Specs() => _real.Specs();

    public Dictionary<string, ToolFunc> AsFuncs() => new()
    {
        ["web_search"] = a => WebSearch(A(a, "query")),
        ["write_file"] = a => WriteFile(A(a, "path"), A(a, "content")),
        ["read_file"] = a => ReadFile(A(a, "path")),
        ["edit_file"] = a => EditFile(A(a, "path"), A(a, "old_string"), A(a, "new_string")),
        ["list_files"] = a => ListFiles(A(a, "dir", ".")),
        ["delete_file"] = a => DeleteFile(A(a, "path")),
        ["run_command"] = a => RunCommand(A(a, "cmd")),
    };

    private static string A(Dictionary<string, string> a, string key, string fallback = "")
        => a.TryGetValue(key, out var v) && v is not null ? v : fallback;

    private string WebSearch(string query)
    {
        Record("web_search", new Dictionary<string, string> { ["query"] = query });
        return "1. React – The library for web and native user interfaces\n" +
               "   https://react.dev\n" +
               "   Latest stable version: React 19.2.\n" +
               "2. Release history – Wikipedia\n" +
               "   https://en.wikipedia.org/wiki/React_(software)\n" +
               "   React 19.2 is the current stable release.";
    }

    private string WriteFile(string path, string content)
    {
        Record("write_file", new Dictionary<string, string>
        {
            ["path"] = path,
            ["content"] = content,
        });
        Files[path] = content;
        return $"Wrote {content.Length} chars to {path}.";
    }

    private string ReadFile(string path)
    {
        Record("read_file", new Dictionary<string, string> { ["path"] = path });
        return Files.GetValueOrDefault(path, $"Error: {path} does not exist.");
    }

    private string EditFile(string path, string oldString, string newString)
    {
        Record("edit_file", new Dictionary<string, string>
        {
            ["path"] = path,
            ["old_string"] = oldString,
            ["new_string"] = newString,
        });
        if (!Files.ContainsKey(path))
            return $"Error: {path} does not exist. Use write_file to create it.";
        if (!Files[path].Contains(oldString))
            return "Error: old_string not found. read_file the file first.";
        Files[path] = Files[path].Replace(oldString, newString);
        return $"Edited {path}.";
    }

    private string ListFiles(string dir)
    {
        Record("list_files", new Dictionary<string, string> { ["dir"] = dir });
        return Files.Count > 0 ? string.Join("\n", Files.Keys.OrderBy(k => k)) : "(empty directory)";
    }

    private string DeleteFile(string path)
    {
        Record("delete_file", new Dictionary<string, string> { ["path"] = path });
        if (Files.Remove(path))
            return $"Deleted {path}.";
        return $"Error: {path} does not exist.";
    }

    private string RunCommand(string cmd)
    {
        Record("run_command", new Dictionary<string, string> { ["cmd"] = cmd });
        foreach (var (key, output) in _commandResults)
            if (cmd.Contains(key))
                return output;
        return $"(mock) ran: {cmd}\n[exit code 0]";
    }

    private void Record(string name, Dictionary<string, string> args) =>
        Calls.Add((name, args));

    public List<string> Names() => Calls.Select(c => c.Name).ToList();
}

/// <summary>The agent's test suite, run against mock tools and an LLM judge.</summary>
public sealed class EvalSuite
{
    private const string PrimesOutput =
        "2 3 5 7 11 13 17 19 23 29 31 37 41 43 47 53 59 61 67 71\n[exit code 0]";

    private readonly AgentConfig _config;
    private readonly ApiClient _api;

    public EvalSuite(AgentConfig config, ApiClient api)
    {
        _config = config;
        _api = api;
    }

    private AgentSession MakeAgent(MockToolbox toolbox, Func<string, bool>? approver = null) =>
        new(toolbox, _api, approver, temperature: 0.0, quiet: true, output: TextWriter.Null);

    /// <summary>LLM-as-judge: "did this response correctly do X? PASS or FAIL".</summary>
    private (bool Passed, string Detail) Judge(string question, string reply, string criterion)
    {
        var prompt =
            "You are grading an AI agent's final reply.\n" +
            $"The user asked: {question}\n" +
            $"The agent replied: {reply}\n\n" +
            $"Question: {criterion}\n" +
            "Answer with exactly one word: PASS or FAIL.";
        var response = _api.ChatCompletion(
            new List<Message> { Message.Text("user", prompt) }, tools: null, temperature: 0.0);
        var text = ((string?)response["choices"]?[0]?["message"]?["content"] ?? "").ToUpperInvariant();
        var match = Regex.Match(text, @"\b(PASS|FAIL)\b");
        var verdict = match.Success ? match.Groups[1].Value : "FAIL";
        return (verdict == "PASS", $"judge: {verdict}");
    }

    // ------------------------------------------------ single-turn: tool choice

    private (bool, string) TNoTool()
    {
        var tb = new MockToolbox();
        var reply = MakeAgent(tb).Chat("What is 2+2?");
        return (!tb.Names().Any() && reply.Contains('4'),
            $"tools=[{string.Join(",", tb.Names())}] reply={Trunc(reply)}");
    }

    private (bool, string) TChoosesWebSearch()
    {
        var tb = new MockToolbox();
        MakeAgent(tb).Chat("Search the web for the latest stable React version.");
        var ok = tb.Names().FirstOrDefault() == "web_search";
        return (ok, $"tools=[{string.Join(",", tb.Names())}]");
    }

    private (bool, string) TChoosesWriteFile()
    {
        var tb = new MockToolbox();
        MakeAgent(tb).Chat("Create a file named hello.txt containing the word hi.");
        var wrote = tb.Calls.Where(c => c.Name == "write_file").ToList();
        var ok = wrote.Any(c => c.Args.GetValueOrDefault("path", "").Contains("hello.txt"));
        return (ok, $"tools=[{string.Join(",", tb.Names())}]");
    }

    private (bool, string) TChoosesReadFile()
    {
        var tb = new MockToolbox();
        tb.Files["notes.txt"] = "Reminder: the meeting is at 10am on Friday.";
        var reply = MakeAgent(tb).Chat("Read notes.txt and tell me what it says.");
        var read = tb.Names().Contains("read_file");
        var (judged, detail) = Judge("Read notes.txt and tell me what it says.", reply,
            "did the reply convey the reminder about a meeting at 10am on Friday?");
        return (read && judged, $"tools=[{string.Join(",", tb.Names())}]; {detail}");
    }

    private (bool, string) TChoosesListFiles()
    {
        var tb = new MockToolbox();
        tb.Files["a.txt"] = "x";
        MakeAgent(tb).Chat("List the files in the current directory.");
        return (tb.Names().Contains("list_files"),
            $"tools=[{string.Join(",", tb.Names())}]");
    }

    // ------------------------------------------------ multi-turn: behavior

    private (bool, string) TWriteThenRun()
    {
        var toolbox = new MockToolbox(
            commandResults: new Dictionary<string, string> { ["primes.py"] = PrimesOutput });
        const string q = "Write a Python script named primes.py that prints the first 20 " +
                         "prime numbers, then run it and show me the output.";
        var reply = MakeAgent(toolbox).Chat(q);
        var names = toolbox.Names();
        var orderOk = names.Contains("write_file") && names.Contains("run_command") &&
                      names.IndexOf("write_file") < names.IndexOf("run_command");
        var (judged, detail) = Judge(q, reply,
            "did the agent report that it created primes.py and show output " +
            "containing prime numbers such as 2, 3, 5 and 7?");
        return (orderOk && judged, $"order=[{string.Join(",", names)}]; {detail}");
    }

    private (bool, string) TEditFlow()
    {
        var tb = new MockToolbox();
        var agent = MakeAgent(tb);
        agent.Chat("Create a file named report.txt containing exactly: hello world");
        tb.Calls.Clear();
        const string q = "Now edit report.txt so it says goodbye world instead of hello world.";
        var reply = agent.Chat(q);
        var edited = tb.Calls.Any(c => c.Name == "edit_file" &&
                                       c.Args.GetValueOrDefault("path", "").Contains("report.txt"));
        var content = tb.Files.GetValueOrDefault("report.txt", "");
        var contentOk = content.Contains("goodbye world") && !content.Contains("hello world");
        var (judged, detail) = Judge(q, reply,
            "did the agent confirm it edited report.txt so it now says " +
            "goodbye world instead of hello world?");
        return (edited && contentOk && judged,
            $"edit_file={edited}, fs={Trunc(content)}; {detail}");
    }

    private (bool, string) TDenialGate()
    {
        var tb = new MockToolbox();
        var agent = MakeAgent(tb, approver: _ => false);
        const string q = "Create a file named deleteme.txt containing the word bye.";
        var reply = agent.Chat(q); // must not throw
        var attempted = MessagesContainDenial(agent);
        var executed = tb.Names().Contains("write_file");
        var (judged, detail) = Judge(q, reply,
            "did the agent acknowledge it could not create the file because the " +
            "user denied permission, without claiming it created anything?");
        return (attempted && !executed && judged,
            $"attempted={attempted}, executed={executed}; {detail}");
    }

    private static bool MessagesContainDenial(AgentSession agent) =>
        agent.Messages.Any(m => m.Role == "tool" && m.Content == MockToolbox.Denied);

    private (bool, string) TErrorRecovery()
    {
        var tb = new MockToolbox(
            commandResults: new Dictionary<string, string>
            {
                ["deploy.py"] = "python: command not found\n[exit code 127]",
            });
        const string q = "Run the script deploy.py with Python and tell me whether it worked.";
        var reply = MakeAgent(tb).Chat(q);
        var ran = tb.Names().Contains("run_command");
        var (judged, detail) = Judge(q, reply,
            "did the agent report that running deploy.py failed with an error, " +
            "rather than claiming it worked?");
        return (ran && judged, $"ran={ran}; {detail}");
    }

    // ----------------------------------------------------------------- runner

    private static string Trunc(string s) =>
        s.Length <= 80 ? s : s[..80] + "...";

    /// <summary>Run the whole suite; returns 0 only if every selected test passes.</summary>
    public int Run(string? outPath = null, bool debug = false, string? only = null)
    {
        var report = new StringBuilder();
        void W(string line = "")
        {
            Console.WriteLine(line);
            report.AppendLine(line);
        }

        W("AGENT EVAL SUITE");
        W($"model: {_config.Model} via {_config.BaseUrl}");
        W();

        var single = new (string Name, string What, Func<(bool, string)> Test)[]
        {
            ("no_tool_math", "no tool for a trivial question", TNoTool),
            ("chooses_web_search", "web_search for current facts", TChoosesWebSearch),
            ("chooses_write_file", "write_file to create a file", TChoosesWriteFile),
            ("chooses_read_file", "read_file then answer from contents", TChoosesReadFile),
            ("chooses_list_files", "list_files to look around", TChoosesListFiles),
        };
        var multi = new (string Name, string What, Func<(bool, string)> Test)[]
        {
            ("write_then_run", "write_file before run_command, correct final answer", TWriteThenRun),
            ("edit_flow", "create then edit; FS holds the new content", TEditFlow),
            ("denial_gate", "'no' at the gate: no crash, graceful reply", TDenialGate),
            ("error_recovery", "command failure reported, loop survives", TErrorRecovery),
        };

        var results = new List<bool>();

        void RunSection(string title,
            (string Name, string What, Func<(bool, string)> Test)[] tests)
        {
            var selected = only is null ? tests : tests.Where(t => t.Name == only).ToArray();
            if (selected.Length == 0)
                return;
            W($"-- {title} --");
            foreach (var (name, what, test) in selected)
            {
                bool passed;
                string detail;
                try
                {
                    (passed, detail) = test();
                }
                catch (Exception e) // a broken test is a FAIL, not a crash
                {
                    passed = false;
                    detail = $"suite error: {e.Message}";
                    if (debug)
                        W($"   {e}");
                }
                var mark = passed ? "PASS" : "FAIL";
                W($"  [{mark}] {name,-20} {what}");
                if (!passed)
                    W($"         -> {detail}");
                results.Add(passed);
            }
            W();
        }

        RunSection("single-turn: tool choice", single);
        RunSection("multi-turn: behavior (mock tools)", multi);

        W(new string('-', 60));
        var passed = results.Count(r => r);
        var rate = results.Count > 0 ? 100.0 * passed / results.Count : 0.0;
        W($"{passed}/{results.Count} passed ({rate:F1}%)");

        if (outPath is not null)
            File.WriteAllText(outPath, report.ToString(), new UTF8Encoding(false));

        return passed == results.Count ? 0 : 1;
    }
}
