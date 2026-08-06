using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace App.Models
{
    internal static partial class NotebookFormat
    {
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        public static string PrepareMarkdown(string content, bool includePasswords = true)
        {
            var normalized = Normalize(content);
            var withTables = ReplaceTableBlocks(normalized);
            return ReplaceInlineExtensions(withTables, includePasswords);
        }

        public static string? GetTitle(string content)
        {
            var document = Markdown.Parse(PrepareMarkdown(content, false), Pipeline);
            var heading = document.Descendants<HeadingBlock>().FirstOrDefault();
            var title = heading is null ? null : InlineText(heading.Inline);
            return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        }

        public static string CreateSearchText(string content)
        {
            var document = Markdown.Parse(PrepareMarkdown(content, false), Pipeline);
            var parts = document.Descendants<LeafBlock>()
                .Select(block => InlineText(block.Inline))
                .Where(value => !string.IsNullOrWhiteSpace(value));
            return string.Join(' ', parts).Trim();
        }

        public static List<string> ExtractAttachmentIds(string content)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var document = Markdown.Parse(PrepareMarkdown(content, false), Pipeline);
            foreach (var link in document.Descendants<LinkInline>())
            {
                var match = LocalAttachmentRegex().Match(link.Url ?? string.Empty);
                if (match.Success && Guid.TryParse(match.Groups["id"].Value, out var id)) ids.Add(id.ToString("D"));
            }
            return [.. ids];
        }

        public static string Normalize(string content) =>
            content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        private static string ReplaceTableBlocks(string content)
        {
            var output = new StringBuilder();
            var lines = content.Split('\n');
            var fenced = false;
            for (var index = 0; index < lines.Length; index++)
            {
                if (IsFenceLine(lines[index]))
                {
                    fenced = !fenced;
                    output.Append(lines[index]);
                    if (index < lines.Length - 1) output.Append('\n');
                    continue;
                }

                if (fenced || !string.Equals(lines[index].TrimEnd(), "@table{", StringComparison.Ordinal))
                {
                    output.Append(lines[index]);
                    if (index < lines.Length - 1) output.Append('\n');
                    continue;
                }

                var closing = index + 1;
                while (closing < lines.Length && !string.Equals(lines[closing], "}", StringComparison.Ordinal)) closing++;
                if (closing >= lines.Length)
                {
                    output.Append(lines[index]);
                    if (index < lines.Length - 1) output.Append('\n');
                    continue;
                }

                var csv = string.Join('\n', lines[(index + 1)..closing]);
                var rows = ParseCsvRows(csv);
                if (rows.Count == 0)
                {
                    output.Append(lines[index]).Append('\n').Append(csv).Append('\n').Append('}');
                    index = closing;
                    if (index < lines.Length - 1) output.Append('\n');
                    continue;
                }

                var columns = rows.Max(row => row.Length);
                output.AppendLine(ToMarkdownRow(rows[0], columns));
                output.AppendLine(ToMarkdownRow(Enumerable.Repeat("---", columns).ToArray(), columns));
                foreach (var row in rows.Skip(1)) output.AppendLine(ToMarkdownRow(row, columns));
                if (closing == lines.Length - 1 && output.Length > 0) output.Length--;
                index = closing;
            }
            return output.ToString();
        }

        private static string ReplaceInlineExtensions(string content, bool includePasswords)
        {
            var output = new StringBuilder();
            var lines = content.Split('\n');
            var fenced = false;
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                if (IsFenceLine(lines[lineIndex]))
                {
                    fenced = !fenced;
                    output.Append(lines[lineIndex]);
                }
                else output.Append(fenced ? lines[lineIndex] : ReplaceInlineExtensionsInLine(lines[lineIndex], includePasswords));
                if (lineIndex < lines.Length - 1) output.Append('\n');
            }
            return output.ToString();
        }

        private static string ReplaceInlineExtensionsInLine(string content, bool includePasswords)
        {
            var output = new StringBuilder();
            for (var index = 0; index < content.Length;)
            {
                if (content[index] == '`')
                {
                    var runLength = 1;
                    while (index + runLength < content.Length && content[index + runLength] == '`') runLength++;
                    var marker = new string('`', runLength);
                    var closing = content.IndexOf(marker, index + runLength, StringComparison.Ordinal);
                    if (closing < 0) { output.Append(content[index..]); break; }
                    output.Append(content[index..(closing + runLength)]);
                    index = closing + runLength;
                    continue;
                }

                var kind = content.AsSpan(index).StartsWith("@password{", StringComparison.Ordinal) ? "password"
                    : content.AsSpan(index).StartsWith("@file{", StringComparison.Ordinal) ? "file"
                    : content.AsSpan(index).StartsWith("@image{", StringComparison.Ordinal) ? "image"
                    : null;
                if (kind is null)
                {
                    output.Append(content[index++]);
                    continue;
                }

                var openingLength = kind.Length + 2;
                var end = FindClosingBrace(content, index + openingLength);
                if (end < 0)
                {
                    output.Append(content[index++]);
                    continue;
                }

                var value = UnescapeClosingBrace(content[(index + openingLength)..end]);
                if (string.IsNullOrWhiteSpace(value))
                {
                    output.Append(content[index..(end + 1)]);
                    index = end + 1;
                    continue;
                }

                if (kind == "password")
                {
                    if (includePasswords)
                    {
                        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
                        output.Append('[').Append(new string('•', value.Length)).Append("](notebook-password:").Append(encoded).Append(')');
                    }
                }
                else
                {
                    var label = DisplayName(value);
                    output.Append(kind == "image" ? "![" : "[")
                        .Append(EscapeLabel(label)).Append("](<").Append(value.Replace(">", "%3E", StringComparison.Ordinal)).Append(">)");
                }
                index = end + 1;
            }
            return output.ToString();
        }

        private static bool IsFenceLine(string line)
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);
        }

        private static int FindClosingBrace(string content, int start)
        {
            for (var index = start; index < content.Length; index++)
                if (content[index] == '}' && (index == start || content[index - 1] != '\\')) return index;
            return -1;
        }

        private static string UnescapeClosingBrace(string value) => value.Replace("\\}", "}", StringComparison.Ordinal);

        private static string DisplayName(string target)
        {
            var trimmed = target.TrimEnd('/', '\\');
            var separator = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
            return separator >= 0 && separator < trimmed.Length - 1 ? trimmed[(separator + 1)..] : trimmed;
        }

        private static string EscapeLabel(string value) =>
            value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);

        private static string ToMarkdownRow(IReadOnlyList<string> row, int columns)
        {
            var cells = Enumerable.Range(0, columns)
                .Select(index => index < row.Count ? row[index].Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ↵ ", StringComparison.Ordinal) : string.Empty);
            return $"| {string.Join(" | ", cells)} |";
        }

        private static List<string[]> ParseCsvRows(string content)
        {
            var rows = new List<string[]>();
            var row = new List<string>();
            var field = new StringBuilder();
            var quoted = false;
            for (var index = 0; index < content.Length; index++)
            {
                var character = content[index];
                if (character == '"')
                {
                    if (quoted && index + 1 < content.Length && content[index + 1] == '"') { field.Append('"'); index++; }
                    else quoted = !quoted;
                }
                else if (character == ',' && !quoted) { row.Add(field.ToString()); field.Clear(); }
                else if (character == '\n' && !quoted) { row.Add(field.ToString()); rows.Add([.. row]); row.Clear(); field.Clear(); }
                else field.Append(character);
            }
            if (quoted) return [];
            if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add([.. row]); }
            return rows;
        }

        private static string InlineText(ContainerInline? container)
        {
            if (container is null) return string.Empty;
            var output = new StringBuilder();
            for (var item = container.FirstChild; item is not null; item = item.NextSibling)
            {
                if (item is LiteralInline literal) output.Append(literal.Content);
                else if (item is CodeInline code) output.Append(code.Content);
                else if (item is LineBreakInline) output.Append(' ');
                else if (item is ContainerInline nested) output.Append(InlineText(nested));
            }
            return output.ToString();
        }

        [GeneratedRegex(@"^local://(?<id>[0-9a-fA-F-]{36})(?:\.[A-Za-z0-9]+)?$", RegexOptions.IgnoreCase)]
        private static partial Regex LocalAttachmentRegex();
    }
}
