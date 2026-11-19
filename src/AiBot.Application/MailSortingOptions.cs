namespace AiBot.Application;

public sealed class MailSortingOptions
{
    public int PollSeconds { get; set; } = 60;
    public int VolumeDivisor { get; set; } = 6000;
    public string OllamaModel { get; set; } = "";
    public string PythonExecutable { get; set; } = "";
    public string PythonParserDirectory { get; set; } = "";
    public string RulesSnapshot { get; set; } = "";
    public Dictionary<string, string> Routes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SortingMailbox> Mailboxes { get; set; } = [];
}

public sealed class SortingMailbox
{
    public bool Enabled { get; set; }
    public string Name { get; set; } = "";
    public string Protocol { get; set; } = "imap";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Username { get; set; } = "";
    public string Address { get; set; } = "";
    public string Folder { get; set; } = "INBOX";
    public string Category { get; set; } = "";
    public bool ImportRecent { get; set; }
}
