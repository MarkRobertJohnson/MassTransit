namespace MassTransit.EntityFrameworkCoreIntegration
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.EntityFrameworkCore.Storage;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Middleware;
    using Serialization;


    public class BusOutboxDeliveryService<TDbContext> :
        BackgroundService
        where TDbContext : DbContext
    {
        readonly IBusControl _busControl;
        readonly IsolationLevel _isolationLevel;
        readonly ILockStatementProvider _lockStatementProvider;
        readonly ILogger _logger;
        readonly OutboxDeliveryServiceOptions _options;
        readonly IServiceProvider _provider;

        public BusOutboxDeliveryService(IBusControl busControl, IOptions<OutboxDeliveryServiceOptions> options,
            IOptions<EntityFrameworkOutboxOptions> outboxOptions,
            ILogger<BusOutboxDeliveryService<TDbContext>> logger, IServiceProvider provider)
        {
            _busControl = busControl;
            _provider = provider;
            _logger = logger;

            _options = options.Value;

            _lockStatementProvider = outboxOptions.Value.LockStatementProvider;
            _isolationLevel = outboxOptions.Value.IsolationLevel;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_options.QueryDelay, stoppingToken).ConfigureAwait(false);

                    await _busControl.WaitForHealthStatus(BusHealthStatus.Healthy, stoppingToken).ConfigureAwait(false);

                    await ProcessMessageBatch(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (exception.CancellationToken == stoppingToken)
                {
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "ProcessMessageBatch faulted");
                }
            }
        }

        async Task ProcessMessageBatch(CancellationToken cancellationToken)
        {
            using var scope = _provider.CreateScope();

            await using var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();

            var messageLimit = _options.QueryMessageLimit;

            List<Guid> outboxIds = await dbContext.Set<OutboxMessage>()
                .Where(x => x.OutboxId != null)
                .OrderBy(x => x.SequenceNumber)
                .Take(messageLimit)
                .Select(x => x.OutboxId.Value)
                .Distinct()
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            await Task.WhenAll(outboxIds.Select(outboxId => DeliverOutbox(outboxId, cancellationToken)));
        }

        async Task DeliverOutbox(Guid outboxId, CancellationToken cancellationToken)
        {
            var scope = _provider.CreateScope();

            var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();

            try
            {
                var statement = _lockStatementProvider.GetRowLockStatement<OutboxState>(dbContext, nameof(OutboxState.OutboxId));

                async Task<bool> Execute()
                {
                    using var timeoutToken = new CancellationTokenSource(_options.QueryTimeout);

                    await using var transaction = await dbContext.Database.BeginTransactionAsync(_isolationLevel, timeoutToken.Token)
                        .ConfigureAwait(false);

                    try
                    {
                        var outboxState = await dbContext.Set<OutboxState>()
                            .FromSqlRaw(statement, outboxId)
                            .SingleOrDefaultAsync(timeoutToken.Token).ConfigureAwait(false);

                        bool continueProcessing;

                        if (outboxState == null)
                        {
                            outboxState = new OutboxState
                            {
                                OutboxId = outboxId,
                            };

                            await dbContext.AddAsync(outboxState, timeoutToken.Token).ConfigureAwait(false);
                            await dbContext.SaveChangesAsync(timeoutToken.Token).ConfigureAwait(false);

                            continueProcessing = true;
                        }
                        else
                        {
                            if (outboxState.Delivered != null)
                            {
                                await RemoveOutbox(dbContext, outboxState, cancellationToken).ConfigureAwait(false);

                                continueProcessing = false;
                            }
                            else
                                continueProcessing = await DeliverOutboxMessages(dbContext, outboxState, cancellationToken).ConfigureAwait(false);
                        }

                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                        return continueProcessing;
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        await RollbackTransaction(transaction).ConfigureAwait(false);
                        throw;
                    }
                    catch (DbUpdateException)
                    {
                        await RollbackTransaction(transaction).ConfigureAwait(false);
                        throw;
                    }
                    catch (Exception)
                    {
                        await RollbackTransaction(transaction).ConfigureAwait(false);
                        throw;
                    }
                }

                var continueProcessing = true;
                while (continueProcessing)
                {
                    var executionStrategy = dbContext.Database.CreateExecutionStrategy();
                    if (executionStrategy is ExecutionStrategy)
                        continueProcessing = await executionStrategy.ExecuteAsync(() => Execute()).ConfigureAwait(false);
                    else
                        continueProcessing = await Execute().ConfigureAwait(false);
                }
            }
            finally
            {
                if (dbContext != null)
                    await dbContext.DisposeAsync().ConfigureAwait(false);

                // ReSharper disable once SuspiciousTypeConversion.Global
                if (scope is IAsyncDisposable disposable)
                    await disposable.DisposeAsync().ConfigureAwait(false);
                else
                    scope.Dispose();
            }
        }

        static async Task RemoveOutbox(TDbContext dbContext, OutboxState outboxState, CancellationToken cancellationToken)
        {
            List<OutboxMessage> messages = await dbContext.Set<OutboxMessage>()
                .Where(x => x.OutboxId == outboxState.OutboxId)
                .OrderBy(x => x.SequenceNumber)
                .ToListAsync(cancellationToken);

            dbContext.RemoveRange(messages);

            dbContext.Remove(outboxState);

            await dbContext.SaveChangesAsync(cancellationToken);

            if (messages.Count > 0)
                LogContext.Debug?.Log("Outbox removed {Count} messages: {OutboxId}", messages.Count, outboxState);
        }

        async Task<bool> DeliverOutboxMessages(TDbContext dbContext, OutboxState outboxState, CancellationToken cancellationToken)
        {
            var messageLimit = _options.MessageDeliveryLimit;

            var lastSequenceNumber = outboxState.LastSequenceNumber ?? 0;

            List<OutboxMessage> messages = await dbContext.Set<OutboxMessage>()
                .Where(x => x.OutboxId == outboxState.OutboxId && x.SequenceNumber > lastSequenceNumber)
                .OrderBy(x => x.SequenceNumber)
                .Take(messageLimit)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            var sentSequenceNumber = 0L;

            var saveChanges = false;

            var messageCount = 0;
            var messageIndex = 0;
            for (; messageIndex < messages.Count && messageCount < messageLimit; messageIndex++)
            {
                var message = messages[messageIndex];

                message.Deserialize(SystemTextJsonMessageSerializer.Instance);

                if (message.DestinationAddress == null)
                {
                    LogContext.Warning?.Log("Outbox message DestinationAddress not present: {SequenceNumber} {MessageId}", message.SequenceNumber,
                        message.MessageId);
                }
                else
                {
                    try
                    {
                        using var sendToken = new CancellationTokenSource(_options.MessageDeliveryTimeout);
                        using var token = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sendToken.Token);

                        var pipe = new OutboxMessageSendPipe(message, message.DestinationAddress);

                        var endpoint = await _busControl.GetSendEndpoint(message.DestinationAddress).ConfigureAwait(false);

                        await endpoint.Send(new Outbox(), pipe, token.Token).ConfigureAwait(false);

                        sentSequenceNumber = message.SequenceNumber;

                        LogContext.Debug?.Log("Outbox Sent: {OutboxId} {SequenceNumber} {MessageId}", message.OutboxId, sentSequenceNumber, message.MessageId);
                    }
                    catch (Exception ex)
                    {
                        LogContext.Warning?.Log(ex, "Outbox Send Fault: {OutboxId} {SequenceNumber} {MessageId}", message.OutboxId, message.SequenceNumber,
                            message.MessageId);

                        break;
                    }

                    dbContext.Remove((object)message);

                    saveChanges = true;

                    messageCount++;
                }
            }

            if (sentSequenceNumber > 0)
            {
                outboxState.LastSequenceNumber = sentSequenceNumber;
                dbContext.Update(outboxState);

                saveChanges = true;
            }

            if (messageIndex == messages.Count && messages.Count < messageLimit)
            {
                outboxState.Delivered = DateTime.UtcNow;
                dbContext.Update(outboxState);

                saveChanges = true;

                LogContext.Debug?.Log("Outbox Delivered: {OutboxId} {Delivered}", outboxState.OutboxId, outboxState.Delivered);
            }

            if (saveChanges)
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return true;
        }

        static async Task RollbackTransaction(IDbContextTransaction transaction)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception innerException)
            {
                LogContext.Warning?.Log(innerException, "Transaction rollback failed");
            }
        }


        class Outbox
        {
        }
    }
}
