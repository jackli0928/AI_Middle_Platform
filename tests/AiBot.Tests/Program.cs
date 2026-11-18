using System.Net;
using System.Text.RegularExpressions;
using AiBot.Application;
using AiBot.Domain;
using AiBot.Infrastructure;
using AiBot.Tests;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiBot.Tests;
public static class TestRunner
{
public static async Task Main(string[] args)
{
var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); checks++; }
async Task Reject(Func<Task> action, string message)
{ try { await action(); } catch (ArgumentException) { Check(true, message); return; } throw new Exception("FAIL: " + message); }
static string Token(string html) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);

await using var factory = new TestApp();
if (args.Contains("--preview"))
{
    factory.UseKestrel(5099);
    _ = factory.CreateClient();
    using var scope = factory.Services.CreateScope();
    var service = scope.ServiceProvider.GetRequiredService<IRobotService>();
    await service.SaveAsync(new RobotInput { Name = "团队协作助手", BotId = "aibot_team_preview", Secret = "preview-only", Description = "日常团队通知与工作提醒，让重要信息及时触达。" }, default);
    await service.SaveAsync(new RobotInput { Name = "研发通知机器人", BotId = "aibot_engineering_preview", Secret = "preview-only", Description = "服务于研发团队的项目动态和发布通知。" }, default);
    await service.SaveAsync(new RobotInput { Name = "运营消息助手", BotId = "aibot_operations_preview", Secret = "preview-only", Description = "运营活动与业务通知。", Enabled = false }, default);
    var robot = factory.Repository.Robots[0];
    var automation = scope.ServiceProvider.GetRequiredService<IAutomationSettingsService>();
    await automation.SaveKnowledgeAsync(new KnowledgeInput { Question = "如何联系管理员？", Keywords = "管理员\n联系支持", Answer = "请通过团队通讯录联系系统管理员。", MatchMode = "contains" }, default);
    foreach (var provider in new[] { "OpenAI", "DeepSeek", "智谱" })
        await automation.SaveModelAsync(new AiModelInput { Name = provider + " 示例模型", Provider = provider, Endpoint = "https://example.invalid/v1/chat/completions", ModelId = "填写实际模型ID", ApiStyle = "chat" }, default);
    foreach (var r in factory.Repository.Robots) factory.Connections.States.Add(new(r.Id, r.Name, r.BotId, r.Enabled ? "online" : "disabled", r.Enabled ? "已连接，正在接收消息。" : "已停用，不建立连接。", DateTimeOffset.UtcNow, LastHeartbeatAt: DateTimeOffset.UtcNow));
    using (var callback = System.Text.Json.JsonDocument.Parse(InboxTests.Frame("preview-incoming", "group", content: "你好，请查看今天的项目进展。")))
        await factory.Inbox.InsertIfNewAsync(IncomingMessageParser.Parse(robot.Id, robot.Name, callback.RootElement)!, default);
    await service.SendAsync(new SendInput { RobotId = robot.Id, UserId = "zhangsan", Content = "### 工作提醒\n大家好，今日项目进展已更新，请及时查看。" }, "reviewer", default);
    Console.WriteLine("TEST PREVIEW ONLY · http://localhost:5099 · reviewer / PreviewOnly!2026 · no real database or sends");
    await Task.Delay(Timeout.Infinite); return;
}
var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
var protectedPage = await client.GetAsync("/Robots");
Check(protectedPage.StatusCode == HttpStatusCode.Redirect && protectedPage.Headers.Location!.OriginalString.Contains("/Account/Login"), "匿名用户访问管理页必须登录");
var loginHtml = await client.GetStringAsync("/Account/Login");
Check(loginHtml.Contains("欢迎回来"), "MVC 登录页正常渲染");
var noCsrf = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string,string> { ["Username"] = "reviewer", ["Password"] = "PreviewOnly!2026" }));
Check(noCsrf.StatusCode == HttpStatusCode.BadRequest, "登录表单拒绝缺少 CSRF token 的请求");
var login = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string,string> {
    ["Username"] = "reviewer", ["Password"] = "PreviewOnly!2026", ["__RequestVerificationToken"] = Token(loginHtml), ["ReturnUrl"] = "https://evil.example"
}));
Check(login.StatusCode == HttpStatusCode.Redirect && login.Headers.Location?.OriginalString == "/", "登录成功且拒绝外部 ReturnUrl 重定向");
var editHtml = await client.GetStringAsync("/Robots/Edit");
var secret = "sensitive-test-secret";
var save = await client.PostAsync("/Robots/Edit", new FormUrlEncodedContent(new Dictionary<string,string> {
    ["Name"] = "测试协作机器人", ["BotId"] = "test-bot", ["Secret"] = secret, ["Description"] = "用于自动验证", ["Enabled"] = "true", ["__RequestVerificationToken"] = Token(editHtml)
}));
Check(save.StatusCode == HttpStatusCode.Redirect && factory.Repository.Robots.Count == 1, "通过 MVC 新建机器人并持久化");
var robotEntity = factory.Repository.Robots.Single();
Check(robotEntity.ProtectedSecret != secret && robotEntity.ProtectedSecret.Length > 50, "Secret 使用 Data Protection 加密");
var listHtml = WebUtility.HtmlDecode(await client.GetStringAsync("/Robots"));
Check(listHtml.Contains("测试协作机器人") && !listHtml.Contains(secret) && !listHtml.Contains(robotEntity.ProtectedSecret), "列表不泄露明文或密文 Secret");
var editExisting = await client.GetStringAsync("/Robots/Edit/" + robotEntity.Id);
Check(!editExisting.Contains(secret) && !editExisting.Contains(robotEntity.ProtectedSecret), "编辑表单不回显 Secret");
var invalidSave = await client.PostAsync("/Robots/Edit", new FormUrlEncodedContent(new Dictionary<string,string> {
    ["Name"] = "", ["BotId"] = "invalid", ["Secret"] = secret, ["__RequestVerificationToken"] = Token(editExisting)
}));
var invalidHtml = await invalidSave.Content.ReadAsStringAsync();
Check(invalidSave.StatusCode == HttpStatusCode.OK && !invalidHtml.Contains(secret), "表单校验失败时也不回显 Secret");
using var testScope = factory.Services.CreateScope();
var robots = testScope.ServiceProvider.GetRequiredService<IRobotService>();
var oldSecret = robotEntity.ProtectedSecret;
await robots.SaveAsync(new RobotInput { Id = robotEntity.Id, Name = "已编辑", BotId = "test-bot", Secret = null }, default);
Check(robotEntity.ProtectedSecret == oldSecret, "编辑 Secret 留空保留原有凭据");
await Reject(() => robots.SaveAsync(new RobotInput { Name = "重复", BotId = "test-bot", Secret = "test" }, default), "拒绝重复 Bot ID");
await Reject(() => robots.SaveAsync(new RobotInput { Name = "缺少凭据", BotId = "missing" }, default), "新建机器人必须提供 Secret");
foreach (var target in new[] { "@all", "a|b", "a,b", "a;b", "a b", "a，b" })
    await Reject(() => robots.SendAsync(new SendInput { RobotId = robotEntity.Id, UserId = target, Content = "test" }, "reviewer", default), "拒绝非单账号目标 " + target);
