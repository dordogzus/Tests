using System;
using System.Collections.Generic;

namespace MorePlayersMod.Logic;

/// <summary>
/// Matching rules for extending the vanilla lobby member list (pure, unit tested).
/// The vanilla panel is located at runtime by finding the text that shows a lobby
/// member's name; sibling rows are recognized by their GameObject name.
/// </summary>
public static class LobbyRows
{
    public const string CloneTag = "MPM_LobbyRow";

    /// <summary>"PlayerRow (3)", "PlayerRow(Clone)", "PlayerRow_2" all become "playerrow".</summary>
    public static string NormalizeRowName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        string s = name.Replace(CloneTag, "").Replace("(Clone)", "");
        int paren = s.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) s = s.Substring(0, paren);
        s = s.TrimEnd();
        int end = s.Length;
        while (end > 0 && (char.IsDigit(s[end - 1]) || s[end - 1] == '_' || s[end - 1] == ' ')) end--;
        if (end > 0) s = s.Substring(0, end);
        return s.Trim().ToLowerInvariant();
    }

    /// <summary>Whether a UI text shows this member (exact, or with a suffix such as " (You)").</summary>
    public static bool TextShowsName(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(name)) return false;
        string t = StripRichText(text).Trim();
        if (string.Equals(t, name, StringComparison.OrdinalIgnoreCase)) return true;
        return t.StartsWith(name, StringComparison.OrdinalIgnoreCase) && t.Length > name.Length &&
               (t[name.Length] == ' ' || t[name.Length] == '(');
    }

    /// <summary>Members not already shown by vanilla rows, in lobby order; they go into cloned rows.</summary>
    public static List<string> Overflow(IReadOnlyList<string> shownTexts, IReadOnlyList<string> memberNames)
    {
        var result = new List<string>();
        foreach (var name in memberNames)
        {
            bool shown = false;
            foreach (var t in shownTexts) if (TextShowsName(t, name)) { shown = true; break; }
            if (!shown) result.Add(name);
        }
        return result;
    }

    public static string StripRichText(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0) return text ?? "";
        var sb = new System.Text.StringBuilder(text.Length);
        bool inTag = false;
        foreach (char c in text)
        {
            if (c == '<') inTag = true;
            else if (c == '>' && inTag) inTag = false;
            else if (!inTag) sb.Append(c);
        }
        return sb.ToString();
    }
}
