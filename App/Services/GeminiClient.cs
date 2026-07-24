using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using App.Models;

namespace App.Services
{
    internal sealed class GeminiClient : IDisposable
    {
        private const string ApiRoot = "https://generativelanguage.googleapis.com/v1beta";
        private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(3) };
        private readonly string _apiKey;

        public GeminiClient(string apiKey) => _apiKey = apiKey;

        public async Task<List<GeminiModel>> ListModelsAsync(CancellationToken cancellationToken)
        {
            var models = new List<GeminiModel>();
            string? pageToken = null;
            do
            {
                var suffix = string.IsNullOrEmpty(pageToken) ? string.Empty : $"&pageToken={Uri.EscapeDataString(pageToken)}";
                using var request = CreateRequest(HttpMethod.Get, $"{ApiRoot}/models?pageSize=1000{suffix}");
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var root = await ReadJsonAsync(response, cancellationToken);
                foreach (var node in root["models"]?.AsArray() ?? [])
                {
                    if (node is not JsonObject model) continue;
                    var name = model["name"]?.GetValue<string>() ?? string.Empty;
                    var id = name.StartsWith("models/", StringComparison.Ordinal) ? name[7..] : name;
                    if (!id.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)) continue;
                    var methods = model["supportedGenerationMethods"]?.AsArray().Select(value => value?.GetValue<string>() ?? string.Empty).ToList() ?? [];
                    models.Add(new GeminiModel
                    {
                        Id = id,
                        DisplayName = model["displayName"]?.GetValue<string>() ?? id,
                        Description = model["description"]?.GetValue<string>() ?? string.Empty,
                        SupportsChat = methods.Any(method => method.Contains("generateContent", StringComparison.OrdinalIgnoreCase)),
                        SupportsThinking = model["thinking"]?.GetValue<bool>() ?? id.Contains("2.5", StringComparison.OrdinalIgnoreCase) || id.Contains("3", StringComparison.OrdinalIgnoreCase)
                    });
                }
                pageToken = root["nextPageToken"]?.GetValue<string>();
            } while (!string.IsNullOrEmpty(pageToken));
            return models.OrderByDescending(model => model.SupportsChat).ThenBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public async Task<ChatAttachment> UploadFileAsync(string path, CancellationToken cancellationToken)
        {
            var fileInfo = new FileInfo(path);
            var mimeType = GetMimeType(path);
            using var start = CreateRequest(HttpMethod.Post, $"https://generativelanguage.googleapis.com/upload/v1beta/files");
            start.Headers.Add("X-Goog-Upload-Protocol", "resumable");
            start.Headers.Add("X-Goog-Upload-Command", "start");
            start.Headers.Add("X-Goog-Upload-Header-Content-Length", fileInfo.Length.ToString());
            start.Headers.Add("X-Goog-Upload-Header-Content-Type", mimeType);
            start.Content = JsonContent(new JsonObject { ["file"] = new JsonObject { ["display_name"] = fileInfo.Name } });
            using var startResponse = await _httpClient.SendAsync(start, cancellationToken);
            if (!startResponse.IsSuccessStatusCode) await ThrowApiErrorAsync(startResponse, cancellationToken);
            if (!startResponse.Headers.TryGetValues("X-Goog-Upload-URL", out var urls)) throw new InvalidOperationException("Gemini did not return a file upload URL.");

            await using var stream = File.OpenRead(path);
            using var upload = new HttpRequestMessage(HttpMethod.Post, urls.First());
            upload.Headers.Add("X-Goog-Upload-Offset", "0");
            upload.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
            upload.Content = new StreamContent(stream);
            upload.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
            upload.Content.Headers.ContentLength = fileInfo.Length;
            using var uploadResponse = await _httpClient.SendAsync(upload, cancellationToken);
            var root = await ReadJsonAsync(uploadResponse, cancellationToken);
            var file = root["file"]?.AsObject() ?? throw new InvalidOperationException("Gemini returned an invalid file upload response.");
            return new ChatAttachment(path, fileInfo.Name, file["mimeType"]?.GetValue<string>() ?? mimeType,
                file["name"]?.GetValue<string>(), file["uri"]?.GetValue<string>());
        }

        public async Task DeleteFileAsync(string remoteName, CancellationToken cancellationToken)
        {
            using var request = CreateRequest(HttpMethod.Delete, $"{ApiRoot}/{remoteName}");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
        }