await Reject(() => robots.SendAsync(new SendInput { RobotId = robotEntity.Id, UserId = "test", Content = new string('中', 1366) }, "reviewer", default), "按 UTF-8 字节而非字符限制消息长度");
var sendHtml = await client.GetStringAsync("/Robots/Send/" + robotEntity.Id);
var operation = Guid.NewGuid();
Check(sendHtml.Contains("Input.TargetType") && sendHtml.Contains("Input.ChatId"), "发送页提供单聊与群聊选项和 Chat ID 输入框");
var sendFields = new Dictionary<string,string> { ["Input.RobotId"] = robotEntity.Id.ToString(), ["Input.OperationId"] = operation.ToString(), ["Input.UserId"] = "zhangsan", ["Input.Content"] = "你好，来自 MVC 的测试消息", ["__RequestVerificationToken"] = Token(sendHtml) };
var sent = await client.PostAsync("/Robots/Send", new FormUrlEncodedContent(sendFields));
Check(sent.StatusCode == HttpStatusCode.Redirect && factory.Gateway.Calls == 1 && factory.Repository.Messages.Single().Status == DeliveryStatus.Accepted, "MVC 单账号发送并保存回执");
await client.PostAsync("/Robots/Send", new FormUrlEncodedContent(sendFields));
Check(factory.Gateway.Calls == 1, "重复提交同一 operation ID 不重复发送");
factory.Gateway.Result = new(false, "send-unknown", "等待回执超时");
await robots.SendAsync(new SendInput { RobotId = robotEntity.Id, UserId = "lisi", Content = "test" }, "reviewer", default);
Check(factory.Repository.Messages.Last().Status == DeliveryStatus.Unknown, "超时发送保存为结果未确认");
await Reject(() => robots.SendAsync(new SendInput { RobotId = robotEntity.Id, TargetType = "invalid", UserId = "user", Content = "test" }, "reviewer", default), "拒绝无效发送类型");
await Reject(() => robots.SendAsync(new SendInput { RobotId = robotEntity.Id, TargetType = "group", UserId = "user", Content = "test" }, "reviewer", default), "群聊必须填写 Chat ID，不能用 User ID 代替");
foreach (var target in new[] { "@all", "a|b", "a,b", "a b" })
    await Reject(() => robots.SendAsync(new SendInput { RobotId = robotEntity.Id, TargetType = "group", ChatId = target, Content = "test" }, "reviewer", default), "拒绝非单群聊目标 " + target);
