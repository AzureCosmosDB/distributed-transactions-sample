namespace SampleDTxApp.Models;

public sealed record AccountSnapshot(CosmosAccount AccountA, CosmosAccount AccountB)
{
    public decimal Total => AccountA.Balance + AccountB.Balance;
}
