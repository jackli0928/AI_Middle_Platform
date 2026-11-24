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
        var implicitCm = QuoteDialogue.Apply(new QuoteDialogueState(), "深圳发MUC，2件500kg，每件尺寸60x40x50，普货", new Dictionary<string, object>());
        check(implicitCm.Ready && Math.Abs((double)implicitCm.Calls[0]["volume"] - 0.24) < 0.000001,
            "未标注单位的完整尺寸按厘米计算并进入报价调用");
        var implicitList = QuoteVolumeResolver.Resolve("目的地 MUC，2件500kg，60x40x50+70x50x40", 2, null);
        check(implicitList.Volume is { } listVolume && Math.Abs(listVolume - 0.26) < 0.000001,
            "逐件尺寸清单未标注单位时按厘米计算");
        check(QuoteVolumeResolver.Resolve("目的地 LAX，2件80kg，尺寸待确认，总体积0.24方", 2, 0.24).Volume == 0.24,
            "客户已明确申报总体积时不因未提供尺寸而拦截");
        check(QuoteVolumeResolver.Resolve("目的地 LAX，2件80kg，尺寸60x40x50cm", 2, null).Clarification is not null
            && QuoteVolumeResolver.Resolve("目的地 LAX，2件80kg，每件尺寸60x40x50", 2, 0.24).Volume == 0.24,
            "件数分配不明时追问，未标单位按厘米且与申报体积一致时通过");
        check(QuoteVolumeResolver.Resolve("目的地 LAX，1件80kg，长60cm宽40m高50", 1, null).Clarification is not null,
            "已标注单位互相冲突且有缺失时要求确认");
        check(QuoteVolumeResolver.Resolve("目的地 LAX，2件80kg，2件60x40x50cm，1件70x50cm", 2, null).Volume is null,
            "尺寸清单存在不完整分组时不只按已识别分组报价");
        var conflicting = QuoteVolumeResolver.Resolve("目的地 LAX，2件80kg，每件尺寸60x40x50cm，总体积0.5方", 2, 0.5);
        check(conflicting.Volume is null && conflicting.Clarification!.Contains("0.5") && conflicting.Clarification.Contains("0.24"),
            "申报总体积与尺寸计算值冲突时要求确认");
        check(QuoteVolumeResolver.Resolve("目的地 LAX，2件80kg，每件尺寸60x40x50cm，总体积0.24方", 2, 0.24).Volume == 0.24,
            "申报总体积与尺寸计算值一致时保留客户申报值");
        var shorthand = "2pallets/589.19kgs/120x100x106cm+120x100x102cm，货在东莞，可深圳/广州出，飞HYD，货好了";
        var shorthandFields = QuoteInquiryParser.Parse(shorthand);
        check((int)shorthandFields["piece"] == 2 && (double)shorthandFields["gross_weight"] == 589.19
            && (string)shorthandFields["destination"] == "HYD", "英文托盘、kgs 和飞往机场的简写可识别");
        var shorthandStep = QuoteDialogue.Apply(new QuoteDialogueState(), shorthand, new Dictionary<string, object>());
        check(shorthandStep.Ready && shorthandStep.Calls.Count == 2
            && shorthandStep.Calls[0]["origin"].Equals("SZX") && shorthandStep.Calls[1]["origin"].Equals("CAN")
            && Math.Abs((double)shorthandStep.Calls[0]["volume"] - 2.496) < 0.000001
            && !shorthandStep.Calls[0].ContainsKey("delivery_time") && !shorthandStep.Calls[0].ContainsKey("cargo_type"),
            "完整简写询价按两个可用起运地分别查价，不猜测货好日期和货型");
        var firstTurn = QuoteDialogue.Apply(new QuoteDialogueState(), "目的地 LAX，2件80kg", new Dictionary<string, object>());
        check(!firstTurn.Ready && firstTurn.Question!.Contains("总体积") && firstTurn.State.CustomerMessages.Count == 1,
            "缺体积时只追问缺项并保留本票原文");
        var secondTurn = QuoteDialogue.Apply(firstTurn.State, "每件尺寸60×40×50cm", new Dictionary<string, object>());
        check(secondTurn.Ready && Math.Abs((double)secondTurn.Calls[0]["volume"] - 0.24) < 0.000001
            && (string)secondTurn.Calls[0]["all_content"] == "目的地 LAX，2件80kg\n每件尺寸60×40×50cm",
            "同票多轮补充尺寸后查价，MCP 原文只含本票客户消息");
        var correctedTurn = QuoteDialogue.Apply(secondTurn.State, "改成3件", new Dictionary<string, object>());
        check(correctedTurn.Ready && (int)correctedTurn.Calls[0]["piece"] == 3
            && Math.Abs((double)correctedTurn.Calls[0]["volume"] - 0.36) < 0.000001,
            "明确更正件数时覆盖旧值并重新计算体积");
        check(QuoteDialogue.StartsNewTicket(shorthand, firstTurn.State) && !QuoteDialogue.StartsNewTicket("改成3件", firstTurn.State),
            "完整新询价重新开票，简短更正沿用当前票");
        check(QuoteDialogue.StartsNewTicket("发洛杉矶，2件80kg，0.3方", firstTurn.State,
            new Dictionary<string, object> { ["destination"] = "LAX" }), "模型识别出的完整新询价也不混入待补充旧票");
        check(QuoteDialogue.StartsNewTicket("目的地 SFO，3件90kg", firstTurn.State),
            "新票即使暂缺体积也不会继承待补充旧票资料");
        var frankfurtRoute = QuoteRouteResolver.Parse("深圳发法兰克福，400KG，请报价");
        check(frankfurtRoute.Origin == "SZX" && frankfurtRoute.Destination == "FRA"
            && QuoteRouteResolver.Parse("深圳发SVO，2PLT，400KG").Destination == "SVO"
            && QuoteRouteResolver.Parse("从广州发往洛杉矶").Origin == "CAN"
            && QuoteRouteResolver.Parse("深圳发货了吗").Origin is null,
            "中文起运地发往中文城市或三字码的线路表达可规范化且不误认发货问句");
        var frankfurtFirst = QuoteDialogue.Apply(new QuoteDialogueState(), "深圳发法兰克福，400KG，请报价", new Dictionary<string, object>());
        var frankfurtSecond = QuoteDialogue.Apply(frankfurtFirst.State, "2件，2cbm", new Dictionary<string, object>());
        check(!frankfurtFirst.Ready && frankfurtSecond.Ready && frankfurtSecond.Calls[0]["origin"].Equals("SZX")
            && frankfurtSecond.Calls[0]["destination"].Equals("FRA"),
            "先问缺项再补充时保留标准化起运地和目的地");
        var modelCity = QuoteDialogue.Apply(new QuoteDialogueState(), "起运地深圳，目的地法兰克福，2件400kg2方",
            new Dictionary<string, object> { ["origin"] = "深圳", ["destination"] = "法兰克福" });
        check(modelCity.Ready && modelCity.Calls[0]["origin"].Equals("SZX") && modelCity.Calls[0]["destination"].Equals("FRA"),
            "模型返回中文城市时仍在应用层统一转换明确的机场代码");
        var unknownOrigin = QuoteDialogue.Apply(new QuoteDialogueState(), "汉堡发SVO，2件400kg2方", new Dictionary<string, object>());
        check(!unknownOrigin.Ready && unknownOrigin.Question!.Contains("起运地机场三字码")
            && QuoteRouteResolver.Parse("汉堡发SVO").OriginNeedsConfirmation,
            "明确给出但尚无唯一机场映射的起运城市需确认，不能静默省略");
        var groundedPatch = QuoteDialogue.ParseModelPatch("{\"fields\":{\"destination\":{\"value\":\"LAX\",\"evidence\":\"飞LAX\"},\"piece\":{\"value\":2,\"evidence\":\"不存在\"}}}", "飞LAX");
        check(groundedPatch.ContainsKey("destination") && !groundedPatch.ContainsKey("piece"), "模型字段必须有当前消息中的原文证据");
        var unsafeVolume = QuoteDialogue.ParseModelPatch("{\"fields\":{\"volume\":{\"value\":2.5,\"evidence\":\"120x100x106cm\"},\"piece\":{\"value\":2,\"evidence\":\"两托\"}}}", "两托，120x100x106cm");
        check(!unsafeVolume.ContainsKey("volume") && (int)unsafeVolume["piece"] == 2,
            "模型不能把尺寸当作客户申报体积，中文数量证据可提取件数");
        var cleared = QuoteDialogue.Apply(new QuoteDialogueState { Fields = new() { ["cargo_type"] = "带电", ["server_type"] = "直飞" } },
            "不带电，直飞转飞均可", new Dictionary<string, object> { ["cargo_type"] = "普货", ["server_type"] = "转飞" });
        check(!cleared.State.Fields.ContainsKey("cargo_type") && !cleared.State.Fields.ContainsKey("server_type"),
            "否定旧货型和放宽航班条件时不保留旧筛选，也不接受模型推断");
        var longText = AutoReplyService.LimitUtf8(string.Concat(Enumerable.Repeat("中文😀", 1000)), 4096);
        check(Encoding.UTF8.GetByteCount(longText) <= 4096 && !longText.Contains('\uFFFD'), "AI 超长回复按 UTF-8 截断且不损坏 Unicode 字符");
        var completeQuote = string.Concat(Enumerable.Repeat("方案😀\n", 2000));
        var segments = QuoteReplySegments.Split(completeQuote, 4096);
        check(segments.Count > 1 && segments.All(x => Encoding.UTF8.GetByteCount(x) <= 4096)
            && string.Concat(segments) == completeQuote, "长报价按企微字节限制分条发送且保留全部字符");

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
        handler.Body = "{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"},\"output\":[]}";
        try { await client.CompleteAsync(a, "", "", default); check(false, "达到输出上限应抛出"); }
        catch (AiCompletionException ex) { check(ex.Message.Contains("Token 上限"), "模型未完成时记录安全且可操作的原因"); }

        var quoteConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["QuoteMcp:Transport"] = "http", ["QuoteMcp:Url"] = "https://quote.example.invalid/mcp", ["QuoteMcp:ToolName"] = "get_air_quote" }).Build();
        var quoteHandler = new QuoteHandler();
        using var quoteHttp = new HttpClient(quoteHandler);
        var quoteClient = new QuoteMcpClient(quoteHttp, quoteConfig);
        using var quoteArgs = JsonDocument.Parse("{\"destination\":\"LAX\",\"piece\":10,\"gross_weight\":200,\"volume\":2,\"all_content\":\"10件200kg2方到LAX\"}");
        check(await quoteClient.QuoteAsync(quoteArgs.RootElement, default) == "测试报价 12 CNY/KG"
            && quoteHandler.Tool == "get_air_quote" && quoteHandler.Arguments.GetProperty("piece").GetInt32() == 10,
            "报价 MCP 使用真实工具名和结构化入参，只接受报价正文");
        quoteHandler.ToolBody = "{\"result\":{\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":\"{\\\"status\\\":\\\"invalid_arguments\\\",\\\"missingFields\\\":[\\\"volume\\\"]}\"}]}}";
        try { await quoteClient.QuoteAsync(quoteArgs.RootElement, default); check(false, "MCP 缺项应抛出"); }
        catch (QuoteMcpInputException ex) { check(ex.MissingFields.SequenceEqual(["volume"]), "MCP invalid_arguments 缺项安全传递给追问流程"); }

        var nodeAvailable = false;
        try
        {
            using var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("node", "--version")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (probe is not null) { await probe.WaitForExitAsync(); nodeAvailable = probe.ExitCode == 0; }
        }
        catch (System.ComponentModel.Win32Exception) { }
        if (nodeAvailable)
        {
            var script = Path.Combine(Path.GetTempPath(), "aibot-utf8-" + Guid.NewGuid().ToString("N") + ".mjs");
            try
            {
                await File.WriteAllTextAsync(script, """
                    import { createInterface } from 'node:readline';
                    for await (const line of createInterface({ input: process.stdin })) {
                      const request = JSON.parse(line);
                      const data = request.method === 'initialize'
                        ? { protocolVersion: '2025-03-26', capabilities: {} }
                        : { content: [{ type: 'text', text: JSON.stringify({ status: 'quoted', customerReply: '空运报价｜SZX → MUC\n货物：2 件 · 500 kg · 😀' }) }] };
                      process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id: request.id, result: data }) + '\n');
                    }
                    """, new UTF8Encoding(false));
                var stdioConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["QuoteMcp:Transport"] = "stdio", ["QuoteMcp:ScriptPath"] = script, ["QuoteMcp:ToolName"] = "get_air_quote" }).Build();
                using var stdioHttp = new HttpClient();
                var stdioClient = new QuoteMcpClient(stdioHttp, stdioConfig);
                check(await stdioClient.QuoteAsync(quoteArgs.RootElement, default)
                    == "空运报价｜SZX → MUC\n货物：2 件 · 500 kg · 😀", "本地 MCP stdio 中文和表情按 UTF-8 完整往返");
                await File.WriteAllTextAsync(script, """
                    import { createInterface } from 'node:readline';
                    import { existsSync, writeFileSync } from 'node:fs';
                    const marker = process.argv[1] + '.attempt';
                    for await (const line of createInterface({ input: process.stdin })) {
                      const request = JSON.parse(line);
                      let data;
                      if (request.method === 'initialize') data = { protocolVersion: '2025-03-26' };
                      else if (!existsSync(marker)) {
                        writeFileSync(marker, '1');
                        data = { isError: true, content: [{ type: 'text', text: JSON.stringify({ status: 'connection_error' }) }] };
                      } else data = { content: [{ type: 'text', text: JSON.stringify({ status: 'quoted', customerReply: '重试后报价成功' }) }] };
                      process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id: request.id, result: data }) + '\n');
                    }
                    """, new UTF8Encoding(false));
                check(await stdioClient.QuoteAsync(quoteArgs.RootElement, default) == "重试后报价成功"
                    && File.Exists(script + ".attempt"), "MCP 上游连接瞬时失败时有界重试一次");
            }
            finally { File.Delete(script); File.Delete(script + ".attempt"); }
        }
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
        public string? ToolBody;
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
                "tools/call" => ToolBody ?? "{\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"status\\\":\\\"quoted\\\",\\\"customerReply\\\":\\\"测试报价 12 CNY/KG\\\"}\"}]}}",
                _ => "{}"
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
