namespace Factory.Store.Abstractions;

/// <summary>What the manifest flow's code converts to: the new App's id, slug, OAuth client, private key and owner.</summary>
public sealed record AppRegistration(string Id, string Slug, string ClientId, string ClientSecret, string PrivateKey, string Owner);
