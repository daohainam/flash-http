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

# Quick benchmark pass while iterating (in-process, few iterations; allocations are exact, times are rough)
dotnet run -c Release --project benchmark/BenchmarkSuite/BenchmarkSuite.csproj -- --filter *Loopback* --inProcess --launchCount 1 --warmupCount 3 --iterationCount 6
```

The benchmark suite has two classes: `HttpParserBenchmarks` (parser micro-benchmarks: small GET, browser-style GET, segmented input, POST with body, pooled request) and `LoopbackBenchmarks` (end-to-end round trips against a real server over a loopback socket: keep-alive GET, pipelined GETs, 1 MB `byte[]`/stream responses, 1 MB upload). Run the loopback class before and after any change to the connection or parser; a change that does not move those numbers is not a performance improvement.

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

1. **[FlashHttpServer.cs](src/FlashHttp/Server/FlashHttpServer.cs)** — `StartAsync` runs a `TcpListener.AcceptTcpClientAsync` loop (the `MaxConcurrentConnections` semaphore is acquired *before* accept so excess clients wait in the kernel backlog). Each accepted client is dispatched to `HandleNewClientConnectionAsync` (fire-and-forget Task) with `NoDelay` set. If `FlashHttpServerOptions.Certificate` is set, the raw `NetworkStream` is wrapped in `SslStream` with ALPN `Http11` and `SslProtocols.None` (let the OS pick); the `SslServerAuthenticationOptions` (with a prebuilt `SslStreamCertificateContext`) are created once in `StartAsync` and shared by all handshakes. Three `ObjectPool<T>` instances (request / response / context) are owned here and passed into the connection. Shutdown has two tokens: cancelling the token passed to `StartAsync` is a hard stop (aborts connections, `StartAsync` throws `OperationCanceledException`); `StopAsync(token)` is graceful (stops accepting, closes idle keep-alive connections, lets in-flight requests finish, aborts the rest when `token` fires; `StartAsync` then returns normally). Peer resets and TLS probe failures are logged at Debug/Information, not Error.

2. **[FlashHttpConnection.cs](src/FlashHttp/Server/FlashHttpConnection.cs)** — per-connection processing built on `System.IO.Pipelines`, with a `PipeReader` and a `PipeWriter` wrapping the stream directly (there is no intermediate `Pipe` and no pump task; the reader pulls from the stream only when `RunRequestLoopAsync` asks for input, which is the back-pressure). The read side:
   - The reader uses zero-byte reads and a 16 KB buffer, so an idle keep-alive connection holds no buffer and a burst of pipelined requests arrives in one read.
   - The head is parsed incrementally (`TryReadRequestHead`); consumed lines are released on every pass, so the reader never holds more than one unterminated line.
   - The body is collected in its own array: the part that arrived with the head is copied out of the reader, the rest is read **straight from the stream into the array** (`ReadBodyFromStreamAsync`), never past `Content-Length`. The array starts at 4 KB (or what has already arrived) and grows ×4 (jumping to the exact declared length once that is within 8× of what has been received), so a client cannot make the server commit a large array by declaring a large `Content-Length` without sending it.
   - One `CancellationTokenSource` per connection covers every read: linked to the abort and graceful-stop tokens, re-armed with `ReceiveTimeout` before each read and reset after it (`ArmReadTimeout` / `DisarmReadTimeout`). Do not go back to a linked source per read.

   The write side:
   - The status line (pre-rendered per status code) and all headers are written through one span (`WriteResponseHead`); a `Body` up to 64 KB rides in the same block. A larger `Body` is written directly from the handler's array in 64 KB blocks (one multi-megabyte socket write measured ~4x slower over loopback). Stream bodies are read into the writer in 64 KB blocks; chunked ones are framed as one contiguous block per chunk.
   - **Responses are not flushed one by one.** The loop flushes when every request already buffered has been answered, when 64 KB is pending, or when a handler does not complete synchronously (so a finished response never waits for a later slow handler). Any new write path must leave the writer in a state where that flush sends everything in order — in particular, flush the writer before writing to the stream directly.
   - When the server closes after an error response (rejected request, header timeout, handler fault) it sets `ClosedAfterError`, and `FlashHttpServer` performs a lingering close (`Shutdown(Send)`, then discard input for up to 2 s). Closing a socket with unread input resets the connection and can destroy the error response.
   - Every library `await` uses `ConfigureAwait(false)`; handlers are invoked without the host's `SynchronizationContext` (`TransportTests`). Server-owned response headers (`Date`, `Content-Length`/`Transfer-Encoding`, `Connection`) are always written by the server; handler-supplied duplicates are skipped, and handler headers containing CR/LF/NUL are rejected (500). HEAD, 1xx, 204 and 304 responses never carry a body. Non-seekable `BodyStream`s are sent chunked. Unhandled handler/middleware exceptions produce `500` + `Connection: close` (nothing is written before the handler returns, so this is always safe). `Expect: 100-continue` is answered when the header section is complete and the body is still pending.

3. **[FlashHttpParser.cs](src/FlashHttp/Server/FlashHttpParser.cs)** — `TryReadRequestHead` is what the connection uses: it parses the request line and headers **incrementally**. Each complete line in the buffer is parsed exactly once and sliced off (so the connection can release it immediately); an unterminated trailing line stays in the buffer; progress lives in a `RequestHead` struct that the caller keeps between reads. It returns `Incomplete` until the blank line ending the head has been consumed, then `Success` with the buffer positioned at the body. The body is not the parser's job. `TryReadHttpRequest` is a whole-request wrapper over it (head + `Content-Length` body in one buffer, buffer untouched unless `Success`, `IncompleteBody` when the body has not fully arrived) kept for tests and benchmarks. Headers are added to the request's own `List<HttpHeader>` in place, well-known header names are returned as shared strings in canonical casing whatever their case on the wire ([CommonHeaders.cs](src/FlashHttp/Server/CommonHeaders.cs)), and a path that matches a registered route reuses the registered string (`PathLookup` → `HandlerSet.FindRegisteredPath`). Lines are sliced, not copied, when the input is one segment. Results are `TryReadHttpRequestResults`: `Incomplete` (headers still arriving), `Success`, or a typed failure mapped by the connection to a status: `RequestLineTooLong` → 414, `HeaderLineTooLong` / `HeadersTooLarge` / `TooManyHeaders` → 431, `RequestBodyTooLarge` → 413, `UnsupportedHttpVersion` → 505, `UnsupportedMethod` / `UnsupportedTransferEncoding` → 501, `InvalidRequest` → 400. All failures close the socket. Over-long lines are rejected *before* their LF arrives so a client cannot force unbounded buffering. The parser requires exactly one `Host` header, rejects `Transfer-Encoding` (no chunked request decoding yet; this also blocks TE.CL smuggling), rejects whitespace before the header colon and obs-fold, accepts absolute-form and `OPTIONS *` targets, and reads `Connection`/`Expect` as token lists. Header values are decoded as Latin-1; the path is **not** percent-decoded (routing matches the raw path). Limits come from `FlashHttpServerOptions.MaxHeaderCount` / `MaxRequestHeadersTotalSize` / `MaxRequestBodySize` and a hard-coded `MaxRequestLineSize = 8192`.

4. **[FlashPipelineBuilder.cs](src/FlashHttp/Server/FlashPipelineBuilder.cs)** — composes a list of `FlashMiddleware` delegates into a single `HandlerSet.FlashRequestAsyncDelegate` by walking from last to first and capturing `next`. The terminal delegate is `HandlerSet.HandleAsync` (route dispatch).

5. **[HandlerSet.cs](src/FlashHttp/Server/HandlerSet.cs)** — one `Dictionary<string, FlashRequestAsyncDelegate>` per HTTP verb, held in an immutable `RouteTable` snapshot. `Register` takes `_lock` and publishes a new snapshot (copy-on-write); dispatch is lock-free. Routing is **exact path string match only** — there is no template / wildcard / parameter binding. HEAD falls back to the GET handler (the connection strips the body). If no handler matches but another verb is registered for the path, the response is 405 with `Allow` (HEAD implied by GET); otherwise 404. The `Allow` value of every registered path is pre-rendered at `Register` time (`RouteTable.AllowByPath`), so a miss costs one lookup; the same dictionary backs `FindRegisteredPath`, which the parser uses to intern request paths. A path added by mutating the obsolete dictionaries directly is not in that table (it dispatches, but other verbs get 404 instead of 405). The public `OnXxxHandlers` dictionary properties are `[Obsolete]` because they expose a snapshot; always use `Register(method, path, handler)`.

6. **DI scope** — if the `IServiceProvider` resolves an `IServiceScopeFactory` (resolved once, in the `FlashHttpServer` constructor), the context is a `ScopedFlashHandlerContext` whose `Services` getter creates the per-request scope **on first access**; a request whose handler never touches `Services` creates no scope. The scope is released after the response has been serialised. If no scope factory exists, the root provider is reused.

### Object pooling and Reset()

Performance is built on `Microsoft.Extensions.ObjectPool`. Three pools live for the lifetime of `FlashHttpServer`:

- `ObjectPool<FlashHttpRequest>` (policy: [FlashHttpRequestPooledObjectPolicy.cs](src/FlashHttp/Server/FlashHttpRequestPooledObjectPolicy.cs))
- `ObjectPool<FlashHttpResponse>` ([FlashHttpResponsePooledObjectPolicy.cs](src/FlashHttp/Server/FlashHttpResponsePooledObjectPolicy.cs))
- `ObjectPool<FlashHandlerContext>` ([FlashHttpContextPooledObjectPolicy.cs](src/FlashHttp/Server/FlashHttpContextPooledObjectPolicy.cs))

Pool size is `FlashHttpServerOptions.RequestPoolMaximumRetained` (default 1024). The pools are used **per connection, not per request**: HTTP/1.1 handles one request at a time on a connection, so a connection takes one request/response/context when it starts, resets them between requests (`FlashHttpRequest.Reset()`, `FlashHttpResponsePooledObjectPolicy.Reset`, `ScopedFlashHandlerContext.ReleaseScope`) and returns them when it closes. The request's header list is created once by the request policy and refilled in place for every request. **Anything added as state on these types must be cleared in that reset path as well as in the policy's `Return` — otherwise data leaks across requests.** `FlashHttpRequest.Reset()` already does this for the built-in fields; mirror that pattern for new fields. Handlers must not keep a reference to the request, response, their header lists or the context after they return.

### Metrics

[FlashHttpMetrics.cs](src/FlashHttp/Server/FlashHttpMetrics.cs) exposes an OpenTelemetry `Meter` named **`FlashHttp.Server`** with counters/histograms (`flashhttp.server.active_connections`, `flashhttp.server.requests`, `flashhttp.server.request.duration`, etc.). Metrics recording is gated by `FlashHttpServerOptions.MetricsEnabled` (default `true`) — keep recording calls behind that check. Inside `FlashHttpMetrics`, each `Record*` method also returns early when no listener is attached (`Instrument.Enabled`) and uses cached boxes for the int/bool tag values, so recording does not allocate with or without a listener; `HotPathTests` asserts that. Keep new tags allocation-free the same way.

### Logging

Request-path log calls go through source-generated `[LoggerMessage]` methods in [FlashHttpLog.cs](src/FlashHttp/Server/FlashHttpLog.cs) (no boxing, no params array, level checked before formatting). The per-request "Received HTTP request" line is `Debug`: `Information` is the default minimum level of a generic host, and a line per request at that level would dominate the cost of a small request. Do not add per-request logging above `Debug`.

### Wiring it up

The canonical usage pattern is `FlashHttpServerBuilder.CreateBuilder().UseOptions(...).Build()`, then chain `.WithHandler(method, path, handler)` (or `.WithGetHandler` / `.WithPostHandler` / etc. extensions) and `.Use(middleware)`, finally `await server.StartAsync(token)`. See [src/FlashHttpDemo/Worker.cs](src/FlashHttpDemo/Worker.cs) for the reference example.

The `FlashHttp.Server` extension methods live in two files: [FlashHttpServerExtensions.cs](src/FlashHttp/Server/FlashHttpServerExtensions.cs) (per-verb sugar) and [FlashHttpServerExtensions.Middleware.cs](src/FlashHttp/Server/FlashHttpServerExtensions.Middleware.cs) (per-route pipeline composition).

## Constraints to keep in mind when editing

- **HTTP/1.1 only.** The parser explicitly rejects anything else (`UnsupportedHttpVersion` → 505); HTTP/1.0 clients such as ApacheBench are deliberately unsupported. Do not introduce H/2 or H/3 abstractions casually. Chunked *request* bodies are not decoded yet (rejected with 501); chunked *responses* are produced for non-seekable `BodyStream`s.
- **No path templates.** Routing is dictionary-keyed exact match. If you need parameters, implement them in middleware or extend `HandlerSet` deliberately (it'll be a larger change than it looks because the lookup is on the hot path).
- **Avoid allocations in the parse/dispatch path.** Prefer `ReadOnlySpan<byte>` / UTF-8 byte literals (`"..."u8`), `SequenceReader<byte>`, `ArrayPool<byte>.Shared`, and `Utf8Formatter`. The existing code uses these patterns heavily — match them.
- **Hardening limits exist for a reason.** `MaxHeaderCount`, `MaxRequestHeadersTotalSize`, `MaxRequestBodySize`, `MaxRequestLineSize`, `ReceiveTimeout` (per read) and `RequestHeadersTimeout` (whole header section, defeats slow-trickle) are DoS guards (see `SecurityTests.cs` and `HttpComplianceTests.cs`). Don't relax them silently; if you add new input that scales with attacker control, add a similar guard, and make sure it fires on *incomplete* data too, not only once a terminator arrives.
- **Thread safety.** `HandlerSet` registrations take `_lock` and publish a new immutable snapshot; dispatch reads the snapshot without locking. The accept loop spawns connection tasks concurrently. Any new shared mutable state needs the same treatment.
- **Test port allocation.** Tests bind with `Port = 0` and discover the actual port via reflection in [TestPortAccessor.cs](tests/FlashHttp.Tests/TestPortAccessor.cs), which reads the private `listener` field. If you rename that field, update `TestPortAccessor` too.
