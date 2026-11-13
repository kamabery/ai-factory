namespace Factory.Store.Abstractions;

/// <summary>
/// A smoke GET's answer: the status, the body, and the <c>Server</c> header (<c>-</c> when there is none), which tells the
/// Container Apps ingress (envoy) from the app (Kestrel); canary-7's 400 had an empty body.
/// </summary>
public sealed record ProbeResult(int Status, string Body, string Server);
