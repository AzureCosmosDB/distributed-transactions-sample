using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using SampleDTxApp.Models;
using SampleDTxApp.Options;
using System.Globalization;
using System.Net;

namespace SampleDTxApp.Services;

public sealed class CosmosDistributedTransactionService : IDisposable
{
    private const string AccountAId = "alice";
    private const string AccountBId = "bob";

    private readonly CosmosDbOptions options;
    private readonly ILogger<CosmosDistributedTransactionService> logger;
    private readonly IHostEnvironment environment;
    private readonly object clientLock = new();
    private CosmosClient? client;

    public CosmosDistributedTransactionService(
        IOptions<CosmosDbOptions> options,
        ILogger<CosmosDistributedTransactionService> logger,
        IHostEnvironment environment)
    {
        this.options = options.Value;
        this.logger = logger;
        this.environment = environment;
    }

    public bool IsConfigured =>
        Uri.TryCreate(options.Endpoint, UriKind.Absolute, out Uri? endpoint) &&
        endpoint.Scheme == Uri.UriSchemeHttps;

    public async Task<TransactionResult> ResetAccountsAsync(CancellationToken cancellationToken)
    {
        CosmosAccount accountA = new(AccountAId, AccountAId, 1000m);
        CosmosAccount accountB = new(AccountBId, AccountBId, 1000m);

        try
        {
            CosmosClient cosmosClient = GetClient();
            Container accountsContainer = cosmosClient.GetContainer(
                options.DatabaseName,
                options.AccountsContainerName);

            using DistributedTransactionResponse response = await cosmosClient
                .CreateDistributedWriteTransaction()
                .UpsertItem(accountsContainer, new PartitionKey(accountA.PartitionKey), accountA.Id, accountA)
                .UpsertItem(accountsContainer, new PartitionKey(accountB.PartitionKey), accountB.Id, accountB)
                .ExecuteTransactionAsync(cancellationToken);

            return CreateResult(response, "Both accounts were reset atomically.");
        }
        catch (CosmosException exception)
        {
            return HandleCosmosException(exception, "resetting the accounts");
        }
    }

    public async Task<TransactionResult> PrepareInsufficientFundsAsync(CancellationToken cancellationToken)
    {
        CosmosAccount alice = new(AccountAId, AccountAId, 300m);
        CosmosAccount bob = new(AccountBId, AccountBId, 1000m);

        try
        {
            CosmosClient cosmosClient = GetClient();
            Container accountsContainer = cosmosClient.GetContainer(
                options.DatabaseName,
                options.AccountsContainerName);

            using DistributedTransactionResponse response = await cosmosClient
                .CreateDistributedWriteTransaction()
                .UpsertItem(accountsContainer, new PartitionKey(alice.PartitionKey), alice.Id, alice)
                .UpsertItem(accountsContainer, new PartitionKey(bob.PartitionKey), bob.Id, bob)
                .ExecuteTransactionAsync(cancellationToken);

            return CreateResult(response, "Scenario ready: Alice has 300 and Bob has 1,000. Both account updates committed together.");
        }
        catch (CosmosException exception)
        {
            return HandleCosmosException(exception, "preparing the insufficient-funds scenario");
        }
    }

    public async Task<TransactionResult> UpsertAccountsIndividuallyAsync(CancellationToken cancellationToken)
    {
        CosmosClient cosmosClient = GetClient();
        Container accountsContainer = cosmosClient.GetContainer(
            options.DatabaseName,
            options.AccountsContainerName);

        async Task<(bool Succeeded, string Status, string Detail)> UpsertAsync(CosmosAccount account)
        {
            try
            {
                ItemResponse<CosmosAccount> response = await accountsContainer.UpsertItemAsync(
                    account,
                    new PartitionKey(account.PartitionKey),
                    cancellationToken: cancellationToken);

                return (true, response.StatusCode.ToString(),
                    $"{account.Id}: {response.StatusCode} (Activity ID: {response.ActivityId}).");
            }
            catch (CosmosException exception)
            {
                logger.LogError(exception, "Individual upsert failed for {AccountId}", account.Id);
                return (false, exception.StatusCode.ToString(),
                    $"{account.Id}: {exception.StatusCode} (Activity ID: {exception.ActivityId}). {exception.Message}");
            }
        }

        var accountAResult = await UpsertAsync(new CosmosAccount(AccountAId, AccountAId, 1000m));
        var accountBResult = await UpsertAsync(new CosmosAccount(AccountBId, AccountBId, 1000m));

        return new TransactionResult(
            accountAResult.Succeeded && accountBResult.Succeeded,
            $"{accountAResult.Detail} {accountBResult.Detail} These are separate writes; one can succeed even if the other fails.",
            $"A: {accountAResult.Status}; B: {accountBResult.Status}",
            "No transaction");
    }