factory.Gateway.Result = new(true, "send", "企业微信已接受群聊消息。", "group-receipt", 0);
var groupHtml = await client.GetStringAsync("/Robots/Send");
var groupFields = new Dictionary<string,string> { ["Input.RobotId"] = robotEntity.Id.ToString(), ["Input.OperationId"] = Guid.NewGuid().ToString(), ["Input.TargetType"] = "group", ["Input.ChatId"] = "  wr-test-group  ", ["Input.UserId"] = "ignored-user", ["Input.Content"] = "群聊消息测试", ["__RequestVerificationToken"] = Token(groupHtml) };
var groupResponse = await client.PostAsync("/Robots/Send", new FormUrlEncodedContent(groupFields));
var groupLog = factory.Repository.Messages.Last();
Check(groupResponse.StatusCode == HttpStatusCode.Redirect && factory.Gateway.LastTargetType == "group" && factory.Gateway.LastTargetId == "wr-test-group", "MVC 群聊发送使用选定 Chat ID 并忽略非当前类型的 User ID");
Check(groupLog.Status == DeliveryStatus.Accepted && groupLog.TargetType == "group" && groupLog.ChatId == "wr-test-group" && groupLog.UserId == "", "消息记录分别存储群聊类型与 Chat ID");
var groupRecords = WebUtility.HtmlDecode(await client.GetStringAsync("/Messages?search=wr-test-group"));
Check(groupRecords.Contains("群聊 · wr-test-group"), "消息记录支持按 Chat ID 搜索及展示群聊标识");
var callsBeforeDuplicate = factory.Gateway.Calls;
await client.PostAsync("/Robots/Send", new FormUrlEncodedContent(groupFields));
Check(factory.Gateway.Calls == callsBeforeDuplicate, "群聊重复提交不重复发送");
groupFields["Input.OperationId"] = Guid.NewGuid().ToString(); groupFields["Input.ChatId"] = "";
var missingGroup = await client.PostAsync("/Robots/Send", new FormUrlEncodedContent(groupFields));
Check(missingGroup.StatusCode == HttpStatusCode.OK && factory.Gateway.Calls == callsBeforeDuplicate && WebUtility.HtmlDecode(await missingGroup.Content.ReadAsStringAsync()).Contains("请填写群聊 Chat ID"), "MVC 缺少 Chat ID 时保留表单并显示错误");
foreach (var path in new[] { "/", "/Robots", "/Robots/Send", "/Messages", "/Account/Password" })
    Check((await client.GetAsync(path)).StatusCode == HttpStatusCode.OK, "已登录页面正常渲染 " + path);
var disabled = Robot.Create("停用", "disabled", "encrypted", "", false); factory.Repository.Add(disabled);
await InboxTests.RunAsync(factory, client, Check);
await Reject(() => robots.SendAsync(new SendInput { RobotId = disabled.Id, UserId = "test", Content = "test" }, "reviewer", default), "停用机器人禁止发送");
await robots.DeleteAsync(robotEntity.Id, default);
Check(factory.Repository.Messages.Count == 3, "删除机器人保留单聊和群聊历史消息");
foreach (var path in new[] { "/Knowledge", "/Knowledge/Edit", "/AiModels", "/AiModels/Edit" })
    Check((await client.GetAsync(path)).StatusCode == HttpStatusCode.OK, "新增管理页面正常渲染 " + path);
