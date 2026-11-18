using System.Security.Claims;
using AiBot.Application;
using AiBot.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AiBot.Web.Controllers;

public sealed class AccountController(IAccountService accounts, DatabaseState database) : Controller
{
    [HttpGet, AllowAnonymous]
    public IActionResult Login(string? returnUrl)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Home");
        ViewBag.DatabaseReady = database.Ready;
        return View(new LoginInput { ReturnUrl = returnUrl });
    }
    [HttpPost, AllowAnonymous, EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginInput input, CancellationToken ct)
    {
        ViewBag.DatabaseReady = database.Ready;
        if (!database.Ready) ModelState.AddModelError("", "数据库尚未就绪，请先完成服务端连接及管理员配置。");
        if (!ModelState.IsValid) return View(input);
        var user = await accounts.AuthenticateAsync(input.Username, input.Password, ct);
        if (user is null) { ModelState.AddModelError("", "用户名或密码错误；连续错误 5 次将锁定 15 分钟。"); return View(input); }
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username)
        ], CookieAuthenticationDefaults.AuthenticationScheme));
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        return Url.IsLocalUrl(input.ReturnUrl) ? LocalRedirect(input.ReturnUrl!) : RedirectToAction("Index", "Home");
    }
    [HttpPost, Authorize]
    public async Task<IActionResult> Logout()
    { await HttpContext.SignOutAsync(); return RedirectToAction(nameof(Login)); }
    [HttpGet, Authorize] public IActionResult Password() => View(new PasswordInput());
    [HttpPost, Authorize, EnableRateLimiting("login")]
    public async Task<IActionResult> Password(PasswordInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(input);
        if (!await accounts.ChangePasswordAsync(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), input.CurrentPassword, input.NewPassword, ct))
        { ModelState.AddModelError("", "当前密码不正确。"); return View(input); }
        await HttpContext.SignOutAsync(); TempData["Success"] = "密码已更新，请使用新密码登录。";
        return RedirectToAction(nameof(Login));
    }
}