    public async Task<TransactionResult> TransferAsync(decimal amount, CancellationToken cancellationToken)
    {
        if (amount <= 0)
        {
            return TransactionResult.Failure("The transfer amount must be greater than zero.");
        }

        LedgerEntry? entry = null;
        try
        {
            CosmosClient cosmosClient = GetClient();
            Container accountsContainer = cosmosClient.GetContainer(
                options.DatabaseName,
                options.AccountsContainerName);
            Container ledgerContainer = cosmosClient.GetContainer(
                options.DatabaseName,
                options.LedgerContainerName);

            async Task<ItemResponse<CosmosAccount>> ReadAccountAsync(string accountId)
            {
                try
                {
                    return await accountsContainer.ReadItemAsync<CosmosAccount>(
                        accountId, new PartitionKey(accountId), cancellationToken: cancellationToken);
                }
                catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new InvalidOperationException(
                        $"Cannot read id '{accountId}' with pk '{accountId}' from " +
                        $"{options.DatabaseName}/{options.AccountsContainerName}. " +
                        "Use Test individual writes to create both accounts; also verify the database, container, and /pk partition key. " +
                        $"Activity ID: {exception.ActivityId}", exception);
                }
            }

            ItemResponse<CosmosAccount> alice = await ReadAccountAsync(AccountAId);
            ItemResponse<CosmosAccount> bob = await ReadAccountAsync(AccountBId);

            if (string.IsNullOrWhiteSpace(alice.ETag) || string.IsNullOrWhiteSpace(bob.ETag))
            {
                return TransactionResult.Failure("The account reads did not return both ETags; no transfer was submitted.");
            }

            if (alice.Resource.Balance < amount)
            {
                return new TransactionResult(
                    false,
                    $"Alice has {alice.Resource.Balance:N2}, less than the requested {amount:N2}. The client stopped before submitting a write transaction.",
                    "Not submitted",
                    "Client-side validation",
                    IsClientSideRejection: true);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            entry = new LedgerEntry(
                $"txn-{Guid.NewGuid():N}",
                now.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                AccountAId,
                AccountBId,
                amount,
                now);
            string amountLiteral = amount.ToString(CultureInfo.InvariantCulture);

            using DistributedTransactionResponse response = await cosmosClient
                .CreateDistributedWriteTransaction()
                .PatchItem(
                    accountsContainer,
                    new PartitionKey(AccountAId),
                    AccountAId,
                    [PatchOperation.Increment("/balance", -(double)amount)],
                    new DistributedTransactionPatchItemRequestOptions
                    {
                        IfMatchEtag = alice.ETag,
                        FilterPredicate = $"from c where c.balance >= {amountLiteral}"
                    })
                .PatchItem(
                    accountsContainer,
                    new PartitionKey(AccountBId),
                    AccountBId,
                    [PatchOperation.Increment("/balance", (double)amount)],
                    new DistributedTransactionPatchItemRequestOptions { IfMatchEtag = bob.ETag })
                .CreateItem(ledgerContainer, new PartitionKey(entry.PartitionKey), entry.Id, entry)
                .ExecuteTransactionAsync(cancellationToken);

            TransactionResult result = CreateResult(response,
                $"Transferred {amount:C} from Alice to Bob and recorded the ledger entry atomically.");
            if (!result.IsPending && response.Count > 0 &&
                response[0].StatusCode == HttpStatusCode.PreconditionFailed)
            {
                try
                {
                    ItemResponse<CosmosAccount> currentAlice = await ReadAccountAsync(AccountAId);
                    string explanation = currentAlice.ETag != alice.ETag
                        ? "Alice's ETag changed since the initial read; her conditional patch was rejected."
                        : currentAlice.Resource.Balance < amount
                            ? "Alice's balance is now below the transfer amount; the server-side funds condition rejected the patch."
                            : "Alice's ETag and balance currently pass the checks; the server did not identify which precondition failed. Inspect SDK diagnostics.";

                    result = result with { Message = $"{result.Message} {explanation}" };
                }
                catch (Exception exception) when (exception is CosmosException or InvalidOperationException)
                {
                    logger.LogWarning(exception, "Could not reread Alice after a precondition failure.");
                }
            }

            return result with
            {
                Message = $"{result.Message} Attempt ledger id '{entry.Id}' in pk '{entry.PartitionKey}'." +
                    (result.IsPending ? " Check this entry before another transfer." : string.Empty),
                LedgerId = result.Succeeded && !result.IsPending ? entry.Id : null,
                LedgerPartitionKey = result.Succeeded && !result.IsPending ? entry.PartitionKey : null
            };
        }
        catch (CosmosException exception)
        {
            TransactionResult result = HandleCosmosException(exception, "submitting the transfer");
            if (exception.StatusCode == HttpStatusCode.RequestTimeout)
            {
                result = result with
                {
                    IsPending = true,
                    Message = "The transfer timed out; its final outcome is unknown. Do not retry or reset the accounts yet. " +
                        $"Activity ID: {exception.ActivityId}"
                };
            }

            return entry is null ? result : result with
            {
                Message = $"{result.Message} Attempt ledger id '{entry.Id}' in pk '{entry.PartitionKey}'."
            };
        }
    }

