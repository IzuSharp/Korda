using System.Text;
using System.Text.Json.Serialization.Metadata;

namespace NativeWeave.Server;

/// <summary>Registers typed endpoints before the first Start; no member opens a listener or translates a request path.</summary>
public abstract class ServerRoutes
{
    internal RouteRegistry Registry { get; }
    internal GroupPolicy Group { get; }
    internal ServerRoutes(RouteRegistry registry, GroupPolicy group) { Registry = registry; Group = group; }

    /// <summary>Maps a synchronous GET result.</summary>
    public ServerEndpointBuilder MapGet(string pattern, Func<ServerResult> handler)
    { ArgumentNullException.ThrowIfNull(handler); return MapGet(pattern, _ => handler()); }
    /// <summary>Maps a synchronous GET result with explicit request access.</summary>
    public ServerEndpointBuilder MapGet(string pattern, Func<RequestContext, ServerResult> handler)
    { ArgumentNullException.ThrowIfNull(handler); return MapGet(pattern, (context, _) => Task.FromResult(handler(context))); }
    /// <summary>Maps an asynchronous GET result.</summary>
    public ServerEndpointBuilder MapGet(string pattern, ResponseHandler handler) => MapMethods(pattern, ["GET"], handler);
    /// <summary>Maps a synchronous POST result.</summary>
    public ServerEndpointBuilder MapPost(string pattern, Func<RequestContext, ServerResult> handler)
    { ArgumentNullException.ThrowIfNull(handler); return MapPost(pattern, (context, _) => Task.FromResult(handler(context))); }
    /// <summary>Maps an asynchronous POST result.</summary>
    public ServerEndpointBuilder MapPost(string pattern, ResponseHandler handler) => MapMethods(pattern, ["POST"], handler);
    /// <summary>Maps a statically serialized JSON POST without dynamic delegate binding.</summary>
    public ServerEndpointBuilder MapPost<TRequest, TResponse>(string pattern, Func<TRequest, CancellationToken, Task<TResponse>> handler, JsonTypeInfo<TRequest>? requestTypeInfo = null, JsonTypeInfo<TResponse>? responseTypeInfo = null)
    { ArgumentNullException.ThrowIfNull(handler); return MapPost<TRequest, TResponse>(pattern, (value, _, token) => handler(value, token), requestTypeInfo, responseTypeInfo); }
    /// <summary>Maps a JSON POST with request context and static metadata.</summary>
    public ServerEndpointBuilder MapPost<TRequest, TResponse>(string pattern, JsonRequestHandler<TRequest, TResponse> handler, JsonTypeInfo<TRequest>? requestTypeInfo = null, JsonTypeInfo<TResponse>? responseTypeInfo = null)
        => MapJson(pattern, ["POST"], handler, requestTypeInfo, responseTypeInfo);
    /// <summary>Maps a JSON POST returning an explicit HTTP result.</summary>
    public ServerEndpointBuilder MapPost<TRequest>(string pattern, JsonResultHandler<TRequest> handler, JsonTypeInfo<TRequest>? requestTypeInfo = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        JsonTypeInfo<TRequest>? input = null;
        var route = Add(pattern, ["POST"], async (context, token) =>
        {
            var value = await context.Request.ReadRequiredJsonAsync(input!, token).ConfigureAwait(false);
            return await handler(value, context, token).ConfigureAwait(false);
        }, null);
        route.Definition.ValidateActions.Add(config => input = config.Resolve(requestTypeInfo));
        return route;
    }
    /// <summary>Maps an asynchronous PUT result.</summary>
    public ServerEndpointBuilder MapPut(string pattern, ResponseHandler handler) => MapMethods(pattern, ["PUT"], handler);
    /// <summary>Maps an asynchronous PATCH result.</summary>
    public ServerEndpointBuilder MapPatch(string pattern, ResponseHandler handler) => MapMethods(pattern, ["PATCH"], handler);
    /// <summary>Maps an asynchronous DELETE result.</summary>
    public ServerEndpointBuilder MapDelete(string pattern, ResponseHandler handler) => MapMethods(pattern, ["DELETE"], handler);
    /// <summary>Maps explicit validated HTTP methods; native routing owns matching and dispatch.</summary>
    public ServerEndpointBuilder MapMethods(string pattern, IReadOnlyList<string> methods, ResponseHandler handler)
    { ArgumentNullException.ThrowIfNull(handler); return Add(pattern, methods, handler, null); }
    /// <summary>Maps typed JSON for explicit HTTP methods with metadata validated before native startup.</summary>
    public ServerEndpointBuilder MapJson<TRequest, TResponse>(string pattern, IReadOnlyList<string> methods, JsonRequestHandler<TRequest, TResponse> handler, JsonTypeInfo<TRequest>? requestTypeInfo = null, JsonTypeInfo<TResponse>? responseTypeInfo = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        JsonTypeInfo<TRequest>? input = null; JsonTypeInfo<TResponse>? output = null;
        var route = Add(pattern, methods, async (context, token) =>
        {
            var value = await context.Request.ReadRequiredJsonAsync(input!, token).ConfigureAwait(false);
            var result = await handler(value, context, token).ConfigureAwait(false);
            return Results.Json(result, output!);
        }, null);
        route.Definition.ValidateActions.Add(config => { input = config.Resolve(requestTypeInfo); output = config.Resolve(responseTypeInfo); });
        return route;
    }
    /// <summary>Maps direct writes; successful dispatch automatically joins unfinished response completion.</summary>
    public ServerEndpointBuilder MapRaw(string pattern, IReadOnlyList<string> methods, RequestHandler handler)
    { ArgumentNullException.ThrowIfNull(handler); return Add(pattern, methods, null, handler); }
    /// <summary>Creates a prefix group with policies resolved when the registry freezes.</summary>
    public RouteGroupBuilder MapGroup(string prefix)
    { Registry.Mutable(); RouteRegistry.ValidatePattern(prefix); return new(Registry, new(Group, Group.Prefix + prefix)); }
    private ServerEndpointBuilder Add(string pattern, IReadOnlyList<string> methods, ResponseHandler? handler, RequestHandler? raw)
        => new(Registry, Registry.Add(Group, pattern, methods, handler, raw));
}

