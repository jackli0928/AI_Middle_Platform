namespace AiBot.Domain;

public sealed class AiModelEndpoint
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Name { get; private set; } = "";
    public string Provider { get; private set; } = "";
    public string Endpoint { get; private set; } = "";
    public string ModelId { get; private set; } = "";
    public string ApiStyle { get; private set; } = "responses";
    public string ProtectedApiKey { get; private set; } = "";
    public bool Enabled { get; private set; }
    public int TimeoutSeconds { get; private set; } = 15;
    public int MaxOutputTokens { get; private set; } = 1024;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public void Update(string name, string provider, string endpoint, string modelId, string apiStyle, string? protectedKey, bool enabled, int timeoutSeconds, int maxOutputTokens)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 || string.IsNullOrWhiteSpace(provider) || provider.Trim().Length > 80) throw new ArgumentException("请填写模型名称和提供方，最多 80 字。");
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || endpoint.Length > 1024)
            throw new ArgumentException("请填写完整 HTTP/HTTPS 接口地址，不要在地址中包含密钥、查询参数或片段。");
        if (string.IsNullOrWhiteSpace(modelId) || modelId.Trim().Length > 256) throw new ArgumentException("请填写接口实际支持的模型 ID，最多 256 字。");
        if (apiStyle is not ("responses" or "chat") || timeoutSeconds is < 5 or > 30 || maxOutputTokens is < 64 or > 4096) throw new ArgumentException("接口格式、超时或输出上限无效。");
        if (enabled && string.IsNullOrEmpty(protectedKey) && string.IsNullOrEmpty(ProtectedApiKey)) throw new ArgumentException("启用模型接口前请配置 API Key。");
        Name = name.Trim(); Provider = provider.Trim(); Endpoint = uri.AbsoluteUri; ModelId = modelId.Trim(); ApiStyle = apiStyle;
        if (!string.IsNullOrEmpty(protectedKey)) ProtectedApiKey = protectedKey;
        Enabled = enabled; TimeoutSeconds = timeoutSeconds; MaxOutputTokens = maxOutputTokens; UpdatedAt = DateTimeOffset.UtcNow;
    }
}
