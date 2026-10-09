// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using System.Text;

namespace Meltype.Input;

/// <summary>コードの行の、キャレットの位置がどこか。</summary>
public enum LineKind
{
    /// <summary>コード (英数が基本)。</summary>
    Code,
    /// <summary>コメントの中 (// # -- /* <!-- など)。日本語を書くことが多い。</summary>
    Comment,
    /// <summary>文字列の中 ("…" '…' `…`)。日本語を書くことが多い。</summary>
    String,
    /// <summary>ターミナルで動く AI・チャットの入力 (Claude Code・Codex などの「&gt; 」の後)。日本語を書くことが多い。</summary>
    Prompt,
    /// <summary>ターミナルのコマンドの、文を書く引数 (echo の後、git commit -m の後など。引用符の無いもの)。日本語を判定する。</summary>
    Argument,
}

/// <summary>「コード」のアプリで、フォーカスのある入力欄の種類。</summary>
public enum CodeFocus
{
    /// <summary>コードを書く場所ではない (チャット・AI への質問、設定の画面など)。一般のアプリと同じに扱う。</summary>
    None,
    /// <summary>コードエディター。</summary>
    Editor,
    /// <summary>ターミナル (プロンプトは出力なので、改行のたびに読み直す)。</summary>
    Terminal,
}

/// <summary>
/// コードエディター・ターミナル (アプリの種類が「コード」) で、今の行のキャレットより前の文字列から、
/// キャレットがコメントや文字列の中にあるか (日本語を書く場所か) を調べる。
/// 複数行のコメント・文字列 (/* … */ の途中の行、""" … """) は、行頭の * 以外は分からない。
/// </summary>
public static class LineContext
{
    /// <summary>
    /// キャレットより前の文字列 (前の行も含んでよい) から、キャレットの位置の種類を調べる。
    /// 前の行から続く複数行の文字列・コメント (Python の """ … """、/* … */、&lt;!-- … --&gt;) の中なら、その種類。
    /// それ以外は、今の行 (最後の改行より後ろ) だけで調べる。
    /// </summary>
    public static LineKind ClassifyText(string text)
    {
        var lastNewline = text.LastIndexOfAny(['\r', '\n']);
        var line = lastNewline >= 0 ? text[(lastNewline + 1)..] : text;
        if (lastNewline >= 0 && OpenBlock(text) is { } block) return block;
        return Classify(line);
    }

