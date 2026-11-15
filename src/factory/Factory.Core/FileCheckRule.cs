namespace Factory.Core;

/// <summary>One option of <c>factory check file</c>: <c>--heading</c>, <c>--contains</c> or <c>--matches</c>, and its value.</summary>
public sealed record FileCheckRule(string Option, string Value);
