namespace SampleDTxApp.Models;

public sealed record TransferReceipt(AccountSnapshot Accounts, LedgerEntry Ledger, string? ActivityId);
