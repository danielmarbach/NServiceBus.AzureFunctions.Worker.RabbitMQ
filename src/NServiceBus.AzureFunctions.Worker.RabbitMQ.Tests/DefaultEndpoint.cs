namespace ServiceBus.Tests
{
    using System;
    using System.Threading.Tasks;
    using Microsoft.Extensions.DependencyInjection;
    using NServiceBus;
    using NServiceBus.AcceptanceTesting.Customization;
    using NServiceBus.AcceptanceTesting.Support;
    using NServiceBus.Transport.RabbitMQ;

    class DefaultEndpoint : IEndpointSetupTemplate
    {
        public Task<EndpointConfiguration> GetConfiguration(
            RunDescriptor runDescriptor,
            EndpointCustomizationConfiguration endpointConfiguration,
            Action<EndpointConfiguration> configurationBuilderCustomization)
        {
            var configuration = new EndpointConfiguration(endpointConfiguration.EndpointName);

            configuration.TypesToIncludeInScan(endpointConfiguration.GetTypesScopedByTestClass());
            configuration.EnableInstallers();

            configuration.RegisterComponents(c => c
                .AddSingleton(runDescriptor.ScenarioContext.GetType(), runDescriptor.ScenarioContext));

            var recoverability = configuration.Recoverability();
            recoverability.Delayed(delayed => delayed.NumberOfRetries(0));
            recoverability.Immediate(immediate => immediate.NumberOfRetries(0));
            configuration.SendFailedMessagesTo("error");

            var connectionString = Environment.GetEnvironmentVariable(RabbitMQTriggeredEndpointConfiguration.DefaultServiceBusConnectionName);
            var rabbitMqTransport = new RabbitMQTransport(new ConventionalRoutingTopology(true), connectionString);
            var transport = configuration.UseTransport(rabbitMqTransport);

            configuration.UseSerialization<NewtonsoftSerializer>();

            configurationBuilderCustomization(configuration);

            return Task.FromResult(configuration);
        }
    }
}