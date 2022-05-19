namespace NServiceBus
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using AzureFunctions.Worker.ServiceBus;
    using Extensibility;
    using Microsoft.Azure.Functions.Worker;
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
            BasicDeliverEventArgs args,
            FunctionContext functionContext,
            CancellationToken cancellationToken)
        {
            FunctionsLoggerFactory.Instance.SetCurrentLogger(functionContext.GetLogger("NServiceBus"));

            await InitializeEndpointIfNecessary(functionContext, CancellationToken.None)
                .ConfigureAwait(false);

            await Process(args, pipeline, cancellationToken)
                .ConfigureAwait(false);
        }

        internal static async Task Process(
#pragma warning disable IDE0060
            BasicDeliverEventArgs args,
#pragma warning restore IDE0060
            PipelineInvoker pipeline,
            CancellationToken cancellationToken)
        {
            try
            {
                var messageContext = new MessageContext(null, new Dictionary<string, string>(),
                    ReadOnlyMemory<byte>.Empty, new TransportTransaction(), pipeline.ReceiveAddress, new ContextBag());
                await pipeline.PushMessage(messageContext, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                var errorContext = new ErrorContext(
                    exception,
                    new Dictionary<string, string>(),
                    null,
                    ReadOnlyMemory<byte>.Empty,
                    new TransportTransaction(),
                    0,
                    pipeline.ReceiveAddress,
                    new ContextBag());

                var errorHandleResult = await pipeline.PushFailedMessage(errorContext, cancellationToken).ConfigureAwait(false);

                if (errorHandleResult == ErrorHandleResult.Handled)
                {
                    return;
                }

                throw;
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
    }
}