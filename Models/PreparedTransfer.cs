namespace SampleDTxApp.Models;

public sealed record PreparedTransfer(
    AccountSnapshot Before,
    string AliceEtag,
    string BobEtag,
    LedgerEntry Ledger);
