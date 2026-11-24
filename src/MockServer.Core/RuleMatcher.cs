using System.Text.Json.Nodes;

namespace MockServer.Core;

public static class RuleMatcher
{
    public static bool PathMatches(string pattern, string path)
    {
        var expected = pattern.Split('/');
        var actual = path.Split('/');
        return expected.Length == actual.Length && expected.Zip(actual).All(pair =>
            pair.First.StartsWith('{') ? pair.Second.Length > 0 : pair.First == pair.Second);
    }

    public static bool JsonMatches(JsonNode? expected, JsonNode? actual)
    {
        if (expected is JsonObject obj)
            return actual is JsonObject candidate && obj.All(pair => candidate.ContainsKey(pair.Key) && JsonMatches(pair.Value, candidate[pair.Key]));
        return JsonNode.DeepEquals(expected, actual);
    }
}