    public async Task<PreparedTransfer> PrepareCalculatedTransferAsync(decimal amount, CancellationToken cancellationToken)
    {
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "The transfer amount must be greater than zero.");
        }

        CosmosClient cosmosClient = GetClient();
        Container accountsContainer = cosmosClient.GetContainer(options.DatabaseName, options.AccountsContainerName);

        using DistributedTransactionResponse response = await cosmosClient
            .CreateDistributedReadTransaction()
            .ReadItem(accountsContainer, new PartitionKey(AccountAId), AccountAId)
            .ReadItem(accountsContainer, new PartitionKey(AccountBId), AccountBId)
            .ExecuteTransactionAsync(cancellationToken);

        if (!response.IsSuccessStatusCode || response.Count != 2 ||
            response.Any(operation => !operation.IsSuccessStatusCode))
        {
            logger.LogWarning("Could not stage calculated transfer. Activity ID: {ActivityId}. Diagnostics: {Diagnostics}",
                response.ActivityId, response.Diagnostics);
            throw new InvalidOperationException(CreateFailureMessage(response, "Account snapshot failed"));
        }

        string? aliceEtag = response[0].ETag;
        string? bobEtag = response[1].ETag;
        if (string.IsNullOrWhiteSpace(aliceEtag) || string.IsNullOrWhiteSpace(bobEtag))
        {
            throw new InvalidOperationException("Both ETags are required to conditionally write the calculated balances.");
        }

        AccountSnapshot before = new(
            response.GetOperationResultAtIndex<CosmosAccount>(0).Resource,
            response.GetOperationResultAtIndex<CosmosAccount>(1).Resource);
        if (before.AccountA.Balance < amount)
        {
            throw new InvalidOperationException("Alice has insufficient funds. No write transaction was submitted.");
        }
        if (before.AccountB.Balance > decimal.MaxValue - amount)
        {
            throw new InvalidOperationException("Bob's calculated balance would be too large. No write transaction was submitted.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        LedgerEntry entry = new(
            $"txn-{Guid.NewGuid():N}", now.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            AccountAId, AccountBId, amount, now);
        return new PreparedTransfer(before, aliceEtag, bobEtag, entry);
    }

    public async Task<TransactionResult> CommitCalculatedTransferAsync(
        PreparedTransfer staged, CancellationToken cancellationToken)
    {
        LedgerEntry entry = staged.Ledger;
        decimal aliceBalance = staged.Before.AccountA.Balance - entry.Amount;
        decimal bobBalance = staged.Before.AccountB.Balance + entry.Amount;

        try
        {
            CosmosClient cosmosClient = GetClient();
            Container accountsContainer = cosmosClient.GetContainer(options.DatabaseName, options.AccountsContainerName);
            Container ledgerContainer = cosmosClient.GetContainer(options.DatabaseName, options.LedgerContainerName);
            string amountLiteral = entry.Amount.ToString(CultureInfo.InvariantCulture);

            using DistributedTransactionResponse response = await cosmosClient
                .CreateDistributedWriteTransaction()
                .PatchItem(accountsContainer, new PartitionKey(AccountAId), AccountAId,
                    [PatchOperation.Replace("/balance", aliceBalance)],
                    new DistributedTransactionPatchItemRequestOptions
                    {
                        IfMatchEtag = staged.AliceEtag,
                        FilterPredicate = $"from c where c.balance >= {amountLiteral}"
                    })
                .PatchItem(accountsContainer, new PartitionKey(AccountBId), AccountBId,
                    [PatchOperation.Replace("/balance", bobBalance)],
                    new DistributedTransactionPatchItemRequestOptions { IfMatchEtag = staged.BobEtag })
                .CreateItem(ledgerContainer, new PartitionKey(entry.PartitionKey), entry.Id, entry)
                .ExecuteTransactionAsync(cancellationToken);

            TransactionResult result = CreateResult(response,
                $"Calculated transfer of {entry.Amount:C} committed with both captured ETags.");
            bool etagConflict = !result.IsPending &&
                response.Any(operation => operation.StatusCode == HttpStatusCode.PreconditionFailed);
            return result with
            {
                IsEtagConflict = etagConflict,
                Message = $"{result.Message} Ledger id '{entry.Id}' in pk '{entry.PartitionKey}'." +
                    (etagConflict ? " An ETag or balance precondition failed; read fresh state and recalculate before trying again." :
                    result.IsPending ? " Outcome unknown; reconcile the ledger before another attempt." : string.Empty),
                LedgerId = result.Succeeded && !result.IsPending ? entry.Id : null,
                LedgerPartitionKey = result.Succeeded && !result.IsPending ? entry.PartitionKey : null
            };
        }
        catch (CosmosException exception)
        {
            TransactionResult result = HandleCosmosException(exception, "committing the calculated transfer");
            bool timedOut = exception.StatusCode == HttpStatusCode.RequestTimeout;
            return result with
            {
                IsPending = timedOut,
                IsEtagConflict = exception.StatusCode == HttpStatusCode.PreconditionFailed,
                Message = $"{result.Message} Ledger id '{entry.Id}' in pk '{entry.PartitionKey}'." +
                    (timedOut ? " Outcome unknown; reconcile the ledger before another attempt." :
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                        ? " An ETag or balance precondition failed; read fresh state and recalculate before trying again." : string.Empty)
            };
        }
    }

    public async Task<PreparedTransfer> PrepareConcurrentTransferAsync(CancellationToken cancellationToken)
    {
        CosmosClient cosmosClient = GetClient();
        Container accountsContainer = cosmosClient.GetContainer(options.DatabaseName, options.AccountsContainerName);

        ItemResponse<CosmosAccount> alice = await accountsContainer.ReadItemAsync<CosmosAccount>(
            AccountAId, new PartitionKey(AccountAId), cancellationToken: cancellationToken);
        ItemResponse<CosmosAccount> bob = await accountsContainer.ReadItemAsync<CosmosAccount>(
            AccountBId, new PartitionKey(AccountBId), cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(alice.ETag) || string.IsNullOrWhiteSpace(bob.ETag))
        {
            throw new InvalidOperationException("Both account ETags are required to stage the transfer.");
        }

        const decimal amount = 100m;
        if (alice.Resource.Balance < amount)
        {
            throw new InvalidOperationException("Alice needs at least 100 to stage this transfer. Reset the accounts first.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        LedgerEntry entry = new(
            $"txn-{Guid.NewGuid():N}",
            now.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            AccountAId,
            AccountBId,
            amount,
            now);

        return new PreparedTransfer(
            new AccountSnapshot(alice.Resource, bob.Resource), alice.ETag, bob.ETag, entry);
    }

    public async Task<TransactionResult> ApplyConcurrentAliceUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            Container accountsContainer = GetClient().GetContainer(options.DatabaseName, options.AccountsContainerName);
            ItemResponse<CosmosAccount> response = await accountsContainer.PatchItemAsync<CosmosAccount>(
                AccountAId,
                new PartitionKey(AccountAId),
                [PatchOperation.Increment("/balance", 1L)],
                cancellationToken: cancellationToken);

            return new TransactionResult(true,
                "Alice's balance increased by 1 in an ordinary, independent item write. Her ETag changed.",
                response.StatusCode.ToString(), "No transaction", response.ActivityId);
        }
        catch (CosmosException exception)
        {
            TransactionResult result = HandleCosmosException(exception, "modifying Alice between the staged read and commit");
            return exception.StatusCode == HttpStatusCode.RequestTimeout
                ? result with
                {
                    IsPending = true,
                    Message = "Alice's independent update timed out; its outcome is unknown. Check Alice's balance and ETag before trying again."
                }
                : result;
        }
    }

    public async Task<TransactionResult> CommitPreparedTransferAsync(
        PreparedTransfer staged, CancellationToken cancellationToken)
    {
        try
        {
            CosmosClient cosmosClient = GetClient();
            Container accountsContainer = cosmosClient.GetContainer(options.DatabaseName, options.AccountsContainerName);
            Container ledgerContainer = cosmosClient.GetContainer(options.DatabaseName, options.LedgerContainerName);

            ItemResponse<CosmosAccount> currentAlice = await accountsContainer.ReadItemAsync<CosmosAccount>(
                AccountAId, new PartitionKey(AccountAId), cancellationToken: cancellationToken);
            if (currentAlice.ETag == staged.AliceEtag)
            {
                return TransactionResult.Failure(
                    "Alice's ETag has not changed. Run the side-terminal script first; no write transaction was submitted.");
            }

            decimal amount = staged.Ledger.Amount;
            string amountLiteral = amount.ToString(CultureInfo.InvariantCulture);

            using DistributedTransactionResponse response = await cosmosClient
                .CreateDistributedWriteTransaction()
                .PatchItem(accountsContainer, new PartitionKey(AccountAId), AccountAId,
                    [PatchOperation.Increment("/balance", -(double)amount)],
                    new DistributedTransactionPatchItemRequestOptions
                    {
                        IfMatchEtag = staged.AliceEtag,
                        FilterPredicate = $"from c where c.balance >= {amountLiteral}"
                    })
                .PatchItem(accountsContainer, new PartitionKey(AccountBId), AccountBId,
                    [PatchOperation.Increment("/balance", (double)amount)],
                    new DistributedTransactionPatchItemRequestOptions { IfMatchEtag = staged.BobEtag })
                .CreateItem(ledgerContainer, new PartitionKey(staged.Ledger.PartitionKey),
                    staged.Ledger.Id, staged.Ledger)
                .ExecuteTransactionAsync(cancellationToken);

            TransactionResult result = CreateResult(response, "The staged transfer committed unexpectedly; review the account state.");
            if (!result.IsPending && response.Count > 0 &&
                response[0].StatusCode == HttpStatusCode.PreconditionFailed)
            {
                result = result with
                {
                    IsEtagConflict = true,
                    Message = $"{result.Message} Alice's staged ETag was rejected after the independent update."
                };
            }

            return result with
            {
                Message = $"{result.Message} Attempt ledger id '{staged.Ledger.Id}' in pk '{staged.Ledger.PartitionKey}'."
            };
        }
        catch (CosmosException exception)
        {
            TransactionResult result = HandleCosmosException(exception, "committing the staged transfer");
            if (exception.StatusCode == HttpStatusCode.RequestTimeout)
            {
                result = result with
                {
                    IsPending = true,
                    Message = "The staged transfer timed out; its final outcome is unknown. Do not retry before reconciliation."
                };
            }

            return result with
            {
                Message = $"{result.Message} Attempt ledger id '{staged.Ledger.Id}' in pk '{staged.Ledger.PartitionKey}'."
            };
        }
    }

    public async Task<bool> LedgerEntryExistsAsync(PreparedTransfer staged, CancellationToken cancellationToken)
    {
        Container ledgerContainer = GetClient().GetContainer(options.DatabaseName, options.LedgerContainerName);

        try
        {
            await ledgerContainer.ReadItemAsync<LedgerEntry>(
                staged.Ledger.Id, new PartitionKey(staged.Ledger.PartitionKey),
                cancellationToken: cancellationToken);
            return true;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<TransferReceipt> ReadTransferReceiptAsync(
        string ledgerId, string ledgerPartitionKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ledgerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ledgerPartitionKey);

        try
        {
            CosmosClient cosmosClient = GetClient();
            Container accountsContainer = cosmosClient.GetContainer(options.DatabaseName, options.AccountsContainerName);
            Container ledgerContainer = cosmosClient.GetContainer(options.DatabaseName, options.LedgerContainerName);

            using DistributedTransactionResponse response = await cosmosClient
                .CreateDistributedReadTransaction()
                .ReadItem(accountsContainer, new PartitionKey(AccountAId), AccountAId)
                .ReadItem(accountsContainer, new PartitionKey(AccountBId), AccountBId)
                .ReadItem(ledgerContainer, new PartitionKey(ledgerPartitionKey), ledgerId)
                .ExecuteTransactionAsync(cancellationToken);

            if (!response.IsSuccessStatusCode || response.Count != 3 ||
                response.Any(operation => !operation.IsSuccessStatusCode))
            {
                logger.LogWarning("Receipt snapshot failed. Activity ID: {ActivityId}. Diagnostics: {Diagnostics}",
                    response.ActivityId, response.Diagnostics);
                throw new InvalidOperationException(CreateFailureMessage(response, "Receipt snapshot failed"));
            }

            AccountSnapshot accounts = new(
                response.GetOperationResultAtIndex<CosmosAccount>(0).Resource,
                response.GetOperationResultAtIndex<CosmosAccount>(1).Resource);
            LedgerEntry ledger = response.GetOperationResultAtIndex<LedgerEntry>(2).Resource;
            return new TransferReceipt(accounts, ledger, response.ActivityId);
        }
        catch (CosmosException exception)
        {
            logger.LogError(exception, "Cosmos DB rejected the receipt snapshot. Activity ID: {ActivityId}",
                exception.ActivityId);
            throw new InvalidOperationException(
                $"Cosmos DB rejected the receipt snapshot with {exception.StatusCode}. Activity ID: {exception.ActivityId}",
                exception);
        }
    }

    public async Task<AccountSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            CosmosClient cosmosClient = GetClient();
            Container accountsContainer = cosmosClient.GetContainer(
                options.DatabaseName,
                options.AccountsContainerName);

            using DistributedTransactionResponse response = await cosmosClient
                .CreateDistributedReadTransaction()
                .ReadItem(accountsContainer, new PartitionKey(AccountAId), AccountAId)
                .ReadItem(accountsContainer, new PartitionKey(AccountBId), AccountBId)
                .ExecuteTransactionAsync(cancellationToken);

            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotModified)
            {
                throw new InvalidOperationException(CreateFailureMessage(response, "Snapshot transaction failed"));
            }

            for (int index = 0; index < response.Count; index++)
            {
                if (!response[index].IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"Snapshot operation {index} failed with {response[index].StatusCode}. Activity ID: {response.ActivityId}");
                }
            }

            CosmosAccount accountA = response.GetOperationResultAtIndex<CosmosAccount>(0).Resource;
            CosmosAccount accountB = response.GetOperationResultAtIndex<CosmosAccount>(1).Resource;
            return new AccountSnapshot(accountA, accountB);
        }
        catch (CosmosException exception)
        {
            logger.LogError(exception, "Cosmos DB rejected the distributed snapshot transaction.");
            throw new InvalidOperationException(
                $"Cosmos DB rejected the snapshot with {exception.StatusCode}. Activity ID: {exception.ActivityId}",
                exception);
        }
    }

    public void Dispose()
    {
        client?.Dispose();
    }

    private CosmosClient GetClient()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Cosmos DB is not configured. Set a valid HTTPS endpoint, then restart the app.");
        }

        if (client is not null)
        {
            return client;
        }

        lock (clientLock)
        {
            if (client is null)
            {
                CosmosClientOptions clientOptions = new()
                {
                    MaxRetryAttemptsOnAbortedTransactions = 10,
                    MaxRetryWaitTimeOnAbortedTransactions = TimeSpan.FromSeconds(30)
                };

                string? managedIdentityClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
                TokenCredential credential = environment.IsDevelopment()
                    ? new DefaultAzureCredential()
                    : string.IsNullOrWhiteSpace(managedIdentityClientId)
                        ? new ManagedIdentityCredential()
                        : new ManagedIdentityCredential(managedIdentityClientId);
                logger.LogInformation("Connecting to Cosmos DB at {Endpoint} using {AuthenticationMode} authentication.",
                    options.Endpoint, environment.IsDevelopment() ? "developer credential" : "managed identity");
                client = new CosmosClient(options.Endpoint, credential, clientOptions);
            }
        }

        return client;
    }

    private TransactionResult CreateResult(DistributedTransactionResponse response, string successMessage)
    {
        bool succeeded = response.IsSuccessStatusCode && response.All(operation => operation.IsSuccessStatusCode);
        string message;

        if (!succeeded)
        {
            bool timedOut = response.StatusCode == HttpStatusCode.RequestTimeout ||
                response.Any(operation => operation.StatusCode == HttpStatusCode.RequestTimeout);
            message = CreateFailureMessage(response,
                timedOut ? "The transaction timed out; its final outcome is unknown. Do not retry or reset the accounts yet" : "The transaction aborted");
            logger.LogWarning(
                "Distributed transaction aborted with {StatusCode}. Activity ID: {ActivityId}. Error: {ErrorMessage}. Diagnostics: {Diagnostics}",
                response.StatusCode,
                response.ActivityId,
                response.ErrorMessage,
                response.Diagnostics);
        }
        else if (response.ResponseMode == DistributedTransactionResponseMode.Standard)
        {
            message = successMessage;
        }
        else
        {
            message = "Cosmos DB returned FastResponse; the final commit or abort outcome is not yet available.";
        }

        return new TransactionResult(
            succeeded,
            message,
            response.StatusCode.ToString(),
            response.ResponseMode.ToString(),
            response.ActivityId,
            response.StatusCode == HttpStatusCode.RequestTimeout ||
            response.Any(operation => operation.StatusCode == HttpStatusCode.RequestTimeout) ||
            succeeded && response.ResponseMode != DistributedTransactionResponseMode.Standard);
    }

    private TransactionResult HandleCosmosException(CosmosException exception, string operation)
    {
        logger.LogError(
            exception,
            "Cosmos DB failed while {Operation}. Activity ID: {ActivityId}",
            operation,
            exception.ActivityId);

        return new TransactionResult(
            false,
            $"Cosmos DB rejected the request while {operation}: {exception.Message}",
            exception.StatusCode.ToString(),
            "Not available",
            exception.ActivityId);
    }

    private static string CreateFailureMessage(DistributedTransactionResponse response, string prefix)
    {
        string operationStatuses = string.Join(", ",
            Enumerable.Range(0, response.Count).Select(index => $"Operation {index}: {response[index].StatusCode}"));

        return $"{prefix}: {response.StatusCode}. {response.ErrorMessage} " +
            $"{operationStatuses} Activity ID: {response.ActivityId}";
    }
}
