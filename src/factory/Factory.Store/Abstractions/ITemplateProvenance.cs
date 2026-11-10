using System.Text.Json.Nodes;
using Factory.Core;

namespace Factory.Store.Abstractions;

/// <summary>Build check 3 (contract, "Template provenance"): which of a round's changes touch template-sourced files.</summary>
public interface ITemplateProvenance
{
    Provenance For(JsonObject board, Job job);
}
