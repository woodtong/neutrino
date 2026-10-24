using System.Text.Json;
using GhostScreen.Daemon;

/// <summary>In-process MCP client used by the integration tests.</summary>
internal sealed class McpTestClient : IDisposable
{
    private readonly TargetManager _manager;
    private readonly QueueReader _reader = new();
    private readonly QueueWriter _writer = new();
    private readonly Task _serverTask;
    private int _nextId;

    public McpTestClient(TargetManager manager)
    {
        _manager = manager;
        var server = new McpServer(manager, _reader, _writer);
        _serverTask = Task.Run(server.RunAsync);
    }

    public JsonElement Call(string method, object? parameters = null, int timeoutMs = 45_000)
    {
        _nextId++;
        var message = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = _nextId,
            ["method"] = method,
        };

        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        _reader.Push(JsonSerializer.Serialize(message));
        var line = _writer.ReadLine(timeoutMs)
            ?? throw new InvalidOperationException($"MCP returned no response for {method}.");
        using var document = JsonDocument.Parse(line);
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException(error.ToString());
        }

        return document.RootElement.GetProperty("result").Clone();
    }

    public JsonElement Tool(string name, object arguments, int timeoutMs = 45_000) =>
        Call("tools/call", new { name, arguments }, timeoutMs);

    public static string Text(JsonElement result)
    {
        foreach (var item in result.GetProperty("content").EnumerateArray())
        {
            if (item.GetProperty("type").GetString() == "text")
            {
                return item.GetProperty("text").GetString() ?? string.Empty;
            }
        }

        throw new InvalidOperationException("No text content.");
    }

    public static string Image(JsonElement result)
    {
        foreach (var item in result.GetProperty("content").EnumerateArray())
        {
            if (item.GetProperty("type").GetString() == "image")
            {
                return item.GetProperty("data").GetString() ?? string.Empty;
            }
        }

        throw new InvalidOperationException("No image content.");
    }

    public void Dispose()
    {
        _reader.Complete();
        try
        {
            _serverTask.Wait(TimeSpan.FromSeconds(20));
        }
        catch
        {
            // Surface the test failure rather than hanging.
        }

        _manager.CloseAll();
    }
}
