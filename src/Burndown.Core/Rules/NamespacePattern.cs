using System.Text;
using System.Text.RegularExpressions;

namespace Burndown.Core.Rules;

/// <summary>
/// A glob over namespace names.
/// <list type="bullet">
/// <item><c>Shop.Domain</c> matches exactly that namespace.</item>
/// <item><c>*</c> matches within one segment: <c>Shop.*.Api</c> matches <c>Shop.Orders.Api</c>.</item>
/// <item>A trailing <c>.**</c> also matches the namespace itself: <c>Shop.Domain.**</c> matches <c>Shop.Domain</c> and <c>Shop.Domain.Orders.Internal</c>.</item>
/// <item><c>**</c> elsewhere matches any characters, dots included.</item>
/// </list>
/// </summary>
public sealed class NamespacePattern
{
    private readonly Regex _regex;

    private NamespacePattern(string text, Regex regex)
    {
        Text = text;
        _regex = regex;
    }

    public string Text { get; }

    public static NamespacePattern Parse(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ArchitectureConfigException("Namespace patterns can't be empty.");
        }

        var body = pattern.Trim();
        var includeChildren = body.EndsWith(".**", StringComparison.Ordinal);
        if (includeChildren)
        {
            body = body[..^3];
        }

        var regex = new StringBuilder("^");
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '*')
            {
                regex.Append(Regex.Escape(body[i].ToString()));
            }
            else if (i + 1 < body.Length && body[i + 1] == '*')
            {
                regex.Append(".*");
                i++;
            }
            else
            {
                regex.Append("[^.]*");
            }
        }

        if (includeChildren)
        {
            regex.Append(@"(\..+)?");
        }

        regex.Append('$');
        return new NamespacePattern(pattern, new Regex(regex.ToString(), RegexOptions.CultureInvariant));
    }

    public bool Matches(string @namespace) => _regex.IsMatch(@namespace);

    public override string ToString() => Text;
}
