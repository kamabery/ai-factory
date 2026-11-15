namespace Factory.Cli.Abstractions;

/// <summary>A doctor check's outcome: a warn does not fail doctor, a fail does.</summary>
public enum DoctorStatus { Ok, Warn, Fail }
