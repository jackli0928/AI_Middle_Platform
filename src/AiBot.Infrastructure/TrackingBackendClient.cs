using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using AiBot.Domain;
using Microsoft.Extensions.Options;

namespace AiBot.Infrastructure;

public sealed class TrackingOptions
{
    public string BaseUrl { get; set; } = "https://new.keytong.com/";
    public string? PlKey { get; set; }
    public int? TrackInfosSuccessCode { get; set; }
    public string TrackInfosSuccessMessage { get; set; } = "查询成功";
    public int PollSeconds { get; set; } = 300;
}

public sealed class TrackingBackendException(string message, bool outcomeUnknown = false) : Exception(message)
{
    public bool OutcomeUnknown { get; } = outcomeUnknown;
}

public sealed record TrackingChangeResult(string[] AcknowledgedAwbs, IReadOnlyList<TrackingPush> Nodes);

public sealed class TrackingBackendClient(HttpClient http, IOptions<TrackingOptions> options)
{
    private readonly TrackingOptions settings = options.Value;
    public bool CanQuery => settings.TrackInfosSuccessCode == 1 ||
        settings.TrackInfosSuccessCode == 0 && !string.IsNullOrWhiteSpace(settings.TrackInfosSuccessMessage);
    public bool Configured => !string.IsNullOrWhiteSpace(settings.PlKey)
        && !settings.PlKey.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
        && Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    public Task<TrackingChangeResult> ChangeAsync(string[] awbs, string email, string type, bool cancel, CancellationToken ct)
    {
        return SendAsync("subscribeInfos", new { awb_no = string.Join(',', awbs), @event = cancel ? "取消" : "订阅", subscribe_type = type, recipients = email },
            awbs, 1, true, ct);
    }

    public async Task<IReadOnlyList<TrackingPush>> QueryAsync(string[] awbs, CancellationToken ct)
    {
        if (!CanQuery) throw new TrackingBackendException("请先配置并验证回执接口成功码。");
        var response = await SendAsync("trackInfos", new { awb_no = string.Join(',', awbs) }, awbs, settings.TrackInfosSuccessCode!.Value, false, ct);
        return response.Nodes;
    }

    private async Task<TrackingChangeResult> SendAsync(string endpoint, object body, string[] awbs, int successCode, bool write, CancellationToken ct)
    {
        if (!Configured) throw new TrackingBackendException("轨迹服务地址或凭证尚未正确配置。");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.BaseUrl), "/priceListAPI/" + endpoint));
        request.Headers.Add("plKey", settings.PlKey);
        request.Content = JsonContent.Create(body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new TrackingBackendException($"轨迹后台返回 HTTP {(int)response.StatusCode}。", write);
            await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, timeout.Token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var root = document.RootElement;
            var code = root.GetProperty("code").GetInt32();
            if (code != successCode)
                throw new TrackingBackendException($"轨迹后台拒绝请求（业务码 {code}）。", write && (code != 0 || awbs.Length > 1));

            if (!write && successCode == 0 && (!root.TryGetProperty("msg", out var message)
                || !string.Equals(message.GetString(), settings.TrackInfosSuccessMessage, StringComparison.Ordinal)))
                throw new TrackingBackendException("回执接口的成功提示未匹配，请核对接口配置。");

            if (!write) return new(awbs, ReadNodes(root, awbs));

            var acknowledged = ReadAcknowledgedAwbs(root, awbs);
            try { return new(acknowledged, ReadNodes(root, acknowledged)); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            { return new(acknowledged, []); }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or
            JsonException or InvalidOperationException or KeyNotFoundException or FormatException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new TrackingBackendException("轨迹接口连接超时或响应格式异常。", write);
        }
    }

    private static string[] ReadAcknowledgedAwbs(JsonElement root, string[] requested)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("awb_nos", out var returned))
            return requested; // The documented success code also supports an aggregate result.

        var acknowledged = returned.EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal);
        return requested.Where(x => acknowledged.Contains(x)).ToArray();
    }

    private static List<TrackingPush> ReadNodes(JsonElement root, string[] awbs)
    {
        List<TrackingPush> records = [];
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("email_groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            throw new JsonException("缺少邮件推送记录数组。");

        foreach (var group in groups.EnumerateArray())
        {
            var awb = group.GetProperty("awb_no").GetString();
            if (awb is null || !awbs.Contains(awb, StringComparer.Ordinal)) continue;
            var address = group.TryGetProperty("query_address", out var link) ? link.GetString() : null;
            if (address?.Length > 1024 || !Uri.TryCreate(address, UriKind.Absolute, out var query) || query.Scheme is not ("http" or "https"))
                address = null;

            foreach (var node in group.GetProperty("nodes").EnumerateArray())
            {
                var name = node.GetProperty("push_node").GetString()?.Trim();
                var time = node.GetProperty("push_time").GetString();
                if (name is null || name.Length is 0 or > 256 || name.Any(char.IsControl)
                    || !DateTime.TryParseExact(time, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw new JsonException("轨迹节点或推送时间无效。");
                records.Add(new() { Awb = awb, Node = name, PushTime = time!, QueryAddress = address, DetectedAt = DateTimeOffset.UtcNow });
            }
        }
        return records;
    }
}
