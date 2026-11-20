using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace AiBot.Infrastructure;

public sealed class QuoteMcpInputException(IReadOnlyList<string> missingFields) : Exception("报价工具要求补充或确认询价条件。")
{
    public IReadOnlyList<string> MissingFields { get; } = missingFields;
}

public sealed class QuoteMcpClient(HttpClient http, IConfiguration configuration)
{
    public bool Configured => !string.IsNullOrWhiteSpace(configuration["QuoteMcp:ToolName"])
        && (configuration["QuoteMcp:Transport"] == "stdio" && File.Exists(configuration["QuoteMcp:ScriptPath"])
            || configuration["QuoteMcp:Transport"] != "stdio" && Uri.TryCreate(configuration["QuoteMcp:Url"], UriKind.Absolute, out var url)
                && url.Scheme is "https" or "http");

    public async Task<string> QuoteAsync(JsonElement arguments, CancellationToken ct)
    {
        if (!Configured) throw new InvalidOperationException("报价 MCP 尚未配置。");
        var url = configuration["QuoteMcp:Url"]!;
        var tool = configuration["QuoteMcp:ToolName"]!;
        var token = configuration["QuoteMcp:Token"];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        string? session = null;
        async Task<JsonDocument> CallAsync(string method, object? parameters)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
            request.Content = JsonContent.Create(new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = parameters });
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var values)) session = values.FirstOrDefault();
            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            if (body.Length > 1024 * 1024) throw new InvalidOperationException("报价 MCP 响应过大。");
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                var data = body.Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.StartsWith("data:", StringComparison.Ordinal))
                    .Select(x => x[5..].Trim()).LastOrDefault(x => x.StartsWith('{'));
                body = data ?? throw new InvalidOperationException("报价 MCP 未返回结果。");
            }
            var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out _)) { json.Dispose(); throw new InvalidOperationException("报价 MCP 调用失败。"); }
            return json;
        }

        if (configuration["QuoteMcp:Transport"] == "stdio") return await QuoteStdioAsync(tool, arguments, deadline.Token);
        using (await CallAsync("initialize", new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "Li.AiBot", version = "1.0" } })) { }
        using (var notification = new HttpRequestMessage(HttpMethod.Post, url))
        {
            notification.Headers.Accept.ParseAdd("application/json");
            notification.Headers.Accept.ParseAdd("text/event-stream");
            if (!string.IsNullOrWhiteSpace(token)) notification.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (session is not null) notification.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
            notification.Content = JsonContent.Create(new { jsonrpc = "2.0", method = "notifications/initialized" });
            using var response = await http.SendAsync(notification, deadline.Token);
            response.EnsureSuccessStatusCode();
        }
        using var result = await CallAsync("tools/call", new { name = tool, arguments });
        var payload = result.RootElement.GetProperty("result");
        ThrowIfToolError(payload);
        if (payload.TryGetProperty("structuredContent", out var noQuote) && noQuote.ValueKind == JsonValueKind.Object
            && noQuote.TryGetProperty("status", out var emptyStatus) && emptyStatus.GetString() == "no_quotes")
            return "当前条件下暂无运价";
        if (payload.TryGetProperty("structuredContent", out var structured) && structured.ValueKind == JsonValueKind.Object
            && structured.TryGetProperty("status", out var quoteStatus) && quoteStatus.GetString() == "quoted"
            && structured.TryGetProperty("customerReply", out var reply) && reply.ValueKind == JsonValueKind.String)
            return ValidateReply(reply.GetString());
        if (payload.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var raw = content.EnumerateArray().Where(x => x.TryGetProperty("type", out var type) && type.GetString() == "text")
                .Select(x => x.GetProperty("text").GetString()).FirstOrDefault();
            if (raw is not null)
            {
                using var parsed = JsonDocument.Parse(raw);
                if (parsed.RootElement.TryGetProperty("status", out var quotedStatus) && quotedStatus.GetString() == "quoted"
                    && parsed.RootElement.TryGetProperty("customerReply", out var text) && text.ValueKind == JsonValueKind.String)
                    return ValidateReply(text.GetString());
                if (parsed.RootElement.TryGetProperty("status", out var status) && status.GetString() == "no_quotes")
                    return "当前条件下暂无运价";
            }
        }
        throw new InvalidOperationException("报价工具未返回 customerReply。");
    }

    private async Task<string> QuoteStdioAsync(string tool, JsonElement arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo("node") { RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(configuration["QuoteMcp:ScriptPath"]!);
        start.Environment["AIR_QUOTE_PL_KEY"] = configuration["QuoteMcp:Token"] ?? "";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动报价 MCP。");
        try
        {
            async Task<JsonDocument> ExchangeAsync(string method, object parameters)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = parameters }));
                var line = await process.StandardOutput.ReadLineAsync(ct) ?? throw new InvalidOperationException("报价 MCP 已退出。");
                var json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("error", out _)) { json.Dispose(); throw new InvalidOperationException("报价 MCP 调用失败。"); }
                return json;
            }
            using (await ExchangeAsync("initialize", new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "Li.AiBot", version = "1.0" } })) { }
            using var result = await ExchangeAsync("tools/call", new { name = tool, arguments });
            var payload = result.RootElement.GetProperty("result");
            ThrowIfToolError(payload);
            var raw = payload.GetProperty("content")[0].GetProperty("text").GetString()!;
            using var quote = JsonDocument.Parse(raw);
            var data = quote.RootElement;
            if (data.GetProperty("status").GetString() == "no_quotes") return "当前条件下暂无运价";
            if (data.GetProperty("status").GetString() != "quoted") throw new InvalidOperationException("报价 MCP 未返回有效报价。");
            return ValidateReply(data.GetProperty("customerReply").GetString());
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static string ValidateReply(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply) || reply.Length > 16384) throw new InvalidOperationException("报价回复为空或过长。");
        return reply;
    }

    private static void ThrowIfToolError(JsonElement payload)
    {
        if (!payload.TryGetProperty("isError", out var error) || error.ValueKind != JsonValueKind.True) return;
        JsonElement data = default;
        JsonDocument? document = null;
        try
        {
            if (payload.TryGetProperty("structuredContent", out var structured) && structured.ValueKind == JsonValueKind.Object)
                data = structured;
            else if (payload.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                var raw = content.EnumerateArray().FirstOrDefault(x => x.TryGetProperty("type", out var type)
                    && type.GetString() == "text" && x.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String);
                if (raw.ValueKind == JsonValueKind.Object) document = JsonDocument.Parse(raw.GetProperty("text").GetString()!);
                if (document is not null) data = document.RootElement;
            }
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("status", out var status)
                && status.GetString() == "invalid_arguments")
            {
                var missing = data.TryGetProperty("missingFields", out var fields) && fields.ValueKind == JsonValueKind.Array
                    ? fields.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
                        .Where(x => x is "destination" or "piece" or "gross_weight" or "volume" or "all_content").Distinct().ToArray()
                    : [];
                throw new QuoteMcpInputException(missing);
            }
        }
        catch (JsonException) { /* Malformed tool errors are reported without copying provider content. */ }
        finally { document?.Dispose(); }
        throw new InvalidOperationException("报价 MCP 返回错误。");
    }
}