/// <summary>Configures metadata of one endpoint before route freeze; not thread safe.</summary>
public sealed class ServerEndpointBuilder
{
    private readonly RouteRegistry _registry;
    internal RouteDefinition Definition { get; }
    internal ServerEndpointBuilder(RouteRegistry registry, RouteDefinition definition) { _registry = registry; Definition = definition; }
    /// <summary>Assigns a unique bounded diagnostic operation name.</summary>
    public ServerEndpointBuilder WithName(string name) { _registry.Mutable(); ServerValidation.Text(name, 128, nameof(name)); Definition.Name = name; return this; }
    /// <summary>Assigns bounded documentation tags.</summary>
    public ServerEndpointBuilder WithTags(params string[] tags) { _registry.Mutable(); RouteRegistry.AddTags(Definition.Tags, tags); return this; }
    /// <summary>Assigns bounded documentation summary text.</summary>
    public ServerEndpointBuilder WithSummary(string summary) { _registry.Mutable(); ServerValidation.Text(summary, 4096, nameof(summary)); Definition.Summary = summary; return this; }
    /// <summary>Overrides the finite generic whole-request timeout.</summary>
    public ServerEndpointBuilder WithTimeout(TimeSpan timeout) { _registry.Mutable(); ServerValidation.Duration(timeout, false, nameof(timeout)); Definition.Timeout = timeout; return this; }
    /// <summary>Overrides the HTTP wire-body cap; protocol routes reject this option at freeze.</summary>
    public ServerEndpointBuilder WithMaxRequestBodyBytes(long bytes) { _registry.Mutable(); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes); Definition.MaxRequestBytes = bytes; return this; }
    /// <summary>Overrides the HTTP JSON wire cap within the existing retained-memory budget.</summary>
    public ServerEndpointBuilder WithMaxJsonBytes(int bytes) { _registry.Mutable(); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes); Definition.MaxJsonBytes = bytes; return this; }
    /// <summary>Attaches a named authorization policy.</summary>
    public ServerEndpointBuilder RequireAuthorization(string policy) { _registry.Mutable(); RouteRegistry.AddPolicy(Definition.Policies, policy); return this; }
    /// <summary>Removes inherited and endpoint authorization requirements.</summary>
    public ServerEndpointBuilder AllowAnonymous() { _registry.Mutable(); Definition.Anonymous = true; return this; }
    /// <summary>Adds an HTTP result filter; protocol routes reject all filters before native startup.</summary>
    public ServerEndpointBuilder AddFilter(ServerFilter filter) { _registry.Mutable(); ArgumentNullException.ThrowIfNull(filter); Definition.Filters.Add(filter); return this; }
    /// <summary>Declares a statically serializable documented response shape.</summary>
    public ServerEndpointBuilder Produces<T>(int statusCode = 200, JsonTypeInfo<T>? typeInfo = null, string contentType = "application/json")
    {
        _registry.Mutable(); ServerValidation.Status(statusCode); HeaderCollection.ValidateValue(contentType);
        Definition.ValidateActions.Add(config => _ = config.Resolve(typeInfo)); return this;
    }
}

