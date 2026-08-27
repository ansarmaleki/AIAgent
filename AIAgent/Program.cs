//   dotnet run                          interactive REPL (default)
//   dotnet run -- -p "question"         one-shot question
//   dotnet run -- eval [--out F] [--only NAME] [--debug]
//   dotnet run -- script FILE [--out F] scripted input -> transcript
//   dotnet run -- selftest              tool tests, no API key needed
// Config: env vars (AGENT_API_KEY, AGENT_BASE_URL, AGENT_MODEL) or agent/.env.

using ALAgent;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 2;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var config = AgentConfig.Load();
        using var api = new ApiClient(config);
        return Dispatch(args, config, api);
    }

    private static int Dispatch(string[] args, AgentConfig config, ApiClient api)
    {
        var command = args.FirstOrDefault();

        if (command == "selftest")
            return new SelfTest(new Toolbox()).Run();

        if (config.ApiKey.Length == 0)
            return ReportMissingApiKey();

        if (command == "eval")
            return new EvalSuite(config, api).Run(FlagValue(args, "--out"),
                debug: args.Contains("--debug"), only: FlagValue(args, "--only"));

        if (command == "script" && args.Length >= 2)
            return RunScripted(args[1], FlagValue(args, "--out"), config, api);

        return args.Length >= 2 && args[0] == "-p"
            ? RunOneShot(args[1], config, api)
            : RunRepl(config, api);
    }

    private static int ReportMissingApiKey()
    {
        Console.Error.WriteLine(
            "AGENT_API_KEY is not set. Put it in agent/.env " +
            "(AGENT_API_KEY=...) or export it as an environment variable.");
        return ExitUsage;
    }

    private static string? FlagValue(string[] args, string flag) =>
        args.SkipWhile(a => a != flag).Skip(1).FirstOrDefault();

    // ------------------------------------------------------------- the REPL

    private static int RunSession(AgentSession agent, AgentConfig config, TextReader input,
        TextWriter output, bool interactive)
    {
        WriteBanner(config, output);
        while (true)
        {
            output.WriteLine();
            if (interactive)
                output.Write("you › ");
            if (input.ReadLine() is not { } line)
                break;
            var userText = line.Trim();
            if (!interactive)
                output.WriteLine($"you › {userText}");
            if (userText.Length == 0)
                continue;
            if (userText is "/exit" or "/quit")
                break;
            output.WriteLine();
            RunTurn(agent, userText, output);
        }
        return ExitOk;
    }

    private static void WriteBanner(AgentConfig config, TextWriter output)
    {
        output.WriteLine($"agent ready — {config.Model} via {config.BaseUrl}");
        output.WriteLine("risky tools need your approval (y/n). type /exit to quit.");
    }

    private static void RunTurn(AgentSession agent, string userText, TextWriter output)
    {
        try
        {
            output.WriteLine($"agent › {agent.Chat(userText)}");
        }
        catch (Exception e)
        {
            output.WriteLine($"agent › (error: {e.Message})");
        }
    }

    private static AgentSession MakeAgent(AgentConfig config, ApiClient api, TextReader input, TextWriter output) =>
        new(new Toolbox(), api, new HumanGate(input, output).Approve, output: output);

    private static int RunRepl(AgentConfig config, ApiClient api) =>
        RunSession(MakeAgent(config, api, Console.In, Console.Out), config,
            Console.In, Console.Out, interactive: true);

    private static int RunOneShot(string question, AgentConfig config, ApiClient api)
    {
        RunTurn(MakeAgent(config, api, Console.In, Console.Out), question, Console.Out);
        return ExitOk;
    }

    private static int RunScripted(string inputPath, string? outPath, AgentConfig config, ApiClient api)
    {
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"script file not found: {inputPath}");
            return ExitUsage;
        }
        using var input = new StreamReader(inputPath);
        using var buffer = new StringWriter();
        RunSession(MakeAgent(config, api, input, buffer), config, input, buffer, interactive: false);
        WriteTranscript(buffer.ToString(), outPath);
        return ExitOk;
    }

    private static void WriteTranscript(string transcript, string? outPath)
    {
        if (outPath is null)
            Console.Write(transcript);
        else
            File.WriteAllText(outPath, transcript, new System.Text.UTF8Encoding(false));
    }
}