        public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            var body = new JsonObject
            {
                ["model"] = "models/gemini-embedding-2",
                ["content"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = text } } }
            };
            using var request = CreateRequest(HttpMethod.Post, $"{ApiRoot}/models/gemini-embedding-2:embedContent");
            request.Content = JsonContent(body);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var root = await ReadJsonAsync(response, cancellationToken);
            return root["embedding"]?["values"]?.AsArray().Select(value => value?.GetValue<float>() ?? 0f).ToArray() ?? throw new InvalidOperationException("Gemini returned no embedding values.");
        }

        public async Task<GeminiTurnResult> CreateInteractionAsync(
            string model,
            IReadOnlyList<JsonObject> history,
            IReadOnlyList<JsonObject> newSteps,
            JsonArray tools,
            string thinkingLevel,
            string systemInstruction,
            CancellationToken cancellationToken)
        {
            var input = new JsonArray();
            foreach (var step in history) input.Add(step.DeepClone());
            foreach (var step in newSteps) input.Add(step.DeepClone());
            var body = new JsonObject
            {
                ["model"] = model,
                ["store"] = false,
                ["input"] = input,
                ["system_instruction"] = systemInstruction,
                ["tools"] = tools.DeepClone(),
                ["generation_config"] = new JsonObject { ["thinking_level"] = thinkingLevel }
            };
            using var request = CreateRequest(HttpMethod.Post, $"{ApiRoot}/interactions");
            request.Content = JsonContent(body);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var root = await ReadJsonAsync(response, cancellationToken);
            var result = new GeminiTurnResult();
            foreach (var node in root["steps"]?.AsArray() ?? [])
            {
                if (node is not JsonObject step) continue;
                result.Steps.Add((JsonObject)step.DeepClone());
                var type = step["type"]?.GetValue<string>();
                if (type == "function_call")
                {
                    result.FunctionCalls.Add(new GeminiFunctionCall(
                        step["id"]?.GetValue<string>() ?? throw new InvalidOperationException("A Gemini function call did not include an id."),
                        step["name"]?.GetValue<string>() ?? string.Empty,
                        step["arguments"] as JsonObject ?? new JsonObject()));
                }
                else if (type == "model_output")
                {
                    foreach (var content in step["content"]?.AsArray() ?? [])
                        if (content?["type"]?.GetValue<string>() == "text") result.Text += content["text"]?.GetValue<string>();
                }
            }
            return result;
        }

        public async Task<GeminiTurnResult> CreateSimpleInteractionAsync(
            string model,
            IReadOnlyList<JsonObject> history,
            IReadOnlyList<JsonObject> newSteps,
            string systemInstruction,
            JsonArray? tools,
            string? thinkingLevel,
            CancellationToken cancellationToken)
        {
            var input = new JsonArray();
            foreach (var step in history) input.Add(step.DeepClone());
            foreach (var step in newSteps) input.Add(step.DeepClone());
            var body = new JsonObject
            {
                ["model"] = model,
                ["store"] = false,
                ["input"] = input,
                ["system_instruction"] = systemInstruction
            };
            if (tools is not null && tools.Count > 0) body["tools"] = tools.DeepClone();
            if (!string.IsNullOrWhiteSpace(thinkingLevel))
                body["generation_config"] = new JsonObject { ["thinking_level"] = thinkingLevel };
            using var request = CreateRequest(HttpMethod.Post, $"{ApiRoot}/interactions");
            request.Content = JsonContent(body);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var root = await ReadJsonAsync(response, cancellationToken);
            return ParseInteraction(root);
        }

        public async Task<GeminiTurnResult> CreateArtistInteractionAsync(
            IReadOnlyList<JsonObject> history,
            JsonObject userStep,
            string systemInstruction,
            string? thinkingLevel,
            CancellationToken cancellationToken)
        {
            var input = new JsonArray();
            foreach (var step in history) input.Add(step.DeepClone());
            input.Add(userStep.DeepClone());
            var body = new JsonObject
            {
                ["model"] = "gemini-3.1-flash-image",
                ["store"] = false,
                ["input"] = input,
                ["system_instruction"] = systemInstruction,
                ["response_format"] = new JsonObject { ["type"] = "image", ["mime_type"] = "image/png" }
            };
            if (!string.IsNullOrWhiteSpace(thinkingLevel))
                body["generation_config"] = new JsonObject { ["thinking_level"] = thinkingLevel };
            using var request = CreateRequest(HttpMethod.Post, $"{ApiRoot}/interactions");
            request.Content = JsonContent(body);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var root = await ReadJsonAsync(response, cancellationToken);
            return ParseInteraction(root);
        }

        public static JsonObject CreateUserStep(string text, IEnumerable<ChatAttachment> attachments)
        {
            var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } };
            foreach (var attachment in attachments.Where(item => !string.IsNullOrWhiteSpace(item.RemoteUri)))
                content.Add(new JsonObject { ["type"] = GetContentType(attachment.MimeType), ["uri"] = attachment.RemoteUri, ["mime_type"] = attachment.MimeType });
            return new JsonObject { ["type"] = "user_input", ["content"] = content };
        }

        public static JsonObject CreateArtistStep(string text, IEnumerable<ChatAttachment> attachments, IEnumerable<GeneratedImage> priorImages)
        {
            var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } };
            foreach (var image in priorImages)
                content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(image.Data), ["mime_type"] = image.MimeType });
            foreach (var attachment in attachments.Where(item => !string.IsNullOrWhiteSpace(item.RemoteUri)))
                content.Add(new JsonObject { ["type"] = GetContentType(attachment.MimeType), ["uri"] = attachment.RemoteUri, ["mime_type"] = attachment.MimeType });
            return new JsonObject { ["type"] = "user_input", ["content"] = content };
        }

        private static string GetContentType(string mimeType) => mimeType.Split('/')[0] switch
        {
            "image" => "image",
            "audio" => "audio",
            "video" => "video",
            _ => "document"
        };

        public static JsonObject CreateFunctionResult(GeminiFunctionCall call, ToolResult result)
        {
            JsonNode content;
            if (result.Image is null)
            {
                // Gemini 2.x treats a content-block array as a multimodal function response,
                // even when that array contains only text. Use the standard text result shape.
                content = JsonValue.Create(result.Output)!;
            }
            else
            {
                content = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = result.Output },
                    new JsonObject
                    {
                        ["type"] = "image",
                        ["data"] = Convert.ToBase64String(result.Image.Data),
                        ["mime_type"] = result.Image.MimeType
                    }
                };
            }
            return new JsonObject
            {
                ["type"] = "function_result",
                ["name"] = call.Name,
                ["call_id"] = call.Id,
                ["result"] = content
            };
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string uri)
        {
            var request = new HttpRequestMessage(method, uri);
            request.Headers.Add("x-goog-api-key", _apiKey);
            return request;
        }

        private static StringContent JsonContent(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");

        private static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(content, response));
            return JsonNode.Parse(content)?.AsObject() ?? new JsonObject();
        }

        private static GeminiTurnResult ParseInteraction(JsonObject root)
        {
            var result = new GeminiTurnResult { InteractionId = root["id"]?.GetValue<string>() };
            foreach (var node in root["steps"]?.AsArray() ?? [])
            {
                if (node is not JsonObject step) continue;
                result.Steps.Add((JsonObject)step.DeepClone());
                var type = step["type"]?.GetValue<string>();
                if (type == "function_call") result.FunctionCalls.Add(new GeminiFunctionCall(step["id"]?.GetValue<string>() ?? throw new InvalidOperationException("A function call did not include an id."), step["name"]?.GetValue<string>() ?? string.Empty, step["arguments"] as JsonObject ?? new JsonObject()));
                else if (type == "model_output")
                    foreach (var content in step["content"]?.AsArray() ?? []) if (content?["type"]?.GetValue<string>() == "text") result.Text += content["text"]?.GetValue<string>();
            }
            var outputImage = root["output_image"]?.AsObject();
            var data = outputImage?["data"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(data)) result.Image = new GeneratedImage(Convert.FromBase64String(data), outputImage?["mime_type"]?.GetValue<string>() ?? "image/png");
            foreach (var item in root["grounding_metadata"]?["grounding_chunks"]?.AsArray() ?? [])
            {
                var web = item?["web"]?.AsObject();
                var uri = web?["uri"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(uri)) result.Sources.Add(new GroundedSource(web?["title"]?.GetValue<string>() ?? uri, uri));
            }
            return result;
        }

        private static async Task ThrowApiErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ReadError(content, response));
        }

        private static string ReadError(string content, HttpResponseMessage response)
        {
            try { return JsonNode.Parse(content)?["error"]?["message"]?.GetValue<string>() ?? $"Gemini request failed ({(int)response.StatusCode})."; }
            catch (JsonException) { return $"Gemini request failed ({(int)response.StatusCode}): {content}"; }
        }

        private static string GetMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp",
            ".pdf" => "application/pdf", ".json" => "application/json", ".csv" => "text/csv", ".html" => "text/html",
            ".md" => "text/markdown", ".txt" or ".cs" or ".xaml" or ".js" or ".ts" or ".py" => "text/plain",
            _ => "application/octet-stream"
        };

        public void Dispose() => _httpClient.Dispose();
    }
}
