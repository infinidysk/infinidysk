using System.Xml;

namespace NzbWebDAV.Utils;

/// <summary>
/// XML 1.0 forbids U+FFFE, U+FFFF, lone surrogates, and most C0 controls. Names
/// carrying those (e.g. mis-decoded NZB subjects) make XmlWriter throw while
/// serializing a PROPFIND response, which turns the whole listing into a 500.
/// </summary>
public static class XmlTextUtil
{
    public const char ReplacementChar = '\uFFFD';

    public static bool ContainsInvalidXmlChars(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (XmlConvert.IsXmlChar(text[i]))
                continue;

            if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], text[i]))
            {
                i++;
                continue;
            }

            return true;
        }

        return false;
    }

    public static string ReplaceInvalidXmlChars(string text, char replacement = ReplacementChar)
    {
        if (!ContainsInvalidXmlChars(text))
            return text;

        var buffer = new char[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (XmlConvert.IsXmlChar(ch))
            {
                buffer[i] = ch;
            }
            else if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], ch))
            {
                buffer[i] = ch;
                buffer[i + 1] = text[i + 1];
                i++;
            }
            else
            {
                buffer[i] = replacement;
            }
        }

        return new string(buffer);
    }
}
