# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

FlashHttp is an embeddable HTTP/1.1 server library for .NET 10 — explicitly **not** built on ASP.NET Core / Kestrel. It is designed to be dropped into an existing process (worker service, game server, agent, etc.) and prioritizes throughput, low allocations, and explicit control over routing and middleware.

The solution targets **net10.0** across all projects and uses `<Nullable>enable</Nullable>` + `<ImplicitUsings>enable</ImplicitUsings>`.

## Common commands

Solution file is `flash-http.slnx` (the new XML-based SLN format — pass it explicitly to `dotnet` commands).

```powershell
# Build everything
dotnet build flash-http.slnx

# Run the full test suite (xUnit)
dotnet test tests/FlashHttp.Tests/FlashHttp.Tests.csproj

# Run a single test or class (xUnit filter syntax)
dotnet test tests/FlashHttp.Tests/FlashHttp.Tests.csproj --filter "FullyQualifiedName~FlashHttpParserTests.MethodName"

# Run the demo server (listens on :8080 by default — see src/FlashHttpDemo/Program.cs)
dotnet run --project src/FlashHttpDemo/FlashHttpDemo.csproj

# Run BenchmarkDotNet suite (always use Release)
dotnet run -c Release --project benchmark/BenchmarkSuite/BenchmarkSuite.csproj
```

Tests are configured with `<ParallelizeTestCollections>false</ParallelizeTestCollections>` — they bind real TCP sockets (typically on port 0 / loopback) and must not run in parallel. Keep this property when adding test projects.

The demo project (`FlashHttpDemo`) sets `<PublishAot>true</PublishAot>`, `<InvariantGlobalization>true</InvariantGlobalization>`, and `<ServerGarbageCollection>true</ServerGarbageCollection>`. Anything added to `FlashHttp` must remain AOT-compatible (no reflection-emit, no dynamic code generation in hot paths).

## Architecture

### Project layout

- **`src/FlashHttp.Abstractions/`** — zero-dependency public types: `FlashHttpRequest`, `FlashHttpResponse`, `IFlashHandlerContext`/`FlashHandlerContext`, `FlashMiddleware`/`FlashNext` delegates, `HttpMethodsEnum`, `HttpHeader`, `HttpVersions`, `HttpStatusCodes`. Library consumers should reference this assembly for handler signatures.
- **`src/FlashHttp/`** — the server implementation. Depends on `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.ObjectPool`, and `OpenTelemetry.Exporter.OpenTelemetryProtocol`. Exposes `InternalsVisibleTo("BenchmarkSuite")`.
- **`src/FlashHttpDemo/`** — `Microsoft.NET.Sdk.Worker` host showing handler registration and middleware.
- **`tests/FlashHttp.Tests/`** — xUnit tests including a `SecurityTests.cs` suite covering DoS hardening.
- **`benchmark/BenchmarkSuite/`** — BenchmarkDotNet driver for parser micro-benchmarks.

### Request pipeline (the big picture)

The request path spans several files; understanding the flow is essential before changing any of them:

1. **[FlashHttpServer.cs](src/FlashHttp/Server/FlashHttpServer.cs)** — `StartAsync` runs a `TcpListener.AcceptTcpClientAsync` loop. Each accepted client is dispatched to `HandleNewClientConnectionAsync` (fire-and-forget Task). If `FlashHttpServerOptions.Certificate` is set, the raw `NetworkStream` is wrapped in `SslStream` with ALPN `Http11` and `SslProtocols.None` (let the OS pick). Three `ObjectPool<T>` instances (request / response / context) are owned here and passed into the connection.

2. **[FlashHttpConnection.cs](src/FlashHttp/Server/FlashHttpConnection.cs)** — per-connection processing built on `System.IO.Pipelines`. `FillPipeAsync` copies bytes from the stream into a `Pipe`; `ReadPipeAsync` consumes them, calls the parser, dispatches to the app delegate, then writes the response via a `PipeWriter` wrapping the stream. The two tasks share a linked `CancellationTokenSource` so closing the connection on the consumer side cancels the reader. Reserved response headers (`Content-Length`, `Connection`) are always written by the server; handler-supplied duplicates are skipped.

3. **[FlashHttpParser.cs](src/FlashHttp/Server/FlashHttpParser.cs)** — `TryReadHttpRequest` parses one HTTP/1.1 request from a `ReadOnlySequence<byte>`. Returns `TryReadHttpRequestResults` (`Incomplete`, `Success`, or a typed failure — `RequestLineTooLong`, `HeaderLineTooLong`, `TooManyHeaders`, `RequestBodyTooLarge`, `UnsupportedHttpVersion`, `InvalidRequest`). The connection translates failures to 400/413 responses and closes the socket. Limits come from `FlashHttpServerOptions.MaxHeaderCount` / `MaxRequestBodySize` and a hard-coded `MaxRequestLineSize = 8192`.

