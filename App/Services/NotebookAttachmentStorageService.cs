using App.Models;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace App.Services
{
    internal sealed class NotebookAttachmentStorageService
    {
        private readonly string _cachePath;

        public NotebookAttachmentStorageService(string cachePath)
        {
            _cachePath = cachePath;
            Directory.CreateDirectory(_cachePath);
        }

        public string GetFullPath(string attachmentId)
        {
            var attachment = App.Settings.Database.Attachments.FindById(attachmentId);
            if (attachment is null) return string.Empty;
            var extension = Path.GetExtension(attachment.Filename);
            var destination = Path.Combine(_cachePath, $"{attachment.Id}{extension}");
            if (!File.Exists(destination)) File.WriteAllBytes(destination, attachment.Value);
            return destination;
        }

        public async Task<string> CopyFromPathAsync(string sourcePath)
        {
            var bytes = await File.ReadAllBytesAsync(sourcePath);
            return Store(bytes, sourcePath, MimeFromExtension(sourcePath));
        }

        public async Task<string> CopyBitmapAsync(RandomAccessStreamReference bitmap)
        {
            await using var source = (await bitmap.OpenReadAsync()).AsStreamForRead();
            using var memory = new MemoryStream();
            await source.CopyToAsync(memory);
            var filename = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
            return Store(memory.ToArray(), filename, "image/png");
        }

        private string Store(byte[] bytes, string filename, string mimeType)
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var existing = App.Settings.Database.Attachments.FindOne(item => item.Hash == hash);
            if (existing is not null) return existing.Id;
            var document = new AttachmentDocument
            {
                Value = bytes,
                Filename = filename,
                Mimetype = mimeType,
                Hash = hash,
                Size = bytes.LongLength,
                CreatedAt = DateTime.UtcNow
            };
            App.Settings.Database.Attachments.Insert(document);
            return document.Id;
        }

        private static string MimeFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif",
            ".webp" => "image/webp", ".bmp" => "image/bmp", ".pdf" => "application/pdf",
            ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".mp4" => "video/mp4",
            ".txt" => "text/plain", _ => "application/octet-stream"
        };

        public static bool IsImagePath(string path) =>
            Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
    }
}
