namespace AiBot.Web.Models;

public sealed record TrackingPager(int Page, int PageSize, int Total, string Action, string? Awb = null, string? From = null, string? To = null, bool? Cached = null);
