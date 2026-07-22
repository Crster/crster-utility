using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace App.Services
{
    internal sealed class NotebookAttachmentStorageService
    {
        private readonly string _rootPath;
        private readonly string _attachmentsPath;

        public NotebookAttachmentStorageService(string rootPath)
        {
            _rootPath = rootPath;
            _attachmentsPath = Path.Combine(rootPath, "attachments");
            Directory.CreateDirectory(_attachmentsPath);
        }

        public string GetFullPath(string relativePath) => Path.Combine(_rootPath, relativePath);

        public async Task<string> CopyFromPathAsync(string sourcePath)
        {
            Directory.CreateDirectory(_attachmentsPath);
            var extension = Path.GetExtension(sourcePath);
            var storedFileName = $"{Guid.NewGuid():N}{extension}";
            var destinationPath = Path.Combine(_attachmentsPath, storedFileName);
            await using var source = File.OpenRead(sourcePath);
            await using var destination = File.Create(destinationPath);
            await source.CopyToAsync(destination);
            return Path.Combine("attachments", storedFileName);
        }

        public async Task<string> CopyBitmapAsync(RandomAccessStreamReference bitmap)
        {
            Directory.CreateDirectory(_attachmentsPath);
            var storedFileName = $"{Guid.NewGuid():N}.png";
            var destinationPath = Path.Combine(_attachmentsPath, storedFileName);
            await using var source = (await bitmap.OpenReadAsync()).AsStreamForRead();
            await using var destination = File.Create(destinationPath);
            await source.CopyToAsync(destination);
            return Path.Combine("attachments", storedFileName);
        }

        public static bool IsImagePath(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
    }
}
