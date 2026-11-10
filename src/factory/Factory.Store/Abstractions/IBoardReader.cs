using System.Text.Json.Nodes;

namespace Factory.Store.Abstractions;

public interface IBoardReader
{
    JsonObject Load();
}
