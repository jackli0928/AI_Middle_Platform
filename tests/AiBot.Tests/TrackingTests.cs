using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiBot.Application;
using AiBot.Domain;
using AiBot.Infrastructure;
using AiBot.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiBot.Tests;

internal static class TrackingTests
{
    public static async Task RunPostgresAsync(BotDbContext db, ISecretProtector secrets, Action<bool, string> check)
    {
        const string awb = "545-98910626";
        const string email = "jack@example.com";
        var model = new AiModelEndpoint();
        model.Update("轨迹意图测试模型", "测试", "https://example.invalid/model", "test", "chat", "test-only", true, 15, 512);
        var robot = Robot.Create("轨迹测试", "tracking-test", secrets.Protect("test-only"), "", true);
        robot.ConfigureTracking(model.Id);
        robot.ConfigureAutoReply(true, "quote", true, "", []);
        db.AddRange(model, robot);
        await db.SaveChangesAsync();

        using var handler = new TrackingHttpHandler();
        using var http = new HttpClient(handler);
        var settings = Options.Create(new TrackingOptions
        {
            BaseUrl = "https://example.invalid/",
            PlKey = "test-only",
            TrackInfosSuccessCode = 0
        });
        var backend = new TrackingBackendClient(http, settings);
        var history = new TrackingHistory(db, backend);
        var inbox = new InboxRepository(db);
        var gateway = new FakeGateway();
        var automation = new AutomationRepository(db);
        var repository = new RobotRepository(db);
        var ai = new FakeAiClient();
        var processor = new TrackingCommandProcessor(db, backend, history, ai,
            new AutoReplyService(automation, repository, secrets, ai, gateway),
            new RobotService(repository, secrets, gateway, automation), gateway, secrets, NullLogger<TrackingCommandProcessor>.Instance);

        async Task<TrackingCommand> ProcessAsync(string input, TrackingIntent intent)
        {
            using var frame = JsonDocument.Parse(InboxTests.Frame(Guid.NewGuid().ToString(), content: input));
            var message = IncomingMessageParser.Parse(robot.Id, robot.Name, frame.RootElement)!;
            check(await inbox.InsertIfNewAsync(message, default) && !await inbox.InsertIfNewAsync(message, default),
                "轨迹消息与指令原子入库，重复企微回调被去重");
            ai.Generate = _ => JsonSerializer.Serialize(new { action = intent.Action, awbs = intent.Awbs, emails = intent.Emails, types = intent.Types, routes = intent.Routes?.Select(x => new { awbs = x.Awbs, email = x.Email, type = x.Type }) });
            await processor.ProcessAsync(message.Id, default);
            return await db.TrackingCommands.AsNoTracking().SingleAsync(x => x.Id == message.Id);
        }

        var partial = await ProcessAsync($"帮我盯一下 {awb} 的物流进展", new("subscribe", [awb], [], [TrackingIntent.Track]));
        check(partial.Status == "clarify" && handler.Requests.Count == 0, "自然语言缺邮箱时追问，不发送订阅请求");
        var subscribed = await ProcessAsync($"就用 {email}", new("subscribe", [awb], [email], [TrackingIntent.Track]));
        check(subscribed.Status == "succeeded" && handler.Requests.Single() == ("subscribeInfos", awb, "订阅", "轨迹跟踪", email),
            "补充邮箱后使用会话中的主单调用订阅 API");
        var exception = await ProcessAsync("这票出异常的话，也发到这个邮箱",
            new("subscribe", [awb], [email], [TrackingIntent.Exception]));
        check(exception.Status == "succeeded" && handler.Requests.Last().Type == "异常监控", "相同主单可独立开启异常监控");

        await history.RefreshAsync([awb], default);
        await history.RefreshAsync([awb], default);
        check(await db.TrackingPushes.CountAsync(x => x.Awb == awb) == 1 && gateway.Calls == 3,
            "回执轮询按节点和推送时间去重，企微仅发送当次指令回复");
        handler.QueryMessage = "查询失败";
        check(!await history.RefreshAsync([awb], default), "回执成功码为零时同时校验成功提示");
        handler.QueryMessage = "查询成功";

        var cancelled = await ProcessAsync("这票别再发轨迹了，异常继续盯",
            new("cancel", [awb], [], [TrackingIntent.Track]));
        check(cancelled.Status == "succeeded" && handler.Requests.Last() == ("subscribeInfos", awb, "取消", "轨迹跟踪", email)
            && await db.TrackingSubscriptions.AnyAsync(x => x.Awb == awb && x.Type == "异常监控" && x.Status == "active"),
            "按类型取消只影响轨迹跟踪，保留异常监控");

        var before = handler.Requests.Count;
        var invalid = await ProcessAsync($"请给 {email} 开启监控", new("subscribe", ["235-98910626"], [email], [TrackingIntent.Track]));
        check(invalid.Status == "clarify" && handler.Requests.Count == before, "模型生成的陌生主单不会触发写接口");
        var quote = await ProcessAsync("LAX 有什么报价", new("other", [], [], []));
        check(await db.AutoReplies.AnyAsync(x => x.ReceivedMessageId == quote.Id && x.Mode == "quote" && x.Status == "queued"),
            "同一机器人可将普通询价交还原报价队列");

        handler.FailWrites = true;
        var uncertain = await ProcessAsync($"重新订阅 {awb}，发到 {email}", new("subscribe", [awb], [email], [TrackingIntent.Track]));
        before = handler.Requests.Count;
        var repeated = await ProcessAsync($"再试一次订阅 {awb} 到 {email}", new("subscribe", [awb], [email], [TrackingIntent.Track]));
        check(uncertain.Status == "unknown" && repeated.Status == "unknown" && handler.Requests.Count == before,
            "写接口结果不明确时保留待核实，不重复提交");
        handler.FailWrites = false;
        before = handler.Requests.Count;
        var combined = await ProcessAsync("帮我监控 999-97974866、999-97973606，有更新和异常都推送到 jack@example.com",
            new("subscribe", ["999-97974866", "999-97973606"], [email], [TrackingIntent.Track, TrackingIntent.Exception]));
        check(combined.Status == "succeeded" && handler.Requests.Skip(before).SequenceEqual(new[]
            {
                ("subscribeInfos", "999-97974866,999-97973606", (string?)"订阅", (string?)"轨迹跟踪", (string?)email),
                ("subscribeInfos", "999-97974866,999-97973606", (string?)"订阅", (string?)"异常监控", (string?)email)
            }),
            "企微私聊无需中台白名单，同邮箱的两票按订阅类型各提交一次");
        const string updateEmail = "tracking@example.com";
        const string alertEmail = "alerts@example.com";
        string[] shipmentNumbers = ["999-97973610", "999-97974262"];
        var batchAwbs = string.Join(',', shipmentNumbers);
        var mixedText = $"帮我监控 {batchAwbs}，有更新推送到 {updateEmail}，有异常推送到 {alertEmail}";
        before = handler.Requests.Count;
        var ambiguous = await ProcessAsync(mixedText,
            new("subscribe", shipmentNumbers, [updateEmail, alertEmail], [TrackingIntent.Track, TrackingIntent.Exception]));
        check(ambiguous.Status == "clarify" && handler.Requests.Count == before, "遇到未明确的类型和邮箱映射时追问，避免交叉订阅");

        handler.OmitAcknowledgementFor = shipmentNumbers[1];
        var mixed = await ProcessAsync(mixedText, new("subscribe", shipmentNumbers, [], [],
            [new([], updateEmail, TrackingIntent.Track), new([], alertEmail, TrackingIntent.Exception)]));
        check(mixed.Status == "unknown" && handler.Requests.Skip(before).SequenceEqual(new[]
            {
                ("subscribeInfos", batchAwbs, (string?)"订阅", (string?)TrackingIntent.Track, (string?)updateEmail),
                ("subscribeInfos", batchAwbs, (string?)"订阅", (string?)TrackingIntent.Exception, (string?)alertEmail)
            }), "不同订阅类型发送到各自邮箱，每组的两票主单用逗号拼接");
        check(await db.TrackingSubscriptions.CountAsync(x => shipmentNumbers.Contains(x.Awb) && x.Status == "active") == 2
            && await db.TrackingSubscriptions.CountAsync(x => shipmentNumbers.Contains(x.Awb) && x.Status == "subscribe_unknown") == 2,
            "批量回执只确认一票时逐单保留成功或待核实");
        before = handler.Requests.Count;
        await ProcessAsync(mixedText, new("subscribe", shipmentNumbers, [], [],
            [new([], updateEmail, TrackingIntent.Track), new([], alertEmail, TrackingIntent.Exception)]));
        check(handler.Requests.Count == before, "重复提交批量指令时不重发已订阅或待核实的主单");

        var streamFrames = new List<(Guid MessageId, string Content, bool Finish)>();
        gateway.ReplyStream = (id, content, finish) =>
        {
            streamFrames.Add((id, content, finish));
            return new(true, "stream", "企业微信已接受流式回复。", "tracking-stream");
        };
        var activeSends = gateway.Calls;
        const string progressAwb = "999-97974263";
        var progressCommand = await ProcessAsync($"帮我盯 {progressAwb}，有新轨迹和异常都发到 {email}",
            new("subscribe", [progressAwb], [email], [TrackingIntent.Track, TrackingIntent.Exception]));
        check(streamFrames.Count == 3 && streamFrames.All(x => x.MessageId == progressCommand.Id)
            && streamFrames[0].Content.Contains("1/2") && !streamFrames[0].Finish
            && streamFrames[1].Content.Contains("2/2") && !streamFrames[1].Finish
            && streamFrames[2].Finish && streamFrames[2].Content.Contains("订阅成功") && gateway.Calls == activeSends,
            "轨迹分组进度和最终结果更新同一企微流，成功后不另发结果");
        check(await db.Messages.AnyAsync(x => x.Id == progressCommand.Id && x.Status == DeliveryStatus.Accepted),
            "轨迹流式结果保留发送审计");

        gateway.ReplyStream = (_, _, finish) => finish ? new(false, "stream-unknown", "流式回执未确认。") : null;
        const string uncertainReplyAwb = "999-97974264";
        var uncertainReply = await ProcessAsync($"订阅 {uncertainReplyAwb} 的轨迹到 {email}",
            new("subscribe", [uncertainReplyAwb], [email], [TrackingIntent.Track]));
        check(uncertainReply.Status == "succeeded" && gateway.Calls == activeSends
            && await db.Messages.AnyAsync(x => x.Id == uncertainReply.Id && x.Status == DeliveryStatus.Unknown),
            "流式终帧未确认时保留业务状态和发送待核实，避免重复通知");
        gateway.ReplyStream = null;

        var cancelException = await ProcessAsync($"取消 {progressAwb} 的异常监控，收件人 {email}",
            new("cancel", [progressAwb], [email], [TrackingIntent.Exception]));
        check(cancelException.Status == "succeeded"
            && handler.Requests.Last() == ("subscribeInfos", progressAwb, "取消", TrackingIntent.Exception, email)
            && await db.TrackingSubscriptions.AnyAsync(x => x.Awb == progressAwb && x.Type == TrackingIntent.Track && x.Status == "active")
            && await db.TrackingSubscriptions.AnyAsync(x => x.Awb == progressAwb && x.Type == TrackingIntent.Exception && x.Status == "cancelled"),
            "默认配置通过订阅 API 取消异常监控，保留轨迹跟踪");
        await CheckMvcAsync(db, backend, handler, awb, check);
    }

