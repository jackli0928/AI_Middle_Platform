using AiBot.Domain;

namespace AiBot.Web;

public static class Display
{
    public static string AutoReplyStatus(string status) => status switch { "accepted" => "企微已接受", "failed" => "回复失败", "unknown" => "结果未确认", "skipped" => "未发送", _ => "处理中／未完成" };
    public static string MessageType(string type) => type switch { "text" => "文本", "image" => "图片", "mixed" => "图文", "voice" => "语音", "file" => "文件", "video" => "视频", _ => "其他" };
    public static string Connection(string state) => state switch { "online" => "已连接", "connecting" => "连接中", "authenticating" => "认证中", "reconnecting" => "重连中", "disabled" => "已停用", "standby" => "其他实例连接中", "conflict" => "连接被接管", _ => "配置异常" };
    public static string ConnectionTone(string state) => state == "online" ? "success" : state is "error" or "conflict" ? "danger" : state == "disabled" ? "neutral" : "warning";
    public static string Time(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai")).ToString("MM-dd HH:mm");
    public static string FullTime(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai")).ToString("yyyy-MM-dd HH:mm:ss");
    public static string Status(DeliveryStatus status) => status switch {
        DeliveryStatus.Accepted => "企微已接受", DeliveryStatus.Failed => "发送失败", DeliveryStatus.Unknown => "结果未确认", _ => "等待回执 / 待核实"
    };
    public static string Tone(DeliveryStatus status) => status switch {
        DeliveryStatus.Accepted => "success", DeliveryStatus.Failed => "danger", _ => "warning"
    };
}
