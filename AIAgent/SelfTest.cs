namespace ALAgent;

internal sealed class SelfTest
{
    private readonly Toolbox _tools;
    private readonly List<(string Name, bool Passed, string Detail)> _results = new();

    private string Dir { get; }
    private string File { get; }
    private string Missing { get; }

    public SelfTest(Toolbox tools)
    {
        _tools = tools;
        var root = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.Parent!.FullName;
        Dir = Path.Combine(root, "workspace", "selftest");
        File = Path.Combine(Dir, "roundtrip.txt");
        Missing = Path.Combine(Dir, "missing.txt");
    }

    public int Run()
    {
        Directory.CreateDirectory(Dir);
        try
        {
            FileRoundTrip();
            EditErrorPaths();
            MissingFileErrors();
            Listing();
            Commands();
            Deletion();
            WebSearch();
            ToolDispatch();
        }
        finally
        {
            Directory.Delete(Dir, recursive: true);
        }
        return Report();
    }

    private void FileRoundTrip()
    {
        _tools.WriteFile(File, "alpha beta gamma");
        Check("write_read_roundtrip", _tools.ReadFile(File) == "alpha beta gamma",
            $"read: {_tools.ReadFile(File)}");
        var edit = _tools.EditFile(File, "beta", "delta");
        Check("edit_file", _tools.ReadFile(File) == "alpha delta gamma", edit);
    }

    private void EditErrorPaths()
    {
        Check("edit_not_found_is_error", _tools.EditFile(File, "nope", "x").StartsWith("Error"));
        _tools.WriteFile(File, "dup dup");
        Check("edit_ambiguous_is_error", _tools.EditFile(File, "dup", "x").StartsWith("Error"));
        _tools.WriteFile(File, "one of a kind");
    }

    private void MissingFileErrors()
    {
        Check("edit_missing_file_is_error", _tools.EditFile(Missing, "a", "b").StartsWith("Error"));
        Check("read_missing_is_error", _tools.ReadFile(Missing).StartsWith("Error"));
    }

    private void Listing()
    {
        var listing = _tools.ListFiles(Dir);
        Check("list_files", listing.Contains("roundtrip.txt"), listing);
    }

    private void Commands()
    {
        var ok = _tools.RunCommand("echo selftest-ok", 10);
        Check("run_command_ok", ok.Contains("selftest-ok") && ok.Contains("[exit code 0]"), ok);
        var fail = _tools.RunCommand("cmd /c exit 3", 10);
        Check("run_command_nonzero_exit", fail.Contains("[exit code 3]"), fail);
        var timedOut = _tools.RunCommand("ping -n 30 127.0.0.1", 2);
        Check("run_command_timeout", timedOut.StartsWith("Error: command timed out"), timedOut);
    }

    private void Deletion()
    {
        var deleted = _tools.DeleteFile(File);
        Check("delete_file", !System.IO.File.Exists(File) && deleted.StartsWith("Deleted"), deleted);
        Check("delete_missing_is_error", _tools.DeleteFile(File).StartsWith("Error"));
    }

    private void WebSearch()
    {
        var mock = new MockToolbox();
        var search = mock.AsFuncs()["web_search"](
            new Dictionary<string, string> { ["query"] = "python programming language" });
        Check("web_search", !search.StartsWith("Error") && search.Contains("1."), search);
        Check("web_search_recorded", mock.Names().Contains("web_search"), $"calls=[{string.Join(",", mock.Names())}]");
    }

    private void ToolDispatch()
    {
        _tools.AsFuncs()["write_file"](new Dictionary<string, string> { ["path"] = File, ["content"] = "42" });
        Check("tool_func_dispatch", _tools.ReadFile(File) == "42");
    }

    private void Check(string name, bool passed, string detail = "") =>
        _results.Add((name, passed, detail));

    private int Report()
    {
        foreach (var (name, passed, detail) in _results)
        {
            Console.WriteLine($"  [{(passed ? "PASS" : "FAIL")}] {name}");
            if (!passed)
                Console.WriteLine($"         -> {detail}");
        }
        var passedCount = _results.Count(r => r.Passed);
        Console.WriteLine($"{passedCount}/{_results.Count} tool selftests passed " +
                          $"({100.0 * passedCount / _results.Count:F1}%)");
        return passedCount == _results.Count ? 0 : 1;
    }
}
