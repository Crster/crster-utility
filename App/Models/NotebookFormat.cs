using System;
using System.Collections.Generic;

namespace App.Models
{
    internal enum NoteSectionKind
    {
        Text,
        Title,
        Description,
        Password,
        File,
        Image,
        Table
    }

    internal sealed record NoteSection(NoteSectionKind Kind, string Content, int SourceStart, int SourceLength);

    internal static class NotebookFormat
    {
        public static List<NoteSection> Parse(string content)
        {
            var normalized = Normalize(content);
            var sections = new List<NoteSection>();
            var offset = 0;
            while (offset <= normalized.Length)
            {
                var lineEnd = normalized.IndexOf('\n', offset);
                if (lineEnd < 0) lineEnd = normalized.Length;
                var line = normalized[offset..lineEnd];
                var lineLength = line.Length + (offset + line.Length < normalized.Length ? 1 : 0);
                var isTable = line.StartsWith("@table:", StringComparison.Ordinal) && line[7..].Trim() == "{";
                if (isTable && TryReadSection(normalized, offset, lineLength, out var body, out var totalLength))
                {
                    sections.Add(new NoteSection(NoteSectionKind.Table, body, offset, totalLength));
                    offset += totalLength;
                    continue;
                }

                var kind = NoteSectionKind.Text;
                var value = line;
                if (line.StartsWith("##", StringComparison.Ordinal)) { kind = NoteSectionKind.Description; value = line[2..].TrimStart(); }
                else if (line.StartsWith('#')) { kind = NoteSectionKind.Title; value = line[1..].TrimStart(); }
                else if (line.StartsWith("@password:", StringComparison.Ordinal)) { kind = NoteSectionKind.Password; value = line[10..].TrimStart(); }
                else if (line.StartsWith("@file:", StringComparison.Ordinal)) { kind = NoteSectionKind.File; value = line[6..].TrimStart(); }
                else if (line.StartsWith("@image:", StringComparison.Ordinal)) { kind = NoteSectionKind.Image; value = line[7..].TrimStart(); }

                sections.Add(new NoteSection(kind, value, offset, lineLength));
                offset += lineLength;
                if (offset == normalized.Length && normalized.EndsWith('\n'))
                {
                    sections.Add(new NoteSection(NoteSectionKind.Text, string.Empty, offset, 0));
                    break;
                }
                if (offset >= normalized.Length) break;
            }

            return sections;
        }

        public static string? GetTitle(string content)
        {
            foreach (var section in Parse(content))
                if (section.Kind == NoteSectionKind.Title && !string.IsNullOrWhiteSpace(section.Content)) return section.Content;
            return null;
        }

        public static string CreateSearchText(string content)
        {
            var parts = new List<string>();
            foreach (var section in Parse(content))
            {
                if (section.Kind is NoteSectionKind.File or NoteSectionKind.Image or NoteSectionKind.Password) continue;
                if (section.Kind == NoteSectionKind.Table)
                {
                    parts.Add(section.Content.Replace(',', ' '));
                }
                else parts.Add(RemoveInlineMarkers(section.Content));
            }
            return string.Join(' ', parts).Trim();
        }

        public static string Normalize(string content) => content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        private static bool TryReadSection(string content, int start, int openingLength, out string body, out int totalLength)
        {
            var bodyStart = start + openingLength;
            var cursor = bodyStart;
            while (cursor <= content.Length)
            {
                var end = content.IndexOf('\n', cursor);
                if (end < 0) end = content.Length;
                if (content[cursor..end] == "}")
                {
                    body = content[bodyStart..cursor].TrimEnd('\n');
                    totalLength = end - start + (end < content.Length ? 1 : 0);
                    return true;
                }
                if (end == content.Length) break;
                cursor = end + 1;
            }
            body = string.Empty;
            totalLength = openingLength;
            return false;
        }

        private static string RemoveInlineMarkers(string text)
        {
            foreach (var marker in new[] { '"', '\'', '(', ')' }) text = text.Replace(marker.ToString(), string.Empty, StringComparison.Ordinal);
            return text;
        }
    }
}
