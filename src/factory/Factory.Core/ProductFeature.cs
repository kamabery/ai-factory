namespace Factory.Core;

/// <summary>One feature of a product document: its <c>###</c> heading under <c>## Features</c>, and the text below it.</summary>
public sealed record ProductFeature(string Heading, string Text);
