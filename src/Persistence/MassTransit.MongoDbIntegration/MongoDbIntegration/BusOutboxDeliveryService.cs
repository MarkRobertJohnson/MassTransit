namespace MassTransit.MongoDbIntegration
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Middleware;
    using MongoDB.Bson;
    using MongoDB.Driver;
    using Outbox;
    using Serialization;


    public class BusOutboxDeliveryService :
        BackgroundService
    {
        readonly IBusControl _busControl;
        readonly ILogger _logger;
        readonly OutboxDeliveryServiceOptions _options;
        readonly IServiceProvider _provider;

        public BusOutboxDeliveryService(IBusControl busControl, IOptions<OutboxDeliveryServiceOptions> options, ILogger<BusOutboxDeliveryService> logger,
            IServiceProvider provider)
        {
            _busControl = busControl;
            _provider = provider;
            _logger = logger;

            _options = options.Value;
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
            var scope = _provider.CreateScope();

            try
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<MongoDbContext>();

                var messageLimit = _options.QueryMessageLimit;

                FilterDefinitionBuilder<OutboxMessage> builder = Builders<OutboxMessage>.Filter;
                FilterDefinition<OutboxMessage> filter = builder.Not(builder.Eq(x => x.OutboxId, null));

                List<Guid> outboxIds = await dbContext.GetCollection<OutboxMessage>()
                    .Find(filter)
                    .Limit(messageLimit)
                    .Project(x => x.OutboxId.Value)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);

                await Task.WhenAll(outboxIds.Distinct().Select(outboxId => DeliverOutbox(outboxId, cancellationToken)));
            }
            finally
            {
                // ReSharper disable once SuspiciousTypeConversion.Global
                if (scope is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else
                    scope.Dispose();
            }
        }

        async Task DeliverOutbox(Guid outboxId, CancellationToken cancellationToken)
        {
            var scope = _provider.CreateScope();

            var dbContext = scope.ServiceProvider.GetRequiredService<MongoDbContext>();

            FilterDefinitionBuilder<OutboxState> builder = Builders<OutboxState>.Filter;
            FilterDefinition<OutboxState> filter = builder.Eq(x => x.OutboxId, outboxId);

            try
            {
                async Task<bool> Execute()
                {
                    using var timeoutToken = new CancellationTokenSource(_options.QueryTimeout);

                    await dbContext.BeginTransaction(cancellationToken).ConfigureAwait(false);

                    MongoDbCollectionContext<OutboxState> stateCollection = dbContext.GetCollection<OutboxState>();
                    MongoDbCollectionContext<OutboxMessage> messageCollection = dbContext.GetCollection<OutboxMessage>();

                    UpdateDefinition<OutboxState> update = Builders<OutboxState>.Update.Set(x => x.LockToken, ObjectId.GenerateNewId());

                    try
                    {
                        var outboxState = await stateCollection.Lock(filter, update, cancellationToken).ConfigureAwait(false);

                        bool continueProcessing;

                        if (outboxState == null)
                        {
                            outboxState = new OutboxState
                            {
                                OutboxId = outboxId,
                                Version = 1
                            };

                            await stateCollection.InsertOne(outboxState, cancellationToken).ConfigureAwait(false);

                            continueProcessing = true;
                        }
                        else
                        {
                            if (outboxState.Delivered != null)
                            {
                                await RemoveOutbox(messageCollection, stateCollection, outboxState, cancellationToken).ConfigureAwait(false);

                                continueProcessing = false;
                            }
                            else
                                continueProcessing = await DeliverOutboxMessages(messageCollection, outboxState, cancellationToken).ConfigureAwait(false);

                            outboxState.Version++;

                            FilterDefinition<OutboxState> updateFilter =
                                builder.Eq(x => x.OutboxId, outboxId) & builder.Lt(x => x.Version, outboxState.Version);

                            await stateCollection.FindOneAndReplace(updateFilter, outboxState, cancellationToken).ConfigureAwait(false);
                        }

                        await dbContext.CommitTransaction(cancellationToken).ConfigureAwait(false);


                        return continueProcessing;
                    }
                    catch (MongoCommandException)
                    {
                        await AbortTransaction(dbContext).ConfigureAwait(false);
                        throw;
                    }
                    catch (Exception)
                    {
                        await AbortTransaction(dbContext).ConfigureAwait(false);
                        throw;
                    }
                }

                var continueProcessing = true;
                while (continueProcessing)
                    continueProcessing = await Execute().ConfigureAwait(false);
            }
            finally
            {
                // ReSharper disable once SuspiciousTypeConversion.Global
                if (scope is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else
                    scope.Dispose();
            }
        }

        static async Task RemoveOutbox(MongoDbCollectionContext<OutboxMessage> messageCollection, MongoDbCollectionContext<OutboxState> stateCollection,
            OutboxState outboxState, CancellationToken cancellationToken)
        {
            var messages = await messageCollection.DeleteMany(Builders<OutboxMessage>.Filter.Eq(x => x.OutboxId, outboxState.OutboxId), cancellationToken)
                .ConfigureAwait(false);

            if (messages.DeletedCount > 0)
                LogContext.Debug?.Log("Outbox removed {Count} messages: {MessageId}", messages.DeletedCount, outboxState.OutboxId);

            await stateCollection.DeleteOne(Builders<OutboxState>.Filter.Eq(x => x.OutboxId, outboxState.OutboxId), cancellationToken)
                .ConfigureAwait(false);
        }

        async Task<bool> DeliverOutboxMessages(MongoDbCollectionContext<OutboxMessage> messageCollection, OutboxState outboxState,
            CancellationToken
                cancellationToken)
        {
            var messageLimit = _options.MessageDeliveryLimit;

            var lastSequenceNumber = outboxState.LastSequenceNumber ?? 0;

            FilterDefinitionBuilder<OutboxMessage> builder = Builders<OutboxMessage>.Filter;
            FilterDefinition<OutboxMessage> filter = builder.Eq(x => x.OutboxId, outboxState.OutboxId) & builder.Gt(x => x.SequenceNumber, lastSequenceNumber);

            List<OutboxMessage> messages = await messageCollection.Find(filter)
                .Sort(Builders<OutboxMessage>.Sort.Ascending(x => x.SequenceNumber))
                .Limit(messageLimit + 1)
                .ToListAsync(cancellationToken);

            var sentSequenceNumber = 0L;

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

                    await messageCollection.DeleteOne(Builders<OutboxMessage>.Filter.Eq(x => x.MessageId, message.MessageId), cancellationToken)
                        .ConfigureAwait(false);

                    messageCount++;
                }
            }

            if (sentSequenceNumber > 0)
                outboxState.LastSequenceNumber = sentSequenceNumber;

            if (messageIndex == messages.Count && messages.Count < messageLimit)
            {
                outboxState.Delivered = DateTime.UtcNow;

                LogContext.Debug?.Log("Outbox Delivered: {OutboxId} {Delivered}", outboxState.OutboxId, outboxState.Delivered);
            }

            return true;
        }

        static async Task AbortTransaction(MongoDbContext dbContext)
        {
            try
            {
                await dbContext.AbortTransaction(CancellationToken.None).ConfigureAwait(false);
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
