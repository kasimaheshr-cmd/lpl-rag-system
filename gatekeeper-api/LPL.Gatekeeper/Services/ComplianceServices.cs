using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LPL.Gatekeeper.Models;

namespace LPL.Gatekeeper.Services;

public sealed record PIIDetectionResult(bool ContainsPII, IReadOnlyList<string> PIITypes);
public interface IPIIDetectionService
{
    PIIDetectionResult Detect(string text);
}

public sealed class PIIDetectionService : IPIIDetectionService
{
    private static readonly Regex Email = new(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex Ssn = new(
        @"\b\d{3}-\d{2}-\d{4}\b",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    public PIIDetectionResult Detect(string text)
    {
        var types = new List<string>();
        if (Email.IsMatch(text)) types.Add("email");
        if (Ssn.IsMatch(text)) types.Add("ssn");
        return new PIIDetectionResult(types.Count > 0, types);
    }
}
