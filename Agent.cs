using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace ALAgent;

public sealed class ToolCall
{
    public string Id = "";
    public string Name = "";
    public string ArgumentsJson = "{}";

    public static ToolCall FromJson(JsonNode? node)
    {
        var fn = (JsonObject?)node?["function"];
        return new ToolCall
        {
            Id = (string?)node?["id"] ?? "",
            Name = (string?)fn?["name"] ?? "",
            ArgumentsJson = (string?)fn?["arguments"] ?? "{}",
        };
    }

    public JsonObject ToJson() => new()
    {
        ["id"] = Id,
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = Name,
            ["arguments"] = ArgumentsJson,
        },
    };
}

/// <summary>One entry in the conversation transcript.</summary>
public sealed class Message
{
    public string Role = "";           // system | user | assistant | tool
    public string? Content;            // text (may be null alongside tool_calls)
    public List<ToolCall>? ToolCalls;  // assistant turns only
    public string? ToolCallId;         // tool turns only

    public static Message Text(string role, string content) =>
        new() { Role = role, Content = content };

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["role"] = Role };
        if (Content is not null)
            o["content"] = Content;
        if (ToolCalls is not null)
        {
            var calls = new JsonArray();
            foreach (var tc in ToolCalls)
                calls.Add(tc.ToJson());
            o["tool_calls"] = calls;
        }
        if (ToolCallId is not null)
            o["tool_call_id"] = ToolCallId;
        return o;
    }
}

public sealed class AgentException : Exception
{
    public AgentException(string message) : base(message) { }
}

/// <summary>Raw client for an OpenAI-compatible /chat/completions endpoint.</summary>
public sealed class ApiClient : IDisposable
{
    private readonly AgentConfig _config;
    private readonly HttpClient _http;

    public ApiClient(AgentConfig config)
    {
        _config = config;
        _http = CreateClient(config);
    }

    private static HttpClient CreateClient(AgentConfig config)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(180) };
        c.DefaultRequestVersion = HttpVersion.Version11;
        c.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        c.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.ApiKey);
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", config.UserAgent);
        return c;
    }

    /// <summary>One completion call. Retries transient HTTP failures.</summary>
    public JsonObject ChatCompletion(IReadOnlyList<Message> messages, JsonArray? tools,
        double temperature, int retries = 2)
    {
        var payload = new JsonObject
        {
            ["model"] = _config.Model,
            ["temperature"] = temperature,
            ["messages"] = new JsonArray(messages.Select(m => m.ToJson()).ToArray()),
        };
        if (tools is not null)
        {
            payload["tools"] = tools.DeepClone();
            payload["tool_choice"] = "auto";
        }

        Exception? last = null;
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                var body = payload.ToJsonString();
                if (Environment.GetEnvironmentVariable("AGENT_TRACE") is { } traceDir)
                    File.WriteAllText(Path.Combine(traceDir, $"req_{DateTime.Now:HHmmss_fff}_{attempt}.json"), body);
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = _http.PostAsync(_config.BaseUrl + "/chat/completions", content).Result;
                var responseBody = response.Content.ReadAsStringAsync().Result;
                if (!response.IsSuccessStatusCode)
                {
                    var err = new AgentException($"API error {(int)response.StatusCode}: {TruncateBody(responseBody)}");
                    if ((int)response.StatusCode is 429 or 500 or 502 or 503 or 504 or 405 && attempt < retries)
                    {
                        last = err;
                        Thread.Sleep(3000);
                        continue;
                    }
                    throw err;
                }
                return (JsonObject)JsonNode.Parse(responseBody)!;
            }
            catch (AggregateException ae) when (ae.InnerException is HttpRequestException or TaskCanceledException)
            {
                last = new AgentException($"network error: {ae.InnerException.Message}");
                if (attempt < retries)
                {
                    Thread.Sleep(3000);
                    continue;
                }
            }
        }
        throw last!;
    }

    private static string TruncateBody(string body) =>
        body.Length <= 200 ? body : body[..200];

    public void Dispose() => _http.Dispose();
}

/// <summary>One conversation. Chat() runs a full user turn including tool calls.</summary>
public sealed class AgentSession
{
    public const int MaxStepsDefault = 15; // step cap: a confused model can't loop forever

    private readonly IToolbox _toolbox;
    private readonly ApiClient _api;
    private readonly Dictionary<string, ToolFunc> _toolFuncs;
    private readonly Func<string, bool>? _approver;
    private readonly int _maxSteps;
    private readonly double _temperature;
    private readonly bool _quiet;
    private readonly TextWriter _out;

    public List<Message> Messages { get; } = new();
    public List<string> ToolLog { get; } = new();  // tools that actually executed, in order
    public List<string> Denied { get; } = new();   // risky calls the approver rejected

    public const string SystemPrompt = """
        You are a helpful assistant that lives in a command-line session on the user's real machine, with access to tools.

        How to behave:
        - General knowledge questions: answer directly from your own knowledge. Do NOT call any tool for questions you can already answer (e.g. "what is 2+2?", "what's the capital of Japan?").
        - Current or external facts (latest versions, news, prices, anything you might not know): use web_search.
        - Files: list_files to look around, read_file to inspect, write_file to create, edit_file to change an existing file (prefer it over rewriting the whole file), delete_file to remove.
        - Running things: run_command when the user asks to execute a script or a shell command. You may chain tools across steps (e.g. write_file then run_command).
        - The user approves every write/edit/delete/run before it happens. If a tool result is exactly "User denied this action.", the user said no: do NOT retry the same action in this turn; acknowledge it and, if useful, suggest an alternative.
        - Tool errors are normal: read the error text, fix your arguments or change your approach, and continue. If stuck, explain the problem in plain text.
        - When you have what the user needs, reply with the final answer as plain text and stop calling tools. Plain text with no tool call ends your turn.
        """;

