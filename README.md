# FlashHttp

**No ASP.NET** stuffs!

A tiny, embeddable HTTP/1.1 server for .NET focused on:
- High performance – very low overhead, optimized parsing and writing
- Low memory usage – minimal allocations, small object model
- Safety & control – you own the process, the handlers, and the integration

FlashHttp is designed to be dropped into your existing app (console, service, desktop, game server, etc.) to expose a fast HTTP API without pulling in the full ASP.NET Core stack.

---

Why FlashHttp?

There are already great web servers in .NET (Kestrel, ASP.NET Core). FlashHttp is for when you need something different:
- You want an embedded server inside an existing process, not a full web framework.
- You care about raw throughput and low GC pressure.
- You only need HTTP/1.1 and a small, explicit feature set.
- You want full control over routing, auth, and middleware without magic.

Typical use cases:
- Custom game servers needing a tiny HTTP status/metrics endpoint.
- Embedded server inside worker/agent/service.
- High-performance gateways or specialized microservices.

---

Features
- HTTP/1.1 support with keep-alive
- Async I/O based on System.IO.Pipelines
- Designed for very high request throughput
- Focus on low allocations
- Supports GET, POST, PUT, DELETE, HEAD, PATCH, OPTIONS
- Optional TLS/HTTPS via SslStream and server certificate
- Simple routing by HTTP method + path
- Extensible middleware-style pipeline for things like:
- Basic authentication
- JWT authentication
- Logging / metrics / custom filters

---

Protocol behaviour

FlashHttp speaks HTTP/1.1 only. The following are deliberate and worth knowing before you put it behind real clients:

- Requests must be `HTTP/1.1` with exactly one `Host` header; HTTP/1.0 gets `505`, a missing or duplicated `Host` gets `400`. ApacheBench (`ab`) speaks HTTP/1.0 and will not work; use `wrk`, `bombardier`, `oha` or `h2load --h1`.
- Request bodies are framed by `Content-Length` only and are fully buffered before your handler runs (bounded by `MaxRequestBodySize`). `Transfer-Encoding: chunked` requests are refused with `501`.
- `Expect: 100-continue` is honoured.
- `HEAD` is served by the `GET` handler with the body stripped; `1xx`, `204` and `304` responses never carry a body.
- A `BodyStream` that cannot seek is sent with `Transfer-Encoding: chunked`, so keep-alive still works for streamed responses.
- Unhandled exceptions in handlers or middleware produce `500 Internal Server Error` and close the connection.
- Every response carries a `Date` header. `Content-Length`, `Transfer-Encoding`, `Connection` and `Date` are owned by the server; set them on the response and they are ignored.
- The request path is matched exactly as sent (no percent-decoding, no trailing-slash normalisation). Absolute-form targets (`GET http://host/path`) are reduced to their path.
- Limits and timeouts (`MaxHeaderCount`, `MaxRequestHeadersTotalSize`, `MaxRequestBodySize`, `ReceiveTimeout`, `RequestHeadersTimeout`, `TlsHandshakeTimeout`, `MaxConcurrentConnections`) live on `FlashHttpServerOptions`. Oversized or slow requests get `414`/`431`/`413`/`408` and the connection is closed.
- `StopAsync` stops accepting, closes idle keep-alive connections and lets in-flight requests finish; cancelling the token passed to `StartAsync` aborts everything immediately.
