using System.ComponentModel.DataAnnotations;
using AiBot.Application;

namespace AiBot.Web.Models;

public sealed class LoginInput
{
    [Required(ErrorMessage = "请输入用户名"), StringLength(80)] public string Username { get; set; } = "";
    [Required(ErrorMessage = "请输入密码"), StringLength(128)] public string Password { get; set; } = "";
    public string? ReturnUrl { get; set; }
}
public sealed class PasswordInput
{
    [Required(ErrorMessage = "请输入当前密码"), StringLength(128)] public string CurrentPassword { get; set; } = "";
    [Required(ErrorMessage = "请输入新密码"), StringLength(128, MinimumLength = 12, ErrorMessage = "新密码需为 12–128 位")] public string NewPassword { get; set; } = "";
    [Compare(nameof(NewPassword), ErrorMessage = "两次输入的新密码不一致")] public string ConfirmPassword { get; set; } = "";
}
public sealed record SendViewModel(SendInput Input, IReadOnlyList<RobotInfo> Robots);
public sealed record InboxListModel(InboxFilter Filter, PageResult<AiBot.Domain.ReceivedMessage> Messages);
public sealed record InboxViewModel(InboxListModel List, IReadOnlyList<RobotInfo> Robots, IReadOnlyList<RobotConnectionState> Connections);
