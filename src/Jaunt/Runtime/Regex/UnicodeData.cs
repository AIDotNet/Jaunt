using System;
using System.Text;
using System.Threading;

namespace Jaunt.Runtime.Regex;

/// <summary>
/// Unicode data for RegExp property escapes, properties of strings and simple case folding.
/// The raw tables live in the generated UnicodeData.g.cs (tools/unicode/generate.py).
/// </summary>
internal static partial class UnicodeData
{
    private static readonly int[]?[] s_tables = new int[]?[TableCount];
    private static readonly string[]?[] s_stringTables = new string[]?[StringTableCount];
    private static string[]? s_rgiEmoji;
    private static int[]? s_foldFrom;
    private static int[]? s_foldTo;

    /// <summary>
    /// Resolves <c>\p{name}</c> (<paramref name="value"/> null) or <c>\p{name=value}</c> per
    /// ECMAScript UnicodeMatchProperty / UnicodeMatchPropertyValue (case-sensitive, no loose matching).
    /// On success, <paramref name="ranges"/> is a fresh array of sorted, merged, inclusive code point
    /// ranges <c>[start0, end0, start1, end1, ...]</c>.
    /// </summary>
    public static bool TryGetProperty(string name, string? value, out int[] ranges)
    {
        int index;
        if (value is null)
        {
            index = GeneralCategoryTable(name);
            if (index < 0)
                index = BinaryPropertyTable(name);
        }
        else
        {
            switch (name)
            {
                case "General_Category":
                case "gc":
                    index = GeneralCategoryTable(value);
                    break;
                case "Script":
                case "sc":
                    index = ScriptTable(value);
                    break;
                case "Script_Extensions":
                case "scx":
                    index = ScriptExtensionsTable(value);
                    break;
                default:
                    index = -1;
                    break;
            }
        }

        if (index < 0)
        {
            ranges = Array.Empty<int>();
            return false;
        }

        ranges = (int[])GetTable(index).Clone();
        return true;
    }

    /// <summary>
    /// Resolves a v-flag property of strings (Basic_Emoji, Emoji_Keycap_Sequence, RGI_Emoji_Modifier_Sequence,
    /// RGI_Emoji_Flag_Sequence, RGI_Emoji_Tag_Sequence, RGI_Emoji_ZWJ_Sequence, RGI_Emoji). On success,
    /// <paramref name="strings"/> is a fresh array of all members as UTF-16 strings, including members that
    /// are a single code point.
    /// </summary>
    public static bool TryGetStringProperty(string name, out string[] strings)
    {
        if (name == "RGI_Emoji")
        {
            strings = (string[])GetRgiEmoji().Clone();
            return true;
        }

        int index = StringPropertyTable(name);
        if (index < 0)
        {
            strings = Array.Empty<string>();
            return false;
        }

        strings = (string[])GetStringTable(index).Clone();
        return true;
    }

    /// <summary>Simple case folding (CaseFolding.txt statuses C and S); identity when unmapped.</summary>
    public static int SimpleCaseFold(int codePoint)
    {
        int[] from = s_foldFrom ?? InitCaseFolding();
        int[] to = s_foldTo!;
        int i = Array.BinarySearch(from, codePoint);
        return i >= 0 ? to[i] : codePoint;
    }

    private static int[] GetTable(int index)
    {
        int[]? table = Volatile.Read(ref s_tables[index]);
        if (table is null)
        {
            table = DecodeRanges(EncodedTable(index));
            table = Interlocked.CompareExchange(ref s_tables[index], table, null) ?? table;
        }
        return table;
    }

    private static string[] GetStringTable(int index)
    {
        string[]? table = Volatile.Read(ref s_stringTables[index]);
        if (table is null)
        {
            table = DecodeStrings(EncodedStringTable(index));
            table = Interlocked.CompareExchange(ref s_stringTables[index], table, null) ?? table;
        }
        return table;
    }

    private static string[] GetRgiEmoji()
    {
        string[]? all = Volatile.Read(ref s_rgiEmoji);
        if (all is null)
        {
            int total = 0;
            for (int i = 0; i < StringTableCount; i++)
                total += GetStringTable(i).Length;
            all = new string[total];
            int pos = 0;
            for (int i = 0; i < StringTableCount; i++)
            {
                string[] part = GetStringTable(i);
                Array.Copy(part, 0, all, pos, part.Length);
                pos += part.Length;
            }
            all = Interlocked.CompareExchange(ref s_rgiEmoji, all, null) ?? all;
        }
        return all;
    }

    private static int[] InitCaseFolding()
    {
        string s = EncodedCaseFolding;
        int pos = 0;
        int count = ReadNumber(s, ref pos);
        int[] from = new int[count];
        int[] to = new int[count];
        int prev = -1;
        for (int i = 0; i < count; i++)
        {
            int cp = prev + 1 + ReadNumber(s, ref pos);
            from[i] = cp;
            to[i] = cp + Unzigzag(ReadNumber(s, ref pos));
            prev = cp;
        }
        // Publish s_foldTo before s_foldFrom: readers check s_foldFrom first.
        Volatile.Write(ref s_foldTo, to);
        Volatile.Write(ref s_foldFrom, from);
        return from;
    }

    private static int[] DecodeRanges(string s)
    {
        int pos = 0;
        int count = ReadNumber(s, ref pos);
        int[] result = new int[count * 2];
        int prevEnd = 0;
        for (int i = 0; i < count; i++)
        {
            int n = ReadNumber(s, ref pos);
            int start = i == 0 ? n : prevEnd + 2 + n;
            int end = start + ReadNumber(s, ref pos);
            result[2 * i] = start;
            result[2 * i + 1] = end;
            prevEnd = end;
        }
        return result;
    }

    private static string[] DecodeStrings(string s)
    {
        int pos = 0;
        int count = ReadNumber(s, ref pos);
        string[] result = new string[count];
        var sb = new StringBuilder();
        int prev = 0;
        for (int i = 0; i < count; i++)
        {
            sb.Clear();
            int length = ReadNumber(s, ref pos);
            for (int j = 0; j < length; j++)
            {
                int cp = prev + Unzigzag(ReadNumber(s, ref pos));
                if (cp >= 0x10000)
                {
                    sb.Append((char)(0xD800 + ((cp - 0x10000) >> 10)));
                    sb.Append((char)(0xDC00 + ((cp - 0x10000) & 0x3FF)));
                }
                else
                {
                    sb.Append((char)cp);
                }
                prev = cp;
            }
            result[i] = sb.ToString();
        }
        return result;
    }

    private static int Unzigzag(int n) => (n & 1) == 0 ? n >> 1 : -((n + 1) >> 1);

    // Self-delimiting base-32 numbers: non-final digits from "A-Z0-5", final digit from "a-z6-9+/".
    private static int ReadNumber(string s, ref int pos)
    {
        int value = 0;
        while (true)
        {
            char c = s[pos++];
            int d;
            bool final;
            if (c >= 'A' && c <= 'Z') { d = c - 'A'; final = false; }
            else if (c >= '0' && c <= '5') { d = 26 + (c - '0'); final = false; }
            else if (c >= 'a' && c <= 'z') { d = c - 'a'; final = true; }
            else if (c >= '6' && c <= '9') { d = 26 + (c - '6'); final = true; }
            else if (c == '+') { d = 30; final = true; }
            else if (c == '/') { d = 31; final = true; }
            else throw new InvalidOperationException("Corrupt Unicode data table.");

            value = (value << 5) | d;
            if (final)
                return value;
        }
    }
}
