using System;
using System.Text;

namespace App.Services
{
    /// <summary>Builds the markdown document that an "Ask Cody" entry point attaches to the chat.
    /// One shape for every entry point, so the model always reads the same layout: a title, labelled
    /// facts, then fenced blocks of raw text. Holds facts only; the request itself goes in the composer.</summary>
    internal sealed class CodyContextDocument
    {
        private const int MinimumFenceLength = 3;
        private readonly StringBuilder _text = new();
        private bool _hasDetails;

        internal CodyContextDocument(string title)
        {
            _text.Append("# ").Append(title.Trim()).Append("\r\n");
        }

        /// <summary>Adds one labelled fact. Skipped when the value is empty.</summary>
        internal CodyContextDocument AddDetail(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return this;
            if (!_hasDetails)
            {
                _text.Append("\r\n");
                _hasDetails = true;
            }

            _text.Append("- ").Append(label).Append(": ").Append(value.Trim()).Append("\r\n");
            return this;
        }

        /// <summary>Adds a fenced block of raw text under its own heading. Skipped when the body is empty.</summary>
        internal CodyContextDocument AddBlock(string heading, string? body, string language = "text")
        {
            if (string.IsNullOrWhiteSpace(body)) return this;
            var content = body.ReplaceLineEndings("\r\n").TrimEnd();
            var fence = new string('`', Math.Max(MinimumFenceLength, LongestBacktickRun(content) + 1));
            _text.Append("\r\n## ").Append(heading).Append("\r\n\r\n")
                .Append(fence).Append(language).Append("\r\n")
                .Append(content).Append("\r\n")
                .Append(fence).Append("\r\n");
            return this;
        }

        public override string ToString() => _text.ToString();

        /// <summary>Length of the longest run of backticks in the body, so the fence can never be closed early.</summary>
        private static int LongestBacktickRun(string content)
        {
            var longest = 0;
            var current = 0;
            foreach (var character in content)
            {
                current = character == '`' ? current + 1 : 0;
                longest = Math.Max(longest, current);
            }

            return longest;
        }
    }
}
