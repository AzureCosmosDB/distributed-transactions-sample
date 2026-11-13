using System.Text.Json.Serialization;

namespace SampleDTxApp.Models;

public sealed record CosmosAccount(
    [property: Newtonsoft.Json.JsonProperty("id"), System.Text.Json.Serialization.JsonPropertyName("id")] string Id,
    [property: Newtonsoft.Json.JsonProperty("pk"), System.Text.Json.Serialization.JsonPropertyName("pk")] string PartitionKey,
    [property: Newtonsoft.Json.JsonProperty("balance"), System.Text.Json.Serialization.JsonPropertyName("balance")] decimal Balance);
