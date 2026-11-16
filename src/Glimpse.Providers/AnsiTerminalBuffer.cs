using System.Text;
using System.Text.RegularExpressions;

namespace Glimpse.Providers;

// Small bounded VT screen for login menus, not a command shell or full terminal
// emulator. CSI styling is ignored; cursor motion/erasure and OSC links are parsed.
internal sealed class AnsiTerminalBuffer(int width, int height)
{
    private readonly char[][] rows = Enumerable.Range(0, height).Select(_ => Enumerable.Repeat(' ', width).ToArray()).ToArray();
    private readonly bool[] wrapped = new bool[height];
    private readonly Queue<string> history = new();
    private readonly StringBuilder control = new();
    private readonly HashSet<string> oscLinks = [];
    private int x, y, savedX, savedY, state, historyChars;
    private static readonly Regex Url = new(@"https?://[^\s<>""\u0000-\u001f]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public string Text
    {
        get
        {
            var text = new StringBuilder();
            foreach (string line in history) text.Append(line);
            for (int i = 0; i < height; i++)
            {
                text.Append(new string(rows[i]).TrimEnd());
                if (!wrapped[i]) text.Append('\n');
            }
            return text.ToString().TrimEnd();
        }
    }
    public List<string> Links => Url.Matches(Text).Select(m => m.Value.TrimEnd('.', ',', ')', ']', '}', ';'))
        .Concat(oscLinks).Where(IsWebUrl).Distinct(StringComparer.Ordinal).TakeLast(12).ToList();
    private static bool IsWebUrl(string value) => value.Length <= 4096 && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Host.Length != 0;
    public void Clear()
    {
        foreach (char[] row in rows) Array.Fill(row, ' ');
        Array.Clear(wrapped); history.Clear(); control.Clear(); oscLinks.Clear();
        x = y = state = historyChars = 0;
    }
    public string Feed(ReadOnlySpan<char> text)
    {
        var response = new StringBuilder();
        foreach (char c in text)
        {
            if (state == 0)
            {
                switch (c)
                {
                    case '\x1b': state = 1; control.Clear(); break;
                    case '\r': x = 0; break;
                    case '\n': Down(); break;
                    case '\b': x = Math.Max(0, x - 1); break;
                    case '\t': x = Math.Min(width - 1, (x / 8 + 1) * 8); break;
                    default:
                        if (c < ' ' || c == '\x7f') break;
                        if (x >= width) { wrapped[y] = true; x = 0; Down(); }
                        rows[y][x++] = c;
                        break;
                }
            }
            else if (state == 1)
            {
                state = c switch { '[' => 2, ']' => 3, 'P' or '^' or '_' => 5, _ => 0 };
                if (c == '7') { savedX = x; savedY = y; }
                else if (c == '8') { x = savedX; y = savedY; }
            }
            else if (state == 2)
            {
                if (c is >= '@' and <= '~')
                {
                    if (c == 'n' && control.ToString() == "6") response.Append($"\x1b[{y + 1};{Math.Min(width, x + 1)}R");
                    else Csi(c, control.ToString());
                    state = 0; control.Clear();
                }
                else if (control.Length < 128) control.Append(c);
                else { state = 0; control.Clear(); }
            }
            else if (state == 3)
            {
                if (c == '\a') FinishOsc();
                else if (c == '\x1b') state = 4;
                else if (control.Length < 8192) control.Append(c);
            }
            else if (state == 4) { if (c == '\\') FinishOsc(); else state = 3; }
            else if (state == 5) { if (c == '\x1b') state = 6; }
            else if (state == 6) state = c == '\\' ? 0 : 5;
        }
        return response.ToString();
    }
    private void FinishOsc()
    {
        string value = control.ToString();
        if (value.StartsWith("8;", StringComparison.Ordinal))
        {
            int split = value.IndexOf(';', 2);
            if (split >= 0 && IsWebUrl(value[(split + 1)..]) && oscLinks.Count < 12) oscLinks.Add(value[(split + 1)..]);
        }
        control.Clear(); state = 0;
    }
    private void Down()
    {
        y++;
        if (y < height) return;
        string line = new string(rows[0]).TrimEnd() + (wrapped[0] ? "" : "\n");
        history.Enqueue(line); historyChars += line.Length;
        while (history.Count > 160 || historyChars > 16000) historyChars -= history.Dequeue().Length;
        for (int i = 0; i < height - 1; i++) { rows[i + 1].CopyTo(rows[i], 0); wrapped[i] = wrapped[i + 1]; }
        Array.Fill(rows[^1], ' '); wrapped[^1] = false; y = height - 1;
    }
    private void Csi(char final, string parameters)
    {
        if (parameters.StartsWith('?') || parameters.StartsWith('>')) return;
        int[] values = parameters.Split(';').Select(s => int.TryParse(s, out int value) ? Math.Clamp(value, 0, 10000) : 0).ToArray();
        int First(int fallback = 1) => values.Length == 0 || values[0] == 0 ? fallback : values[0];
        switch (final)
        {
            case 'A': y = Math.Max(0, y - First()); break;
            case 'B': y = Math.Min(height - 1, y + First()); break;
            case 'C': x = Math.Min(width - 1, x + First()); break;
            case 'D': x = Math.Max(0, x - First()); break;
            case 'E': x = 0; y = Math.Min(height - 1, y + First()); break;
            case 'F': x = 0; y = Math.Max(0, y - First()); break;
            case 'G': x = Math.Min(width - 1, First() - 1); break;
            case 'H': case 'f':
                y = Math.Min(height - 1, First() - 1);
                x = Math.Min(width - 1, values.Length > 1 && values[1] > 0 ? values[1] - 1 : 0); break;
            case 'K':
                int mode = values[0];
                int begin = mode == 0 ? Math.Min(width, x) : 0, end = mode == 1 ? Math.Min(width, x + 1) : width;
                Array.Fill(rows[y], ' ', begin, Math.Max(0, end - begin)); wrapped[y] = false; break;
            case 'J':
                if (values[0] is 2 or 3) { foreach (char[] row in rows) Array.Fill(row, ' '); Array.Clear(wrapped); if (values[0] == 3) { history.Clear(); historyChars = 0; } }
                else if (values[0] == 0)
                {
                    Array.Fill(rows[y], ' ', Math.Min(width, x), Math.Max(0, width - x)); wrapped[y] = false;
                    for (int i = y + 1; i < height; i++) { Array.Fill(rows[i], ' '); wrapped[i] = false; }
                }
                break;
            case 's': savedX = x; savedY = y; break;
            case 'u': x = savedX; y = savedY; break;
        }
    }
}
