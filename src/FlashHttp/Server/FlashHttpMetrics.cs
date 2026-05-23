using FlashHttp.Abstractions;
using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FlashHttp.Server;

internal static class FlashHttpMetrics
{
    internal const string DefaultMeterName = "FlashHttp.Server";

    private static readonly Meter Meter = new(DefaultMeterName);

    internal static readonly UpDownCounter<long> ActiveConnections =
        Meter.CreateUpDownCounter<long>(
            name: "flashhttp.server.active_connections",
            unit: "{connections}",
            description: "Number of currently active TCP connections.");

    internal static readonly Counter<long> RequestsTotal =
        Meter.CreateCounter<long>(
            name: "flashhttp.server.requests",
            unit: "{requests}",
            description: "Total number of HTTP requests processed.");

    internal static readonly Histogram<double> RequestDurationMs =
        Meter.CreateHistogram<double>(
            name: "flashhttp.server.request.duration",
            unit: "ms",
            description: "Time spent processing an HTTP request (handler + response write)." );

    internal static readonly Counter<long> RequestErrorsTotal =
        Meter.CreateCounter<long>(
            name: "flashhttp.server.request.errors",
            unit: "{errors}",
            description: "Total number of request processing errors.");

    internal static readonly Counter<long> ResponseBytesTotal =
        Meter.CreateCounter<long>(
            name: "flashhttp.server.response.bytes",
            unit: "{By}",
            description: "Total number of response body bytes written.");

    internal static readonly Counter<long> RequestBodyBytesTotal =
        Meter.CreateCounter<long>(
            name: "flashhttp.server.request.body.bytes",
            unit: "{By}",
            description: "Total number of request body bytes received.");

    internal static void RecordRequest(
        HttpMethodsEnum method,
        int statusCode,
        bool isHttps,
        bool keepAlive,
        double durationMs,
        int requestBodyBytes,
        int responseBodyBytes)
    {
        // Metrics are observability, not control flow — a misbehaving listener must
        // never bring down the connection handler.
        try
        {
            TagList tags = new();
            tags.Add("http.method", MethodToTagValue(method));
            tags.Add("http.status_code", statusCode);
            tags.Add("net.transport", "ip_tcp");
            tags.Add("url.scheme", isHttps ? "https" : "http");
            tags.Add("http.keep_alive", keepAlive);

            RequestsTotal.Add(1, tags);
            RequestDurationMs.Record(durationMs, tags);

            if (requestBodyBytes > 0)
            {
                RequestBodyBytesTotal.Add(requestBodyBytes, tags);
            }

            if (responseBodyBytes > 0)
            {
                ResponseBytesTotal.Add(responseBodyBytes, tags);
            }
        }
        catch { }
    }

    internal static void RecordRequestError(HttpMethodsEnum method, bool isHttps)
    {
        try
        {
            TagList tags = new();
            tags.Add("http.method", MethodToTagValue(method));
            tags.Add("url.scheme", isHttps ? "https" : "http");
            RequestErrorsTotal.Add(1, tags);
        }
        catch { }
    }

    // Records a request that failed to parse (400/413). The method is unknown at this
    // point — observability needs the status code and scheme far more than the method.
    internal static void RecordErrorResponse(int statusCode, bool isHttps)
    {
        try
        {
            TagList tags = new();
            tags.Add("http.method", "UNKNOWN");
            tags.Add("http.status_code", statusCode);
            tags.Add("net.transport", "ip_tcp");
            tags.Add("url.scheme", isHttps ? "https" : "http");
            tags.Add("http.keep_alive", false);

            RequestsTotal.Add(1, tags);
        }
        catch { }
    }

    internal static void IncrementActiveConnections()
    {
        try { ActiveConnections.Add(1); } catch { }
    }

    internal static void DecrementActiveConnections()
    {
        try { ActiveConnections.Add(-1); } catch { }
    }

    private static string MethodToTagValue(HttpMethodsEnum method)
        => method switch
        {
            HttpMethodsEnum.Get => "GET",
            HttpMethodsEnum.Post => "POST",
            HttpMethodsEnum.Put => "PUT",
            HttpMethodsEnum.Delete => "DELETE",
            HttpMethodsEnum.Patch => "PATCH",
            HttpMethodsEnum.Head => "HEAD",
            HttpMethodsEnum.Options => "OPTIONS",
            _ => "UNKNOWN"
        };

    internal static long GetTimestamp() => Stopwatch.GetTimestamp();

    internal static double GetElapsedMilliseconds(long startTimestamp)
        => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
}
