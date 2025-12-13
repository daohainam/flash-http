# FlashHttp

A tiny, embeddable HTTP/1.1 server for .NET focused on:
	•	High performance – very low overhead, optimized parsing and writing
	•	Low memory usage – minimal allocations, small object model
	•	Safety & control – you own the process, the handlers, and the integration

FlashHttp is designed to be dropped into your existing app (console, service, desktop, game server, etc.) to expose a fast HTTP API without pulling in the full ASP.NET Core stack.

⸻

Why FlashHttp?

There are already great web servers in .NET (Kestrel, ASP.NET Core). FlashHttp is for when you need something different:
	•	You want an embedded server inside an existing process, not a full web framework.
	•	You care about raw throughput and low GC pressure.
	•	You only need HTTP/1.1 and a small, explicit feature set.
	•	You want full control over routing, auth, and middleware without magic.

Typical use cases:
	•	Custom game servers needing a tiny HTTP status/metrics endpoint.
	•	Embedded server inside worker/agent/service.
	•	High-performance gateways or specialized microservices.

⸻

Features
	• HTTP/1.1 support with keep-alive
	• Async I/O based on System.IO.Pipelines
	• Designed for very high request throughput
	• Focus on low allocations
	• Supports GET, POST, PUT, DELETE, HEAD, PATCH, OPTIONS
	• Optional TLS/HTTPS via SslStream and server certificate
	• Simple routing by HTTP method + path
	• Extensible middleware-style pipeline for things like:
	• Basic authentication
	• JWT authentication
	• Logging / metrics / custom filters
