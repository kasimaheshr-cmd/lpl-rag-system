namespace LPL.Gatekeeper.Models;

public sealed class CachedAnswer
{
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public List<string> Sources { get; set; } = new();
    public string Department { get; set; } = "";
    public DateTime CachedAt { get; set; }
}

