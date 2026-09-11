using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Application.Transactions;

namespace Persistence.MerchantRuntime.Payments;

/// <summary>Backend recovery loop for pending Transaction inquiries; it does not depend on a browser.</summary>
internal sealed class TransactionInquiryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<TransactionInquiryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Transaction inquiry batch failed.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task RunBatchAsync(CancellationToken cancellationToken)
    {
        using var discovery = scopeFactory.CreateScope();
        var transactions = discovery.ServiceProvider.GetRequiredService<ITransactionRepository>();
        var clock = discovery.ServiceProvider.GetRequiredService<IClock>();
        var due = await transactions.ListDueAsync(clock.UtcNow, 50, cancellationToken).ConfigureAwait(false);

        foreach (var (merchantId, transactionId) in due)
        {
            using var scope = scopeFactory.CreateScope();
            using var actor = scope.ServiceProvider.GetRequiredService<IActorScope>().Begin(merchantId);
            var processor = scope.ServiceProvider.GetRequiredService<CheckoutTransactionService>();
            try
            {
                await processor.ResumeDueAsync(merchantId, transactionId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Transaction inquiry failed for {TransactionId}.", transactionId);
            }
        }
    }
}
