using System.Text.Json.Serialization;

namespace SampleDTxApp.Models;

public sealed record LedgerEntry(
    [property: Newtonsoft.Json.JsonProperty("id"), System.Text.Json.Serialization.JsonPropertyName("id")] string Id,
    [property: Newtonsoft.Json.JsonProperty("pk"), System.Text.Json.Serialization.JsonPropertyName("pk")] string PartitionKey,
    [property: Newtonsoft.Json.JsonProperty("from"), System.Text.Json.Serialization.JsonPropertyName("from")] string From,
    [property: Newtonsoft.Json.JsonProperty("to"), System.Text.Json.Serialization.JsonPropertyName("to")] string To,
    [property: Newtonsoft.Json.JsonProperty("amount"), System.Text.Json.Serialization.JsonPropertyName("amount")] decimal Amount,
    [property: Newtonsoft.Json.JsonProperty("createdUtc"), System.Text.Json.Serialization.JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc);
