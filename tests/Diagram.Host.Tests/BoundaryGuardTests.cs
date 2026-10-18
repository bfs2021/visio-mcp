using System.Text.RegularExpressions;
using Xunit;

namespace Diagram.Host.Tests;

/// <summary>
/// Static guards for what the application must never do: start programs, open network connections of its own, or write diagram content to a
/// log. They read the source, so a change that adds one fails here and has to be explained.
/// </summary>
public class BoundaryGuardTests
{
    static string Root
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "Diagram.Host.Core"))) dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("repository root not found");
        }
    }

    static IEnumerable<(string File, int Line, string Text)> Lines(string pattern)
    {
        var re = new Regex(pattern);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Root, f).Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/") || rel.Contains("/Generated/")) continue;
            var all = File.ReadAllLines(f);
            for (var i = 0; i < all.Length; i++) if (re.IsMatch(all[i]) && !all[i].TrimStart().StartsWith("//") && !all[i].TrimStart().StartsWith("///")) yield return (rel, i + 1, all[i].Trim());
        }
    }

    [Fact]
    public void NoCodeStartsProgramsOrOpensConnectionsOfItsOwn()
    {
        var hits = Lines(@"Process\.Start|ProcessStartInfo|ShellExecute|\bHttpClient\b|\bWebClient\b|\bTcpClient\b|\bTcpListener\b|\bSocket\b|WebRequest|UseShellExecute").ToList();
        Assert.True(hits.Count == 0, "unexpected program or network access:\n" + string.Join("\n", hits.Select(h => $"{h.File}:{h.Line} {h.Text}")));
    }

    [Fact]
    public void TheOnlyLogLineIsTheShimsStartupNoticeAndItCarriesNoContent()
    {
        var hits = Lines(@"Console\.(Error|Out)\.|Console\.Write|Debug\.Write|Trace\.Write|File\.AppendAll|ILogger").ToList();
        var line = Assert.Single(hits);
        Assert.Equal("src/Diagram.Mcp/Program.cs", line.File);
        Assert.Matches("^Console\\.Error\\.WriteLine\\(\"[A-Za-z. ]+\"\\);$", line.Text); // a fixed string: no document text, paths or ids
    }

    [Fact]
    public void ExceptionsShownToPeopleAreNotBuiltFromDocumentOrAssetBytes()
    {
        // error messages may name a file or a limit, never echo content: nothing formats raw bytes or text blocks into a message
        var hits = Lines(@"Message\s*=\s*\$?"".*\{(bytes|payload|text\.Value|asset\.Bytes)").ToList();
        Assert.Empty(hits);
    }
}
