using System.Net;
using System.Text.Json;
using AiBot.Application;
using AiBot.Infrastructure;

namespace AiBot.Tests;

internal static class InboxTests
{
    internal static string Frame(string id = "msg-1", string chat = "single", string type = "text", string content = "来自用户的消息") =>
        JsonSerializer.Serialize(new { cmd = "aibot_msg_callback", headers = new { req_id = "callback-1" }, body = new {
            msgid = id, aibotid = "test-bot", chattype = chat, chatid = chat == "group" ? "wr-incoming-group" : "",
            from = new { userid = "receiver-test" }, create_time = 1794801600, msgtype = type, text = new { content },
            voice = new { content = "语音转写" }, mixed = new { msg_item = new object[] { new { msgtype = "text", text = new { content = "图文文字" } }, new { msgtype = "image", image = new { url = "https://private.example/file", aeskey = "sensitive-key" } } } },
            response_url = "https://private.example/reply", image = new { url = "https://private.example/image", aeskey = "sensitive-key" }
        } });
    public static async Task RunAsync(TestApp app, HttpClient client, Action<bool,string> check)
    {
        var robot = app.Repository.Robots.First();
        using var singleJson = JsonDocument.Parse(Frame(content: "<script>alert('xss')</script>"));
        var single = IncomingMessageParser.Parse(robot.Id, robot.Name, singleJson.RootElement)!;
        check(single.SenderUserId == "receiver-test" && single.ChatType == "single" && single.SentAt is not null, "回调解析发送人、单聊类型和企微时间");
        check(!single.PayloadJson.Contains("sensitive-key") && !single.PayloadJson.Contains("private.example"), "回调数据递归隐藏媒体地址、解密密钥和临时回复地址");
        check(await app.Inbox.InsertIfNewAsync(single, default) && !await app.Inbox.InsertIfNewAsync(single, default), "重复消息 ID 仅保存一次");
        using var groupJson = JsonDocument.Parse(Frame("group-msg", "group", "mixed"));
        var group = IncomingMessageParser.Parse(robot.Id, robot.Name, groupJson.RootElement)!;
        await app.Inbox.InsertIfNewAsync(group, default);
        check(group.ChatId == "wr-incoming-group" && group.Content.Contains("图文文字") && group.Content.Contains("图片"), "群聊回调提取 Chat ID，并保留图文文字与图片占位");
        using var invalid = JsonDocument.Parse("""{"cmd":"aibot_msg_callback","body":{"msgtype":"text"}}""");
        check(IncomingMessageParser.Parse(robot.Id, robot.Name, invalid.RootElement) is null, "缺少消息 ID 和会话信息的回调不入库");
        foreach (var type in new[] { "image", "voice", "file", "video", "custom" })
        {
            using var json = JsonDocument.Parse(Frame(type, type: type));
            check(IncomingMessageParser.Parse(robot.Id, robot.Name, json.RootElement)!.Content.Length > 0, "解析消息类型 " + type);
        }
        app.Connections.States.Add(new(robot.Id, robot.Name, robot.BotId, "online", "已连接，正在接收消息。", DateTimeOffset.UtcNow));
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/Inbox"));
        check(html.Contains("机器人连接") && html.Contains("收到的消息"), "接收消息页面展示连接状态和消息列表");
        var details = await client.GetStringAsync("/Inbox/Details/" + single.Id);
        check(!details.Contains("<script>alert") && WebUtility.HtmlDecode(details).Contains("<script>alert"), "消息详情以转义文本显示，避免回调内容注入脚本");
        var filtered = WebUtility.HtmlDecode(await client.GetStringAsync("/Inbox/Rows?ChatType=group&Search=wr-incoming-group"));
        check(filtered.Contains("图文文字") && !filtered.Contains("alert('xss')"), "自动刷新接口保留会话与 Chat ID 筛选条件");
        var composer = await client.GetStringAsync($"/Robots/Send/{robot.Id}?targetType=group&targetId=wr-incoming-group");
        check(composer.Contains("value=\"wr-incoming-group\""), "从接收会话跳转可预填群 Chat ID，仍需手动提交");
        var noToken = await client.PostAsync("/Inbox/Reconnect", new FormUrlEncodedContent(new Dictionary<string,string> { ["id"] = robot.Id.ToString() }));
        check(noToken.StatusCode == HttpStatusCode.BadRequest, "重新连接操作受 CSRF 保护");
        using var anonymous = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        check((await anonymous.GetAsync("/Inbox/Connections")).StatusCode == HttpStatusCode.Redirect, "未登录无法读取机器人连接状态");
    }
}
