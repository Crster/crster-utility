using System;
using System.Collections.Generic;
using App.Models;

namespace App.Services
{
    internal sealed class TechnicianContextDocument
    {
        private const string WorkspaceName = "workspace";
        private const string SessionName = "session";
        private const string SpecialistName = "specialist";

        public TechnicianContextDocument(string text) => Text = text ?? string.Empty;

        public string Text { get; private set; }

        public string? Read(TechnicianContextRegion region)
        {
            var bounds = FindRegion(region);
            return bounds is null ? null : Text[bounds.Value.ContentStart..bounds.Value.ContentEnd].Trim();
        }

        public void Replace(TechnicianContextRegion region, string content)
        {
            var bounds = FindRegion(region);
            var block = BuildBlock(region, content);
            if (bounds is null)
            {
                Text = string.IsNullOrWhiteSpace(Text)
                    ? block
                    : $"{Text.TrimEnd()}{Environment.NewLine}{Environment.NewLine}{block}";
                return;
            }

            Text = $"{Text[..bounds.Value.Start]}{block}{Text[bounds.Value.End..]}";
        }

        public void Clear(TechnicianContextRegion region)
        {
            var bounds = FindRegion(region);
            if (bounds is null) return;

            var before = Text[..bounds.Value.Start].TrimEnd();
            var after = Text[bounds.Value.End..].TrimStart();
            Text = before.Length == 0
                ? after
                : after.Length == 0
                    ? before
                    : $"{before}{Environment.NewLine}{Environment.NewLine}{after}";
        }

        public void ClearGeneratedRegions()
        {
            Clear(TechnicianContextRegion.Specialist);
            Clear(TechnicianContextRegion.Session);
            Clear(TechnicianContextRegion.Workspace);
        }

        public string BuildPromptText(int maximumLength)
        {
            if (Text.Length <= maximumLength) return Text;

            var generated = new List<string>();
            var userText = Text;
            foreach (var region in new[] { TechnicianContextRegion.Workspace, TechnicianContextRegion.Session, TechnicianContextRegion.Specialist })
            {
                var bounds = FindRegion(region);
                if (bounds is null) continue;
                generated.Add(Text[bounds.Value.Start..bounds.Value.End]);
            }
            foreach (var block in generated) userText = userText.Replace(block, string.Empty, StringComparison.Ordinal);

            var prioritized = new List<string>();
            if (!string.IsNullOrWhiteSpace(userText)) prioritized.Add(userText.Trim());
            foreach (var region in new[] { TechnicianContextRegion.Workspace, TechnicianContextRegion.Session, TechnicianContextRegion.Specialist })
            {
                var content = Read(region);
                if (!string.IsNullOrWhiteSpace(content)) prioritized.Add(BuildBlock(region, content));
            }

            var result = string.Empty;
            foreach (var part in prioritized)
            {
                var separator = result.Length == 0 ? string.Empty : $"{Environment.NewLine}{Environment.NewLine}";
                var remaining = maximumLength - result.Length - separator.Length;
                if (remaining <= 0) break;
                result += separator + (part.Length <= remaining ? part : $"{part[..Math.Max(0, remaining - 1)]}…");
            }
            return result;
        }

        private RegionBounds? FindRegion(TechnicianContextRegion region)
        {
            var name = Name(region);
            var startMarker = $"<!-- technician:{name}:start -->";
            var endMarker = $"<!-- technician:{name}:end -->";
            var starts = FindAll(startMarker);
            var ends = FindAll(endMarker);
            if (starts.Count == 0 && ends.Count == 0) return null;
            if (starts.Count != 1 || ends.Count != 1 || ends[0] <= starts[0])
                throw new InvalidOperationException($"The {name} context markers are malformed or duplicated. Fix the Context text before it can be updated automatically.");

            var contentStart = starts[0] + startMarker.Length;
            if (contentStart < Text.Length && Text[contentStart] == '\r') contentStart++;
            if (contentStart < Text.Length && Text[contentStart] == '\n') contentStart++;
            var contentEnd = ends[0];
            while (contentEnd > contentStart && (Text[contentEnd - 1] == '\r' || Text[contentEnd - 1] == '\n')) contentEnd--;
            return new RegionBounds(starts[0], ends[0] + endMarker.Length, contentStart, contentEnd);
        }

        private List<int> FindAll(string marker)
        {
            var positions = new List<int>();
            var offset = 0;
            while (offset < Text.Length)
            {
                var index = Text.IndexOf(marker, offset, StringComparison.Ordinal);
                if (index < 0) break;
                positions.Add(index);
                offset = index + marker.Length;
            }
            return positions;
        }

        private static string BuildBlock(TechnicianContextRegion region, string content)
        {
            var name = Name(region);
            var heading = region switch
            {
                TechnicianContextRegion.Workspace => "Workspace",
                TechnicianContextRegion.Session => "Previous session",
                _ => "Current-session guidance"
            };
            return $"<!-- technician:{name}:start -->{Environment.NewLine}## {heading}{Environment.NewLine}{content.Trim()}{Environment.NewLine}<!-- technician:{name}:end -->";
        }

        private static string Name(TechnicianContextRegion region) => region switch
        {
            TechnicianContextRegion.Workspace => WorkspaceName,
            TechnicianContextRegion.Session => SessionName,
            _ => SpecialistName
        };

        private readonly record struct RegionBounds(int Start, int End, int ContentStart, int ContentEnd);
    }
}