/// <summary>Groups routes with inherited prefixes, authorization and filters resolved at freeze.</summary>
public sealed class RouteGroupBuilder : ServerRoutes
{
    internal RouteGroupBuilder(RouteRegistry registry, GroupPolicy group) : base(registry, group) { }
    /// <summary>Adds an inherited named authorization requirement.</summary>
    public RouteGroupBuilder RequireAuthorization(string policy) { Registry.Mutable(); RouteRegistry.AddPolicy(Group.Policies, policy); return this; }
    /// <summary>Adds bounded inherited documentation tags.</summary>
    public RouteGroupBuilder WithTags(params string[] tags) { Registry.Mutable(); RouteRegistry.AddTags(Group.Tags, tags); return this; }
    /// <summary>Adds an inherited result filter; protocol descendants reject filters at freeze.</summary>
    public RouteGroupBuilder AddFilter(ServerFilter filter) { Registry.Mutable(); ArgumentNullException.ThrowIfNull(filter); Group.Filters.Add(filter); return this; }
}

internal sealed class GroupPolicy(GroupPolicy? parent, string prefix)
{
    internal GroupPolicy? Parent { get; } = parent;
    internal string Prefix { get; } = prefix;
    internal List<string> Policies { get; } = [];
    internal List<string> Tags { get; } = [];
    internal List<ServerFilter> Filters { get; } = [];
    internal IEnumerable<GroupPolicy> Ancestors() => Parent is null ? [this] : Parent.Ancestors().Append(this);
}

internal sealed class RouteDefinition(long id, GroupPolicy group, string pattern, string[] methods, ResponseHandler? handler, RequestHandler? raw)
{
    internal long Id { get; } = id;
    internal int Kind { get; set; }
    internal GroupPolicy Group { get; } = group;
    internal string Pattern { get; } = pattern;
    internal string[] Methods { get; } = methods;
    internal ResponseHandler? Handler { get; } = handler;
    internal RequestHandler? Raw { get; } = raw;
    internal string? Name { get; set; }
    internal string? Summary { get; set; }
    internal bool Anonymous { get; set; }
    internal TimeSpan? Timeout { get; set; }
    internal long? MaxRequestBytes { get; set; }
    internal int? MaxJsonBytes { get; set; }
    internal List<string> Policies { get; } = [];
    internal List<string> Tags { get; } = [];
    internal List<ServerFilter> Filters { get; } = [];
    internal List<Action<ServerConfiguration>> ValidateActions { get; } = [];
    internal TimeSpan EffectiveTimeout { get; private set; }
    internal long EffectiveMaxRequestBytes { get; private set; }
    internal int EffectiveMaxJsonBytes { get; private set; }
    internal string[] EffectivePolicies { get; private set; } = [];
    internal ServerFilter[] EffectiveFilters { get; private set; } = [];
    internal object? ProtocolDescriptor { get; set; }
    internal void Validate(ServerConfiguration configuration)
    {
        EffectivePolicies = Anonymous ? [] : Group.Ancestors().SelectMany(x => x.Policies).Concat(Policies).Distinct(StringComparer.Ordinal).ToArray();
        EffectiveFilters = Group.Ancestors().SelectMany(x => x.Filters).Concat(Filters).ToArray();
        if (EffectivePolicies.Length > 32 || (EffectivePolicies.Length != 0 && configuration.Authorizer is null)) throw new InvalidOperationException("Named policies require an authorizer and a maximum of 32 policies.");
        if (Group.Ancestors().SelectMany(x => x.Tags).Concat(Tags).Distinct(StringComparer.Ordinal).Count() > 32) throw new InvalidOperationException("Too many route tags.");
        if (Kind != 0 && (EffectiveFilters.Length != 0 || MaxRequestBytes.HasValue || MaxJsonBytes.HasValue)) throw new InvalidOperationException("Protocol routes do not accept HTTP filters or body/JSON overrides.");
        EffectiveTimeout = Timeout ?? configuration.Options.RequestTimeout;
        EffectiveMaxRequestBytes = MaxRequestBytes ?? configuration.Options.MaxRequestBodyBytes;
        EffectiveMaxJsonBytes = Kind == 0 ? MaxJsonBytes ?? configuration.Options.MaxJsonBytes : 0;
        if (EffectiveMaxJsonBytes > EffectiveMaxRequestBytes || checked(3L * EffectiveMaxJsonBytes) > Math.Min(configuration.Options.Budget.ManagedPayloadBytes, configuration.Options.Budget.RetainedBytesPerRequest)) throw new InvalidOperationException("The route JSON budget exceeds the body or materialization peak cap.");
        foreach (var action in ValidateActions) action(configuration);
    }
}

