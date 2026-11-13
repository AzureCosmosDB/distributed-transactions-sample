namespace SampleDTxApp.Models;

public sealed record TransactionResult(
    bool Succeeded,
    string Message,
    string StatusCode = "Not submitted",
    string ResponseMode = "Not applicable",
    string? ActivityId = null,
    bool IsPending = false,
    bool IsClientSideRejection = false,
    bool IsEtagConflict = false,
    string? LedgerId = null,
    string? LedgerPartitionKey = null)
{
    public static TransactionResult Failure(string message) => new(false, message);
}
