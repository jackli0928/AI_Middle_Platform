using System.Net;
using System.Text;
using System.Text.Json;
using AiBot.Application;
using AiBot.Domain;
using AiBot.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace AiBot.Tests;

internal static class AutomationTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var robots = new MemoryRepository(); var repository = new MemoryAutomationRepository(robots);
        var secrets = new SecretProtector(new EphemeralDataProtectionProvider()); var ai = new FakeAiClient(); var gateway = new FakeGateway();
        var service = new AutoReplyService(repository, robots, secrets, ai, gateway);
        var robot = Robot.Create("回复测试", "auto-bot", secrets.Protect("test-secret"), "", true); robots.Add(robot);
        ReceivedMessage Message(string chat = "single", string sender = "user", string type = "text", string payload = "{}", string text = "你好") =>
            new(robot.Id, robot.Name, Guid.NewGuid().ToString(), "req", sender, chat == "group" ? "chat-test" : "", chat, type, text, payload, null);
        Task Handle(ReceivedMessage message) => service.HandleAsync(robot.Id, robot.BotId, message, default);
        await Handle(Message()); check(gateway.Calls == 0 && repository.Replies.Count == 0, "新机器人默认不自动回复");
        var global = new KnowledgeEntry(); global.Update(null, "你好", "hello", "全局答案", "exact", 100, true); repository.Add(global);
        var scoped = new KnowledgeEntry(); scoped.Update(robot.Id, "你好", "", "专属答案", "exact", 100, true); repository.Add(scoped);
        check(KnowledgeMatcher.Find(repository.Entries, robot.Id, "  ＨＥＬＬＯ  ") == global, "知识库规范化全角、大小写和首尾空格");
        check(KnowledgeMatcher.Find(repository.Entries, robot.Id, "你好") == scoped && KnowledgeMatcher.Find(repository.Entries, Guid.NewGuid(), "你好") == global, "同优先级使用机器人专属知识，其他机器人仅使用全局知识");
        robot.ConfigureAutoReply(true, "knowledge", true, "", []);
        var first = Message(); await Handle(first); await Handle(first);
        check(gateway.Calls == 1 && robots.Messages.Single().Content == "专属答案" && repository.Replies.Single().Status == "accepted", "知识库匹配后只发送一次，重复事件不重发");
        await Handle(Message("group")); check(gateway.LastTargetType == "group" && gateway.LastTargetId == "chat-test", "群聊自动回复发送到原 Chat ID");
        var before = gateway.Calls;
        robot.ConfigureAutoReply(true, "knowledge", false, "", []); await Handle(Message("group"));
        await Handle(Message(sender: robot.BotId)); await Handle(Message(payload: "{\"from\":{\"is_bot\":true}}")); await Handle(Message(type: "image"));
        check(gateway.Calls == before, "关闭群聊、机器人自身及非文本消息不触发回复");
        await Handle(Message(text: "没有匹配的内容")); check(repository.Replies.Last().Status == "skipped" && gateway.Calls == before, "知识库未匹配时不发送兜底废话");
        var a = new AiModelEndpoint(); a.Update("模型一", "测试", "https://example.invalid/v1/responses", "model-a", "responses", secrets.Protect("test-key"), true, 5, 128);
        var b = new AiModelEndpoint(); b.Update("模型二", "测试", "https://example.invalid/chat/completions", "model-b", "chat", secrets.Protect("test-key"), true, 5, 128);
        repository.Add(a); repository.Add(b); robot.ConfigureAutoReply(true, "ai", true, "请简洁回复", [a.Id, b.Id]);
        ai.Generate = model => model.Id == a.Id ? throw new AiCompletionException("模拟失败") : "模型二的答案";
        await Handle(Message());
        check(ai.Calls.SequenceEqual(new[] { a.Id, b.Id }) && gateway.Calls == before + 1 && repository.Replies.Last().ModelName == b.Name, "AI 按优先级尝试，首选失败后切换次选且只发送最终答案");
        before = gateway.Calls; ai.Generate = _ => throw new OperationCanceledException(); await Handle(Message());
        check(gateway.Calls == before && repository.Replies.Last().Status == "failed", "所有模型超时后不发送，记录失败原因");
        ai.Generate = _ => { robot.ConfigureAutoReply(false, "ai", true, "", [a.Id]); return "迟到答案"; }; await Handle(Message());
        check(gateway.Calls == before && repository.Replies.Last().Status == "skipped", "生成期间关闭自动回复阻止迟到发送");
        robot.ConfigureAutoReply(true, "ai", true, "", [a.Id]); ai.Generate = _ => "模拟回答"; gateway.Result = new(false, "send-unknown", "模拟回执超时");
        var uncertain = Message(); await Handle(uncertain); await Handle(uncertain);
        check(gateway.Calls == before + 1 && repository.Replies.Last().Status == "unknown", "发送回执未知时记录状态并阻止重试");
        robot.ConfigureAutoReply(true, "quote", false, "", [a.Id]);
        before = gateway.Calls;
        var quoteMessage = Message(text: "上海到洛杉矶，10 件，200 kg，2 m³");
        await Handle(quoteMessage); await Handle(quoteMessage);
        await Handle(Message("group")); await Handle(Message(type: "voice"));
        check(gateway.Calls == before && repository.Replies.Count(x => x.ReceivedMessageId == quoteMessage.Id) == 1
            && repository.Replies.Last(x => x.ReceivedMessageId == quoteMessage.Id).Status == "queued", "报价单聊只登记一次后台任务，不阻塞接收或直接发送");
        robot.ConfigureAutoReply(true, "quote", true, "", [a.Id]);
        var groupQuote = Message("group", text: "目的地 LAX，10件，200kg，2方");
        await Handle(groupQuote); await Handle(groupQuote);
        check(repository.Replies.Count(x => x.ReceivedMessageId == groupQuote.Id && x.Status == "queued") == 1,
            "开启群聊回复后，群聊询价只登记一次报价任务");
        robot.ConfigureAutoReply(true, "quote", false, "", [a.Id]);
        var disabledGroupQuote = Message("group", text: "目的地 LAX，10件，200kg，2方");
        await Handle(disabledGroupQuote);
        check(repository.Replies.All(x => x.ReceivedMessageId != disabledGroupQuote.Id), "关闭群聊回复后不登记群聊报价任务");
        robot.ConfigureAutoReply(true, "quote", false, "", []);
        check(robot.GetModelPriority().Count == 0, "报价模式可以不配置 AI 模型");
        var parsedInquiry = QuoteInquiryParser.Parse("上海到洛杉矶，10件，200kg，2方");
        check((string)parsedInquiry["destination"] == "洛杉矶" && (int)parsedInquiry["piece"] == 10
            && (double)parsedInquiry["gross_weight"] == 200 && (double)parsedInquiry["volume"] == 2,
            "报价 MCP 可从明确格式询价提取必填字段");
        check(!QuoteInquiryParser.Parse("到洛杉矶，10件，200kg").ContainsKey("volume"), "缺失体积时不会猜测报价参数");
        var perPiece = QuoteVolumeResolver.Resolve("目的地 LAX，2件80kg，每件尺寸60×40×50cm", 2, null);
        check(perPiece.Pieces == 2 && Math.Abs(perPiece.Volume!.Value - 0.24) < 0.000001 && perPiece.Clarification is null,
            "完整单组尺寸与每件数量可计算总体积");
        var grouped = QuoteVolumeResolver.Resolve("目的地 LAX，总件数5件，80kg，2件 60x40x50cm，3件 50x40x40cm", 5, null);
        check(grouped.Pieces == 5 && Math.Abs(grouped.Volume!.Value - 0.48) < 0.000001,
            "多组尺寸按各组件数计算并核对总件数");
        var listed = QuoteVolumeResolver.Resolve("目的地 LAX，2件80kg，尺寸60x40x50cm，70x50x40cm", 2, null);
        check(listed.Pieces == 2 && Math.Abs(listed.Volume!.Value - 0.26) < 0.000001,
            "尺寸清单与总件数一致时每组按一件计算");
        var labelled = QuoteVolumeResolver.Resolve("目的地 LAX，1件80kg，长60cm宽40cm高50cm", 1, null);
        check(labelled.Volume is { } labelledVolume && Math.Abs(labelledVolume - 0.12) < 0.000001,
            "长宽高分别标注单位也可计算总体积");
        var axisUnits = QuoteVolumeResolver.Resolve("目的地 LAX，2件80kg，每件尺寸60cm×40cm×50cm", 2, null);
        check(axisUnits.Volume is { } axisVolume && Math.Abs(axisVolume - 0.24) < 0.000001,
            "三边分别标注单位的尺寸可计算总体积");
        check(QuoteVolumeResolver.Resolve("目的地 LAX，2件80kg，尺寸待确认，总体积0.24方", 2, 0.24).Volume == 0.24,
            "客户已明确申报总体积时不因未提供尺寸而拦截");
        check(QuoteVolumeResolver.Resolve("目的地 LAX，2件80kg，尺寸60x40x50cm", 2, null).Clarification is not null
            && QuoteVolumeResolver.Resolve("目的地 LAX，2件80kg，每件尺寸60x40x50", 2, 0.24).Volume is null,
            "件数分配或尺寸单位不明时不采用模型推测的体积");
        check(QuoteVolumeResolver.Resolve("目的地 LAX，2件80kg，2件60x40x50cm，1件70x50cm", 2, null).Volume is null,
            "尺寸清单存在不完整分组时不只按已识别分组报价");
        var conflicting = QuoteVolumeResolver.Resolve("目的地 LAX，2件80kg，每件尺寸60x40x50cm，总体积0.5方", 2, 0.5);
        check(conflicting.Volume is null && conflicting.Clarification!.Contains("0.5") && conflicting.Clarification.Contains("0.24"),
            "申报总体积与尺寸计算值冲突时要求确认");
        check(QuoteVolumeResolver.Resolve("目的地 LAX，2件80kg，每件尺寸60x40x50cm，总体积0.24方", 2, 0.24).Volume == 0.24,
            "申报总体积与尺寸计算值一致时保留客户申报值");
        var longText = AutoReplyService.LimitUtf8(string.Concat(Enumerable.Repeat("中文😀", 1000)), 4096);
        check(Encoding.UTF8.GetByteCount(longText) <= 4096 && !longText.Contains('\uFFFD'), "AI 超长回复按 UTF-8 截断且不损坏 Unicode 字符");

        var handler = new ModelHandler(); using var http = new HttpClient(handler); var client = new AiCompletionClient(http, secrets);
        handler.Body = "{\"status\":\"completed\",\"output\":[{\"type\":\"reasoning\",\"summary\":[]},{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Responses答案\"}]}]}";
        check(await client.CompleteAsync(a, "规则", "用户问题", default) == "Responses答案", "Responses 解析最终文本，不转发推理内容");
        using (var request = JsonDocument.Parse(handler.Request))
            check(request.RootElement.GetProperty("model").GetString() == "model-a" && !request.RootElement.GetProperty("store").GetBoolean() && request.RootElement.GetProperty("input").GetString() == "用户问题", "Responses 请求使用配置模型且禁用服务端存储");
        handler.Body = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"Chat答案\"}}]}";
        check(await client.CompleteAsync(b, "", "问题", default) == "Chat答案", "兼容 Chat Completions 响应可生成回复");
        using (var request = JsonDocument.Parse(handler.Request)) check(request.RootElement.GetProperty("messages").GetArrayLength() == 2 && !request.RootElement.GetProperty("stream").GetBoolean(), "Chat 仅发送当前问题与系统提示，不携带历史会话");
        handler.Code = HttpStatusCode.Unauthorized; handler.Body = "secret-provider-error-test-key";
        try { await client.CompleteAsync(a, "", "", default); check(false, "HTTP 错误应抛出"); }
        catch (AiCompletionException ex) { check(ex.Message.Contains("401") && !ex.Message.Contains("test-key"), "模型接口错误不记录原始响应及密钥"); }
        handler.Code = HttpStatusCode.OK; handler.Body = "{\"status\":\"incomplete\",\"output\":[]}";
        try { await client.CompleteAsync(a, "", "", default); check(false, "未完成生成应抛出"); }
        catch (AiCompletionException) { check(true, "模型未完成生成时允许切换下一模型"); }

        var quoteConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["QuoteMcp:Transport"] = "http", ["QuoteMcp:Url"] = "https://quote.example.invalid/mcp", ["QuoteMcp:ToolName"] = "get_air_quote" }).Build();
        var quoteHandler = new QuoteHandler();
        using var quoteHttp = new HttpClient(quoteHandler);
        var quoteClient = new QuoteMcpClient(quoteHttp, quoteConfig);
        using var quoteArgs = JsonDocument.Parse("{\"destination\":\"LAX\",\"piece\":10,\"gross_weight\":200,\"volume\":2,\"all_content\":\"10件200kg2方到LAX\"}");
        check(await quoteClient.QuoteAsync(quoteArgs.RootElement, default) == "测试报价 12 CNY/KG"
            && quoteHandler.Tool == "get_air_quote" && quoteHandler.Arguments.GetProperty("piece").GetInt32() == 10,
            "报价 MCP 使用真实工具名和结构化入参，只接受报价正文");
    }
    private sealed class ModelHandler : HttpMessageHandler
    {
        public string Body = "{}"; public string Request = ""; public HttpStatusCode Code = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Request = await request.Content!.ReadAsStringAsync(ct); return new(Code) { Content = new StringContent(Body, Encoding.UTF8, "application/json") }; }
    }
    private sealed class QuoteHandler : HttpMessageHandler
    {
        public string Tool = "";
        public JsonElement Arguments;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var method = json.RootElement.GetProperty("method").GetString();
            if (method == "tools/call")
            {
                var parameters = json.RootElement.GetProperty("params");
                Tool = parameters.GetProperty("name").GetString()!;
                Arguments = parameters.GetProperty("arguments").Clone();
            }
            var body = method switch
            {
                "initialize" => "{\"result\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{}}}",
                "tools/call" => "{\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"status\\\":\\\"quoted\\\",\\\"customerReply\\\":\\\"测试报价 12 CNY/KG\\\"}\"}]}}",
                _ => "{}"
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
