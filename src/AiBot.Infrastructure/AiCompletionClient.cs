using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiBot.Application;
using AiBot.Domain;

namespace AiBot.Infrastructure;

public sealed class AiCompletionClient(HttpClient http, ISecretProtector secrets) : IAiCompletionClient, IMailAiCompletionClient
{
    public Task<string> CompleteAsync(AiModelEndpoint model, string prompt, string message, CancellationToken ct)
    {
        var instructions = "你是企业微信中的工作助手。请用中文简洁回答当前用户问题，不确定时明确说明，不虚构事实。回复控制在 1000 字以内。"
            + (string.IsNullOrWhiteSpace(prompt) ? "" : "\n" + prompt);
        return SendAsync(model, instructions, message, ct);
    }

    public Task<string> CompleteMailAsync(AiModelEndpoint model, string prompt, string data, CancellationToken ct) =>
        SendAsync(model, prompt, data, ct);

    private async Task<string> SendAsync(AiModelEndpoint model, string instructions, string message, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(model.TimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, model.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.Unprotect(model.ProtectedApiKey));
        request.Content = model.ApiStyle == "responses"
            ? JsonContent.Create(new { model = model.ModelId, instructions, input = message, max_output_tokens = model.MaxOutputTokens, store = false })
            : JsonContent.Create(new { model = model.ModelId, messages = new[] { new { role = "system", content = instructions }, new { role = "user", content = message } }, max_tokens = model.MaxOutputTokens, stream = false });
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new AiCompletionException($"接口返回 HTTP {(int)response.StatusCode}，切换下一模型。");
            await response.Content.LoadIntoBufferAsync(1024 * 1024, timeout.Token);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var root = json.RootElement;
            if (model.ApiStyle == "responses")
            {
                if (root.TryGetProperty("status", out var status) && status.GetString() != "completed")
                {
                    var reason = root.TryGetProperty("incomplete_details", out var details) && details.ValueKind == JsonValueKind.Object
                        && details.TryGetProperty("reason", out var value) && value.ValueKind == JsonValueKind.String
                        ? value.GetString() : null;
                    throw new AiCompletionException(reason switch
                    {
                        "max_output_tokens" => "模型输出达到配置的 Token 上限，未生成完整结果。",
                        "content_filter" => "模型输出被内容过滤，未生成完整结果。",
                        _ => "模型生成未完成，切换下一模型。"
                    });
                }
                if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                {
                    var texts = output.EnumerateArray().Where(x => x.TryGetProperty("type", out var type) && type.GetString() == "message" && x.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        .SelectMany(x => x.GetProperty("content").EnumerateArray()).Where(x => x.TryGetProperty("type", out var type) && type.GetString() == "output_text" && x.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetProperty("text").GetString());
                    var result = string.Join("\n", texts); if (!string.IsNullOrWhiteSpace(result)) return result;
                }
            }
            else if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String && finish.GetString() is "length" or "content_filter") throw new AiCompletionException("模型输出被截断或不可用，切换下一模型。");
                if (choice.TryGetProperty("message", out var reply) && reply.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(content.GetString())) return content.GetString()!;
            }
            throw new AiCompletionException("接口未返回有效文本，切换下一模型。");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        { throw new AiCompletionException("接口连接或响应格式异常，切换下一模型。"); }
    }
}
