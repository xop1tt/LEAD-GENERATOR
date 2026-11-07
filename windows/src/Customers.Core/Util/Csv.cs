using System.Text;

namespace Customers.Core;

public static class Csv
{
    /// <summary>
    /// Ячейки, которые Excel считает формулами. Текст из внешних источников может начинаться с «=HYPERLINK(...)»,
    /// поэтому такие значения экранируются апострофом. Номер телефона вида «+7 (843) 212-48-90» формулой быть не может и не меняется.
    /// </summary>
    internal static string NeutralizeFormula(string s)
    {
        if (s.Length == 0 || !"=+-@\t\r".Contains(s[0]))
        {
            return s;
        }

        if (s.All(c => char.IsAsciiDigit(c) || c is '+' or '-' or '(' or ')' or ' ') && s.Any(char.IsAsciiDigit))
        {
            return s;
        }

        return "'" + s;
    }

    public static string Escape(string raw)
    {
        var s = NeutralizeFormula(raw);
        if (s.IndexOfAny([',', '"', '\n', '\r', ';']) >= 0)
        {
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        return s;
    }

    /// <summary>Разделитель строки: считаются только «;», «,» и табуляция вне кавычек.</summary>
    public static char Separator(string line)
    {
        var semi = 0;
        var comma = 0;
        var tab = 0;
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted)
            {
                switch (c)
                {
                    case ';':
                        semi++;
                        break;
                    case ',':
                        comma++;
                        break;
                    case '\t':
                        tab++;
                        break;
                }
            }
        }

        if (semi >= Math.Max(comma, 1) && semi >= tab)
        {
            return ';';
        }

        return tab > comma ? '\t' : ',';
    }

    /// <summary>Разбор одной строки CSV (запятая, точка с запятой или табуляция, кавычки).</summary>
    public static List<string> ParseLine(string line) => ParseLine(line, Separator(line));

    public static List<string> ParseLine(string line, char sep)
    {
        var output = new List<string>();
        var cur = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        cur.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    cur.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == sep)
            {
                output.Add(cur.ToString());
                cur.Clear();
            }
            else
            {
                cur.Append(c);
            }
        }

        output.Add(cur.ToString());
        return output;
    }

    /// <summary>Разбор всего файла: строки внутри кавычек не разрываются.</summary>
    public static List<List<string>> ParseDocument(string text)
    {
        var records = new List<string>();
        var cur = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                quoted = !quoted;
                cur.Append(c);
            }
            else if (!quoted && (c == '\n' || c == '\r'))
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                records.Add(cur.ToString());
                cur.Clear();
            }
            else
            {
                cur.Append(c);
            }
        }

        if (cur.Length > 0)
        {
            records.Add(cur.ToString());
        }

        var nonEmpty = records.Where(r => r.Length > 0).ToList();
        if (nonEmpty.Count == 0)
        {
            return [];
        }

        // Разделитель определяется по заголовку и действует для всего файла.
        var sepAll = Separator(nonEmpty[0]);
        return nonEmpty.Select(r => ParseLine(r, sepAll)).ToList();
    }
}
