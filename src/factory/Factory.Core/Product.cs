namespace Factory.Core;

/// <summary>A New repo idea's product document (contract, "Ideas", spec 22): its title, its <c>- Home:</c> line and its features in build order.</summary>
public sealed record Product(string? Title, string? Home, IReadOnlyList<ProductFeature> Features);