4. **[FlashPipelineBuilder.cs](src/FlashHttp/Server/FlashPipelineBuilder.cs)** — composes a list of `FlashMiddleware` delegates into a single `HandlerSet.FlashRequestAsyncDelegate` by walking from last to first and capturing `next`. The terminal delegate is `HandlerSet.HandleAsync` (route dispatch).

5. **[HandlerSet.cs](src/FlashHttp/Server/HandlerSet.cs)** — one `Dictionary<string, FlashRequestAsyncDelegate>` per HTTP verb, all guarded by a single `_lock`. Routing is **exact path string match only** — there is no template / wildcard / parameter binding. If no handler matches, the response is set to 404. The public `OnXxxHandlers` dictionary properties are `[Obsolete]` because direct access bypasses the lock; always use `Register(method, path, handler)`.

6. **DI scope** — if the `IServiceProvider` resolves an `IServiceScopeFactory`, a new scope is created per request and assigned to `context.Services`; the scope is disposed in `finally`. If no scope factory exists, the root provider is reused.

### Object pooling and Reset()

Performance is built on `Microsoft.Extensions.ObjectPool`. Three pools live for the lifetime of `FlashHttpServer`:

- `ObjectPool<FlashHttpRequest>` (policy: [FlashHttpRequestPooledObjectPolicy.cs](src/FlashHttp/Server/FlashHttpRequestPooledObjectPolicy.cs))
- `ObjectPool<FlashHttpResponse>` ([FlashHttpResponsePooledObjectPolicy.cs](src/FlashHttp/Server/FlashHttpResponsePooledObjectPolicy.cs))
- `ObjectPool<FlashHandlerContext>` ([FlashHttpContextPooledObjectPolicy.cs](src/FlashHttp/Server/FlashHttpContextPooledObjectPolicy.cs))

Pool size is `FlashHttpServerOptions.RequestPoolMaximumRetained` (default 1024). **Anything added as state on these types must be cleared in the policy's `Return` method (or in `Reset()` on the type itself) — otherwise data leaks across requests.** `FlashHttpRequest.Reset()` already does this for the built-in fields; mirror that pattern for new fields.

### Metrics

[FlashHttpMetrics.cs](src/FlashHttp/Server/FlashHttpMetrics.cs) exposes an OpenTelemetry `Meter` named **`FlashHttp.Server`** with counters/histograms (`flashhttp.server.active_connections`, `flashhttp.server.requests`, `flashhttp.server.request.duration`, etc.). Metrics recording is gated by `FlashHttpServerOptions.MetricsEnabled` (default `true`) — keep recording calls behind that check to avoid allocating `TagList`s on the hot path when disabled.

### Wiring it up

The canonical usage pattern is `FlashHttpServerBuilder.CreateBuilder().UseOptions(...).Build()`, then chain `.WithHandler(method, path, handler)` (or `.WithGetHandler` / `.WithPostHandler` / etc. extensions) and `.Use(middleware)`, finally `await server.StartAsync(token)`. See [src/FlashHttpDemo/Worker.cs](src/FlashHttpDemo/Worker.cs) for the reference example.

The `FlashHttp.Server` extension methods live in two files: [FlashHttpServerExtensions.cs](src/FlashHttp/Server/FlashHttpServerExtensions.cs) (per-verb sugar) and [FlashHttpServerExtensions.Middleware.cs](src/FlashHttp/Server/FlashHttpServerExtensions.Middleware.cs) (per-route pipeline composition).

## Constraints to keep in mind when editing

- **HTTP/1.1 only.** The parser explicitly rejects anything else (`UnsupportedHttpVersion`). Do not introduce H/2 or H/3 abstractions casually.
- **No path templates.** Routing is dictionary-keyed exact match. If you need parameters, implement them in middleware or extend `HandlerSet` deliberately (it'll be a larger change than it looks because the lookup is on the hot path).
- **Avoid allocations in the parse/dispatch path.** Prefer `ReadOnlySpan<byte>` / UTF-8 byte literals (`"..."u8`), `SequenceReader<byte>`, `ArrayPool<byte>.Shared`, and `Utf8Formatter`. The existing code uses these patterns heavily — match them.
- **Hardening limits exist for a reason.** `MaxHeaderCount`, `MaxRequestBodySize`, and `MaxRequestLineSize` are DoS guards (see `SecurityTests.cs`). Don't relax them silently; if you add new input that scales with attacker control, add a similar guard.
- **Thread safety.** `HandlerSet` registrations and dispatch both take `_lock`. The accept loop spawns connection tasks concurrently. Any new shared mutable state needs the same treatment.
- **Test port allocation.** Tests bind with `Port = 0` and discover the actual port via reflection in [TestPortAccessor.cs](tests/FlashHttp.Tests/TestPortAccessor.cs), which reads the private `listener` field. If you rename that field, update `TestPortAccessor` too.