var knowledgePage = await client.GetStringAsync("/Knowledge/Edit");
var knowledgeSave = await client.PostAsync("/Knowledge/Edit", new FormUrlEncodedContent(new Dictionary<string,string> {
    ["Question"] = "测试问题", ["Keywords"] = "", ["Answer"] = "测试答案", ["MatchMode"] = "exact", ["Priority"] = "100", ["RobotId"] = "", ["Enabled"] = "true", ["__RequestVerificationToken"] = Token(knowledgePage)
}));
Check(knowledgeSave.StatusCode == HttpStatusCode.Redirect && factory.Automation.Entries.Count == 1, "MVC 知识库匹配词留空也可保存");
var robotForm = await client.GetStringAsync("/Robots/Edit/" + disabled.Id);
var robotSaved = await client.PostAsync("/Robots/Edit", new FormUrlEncodedContent(new Dictionary<string,string> {
    ["Id"] = disabled.Id.ToString(), ["Name"] = disabled.Name, ["BotId"] = disabled.BotId, ["Description"] = "", ["Secret"] = "", ["ReplyPrompt"] = "", ["ReplyMode"] = "knowledge", ["ModelPriority"] = Guid.Empty.ToString(), ["__RequestVerificationToken"] = Token(robotForm)
}));
Check(robotSaved.StatusCode == HttpStatusCode.Redirect, "MVC 机器人备注和 AI 提示词留空可正常保存");
var modelPage = await client.GetStringAsync("/AiModels/Edit");
var modelSave = await client.PostAsync("/AiModels/Edit", new FormUrlEncodedContent(new Dictionary<string,string> {
    ["Name"] = "接口测试", ["Provider"] = "OpenAI", ["Endpoint"] = "https://example.invalid/v1/responses", ["ModelId"] = "custom-model-id", ["ApiStyle"] = "responses", ["ApiKey"] = "secret-api-key-test", ["Enabled"] = "true", ["TimeoutSeconds"] = "10", ["MaxOutputTokens"] = "1024", ["__RequestVerificationToken"] = Token(modelPage)
}));
Check(modelSave.StatusCode == HttpStatusCode.Redirect && factory.Automation.Models.Count == 1 && factory.Ai.Calls.Count == 0, "MVC 新建模型配置且保存不调用外部接口");
var modelEntity = factory.Automation.Models.Single();
Check(!modelEntity.ProtectedApiKey.Contains("secret-api-key-test") && testScope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(modelEntity.ProtectedApiKey) == "secret-api-key-test", "模型密钥加密保存");
var modelEdit = await client.GetStringAsync("/AiModels/Edit/" + modelEntity.Id);
Check(!modelEdit.Contains("secret-api-key-test") && !modelEdit.Contains(modelEntity.ProtectedApiKey), "模型编辑表单不回显明文或密文密钥");
var invalidModel = await client.PostAsync("/AiModels/Edit", new FormUrlEncodedContent(new Dictionary<string,string> { ["Name"] = "", ["ApiKey"] = "secret-api-key-test", ["__RequestVerificationToken"] = Token(modelEdit) }));
Check(!(await invalidModel.Content.ReadAsStringAsync()).Contains("secret-api-key-test"), "模型校验失败时不回显密钥");
await SessionTests.RunAsync(Check);
await AutomationTests.RunAsync(Check);
using var db = new BotDbContext(new DbContextOptionsBuilder<BotDbContext>().UseNpgsql("Host=localhost;Database=li;Username=postgres", x => x.MigrationsHistoryTable("__EFMigrationsHistory", "ai_bot")).Options);
Check(!db.Database.HasPendingModelChanges(), "EF 迁移快照与当前 PostgreSQL 模型一致");
var sql = db.Database.GenerateCreateScript();
Check(sql.Contains("ai_bot.robots") && sql.Contains("ai_bot.message_logs") && sql.Contains("ai_bot.admin_users"), "全部业务表位于 ai_bot schema");
if (args.Contains("--postgres")) await PostgresTests.RunAsync(Check);
Console.WriteLine($"\n{checks} checks passed. Real WeCom network is not used; PostgreSQL integration: {args.Contains("--postgres")}.");
}
}
