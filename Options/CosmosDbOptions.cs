namespace SampleDTxApp.Options;

public sealed class CosmosDbOptions
{
    public const string SectionName = "CosmosDb";

    public string Endpoint { get; set; } = string.Empty;

    public string DatabaseName { get; set; } = "banking";

    public string AccountsContainerName { get; set; } = "accounts";

    public string LedgerContainerName { get; set; } = "ledger";
}