    public AgentSession(IToolbox toolbox, ApiClient api, Func<string, bool>? approver = null,
        int maxSteps = MaxStepsDefault, double temperature = 0.3, bool quiet = false,
        TextWriter? output = null)
    {
        _toolbox = toolbox;
        _api = api;
        _toolFuncs = toolbox.AsFuncs();
        _approver = approver;
        _maxSteps = maxSteps;
        _temperature = temperature;
        _quiet = quiet;
        _out = output ?? Console.Out;
        Messages.Add(Message.Text("system", SystemPrompt));
    }

    /// <summary>One user turn: returns the model's final plain-text answer.</summary>
    public string Chat(string userText)
    {
        Messages.Add(Message.Text("user", userText));

        for (var step = 0; step < _maxSteps; step++)
        {
            var reply = _api.ChatCompletion(Messages, _toolbox.Specs(), _temperature);
            var choice = (JsonObject?)reply["choices"]?[0];
            var message = (JsonObject?)choice?["message"];
            var toolCallNodes = message?["tool_calls"] as JsonArray;
            var content = (string?)message?["content"];

            if (toolCallNodes is null || toolCallNodes.Count == 0)
            {
                // Stop rule: plain text and no tool call = the answer.
                var text = string.IsNullOrWhiteSpace(content) ? "(empty reply)" : content!.Trim();
                Messages.Add(Message.Text("assistant", text));
                return text;
            }

            // Keep a clean copy of the assistant turn for the transcript.
            var entry = new Message
            {
                Role = "assistant",
                Content = string.IsNullOrEmpty(content) ? null : content,
                ToolCalls = toolCallNodes.Select(ToolCall.FromJson).ToList(),
            };
            if (!string.IsNullOrEmpty(content))
                Emit(content!);
            Messages.Add(entry);

            foreach (var node in toolCallNodes)
            {
                var tc = ToolCall.FromJson(node!);
                var result = Execute(tc);
                Messages.Add(new Message
                {
                    Role = "tool",
                    ToolCallId = tc.Id,
                    Content = result,
                });
            }
        }

        return "(hit the step cap before finishing — try a smaller request)";
    }

    /// <summary>Run one tool call; return a string result, error, or denial.</summary>
    private string Execute(ToolCall tc)
    {
        if (!_toolFuncs.TryGetValue(tc.Name, out var func))
            return $"Error: unknown tool '{tc.Name}'.";

        Dictionary<string, string> args;
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(tc.ArgumentsJson) ? "{}" : tc.ArgumentsJson);
            args = [];
            if (node is JsonObject obj)
            {
                foreach (var (key, value) in obj)
                    args[key] = value is null ? "" : (value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value.ToJsonString(null));
            }
        }
        catch (Exception e)
        {
            return $"Error: arguments for {tc.Name} are not valid JSON: {e.Message}";
        }

        if (_toolbox.Risky.Contains(tc.Name))
        {
            var allowed = _approver?.Invoke(Describe(tc.Name, args)) ?? true;
            if (!allowed)
            {
                Denied.Add(tc.Name);
                return "User denied this action.";
            }
        }

        try
        {
            var result = func(args);
            ToolLog.Add(tc.Name);
            return result;
        }
        catch (Exception e) // a tool bug must not kill the loop
        {
            return $"Error: {tc.Name} failed: {e.Message}";
        }
    }

    /// <summary>One-line human summary of a risky action, shown at the y/n gate.</summary>
    public static string Describe(string name, Dictionary<string, string> args) => name switch
    {
        "write_file" => $"I'd like to write {args.GetValueOrDefault("path", "?")}.",
        "edit_file" => $"I'd like to edit {args.GetValueOrDefault("path", "?")}.",
        "delete_file" => $"I'd like to delete {args.GetValueOrDefault("path", "?")}.",
        "run_command" => $"I'd like to run: {args.GetValueOrDefault("cmd", "?")}.",
        _ => $"I'd like to run tool {name}.",
    };

    private void Emit(string text)
    {
        if (!_quiet && !string.IsNullOrWhiteSpace(text))
            _out.WriteLine($"agent › {text.Trim()}");
    }
}

/// <summary>The human-in-the-loop gate: asks a yes/no before any risky action.</summary>
public sealed class HumanGate
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly bool _interactive;

    public HumanGate(TextReader? input = null, TextWriter? output = null)
    {
        _input = input ?? Console.In;
        _output = output ?? Console.Out;
        _interactive = ReferenceEquals(_input, Console.In) && !Console.IsInputRedirected;
    }

    public bool Approve(string description)
    {
        _output.Write($"agent › {description} Allow? (y/n) › ");
        _output.Flush();
        var answer = _input.ReadLine()?.Trim().ToLowerInvariant() ?? "n";
        if (!_interactive)
            _output.WriteLine(answer); // echo piped input so transcripts read naturally
        return answer is "y" or "yes";
    }
}
