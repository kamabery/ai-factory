using System.Text.Json.Nodes;

namespace Factory.UnitTests;

/// <summary>Board jobs as factory.ps1's Add-Job writes them, for command tests.</summary>
public static class Jobs
{
    public static JsonObject New(string id, string stage, int round = 1, string? target = "o/r") => new()
    {
        ["id"] = id, ["idea"] = "I-001", ["kind"] = "feature", ["title"] = "Thing", ["stage"] = stage, ["round"] = round,
        ["branch"] = $"factory/{id}", ["base"] = null, ["target"] = target, ["pr"] = null,
        ["reviews"] = new JsonObject { ["security"] = "pending", ["code"] = "pending", ["ux"] = "pending", ["ui"] = "pending" },
        ["decision"] = null, ["gate"] = null, ["flag"] = null, ["merged"] = null, ["approvedAt"] = "2026-01-01T00:00:00.000Z",
        ["costUsd"] = null, ["durationMs"] = null, ["createdAt"] = "2026-01-01T00:00:00.000Z", ["updatedAt"] = "2026-01-01T00:00:00.000Z",
        ["history"] = new JsonArray(),
    };

    public static JsonObject With(this JsonObject job, string key, JsonNode? value) { job[key] = value; return job; }
}