internal sealed class RouteRegistry
{
    private bool _frozen;
    private readonly List<RouteDefinition> _routes = [];
    internal IReadOnlyList<RouteDefinition> Routes => _routes;
    internal void Mutable() { if (_frozen) throw new InvalidOperationException("The route registry is frozen."); }
    internal RouteDefinition Add(GroupPolicy group, string pattern, IReadOnlyList<string> methods, ResponseHandler? handler, RequestHandler? raw)
    {
        Mutable(); ValidatePattern(pattern); ArgumentNullException.ThrowIfNull(methods);
        if (methods.Count is < 1 or > 32 || _routes.Count >= 16384) throw new ArgumentOutOfRangeException(nameof(methods));
        var copy = methods.ToArray();
        foreach (var method in copy) HeaderCollection.ValidateToken(method);
        if (copy.Distinct(StringComparer.Ordinal).Count() != copy.Length) throw new ArgumentException("Duplicate method.", nameof(methods));
        string complete = group.Prefix + pattern; ValidatePattern(complete);
        // Configuration validates duplicate shapes; native Axum remains the matching authority.
        string shape = Shape(complete);
        if (_routes.Any(r => Shape(r.Pattern) == shape && r.Methods.Intersect(copy, StringComparer.Ordinal).Any())) throw new InvalidOperationException("Duplicate or ambiguous route registration.");
        var route = new RouteDefinition(_routes.Count + 1L, group, complete, copy, handler, raw); _routes.Add(route); return route;
    }
    internal void Freeze(ServerConfiguration configuration)
    {
        Mutable(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in _routes)
        { if (route.Name is { } name && !names.Add(name)) throw new InvalidOperationException("Duplicate endpoint name."); route.Validate(configuration); }
        _frozen = true;
    }
    internal static void ValidatePattern(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        if (!pattern.StartsWith('/') || pattern.Contains('?') || pattern.Contains('#') || Encoding.UTF8.GetByteCount(pattern) > 4096) throw new ArgumentException("Invalid route template.", nameof(pattern));
        var names = new HashSet<string>(StringComparer.Ordinal); var parts = pattern.Split('/');
        for (int i = 1; i < parts.Length; i++)
        {
            string part = parts[i];
            if (!part.Contains('{') && !part.Contains('}')) continue;
            if (!part.StartsWith('{') || !part.EndsWith('}')) throw new ArgumentException("A parameter must occupy its path segment.", nameof(pattern));
            var name = part[1..^1];
            if (name.StartsWith('*')) { if (i != parts.Length - 1) throw new ArgumentException("Catch-all must be last.", nameof(pattern)); name = name[1..]; }
            if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_') || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') || !names.Add(name)) throw new ArgumentException("Invalid or duplicate route parameter.", nameof(pattern));
        }
    }
    private static string Shape(string pattern) => string.Join('/', pattern.Split('/').Select(p => p.StartsWith("{*", StringComparison.Ordinal) ? "{*}" : p.StartsWith('{') ? "{}" : p));
    internal static void AddTags(List<string> target, string[] tags)
    { ArgumentNullException.ThrowIfNull(tags); foreach (var tag in tags) ServerValidation.Text(tag, 128, nameof(tags)); if (target.Concat(tags).Distinct(StringComparer.Ordinal).Count() > 32) throw new ArgumentOutOfRangeException(nameof(tags)); target.AddRange(tags); }
    internal static void AddPolicy(List<string> target, string policy)
    { ServerValidation.Text(policy, 128, nameof(policy)); if (target.Count >= 32) throw new ArgumentOutOfRangeException(nameof(policy)); target.Add(policy); }
}