    /// <summary>
    /// 前の行から始まって、まだ閉じていない複数行の文字列・コメントがあれば、その種類 (無ければ null)。
    /// 1 行のコメント (# //) と 1 行の文字列 ("…" '…') の中の """ /* は数えない。
    /// </summary>
    private static LineKind? OpenBlock(string text)
    {
        string? close = null; // 閉じる記号 (""" ''' */ -->)
        var kind = LineKind.Code;
        var lineStart = true;
        var startedBeforeLastLine = false;
        var lastNewline = text.LastIndexOfAny(['\r', '\n']);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n')
            {
                lineStart = true;
                continue;
            }
            if (close is not null)
            {
                if (StartsWith(text, i, close))
                {
                    i += close.Length - 1;
                    close = null;
                }
                continue;
            }
            var atStart = lineStart && !char.IsWhiteSpace(c);
            if (!char.IsWhiteSpace(c)) lineStart = false;
            string? open = null;
            if (StartsWith(text, i, "\"\"\"")) (open, kind) = ("\"\"\"", LineKind.String);
            else if (StartsWith(text, i, "'''")) (open, kind) = ("'''", LineKind.String);
            else if (StartsWith(text, i, "/*")) (open, kind) = ("*/", LineKind.Comment);
            else if (StartsWith(text, i, "<!--")) (open, kind) = ("-->", LineKind.Comment);
            if (open is not null)
            {
                close = open;
                startedBeforeLastLine = i < lastNewline;
                i += (open == "*/" ? 2 : open == "-->" ? 4 : 3) - 1;
                continue;
            }
            // 1 行のコメント: 行の終わりまで飛ばす
            if (StartsWith(text, i, "//") || (c == '#' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]) || text[i + 1] == '#')) ||
                (atStart && StartsWithIgnoreCase(text, i, "rem ")))
            {
                while (i + 1 < text.Length && text[i + 1] is not ('\r' or '\n')) i++;
                continue;
            }
            // 1 行の文字列: 同じ引用符か行の終わりまで飛ばす
            if (c is '"' or '\'' && !(c == '\'' && i > 0 && char.IsLetterOrDigit(text[i - 1])))
            {
                for (i++; i < text.Length && text[i] != c && text[i] is not ('\r' or '\n'); i++)
                {
                    if (text[i] == '\\') i++;
                }
                if (i < text.Length && text[i] is '\r' or '\n') i--;
            }
        }
        // 今の行より前で開いて、まだ閉じていない。今の行で開いたものは、今の行だけで調べる (Classify)。
        return close is not null && startedBeforeLastLine ? kind : null;
    }

    public static LineKind Classify(string line)
    {
        if (IsChatPrompt(line)) return LineKind.Prompt;
        char? quote = null;
        var firstText = true; // まだ空白以外の文字が出ていない
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is { } q)
            {
                if (c == '\\') i++; // エスケープ (\" は文字列の終わりではない)
                else if (c == q) quote = null;
                continue;
            }
            if (char.IsWhiteSpace(c)) continue;
            var atStart = firstText;
            firstText = false;

            if (c is '"' or '`' || (c == '\'' && !(i > 0 && char.IsLetterOrDigit(line[i - 1]))))
            {
                // ' は英単語の中 (don't) なら文字列の始まりではない。
                quote = c;
                continue;
            }
            if (StartsWith(line, i, "//")) return LineKind.Comment;
            if (StartsWith(line, i, "/*"))
            {
                var close = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) return LineKind.Comment;
                i = close + 1;
                continue;
            }
            if (StartsWith(line, i, "<!--"))
            {
                var close = line.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (close < 0) return LineKind.Comment;
                i = close + 2;
                continue;
            }
            // # コメント (Python・シェル・PowerShell・YAML)。#include や #region のように # の直後が文字なら指令なので除く。
            if (c == '#' && (i + 1 == line.Length || char.IsWhiteSpace(line[i + 1]) || line[i + 1] == '#')) return LineKind.Comment;
            // -- コメント (SQL・Lua・Haskell)。x-- のような減算は直後が空白でないことが多い。
            if (StartsWith(line, i, "--") && (i + 2 == line.Length || line[i + 2] == ' ') && (atStart || i > 0 && line[i - 1] == ' ')) return LineKind.Comment;
            // 複数行コメントの途中の行 ( * …)
            if (atStart && c == '*' && (i + 1 == line.Length || line[i + 1] == ' ')) return LineKind.Comment;
            // REM コメント (バッチファイル)
            if (atStart && StartsWithIgnoreCase(line, i, "rem ")) return LineKind.Comment;
        }
        return quote is null ? LineKind.Code : LineKind.String;
    }

    /// <summary>
    /// ターミナルで動く AI・チャットの入力行か (Claude Code・Gemini CLI の「&gt; 」、Codex の「› 」。枠線の │ は飛ばす)。
    /// PowerShell の続きの行「&gt;&gt; 」や、シェルのプロンプト (PS C:\&gt;、$、❯) は含めない。
    /// </summary>
    private static bool IsChatPrompt(string line)
    {
        var i = 0;
        while (i < line.Length && (char.IsWhiteSpace(line[i]) || line[i] is '│' or '┃' or '║' or '|' or '╎' or '┆')) i++;
        if (i >= line.Length || line[i] is not ('>' or '›')) return false;
        return i + 1 == line.Length || line[i + 1] == ' ' || line[i + 1] == '\u00A0';
    }

    /// <summary>
    /// ターミナルの今の行 (キャレットより前) で、キャレットの位置が日本語を書く場所かを調べる (#71)。
    /// コマンド名・オプション・パス・リダイレクト先・打ちかけの語は <see cref="LineKind.Code"/> (英字のまま通す)。
    /// 引用符の中は String、# の後は Comment、AI の入力行は Prompt、echo などの文・-m / --message の後は Argument。
    /// hasPrompt: 画面から読んだ行で、先頭にプロンプト (PS C:\&gt;・$・❯ など) が付いているか (打鍵だけから追いかけた行には付かない)。
    /// </summary>
    public static LineKind ClassifyShell(string text, bool hasPrompt)
    {
        var lastNewline = text.LastIndexOfAny(['\r', '\n']);
        var line = lastNewline >= 0 ? text[(lastNewline + 1)..] : text;
        if (IsChatPrompt(line)) return LineKind.Prompt;
        if (hasPrompt) line = StripShellPrompt(line);

        var words = new List<string>(); // 今のコマンド (| ; & の区切りの後) の、打ち終えた語
        var word = new StringBuilder();
        var inWord = false;
        var redirect = false; // > < の後 (次の語はファイル名)
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is { } q)
            {
                // "…" の中のエスケープ (bash の \"、PowerShell の `")
                if (q == '"' && c is '\\' or '`' && i + 1 < line.Length && line[i + 1] == '"') i++;
                else if (c == q) quote = null;
                else word.Append(c);
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                inWord = true;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                EndWord();
                continue;
            }
            // # コメント (bash・PowerShell。語の途中の # は除く)
            if (c == '#' && !inWord) return LineKind.Comment;
            if (c is '|' or ';' or '&' or '(' or ')' or '{' or '}')
            {
                EndWord();
                words.Clear();
                redirect = false;
                continue;
            }
            if (c is '>' or '<')
            {
                // 2>&1 の 2 は語ではない。echo hi>out の hi は語。
                if (inWord && word.ToString() is "1" or "2")
                {
                    word.Clear();
                    inWord = false;
                }
                EndWord();
                redirect = true;
                // >> と >&1 の続きの記号も、リダイレクトの一部
                while (i + 1 < line.Length && line[i + 1] is '>' or '&') i++;
                if (i > 0 && line[i] == '&' && i + 1 < line.Length && char.IsAsciiDigit(line[i + 1]))
                {
                    i++;
                    redirect = false;
                }
                continue;
            }
            word.Append(c);
            inWord = true;
        }
        if (quote is not null) return LineKind.String;
        // 確定した日本語のすぐ後ろは、続けて文を書いている (echo 今日は|)。
        if (inWord && word.ToString().Any(ch => ch > '\u007F')) EndWord();
        // 打ちかけの語 (そのまま通した src/fo など) の続きは、そのまま通す。
        if (inWord || redirect || words.Count == 0) return LineKind.Code;
        var command = CommandName(words[0]);
        if (TextCommands.Contains(command)) return LineKind.Argument;
        // -m は git などのコミットの文だけ (python -m のモジュール名は英字のまま)。
        for (var w = 1; w < words.Count; w++)
        {
            if (MessageOptions.Contains(words[w]) || (words[w] == "-m" && CommitCommands.Contains(command))) return LineKind.Argument;
        }
        return LineKind.Code;

        void EndWord()
        {
            if (!inWord) return;
            if (redirect) redirect = false; // リダイレクト先のファイル名は、コマンドの語に数えない
            else words.Add(word.ToString());
            word.Clear();
            inWord = false;
        }
    }

    /// <summary>画面から読んだ行の先頭のプロンプトを除く。見つからなければそのまま。</summary>
    private static string StripShellPrompt(string line)
    {
        // PowerShell: PS C:\Users\me>
        if (line.StartsWith("PS ", StringComparison.Ordinal) && line.IndexOf('>') is var ps and > 0) return line[(ps + 1)..];
        // コマンドプロンプト: C:\Users\me>
        if (line.Length >= 3 && char.IsAsciiLetter(line[0]) && line[1] == ':' && line[2] == '\\' && line.IndexOf('>') is var cmd and > 0) return line[(cmd + 1)..];
        // bash・zsh・fish・starship など: いちばん前の「$ 」「❯ 」「% 」「# 」「➜ 」「λ 」「» 」「> 」まで
        var end = -1;
        foreach (var marker in (ReadOnlySpan<string>)["$ ", "❯ ", "% ", "# ", "➜ ", "λ ", "» ", "> "])
        {
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && (end < 0 || index < end)) end = index + marker.Length - 1;
        }
        return end >= 0 ? line[end..] : line;
    }

    /// <summary>コマンド名 (パスと .exe を除き、小文字)。</summary>
    private static string CommandName(string word)
    {
        var name = word[(word.LastIndexOfAny(['/', '\\']) + 1)..].ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }

    /// <summary>引数に文を書くコマンド (引用符が無くても、引数で日本語を判定する)。</summary>
    private static readonly HashSet<string> TextCommands = new(StringComparer.Ordinal)
    {
        "echo", "printf", "write-host", "write-output", "claude", "codex", "gemini",
    };

    /// <summary>この後に文を書くオプション (git commit -m・gh issue create --title など)。</summary>
    private static readonly HashSet<string> MessageOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--message", "--title", "--body", "--notes", "--subject", "--prompt", "-Message",
    };

    /// <summary>-m の後に文 (コミットメッセージ) を書くコマンド。</summary>
    private static readonly HashSet<string> CommitCommands = new(StringComparer.Ordinal) { "git", "hg", "svn", "jj" };

    private static readonly string[] ChatWords =
        ["chat", "チャット", "copilot", "claude", "codex", "gemini", "prompt", "プロンプト", "message", "メッセージ", "ask ", "質問", "composer", "agent", "エージェント", "cascade", "assistant", "アシスタント"];

    private static readonly string[] EditorWords = ["editor", "エディター", "エディタ"];

    private static readonly string[] TerminalWords = ["terminal", "ターミナル", "端末", "console", "コンソール"];

    // VS Code の検索・コマンドパレット・ファイル名の入力など (英数で打つもの)
    private static readonly string[] SearchWords = ["search", "検索", "command", "コマンド", "quick", "file name", "ファイル名", "filter", "フィルター", "go to", "移動", "find", "replace", "置換"];

    /// <summary>ターミナルのアプリ (アプリ全体がターミナル)。</summary>
    public static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal.exe", "OpenConsole.exe", "conhost.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
        "wezterm-gui.exe", "alacritty.exe", "mintty.exe",
    };

    /// <summary>エディター以外の入力欄 (チャット・拡張機能の画面) も多い、Electron 製のエディター。</summary>
    private static readonly HashSet<string> ElectronEditors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code.exe", "Code - Insiders.exe", "Cursor.exe", "Windsurf.exe", "zed.exe",
    };

    /// <summary>
    /// 「コード」のアプリで、フォーカスのある入力欄がコードを書く場所か (UI Automation の名前・クラス名から)。
    /// チャット・AI への入力欄 (VS Code の Copilot Chat、Claude Code の画面など) は一般のアプリと同じに扱う。
    /// VS Code などの Electron 製のエディターは入力欄の種類が多いので、エディター・ターミナル・検索と分かるものだけをコードとする。
    /// </summary>
    public static CodeFocus ClassifyFocus(string process, string name, string className)
    {
        if (ContainsAny(name, ChatWords)) return CodeFocus.None;
        if (TerminalProcesses.Contains(process) || ContainsAny(name, TerminalWords) || className.Contains("TermControl", StringComparison.Ordinal)) return CodeFocus.Terminal;
        if (ElectronEditors.Contains(process)) return ContainsAny(name, EditorWords) || ContainsAny(name, SearchWords) ? CodeFocus.Editor : CodeFocus.None;
        return CodeFocus.Editor;
    }

    private static bool ContainsAny(string text, string[] words)
    {
        foreach (var word in words)
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool StartsWith(string text, int index, string value) =>
        string.CompareOrdinal(text, index, value, 0, value.Length) == 0 && index + value.Length <= text.Length;

    private static bool StartsWithIgnoreCase(string text, int index, string value) =>
        index + value.Length <= text.Length && string.Compare(text, index, value, 0, value.Length, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>ウィンドウのタイトルが文章のファイル (README.md など) を開いているか。コードエディターでも文章なら「一般」として扱う。</summary>
    public static bool IsDocumentTitle(string title)
    {
        foreach (var extension in (ReadOnlySpan<string>)[".md", ".markdown", ".txt", ".rst", ".adoc", ".org", ".tex"])
        {
            var index = title.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (index > 0 && (index + extension.Length == title.Length || !char.IsLetterOrDigit(title[index + extension.Length]))) return true;
        }
        return false;
    }
}

/// <summary>
/// キャレットより前の文字列を、打鍵から追いかける (フックのスレッドと UI スレッドから呼ばれる)。
/// 複数行のコメント・文字列 (""" … """、/* … */) の中かを調べるため、前の行も最大 4000 文字持つ (行の区切りは改行 1 文字)。
/// キャレットが動いた (矢印キー・クリック・フォーカスの変化・Ctrl の操作) ら分からなくなり、UI Automation で読み直す。
/// </summary>
public sealed class LineTracker
{
    private const int MaxLength = 4000;
    private readonly object _gate = new();
    private readonly StringBuilder _text = new();
    private bool _known;
    // 分からない間 (画面を読み終わるまで) にアプリへ届いた文字。読んだ文字列にまだ出ていなければ足す (#71: 1 文字目の取りこぼし)。
    private readonly StringBuilder _unread = new();
    private bool _fromScreen;

    /// <summary>今の行を画面から読んだか (読んだ行にはターミナルのプロンプトが付いている)。打鍵だけで追いかけた行なら false。</summary>
    public bool FromScreen
    {
        get { lock (_gate) return _known && _fromScreen; }
    }

    /// <summary>キャレットより前の文字列 (前の行も含む)。分からなければ null。</summary>
    public string? Text
    {
        get { lock (_gate) return _known ? _text.ToString() : null; }
    }

    public void Append(string text)
    {
        lock (_gate)
        {
            if (!_known)
            {
                if (_unread.Length + text.Length <= MaxLength) _unread.Append(text);
                return;
            }
            foreach (var c in text) _text.Append(c == '\r' ? '\n' : c);
            Trim();
        }
    }

    public void Backspace()
    {
        lock (_gate)
        {
            if (!_known)
            {
                if (_unread.Length > 0) _unread.Length--;
                return;
            }
            // 改行を消したら前の行とつながる (前の行も持っているので分かる)。
            if (_text.Length > 0) _text.Length--;
            else _known = false;
        }
    }

    /// <summary>Enter: 新しい行 (自動のインデントは分からないが、空白なので判定には影響しない)。</summary>
    public void NewLine()
    {
        lock (_gate)
        {
            // 前の行が分からなければ、新しい行から追いかける (複数行のコメント・文字列の中かは分からない)。
            if (!_known) _text.Clear();
            else _text.Append('\n');
            _known = true;
            _fromScreen = false;
            _unread.Clear();
            Trim();
        }
    }

    /// <summary>キャレットが動いたかもしれない。</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _text.Clear();
            _unread.Clear();
            _known = false;
        }
    }

    public bool IsKnown
    {
        get { lock (_gate) return _known; }
    }

    /// <summary>UI Automation で読んだキャレットより前の文字列 (前の行も含めて持つ)。</summary>
    public void SetFromText(string before)
    {
        lock (_gate)
        {
            var text = before.Replace("\r\n", "\n").Replace('\r', '\n');
            _text.Clear();
            _text.Append(text);
            // 読む前に打った文字が、読んだ時にはまだ画面に出ていなかったら足す。
            var unread = _unread.ToString();
            if (unread.Length > 0 && !text.EndsWith(unread, StringComparison.Ordinal)) _text.Append(unread.Replace('\r', '\n'));
            _unread.Clear();
            // 今の行に何か書いてあれば (プロンプト)、画面から読んだ行。空なら打鍵だけで追いかけるのと同じ。
            _fromScreen = text.Length > 0 && text[^1] != '\n';
            _known = true;
            Trim();
        }
    }

    private void Trim()
    {
        if (_text.Length > MaxLength) _text.Remove(0, _text.Length - MaxLength);
    }
}
