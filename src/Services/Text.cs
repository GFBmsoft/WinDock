using System.Globalization;
using System.Text;

namespace WinDock.Services;

/// <summary>Normalizacao usada pela busca do launcher.</summary>
public static class Text
{
    /// <summary>Minusculas e sem acento: "área" e "area" sao a mesma busca.</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
