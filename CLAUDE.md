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

1. **[FlashHttpServer.cs](src/FlashHttp/Server/FlashHttpServer.cs)** — `StartAsync` runs a `TcpListener.AcceptTcpClientAsync` loop (the `MaxConcurrentConnections` semaphore is acquired *before* accept so excess clients wait in the kernel backlog). Each accepted client is dispatched to `HandleNewClientConnectionAsync` (fire-and-forget Task) with `NoDelay` set. If `FlashHttpServerOptions.Certificate` is set, the raw `NetworkStream` is wrapped in `SslStream` with ALPN `Http11` and `SslProtocols.None` (let the OS pick). Three `ObjectPool<T>` instances (request / response / context) are owned here and passed into the connection. Shutdown has two tokens: cancelling the token passed to `StartAsync` is a hard stop (aborts connections, `StartAsync` throws `OperationCanceledException`); `StopAsync(token)` is graceful (stops accepting, closes idle keep-alive connections, lets in-flight requests finish, aborts the rest when `token` fires; `StartAsync` then returns normally). Peer resets and TLS probe failures are logged at Debug/Information, not Error.

2. **[FlashHttpConnection.cs](src/FlashHttp/Server/FlashHttpConnection.cs)** — per-connection processing built on `System.IO.Pipelines`. `FillPipeAsync` copies bytes from the stream into a `Pipe`; `ReadPipeAsync` consumes them, calls the parser, dispatches to the app delegate, then writes the response via a `PipeWriter` wrapping the stream. The parser buffers a **whole** request (headers + Content-Length body) before consuming anything, so `CreatePipeOptions` sizes `pauseWriterThreshold` from `MaxRequestBodySize + MaxRequestHeadersTotalSize`; never lower it independently or large bodies deadlock the pipe. Server-owned response headers (`Date`, `Content-Length`/`Transfer-Encoding`, `Connection`) are always written by the server; handler-supplied duplicates are skipped, and handler headers containing CR/LF/NUL are rejected (500). HEAD, 1xx, 204 and 304 responses never carry a body. Non-seekable `BodyStream`s are sent chunked. Unhandled handler/middleware exceptions produce `500` + `Connection: close` (nothing is written before the handler returns, so this is always safe). `Expect: 100-continue` is answered when the header section is complete and the body is still pending.

3. **[FlashHttpParser.cs](src/FlashHttp/Server/FlashHttpParser.cs)** — `TryReadHttpRequest` parses one HTTP/1.1 request from a `ReadOnlySequence<byte>`. Returns `TryReadHttpRequestResults`: `Incomplete` (headers still arriving), `IncompleteBody` (headers done, body pending), `Success`, or a typed failure mapped by the connection to a status: `RequestLineTooLong` → 414, `HeaderLineTooLong` / `HeadersTooLarge` / `TooManyHeaders` → 431, `RequestBodyTooLarge` → 413, `UnsupportedHttpVersion` → 505, `UnsupportedMethod` / `UnsupportedTransferEncoding` → 501, `InvalidRequest` → 400. All failures close the socket. Over-long lines are rejected *before* their LF arrives so a client cannot force unbounded buffering. The parser requires exactly one `Host` header, rejects `Transfer-Encoding` (no chunked request decoding yet; this also blocks TE.CL smuggling), rejects whitespace before the header colon and obs-fold, accepts absolute-form and `OPTIONS *` targets, and reads `Connection`/`Expect` as token lists. Header values are decoded as Latin-1; the path is **not** percent-decoded (routing matches the raw path). Limits come from `FlashHttpServerOptions.MaxHeaderCount` / `MaxRequestHeadersTotalSize` / `MaxRequestBodySize` and a hard-coded `MaxRequestLineSize = 8192`.

4. **[FlashPipelineBuilder.cs](src/FlashHttp/Server/FlashPipelineBuilder.cs)** — composes a list of `FlashMiddleware` delegates into a single `HandlerSet.FlashRequestAsyncDelegate` by walking from last to first and capturing `next`. The terminal delegate is `HandlerSet.HandleAsync` (route dispatch).

5. **[HandlerSet.cs](src/FlashHttp/Server/HandlerSet.cs)** — one `Dictionary<string, FlashRequestAsyncDelegate>` per HTTP verb, held in an immutable `RouteTable` snapshot. `Register` takes `_lock` and publishes a new snapshot (copy-on-write); dispatch is lock-free. Routing is **exact path string match only** — there is no template / wildcard / parameter binding. HEAD falls back to the GET handler (the connection strips the body). If no handler matches but another verb is registered for the path, the response is 405 with `Allow` (HEAD implied by GET); otherwise 404. The public `OnXxxHandlers` dictionary properties are `[Obsolete]` because they expose a snapshot; always use `Register(method, path, handler)`.

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

- **HTTP/1.1 only.** The parser explicitly rejects anything else (`UnsupportedHttpVersion` → 505); HTTP/1.0 clients such as ApacheBench are deliberately unsupported. Do not introduce H/2 or H/3 abstractions casually. Chunked *request* bodies are not decoded yet (rejected with 501); chunked *responses* are produced for non-seekable `BodyStream`s.
- **No path templates.** Routing is dictionary-keyed exact match. If you need parameters, implement them in middleware or extend `HandlerSet` deliberately (it'll be a larger change than it looks because the lookup is on the hot path).
- **Avoid allocations in the parse/dispatch path.** Prefer `ReadOnlySpan<byte>` / UTF-8 byte literals (`"..."u8`), `SequenceReader<byte>`, `ArrayPool<byte>.Shared`, and `Utf8Formatter`. The existing code uses these patterns heavily — match them.
- **Hardening limits exist for a reason.** `MaxHeaderCount`, `MaxRequestHeadersTotalSize`, `MaxRequestBodySize`, `MaxRequestLineSize`, `ReceiveTimeout` (per read) and `RequestHeadersTimeout` (whole header section, defeats slow-trickle) are DoS guards (see `SecurityTests.cs` and `HttpComplianceTests.cs`). Don't relax them silently; if you add new input that scales with attacker control, add a similar guard, and make sure it fires on *incomplete* data too, not only once a terminator arrives.
- **Thread safety.** `HandlerSet` registrations and dispatch both take `_lock`. The accept loop spawns connection tasks concurrently. Any new shared mutable state needs the same treatment.
- **Test port allocation.** Tests bind with `Port = 0` and discover the actual port via reflection in [TestPortAccessor.cs](tests/FlashHttp.Tests/TestPortAccessor.cs), which reads the private `listener` field. If you rename that field, update `TestPortAccessor` too.