    private static async Task CheckMvcAsync(
        BotDbContext db, TrackingBackendClient backend, TrackingHttpHandler handler, string awb, Action<bool, string> check)
    {
        await using var app = new TestApp();
        await using var isolated = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<BotDbContext>();
            services.AddSingleton(db);
            services.RemoveAll<TrackingBackendClient>();
            services.AddSingleton(backend);
        }));
        using var client = isolated.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        check((await client.GetAsync("/Tracking")).StatusCode == HttpStatusCode.Redirect, "轨迹管理页面要求登录");
        var login = await client.GetStringAsync("/Account/Login");
        var token = WebUtility.HtmlDecode(Regex.Match(login, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "reviewer", ["Password"] = "PreviewOnly!2026", ["__RequestVerificationToken"] = token
        });
        await client.PostAsync("/Account/Login", form);
        var subscriptions = WebUtility.HtmlDecode(await client.GetStringAsync("/Tracking"));
        handler.QueryNode = "RPP(理货完成)";
        var before = handler.Requests.Count;
        var opened = WebUtility.HtmlDecode(await client.GetStringAsync($"/Tracking/Nodes?awb={awb}"));
        check(handler.Requests.Count == before + 1 && handler.Requests.Last().Path == "trackInfos"
            && handler.Requests.Last().Awb == awb && opened.Contains(handler.QueryNode)
            && await db.TrackingPushes.AnyAsync(x => x.Awb == awb && x.Node == handler.QueryNode),
            "打开主单详情即时调用查询 API，并在第一次渲染前保存新推送节点");

        before = handler.Requests.Count;
        var nodes = WebUtility.HtmlDecode(await client.GetStringAsync($"/Tracking/Nodes?awb={awb}&from=2026-11-20&to=2026-11-20&cached=true"));
        check(handler.Requests.Count == before, "筛选已同步的推送记录不重复调用查询 API");

        handler.QueryMessage = "查询失败";
        var failed = WebUtility.HtmlDecode(await client.GetStringAsync($"/Tracking/Nodes?awb={awb}"));
        check(failed.Contains("本次查询失败") && failed.Contains(handler.QueryNode)
            && await db.TrackingPushes.AnyAsync(x => x.Awb == awb && x.Node == handler.QueryNode),
            "详情实时查询失败时提示原因并保留已同步节点");
        handler.QueryMessage = "查询成功";
        var operations = WebUtility.HtmlDecode(await client.GetStringAsync("/Tracking/Operations"));
        check(subscriptions.Contains(awb) && subscriptions.Contains("jack@example.com") && subscriptions.Contains("异常监控"),
            "订阅页展示主单、订阅类型及配置的收件邮箱");
        check(nodes.Contains("FAD(已抵达)") && nodes.Contains("2026-11-20 10:44:21"),
            "主单详情页按日期查询并渲染邮件推送节点及时间");
        check(operations.Contains("待核实") && operations.Contains("轨迹测试"), "操作记录页展示机器人和执行结果");
    }

    private sealed class TrackingHttpHandler : HttpMessageHandler
    {
        public List<(string Path, string Awb, string? Event, string? Type, string? Email)> Requests { get; } = [];
        public string QueryMessage { get; set; } = "查询成功";
        public string QueryNode { get; set; } = "FAD(已抵达)";
        public bool FailWrites { get; set; }
        public string? OmitAcknowledgementFor { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Post || request.Headers.GetValues("plKey").Single() != "test-only")
                throw new InvalidOperationException("意外的轨迹 API 请求。");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var data = json.RootElement;
            var awb = data.GetProperty("awb_no").GetString()!;
            var path = request.RequestUri!.Segments[^1];
            if (path == "trackInfos")
            {
                Requests.Add((path, awb, null, null, null));
                return JsonResponse(new { code = 0, msg = QueryMessage, data = new {
                    email_groups = new[] { new { awb_no = awb, nodes = new[] { new { push_node = QueryNode, push_time = "2026-11-20 10:44:21" } } } }
                }, totalCount = 0 });
            }
            Requests.Add((path, awb, data.GetProperty("event").GetString(), data.GetProperty("subscribe_type").GetString(),
                data.GetProperty("recipients").GetString()));
            return FailWrites ? new(HttpStatusCode.ServiceUnavailable) : JsonResponse(new
            {
                code = 1, msg = "发送成功",
                data = new { awb_nos = awb.Split(',').Where(x => x != OmitAcknowledgementFor).ToArray() }
            });
        }

        private static HttpResponseMessage JsonResponse(object body) =>
            new(HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(body) };
    }
}
