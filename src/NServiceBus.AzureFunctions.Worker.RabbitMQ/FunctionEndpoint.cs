namespace NServiceBus
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using AzureFunctions.Worker.RabbitMQ;
    using AzureFunctions.Worker.ServiceBus;
    using Extensibility;
    using global::Newtonsoft.Json;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Logging;
    using RabbitMQ.Client;
    using RabbitMQ.Client.Events;
    using Transport;

    /// <summary>
    /// An NServiceBus endpoint hosted in Azure Function which does not receive messages automatically but only handles
    /// messages explicitly passed to it by the caller.
    /// </summary>
    public class FunctionEndpoint : IFunctionEndpoint
    {
        // This ctor is used for the FunctionsHostBuilder scenario where the endpoint is created already during configuration time using the function host's container.
        internal FunctionEndpoint(IStartableEndpointWithExternallyManagedContainer externallyManagedContainerEndpoint, RabbitMQTriggeredEndpointConfiguration configuration, IServiceProvider serviceProvider)
        {
            this.configuration = configuration;
            endpointFactory = _ => externallyManagedContainerEndpoint.Start(serviceProvider);
        }

        /// <inheritdoc />
        public async Task Process(
            ReadOnlyMemory<byte> body,
            FunctionContext functionContext,
            CancellationToken cancellationToken)
        {
            FunctionsLoggerFactory.Instance.SetCurrentLogger(functionContext.GetLogger("NServiceBus"));

            await InitializeEndpointIfNecessary(functionContext, CancellationToken.None)
                .ConfigureAwait(false);

            var args = new BasicDeliverEventArgs { Body = body };
            var logger = functionContext.GetLogger<FunctionEndpoint>();
            foreach (KeyValuePair<string, object> pair in functionContext.BindingContext.BindingData)
            {
                logger.LogWarning($"{pair.Key}: {pair.Value}");
            }
            if (functionContext.BindingContext.BindingData.TryGetValue(nameof(BasicDeliverEventArgs.ConsumerTag), out var consumerTagObject))
            {
                args.ConsumerTag = (string)consumerTagObject;
            }
            if (functionContext.BindingContext.BindingData.TryGetValue(nameof(BasicDeliverEventArgs.DeliveryTag), out var deliveryTagObject))
            {
                args.DeliveryTag = Convert.ToUInt64(deliveryTagObject);
            }
            if (functionContext.BindingContext.BindingData.TryGetValue(nameof(BasicDeliverEventArgs.Redelivered), out var redeliveredObject))
            {
                args.Redelivered = Convert.ToBoolean(redeliveredObject);
            }
            // TODO exchange and routing keys
            if (functionContext.BindingContext.BindingData.TryGetValue(nameof(BasicDeliverEventArgs.BasicProperties), out var basicPropertiesObject))
            {
                args.BasicProperties = JsonConvert.DeserializeObject<BasicProperties>((string)basicPropertiesObject);

                foreach (KeyValuePair<string, object> pair in args.BasicProperties.Headers)
                {
                    logger.LogWarning($"{pair.Key}: {pair.Value}");
                }
            }
            await Process(args, pipeline, cancellationToken)
                .ConfigureAwait(false);
        }

        sealed class BasicProperties : IBasicProperties
        {
            byte deliveryMode;
            bool deliveryModePresent;
            public ushort ProtocolClassId { get; set; }
            public string ProtocolClassName { get; set; }
            public void ClearAppId() => throw new NotImplementedException();

            public void ClearClusterId() => throw new NotImplementedException();

            public void ClearContentEncoding() => throw new NotImplementedException();

            public void ClearContentType() => throw new NotImplementedException();

            public void ClearCorrelationId() => throw new NotImplementedException();

            public void ClearDeliveryMode() => throw new NotImplementedException();

            public void ClearExpiration() => throw new NotImplementedException();

            public void ClearHeaders() => throw new NotImplementedException();

            public void ClearMessageId() => throw new NotImplementedException();

            public void ClearPriority() => throw new NotImplementedException();

            public void ClearReplyTo() => throw new NotImplementedException();

            public void ClearTimestamp() => throw new NotImplementedException();

            public void ClearType() => throw new NotImplementedException();

            public void ClearUserId() => throw new NotImplementedException();

            public bool IsAppIdPresent() => throw new NotImplementedException();

            public bool IsClusterIdPresent() => throw new NotImplementedException();

            public bool IsContentEncodingPresent() => throw new NotImplementedException();

            public bool IsContentTypePresent() => throw new NotImplementedException();

            public bool IsCorrelationIdPresent() => CorrelationId != null;

            public bool IsDeliveryModePresent() => deliveryModePresent;

            public bool IsExpirationPresent() => throw new NotImplementedException();

            public bool IsHeadersPresent() => throw new NotImplementedException();

            public bool IsMessageIdPresent() => MessageId != null;

            public bool IsPriorityPresent() => throw new NotImplementedException();

            public bool IsReplyToPresent() => ReplyTo != null;

            public bool IsTimestampPresent() => throw new NotImplementedException();

            public bool IsTypePresent() => throw new NotImplementedException();

            public bool IsUserIdPresent() => throw new NotImplementedException();

            public string AppId { get; set; }
            public string ClusterId { get; set; }
            public string ContentEncoding { get; set; }
            public string ContentType { get; set; }
            public string CorrelationId { get; set; }

            public byte DeliveryMode
            {
                get => deliveryMode;
                set
                {
                    deliveryModePresent = true;
                    deliveryMode = value;
                }
            }
            public string Expiration { get; set; }
            public IDictionary<string, object> Headers { get; set; }
            public string MessageId { get; set; }
            public bool Persistent { get; set; }
            public byte Priority { get; set; }
            public string ReplyTo { get; set; }
            public PublicationAddress ReplyToAddress { get; set; }
            public AmqpTimestamp Timestamp { get; set; }
            public string Type { get; set; }
            public string UserId { get; set; }
        }

        internal async Task Process(
            BasicDeliverEventArgs message,
            PipelineInvoker pipeline,
            CancellationToken cancellationToken)
        {
            Dictionary<string, string> headers;

            try
            {
                headers = messageConverter.RetrieveHeaders(message);
            }
            catch (Exception)
            {
                // TODO:
                // Logger.Error(
                //     $"Failed to retrieve headers from poison message. Moving message to queue '{settings.ErrorQueue}'...",
                //     ex);
                // await MovePoisonMessage(consumer, message, settings.ErrorQueue, messageProcessingCancellationToken).ConfigureAwait(false);

                throw;
                // return;
            }

            string messageId;

            try
            {
                messageId = messageConverter.RetrieveMessageId(message, headers);
            }
            catch (Exception)
            {
                // TODO:
                // Logger.Error(
                //     $"Failed to retrieve ID from poison message. Moving message to queue '{settings.ErrorQueue}'...",
                //     ex);
                // await MovePoisonMessage(consumer, message, settings.ErrorQueue, messageProcessingCancellationToken).ConfigureAwait(false);
                throw;
                // return;
            }

            var processed = false;
            var errorHandled = false;
            var numberOfDeliveryAttempts = 0;
            var transportTransaction = new TransportTransaction();

            while (!processed && !errorHandled)
            {
                var processingContext = new ContextBag();

                processingContext.Set(message);

                try
                {
                    var messageContext = new MessageContext(messageId, headers, message.Body, transportTransaction, pipeline.ReceiveAddress, processingContext);

                    await pipeline.PushMessage(messageContext, cancellationToken).ConfigureAwait(false);
                    processed = true;
                }
                catch (Exception ex) when (!ex.IsCausedBy(cancellationToken))
                {
                    ++numberOfDeliveryAttempts;
                    headers = messageConverter.RetrieveHeaders(message);

                    var errorContext = new ErrorContext(ex, headers, messageId, message.Body, transportTransaction, numberOfDeliveryAttempts, pipeline.ReceiveAddress, processingContext);

                    try
                    {
                        errorHandled =
                            await pipeline.PushFailedMessage(errorContext, cancellationToken).ConfigureAwait(false) ==
                            ErrorHandleResult.Handled;

                        if (!errorHandled)
                        {
                            headers = messageConverter.RetrieveHeaders(message);
                        }
                    }
                    catch (Exception onErrorEx) when (!onErrorEx.IsCausedBy(cancellationToken))
                    {
                        // TODO: 
                        // criticalErrorAction(
                        //     $"Failed to execute recoverability policy for message with native ID: `{messageId}`", onErrorEx,
                        //     messageProcessingCancellationToken);
                        // consumer.Model.BasicRejectAndRequeueIfOpen(message.DeliveryTag);

                        return;
                    }
                }
            }
        }

        async Task InitializeEndpointIfNecessary(FunctionContext functionContext, CancellationToken cancellationToken)
        {
            if (pipeline == null)
            {
                await semaphoreLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (pipeline == null)
                    {
                        endpoint = await endpointFactory(functionContext).ConfigureAwait(false);

                        pipeline = configuration.PipelineInvoker;

                        messageConverter = new MessageConverter(configuration.Transport.MessageIdStrategy ?? MessageConverter.DefaultMessageIdStrategy);
                    }
                }
                finally
                {
                    semaphoreLock.Release();
                }
            }
        }

        /// <inheritdoc />
        public async Task Send(object message, SendOptions options, FunctionContext functionContext, CancellationToken cancellationToken)
        {
            FunctionsLoggerFactory.Instance.SetCurrentLogger(functionContext.GetLogger("NServiceBus"));

            await InitializeEndpointIfNecessary(functionContext, cancellationToken).ConfigureAwait(false);
            await endpoint.Send(message, options, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task Send(object message, FunctionContext functionContext, CancellationToken cancellationToken)
            => Send(message, new SendOptions(), functionContext, cancellationToken);

        /// <inheritdoc />
        public async Task Send<T>(Action<T> messageConstructor, SendOptions options, FunctionContext functionContext, CancellationToken cancellationToken)
        {
            FunctionsLoggerFactory.Instance.SetCurrentLogger(functionContext.GetLogger("NServiceBus"));

            await InitializeEndpointIfNecessary(functionContext, cancellationToken).ConfigureAwait(false);
            await endpoint.Send(messageConstructor, options, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task Send<T>(Action<T> messageConstructor, FunctionContext functionContext, CancellationToken cancellationToken)
            => Send(messageConstructor, new SendOptions(), functionContext, cancellationToken);

        /// <inheritdoc />
        public async Task Publish(object message, PublishOptions options, FunctionContext functionContext, CancellationToken cancellationToken)
        {
            FunctionsLoggerFactory.Instance.SetCurrentLogger(functionContext.GetLogger("NServiceBus"));

            await InitializeEndpointIfNecessary(functionContext, cancellationToken).ConfigureAwait(false);
            await endpoint.Publish(message, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task Publish(object message, FunctionContext functionContext, CancellationToken cancellationToken)
            => Publish(message, new PublishOptions(), functionContext, cancellationToken);

        /// <inheritdoc />
        public async Task Publish<T>(Action<T> messageConstructor, PublishOptions options, FunctionContext functionContext, CancellationToken cancellationToken)
        {
            FunctionsLoggerFactory.Instance.SetCurrentLogger(functionContext.GetLogger("NServiceBus"));

            await InitializeEndpointIfNecessary(functionContext, cancellationToken).ConfigureAwait(false);
            await endpoint.Publish(messageConstructor, options, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task Publish<T>(Action<T> messageConstructor, FunctionContext functionContext, CancellationToken cancellationToken)
            => Publish(messageConstructor, new PublishOptions(), functionContext, cancellationToken);

        /// <inheritdoc />
        public async Task Subscribe(Type eventType, SubscribeOptions options, FunctionContext functionContext, CancellationToken cancellationToken)
        {
            FunctionsLoggerFactory.Instance.SetCurrentLogger(functionContext.GetLogger("NServiceBus"));

            await InitializeEndpointIfNecessary(functionContext, cancellationToken).ConfigureAwait(false);
            await endpoint.Subscribe(eventType, options, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task Subscribe(Type eventType, FunctionContext functionContext, CancellationToken cancellationToken)
            => Subscribe(eventType, new SubscribeOptions(), functionContext, cancellationToken);

        /// <inheritdoc />
        public async Task Unsubscribe(Type eventType, UnsubscribeOptions options, FunctionContext functionContext, CancellationToken cancellationToken)
        {
            FunctionsLoggerFactory.Instance.SetCurrentLogger(functionContext.GetLogger("NServiceBus"));

            await InitializeEndpointIfNecessary(functionContext, cancellationToken).ConfigureAwait(false);
            await endpoint.Unsubscribe(eventType, options, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task Unsubscribe(Type eventType, FunctionContext functionContext, CancellationToken cancellationToken)
            => Unsubscribe(eventType, new UnsubscribeOptions(), functionContext, cancellationToken);

        readonly Func<FunctionContext, Task<IEndpointInstance>> endpointFactory;

        readonly SemaphoreSlim semaphoreLock = new SemaphoreSlim(initialCount: 1, maxCount: 1);
        RabbitMQTriggeredEndpointConfiguration configuration;

        PipelineInvoker pipeline;
        IEndpointInstance endpoint;
        MessageConverter messageConverter;
    }
}