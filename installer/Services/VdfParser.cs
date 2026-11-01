using System.Text;

namespace SailawayRevival.Installer.Services;

// Minimal reader for Valve's VDF/KeyValues text format (used by
// libraryfolders.vdf). Only handles what we need: quoted-string tokens and
// braces. No comments, no escape sequences beyond \" and \\, both of which
// Steam's own writer uses.
public static class VdfParser
{
    // A VDF node's children are either a nested object (Dictionary) or a
    // leaf string value. Steam's format mixes both at variable depth
    // depending on version, so we don't impose a shape here - callers walk
    // the tree looking for whatever keys they need.
    public static Dictionary<string, object> Parse(string text)
    {
        var tokens = Tokenize(text);
        int pos = 0;
        // The file is one root object with no enclosing braces, e.g.
        // "libraryfolders" { ... } - read it as an implicit single entry.
        var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (pos < tokens.Count)
        {
            string key = tokens[pos++];
            if (pos >= tokens.Count)
            {
                break;
            }
            if (tokens[pos] == "{")
            {
                pos++;
                root[key] = ParseObject(tokens, ref pos);
            }
            else
            {
                root[key] = tokens[pos++];
            }
        }
        return root;
    }

    static Dictionary<string, object> ParseObject(List<string> tokens, ref int pos)
    {
        var obj = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (pos < tokens.Count && tokens[pos] != "}")
        {
            string key = tokens[pos++];
            if (pos >= tokens.Count)
            {
                break;
            }
            if (tokens[pos] == "{")
            {
                pos++;
                obj[key] = ParseObject(tokens, ref pos);
            }
            else if (tokens[pos] == "}")
            {
                // malformed (key with no value before the closing brace) - ignore.
                break;
            }
            else
            {
                obj[key] = tokens[pos++];
            }
        }
        if (pos < tokens.Count && tokens[pos] == "}")
        {
            pos++; // consume the closing brace
        }
        return obj;
    }

    static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (c == '{' || c == '}')
            {
                tokens.Add(c.ToString());
                i++;
                continue;
            }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        sb.Append(text[i + 1]);
                        i += 2;
                    }
                    else
                    {
                        sb.Append(text[i]);
                        i++;
                    }
                }
                i++; // consume closing quote
                tokens.Add(sb.ToString());
                continue;
            }
            // Unquoted token (not expected in Steam's own files, but skip
            // gracefully rather than looping forever on something unexpected).
            i++;
        }
        return tokens;
    }

    // Recursively collects every string value found under a key with this
    // exact name (case-insensitive), at any depth. Steam's libraryfolders.vdf
    // always has a "path" key at the library-block level regardless of
    // format generation (old flat list vs. newer per-library "apps" map), so
    // this sidesteps needing to parse the rest of the structure at all.
    public static List<string> CollectValues(object node, string key)
    {
        var results = new List<string>();
        Walk(node, key, results);
        return results;
    }

    static void Walk(object node, string key, List<string> results)
    {
        if (node is not Dictionary<string, object> dict)
        {
            return;
        }
        foreach (var (k, v) in dict)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase) && v is string s)
            {
                results.Add(s);
            }
            if (v is Dictionary<string, object>)
            {
                Walk(v, key, results);
            }
        }
    }
}
