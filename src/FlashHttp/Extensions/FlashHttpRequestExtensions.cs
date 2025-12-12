using FlashHttp.Abstractions;
using System;
using System.Collections.Generic;
using System.Text;

namespace FlashHttp.Extensions;

public static class FlashHttpRequestExtensions
{
    public static string GetHeaderValue(this FlashHttpRequest request, string headerName)
    {
        foreach (var header in request.Headers)
        {
            if (string.Equals(header.Name, headerName, StringComparison.OrdinalIgnoreCase))
            {
                return header.Value;
            }
        }
        return string.Empty;
    }

    public static string GetBodyAsString(this FlashHttpRequest request, Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
        return encoding.GetString(request.Body);
    }

    public static T? GetBodyAsJson<T>(this FlashHttpRequest request, Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
        var jsonString = encoding.GetString(request.Body);
        return System.Text.Json.JsonSerializer.Deserialize<T>(jsonString);
    }

    public static string[] GetPathParts(this FlashHttpRequest request)
    {
        var path = request.Path;
        if (string.IsNullOrEmpty(path))
        {
            return [];
        }

        var span = path.AsSpan();
        int idx = span.IndexOf('?');
        if (idx >= 0)
        {
            span = span[..idx];
        }
        return span.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    public static ReadOnlySpan<char> GetQueryString(this FlashHttpRequest request)
    {
        var path = request.Path;
        if (string.IsNullOrEmpty(path))
        {
            return [];
        }

        ReadOnlySpan<char> span = path.AsSpan();
        int idx = span.IndexOf('?');
        if (idx < 0 || idx >= span.Length - 1)
        {
            return [];
        }

        return span[(idx + 1)..];
    }

    public static Dictionary<string, string> ParseQueryParameters(this FlashHttpRequest request)
    {
        var queryString = request.GetQueryString();
        var queryParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (queryString.IsEmpty)
        {
            return queryParameters;
        }
        var pairs = queryString.ToString().Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var keyValue = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(keyValue[0]);
            var value = keyValue.Length > 1 ? Uri.UnescapeDataString(keyValue[1]) : string.Empty;
            queryParameters[key] = value;
        }
        return queryParameters;
    }
}