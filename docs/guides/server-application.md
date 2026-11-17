# C# Server application

This B15 work-in-progress checkpoint compiles the pinned C# API and composes the
B16 owned body facade. Final product execution and package acceptance require the
actual B17 Rust Host, generated endpoint tests and the batch's independent review.
It does not yet claim an installable accepted Server release.

The application surface owns frozen options/routes, explicit static JSON,
lazy request DI scopes, typed handlers, middleware, filters and authorization.
Native routing, network parsing, HTTP/TLS and execution remain B17 responsibilities.
Body reads select exactly one mode; owned BodyChunk instances require disposal.
A successful HTTP handler, custom result or middleware short circuit automatically
joins response completion before LIFO callbacks, disposables and scope retirement.

Ordinary application code requires no Rust source or per-handler native rebuild.
Use explicit JsonTypeInfo or builder.UseJson metadata. Unknown configuration keys,
duplicate routes, unsupported handler shapes and missing metadata fail before serving.
The independently owned native instance has no default global runtime.
