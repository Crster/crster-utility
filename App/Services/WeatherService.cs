using App.Models;
using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace App.Services
{
    internal sealed class WeatherService : IDisposable
    {
        private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

        public async Task<ToolResult> GetWeatherAsync(
            string location,
            DateOnly? startDate,
            DateOnly? endDate,
            bool includeHourly,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(location))
                return Error("location_required", "Tell Secretary which city to use for weather.");

            var geocodingUri =
                $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(location.Trim())}&count=5&language=en&format=json";
            using var geocodingResponse = await _httpClient.GetAsync(geocodingUri, token);
            var geocodingRoot = await ReadJsonAsync(geocodingResponse, token);
            var matches = geocodingRoot["results"]?.AsArray();
            if (matches is null || matches.Count == 0)
                return Error("location_not_found", $"No weather location matched “{location.Trim()}”.");

            var best = matches[0]?.AsObject() ?? throw new InvalidOperationException("The weather service returned an invalid location.");
            var latitude = best["latitude"]?.GetValue<double>() ?? throw new InvalidOperationException("The weather location had no latitude.");
            var longitude = best["longitude"]?.GetValue<double>() ?? throw new InvalidOperationException("The weather location had no longitude.");
            var resolvedName = string.Join(", ", new[]
            {
                best["name"]?.GetValue<string>(),
                best["admin1"]?.GetValue<string>(),
                best["country"]?.GetValue<string>()
            }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var start = startDate ?? today;
            var end = endDate ?? start.AddDays(6);
            if (end < start) return Error("invalid_date_range", "The weather end date must not be before the start date.");
            if (end.DayNumber - start.DayNumber > 15)
                return Error("date_range_too_large", "Weather forecasts are limited to 16 days.");

            var hourly = includeHourly
                ? "&hourly=temperature_2m,apparent_temperature,precipitation_probability,weather_code,wind_speed_10m"
                : string.Empty;
            var forecastUri =
                "https://api.open-meteo.com/v1/forecast"
                + $"?latitude={latitude.ToString(CultureInfo.InvariantCulture)}"
                + $"&longitude={longitude.ToString(CultureInfo.InvariantCulture)}"
                + "&current=temperature_2m,apparent_temperature,precipitation,weather_code,wind_speed_10m"
                + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset"
                + hourly
                + $"&start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}"
                + "&temperature_unit=celsius&wind_speed_unit=kmh&precipitation_unit=mm&timezone=auto";
            using var forecastResponse = await _httpClient.GetAsync(forecastUri, token);
            var forecast = await ReadJsonAsync(forecastResponse, token);
            var output = new JsonObject
            {
                ["status"] = "completed",
                ["summary"] = $"Loaded weather for {resolvedName}.",
                ["location"] = new JsonObject
                {
                    ["requested"] = location.Trim(),
                    ["resolved"] = resolvedName,
                    ["latitude"] = latitude,
                    ["longitude"] = longitude,
                    ["timezone"] = forecast["timezone"]?.DeepClone()
                },
                ["current"] = forecast["current"]?.DeepClone(),
                ["current_units"] = forecast["current_units"]?.DeepClone(),
                ["daily"] = forecast["daily"]?.DeepClone(),
                ["daily_units"] = forecast["daily_units"]?.DeepClone()
            };
            if (includeHourly)
            {
                output["hourly"] = forecast["hourly"]?.DeepClone();
                output["hourly_units"] = forecast["hourly_units"]?.DeepClone();
            }
            return new ToolResult(true, output.ToJsonString());
        }

        private static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
        {
            var content = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"The weather service failed ({(int)response.StatusCode}).");
            return JsonNode.Parse(content)?.AsObject() ?? throw new InvalidOperationException("The weather service returned invalid data.");
        }

        private static ToolResult Error(string category, string summary) =>
            new(false, new JsonObject
            {
                ["status"] = "failed",
                ["error_category"] = category,
                ["summary"] = summary
            }.ToJsonString());

        public void Dispose() => _httpClient.Dispose();
    }
}
