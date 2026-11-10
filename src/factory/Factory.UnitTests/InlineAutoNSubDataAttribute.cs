using AutoFixture.Xunit3;

namespace Factory.UnitTests;

public sealed class InlineAutoNSubDataAttribute(params object?[] values) : InlineAutoDataAttribute(AutoNSubDataAttribute.Create, values);
