namespace NServiceBus
{
    using System;
    using System.Threading.Tasks;
    using AzureFunctions.Worker.ServiceBus;
    using Configuration.AdvancedExtensibility;
    using Logging;
    using Microsoft.Extensions.Configuration;
    using Serialization;

    /// <summary>
    /// Represents a serverless NServiceBus endpoint.
    /// </summary>
    public class RabbitMQTriggeredEndpointConfiguration
    {
        static RabbitMQTriggeredEndpointConfiguration()
        {
            LogManager.UseFactory(FunctionsLoggerFactory.Instance);
        }

        /// <summary>
        /// The Azure Service Bus transport configuration.
        /// </summary>
        public RabbitMQTransport Transport { get; }

        /// <summary>
        /// The routing configuration.
        /// </summary>
        public RoutingSettings<RabbitMQTransport> Routing { get; }

        /// <summary>
        /// Gives access to the underlying endpoint configuration for advanced configuration options.
        /// </summary>
        public EndpointConfiguration AdvancedConfiguration { get; }

        /// <summary>
        /// Creates a serverless NServiceBus endpoint.
        /// </summary>
        internal RabbitMQTriggeredEndpointConfiguration(string endpointName, IConfiguration configuration = null, string connectionString = default)
        {
            var endpointConfiguration = new EndpointConfiguration(endpointName);

            var recoverability = endpointConfiguration.Recoverability();
            recoverability.Immediate(settings => settings.NumberOfRetries(5));
            recoverability.Delayed(settings => settings.NumberOfRetries(3));
            recoverabilityPolicy.SendFailedMessagesToErrorQueue = true;
            endpointConfiguration.Recoverability().CustomPolicy(recoverabilityPolicy.Invoke);

            // Disable diagnostics by default as it will fail to create the diagnostics file in the default path.
            // Can be overriden by ServerlessEndpointConfiguration.LogDiagnostics().
            endpointConfiguration.CustomDiagnosticsWriter((_, __) => Task.CompletedTask);

            // 'WEBSITE_SITE_NAME' represents an Azure Function App and the environment variable is set when hosting the function in Azure.
            var functionAppName = GetConfiguredValueOrFallback(configuration, "WEBSITE_SITE_NAME", true) ?? Environment.MachineName;
            endpointConfiguration.UniquelyIdentifyRunningInstance()
                .UsingCustomDisplayName(functionAppName)
                .UsingCustomIdentifier(DeterministicGuid.Create(functionAppName));

            var licenseText = GetConfiguredValueOrFallback(configuration, "NSERVICEBUS_LICENSE", optional: true);
            if (!string.IsNullOrWhiteSpace(licenseText))
            {
                endpointConfiguration.License(licenseText);
            }

            if (connectionString == null)
            {
                connectionString = GetConfiguredValueOrFallback(configuration, DefaultServiceBusConnectionName, optional: true);

                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new Exception($@"RabbitMQ connection string has not been configured. Specify a connection string through IConfiguration, an environment variable named {DefaultServiceBusConnectionName} or passing it to `UseNServiceBus(ENDPOINTNAME,CONNECTIONSTRING)`");
                }
            }

            Transport = new RabbitMQTransport(Topology.Conventional, connectionString);

            serverlessTransport = new ServerlessTransport(Transport);
            var routing = endpointConfiguration.UseTransport(serverlessTransport);
            // "repack" settings to expected transport type settings:
            Routing = new RoutingSettings<RabbitMQTransport>(routing.GetSettings());

            endpointConfiguration.UseSerialization<NewtonsoftSerializer>();

            AdvancedConfiguration = endpointConfiguration;
        }

        static string GetConfiguredValueOrFallback(IConfiguration configuration, string key, bool optional)
        {
            if (configuration != null)
            {
                var configuredValue = configuration.GetValue<string>(key);
                if (configuredValue != null)
                {
                    return configuredValue;
                }
            }

            var environmentVariable = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrEmpty(environmentVariable) && !optional)
            {
                throw new Exception($"Configuration or environment value for '{key}' was not set or was empty.");
            }
            return environmentVariable;
        }

        internal PipelineInvoker PipelineInvoker => serverlessTransport.PipelineInvoker;


        /// <summary>
        /// Define the serializer to be used.
        /// </summary>
        public SerializationExtensions<T> UseSerialization<T>() where T : SerializationDefinition, new()
        {
            return AdvancedConfiguration.UseSerialization<T>();
        }

        /// <summary>
        /// Disables moving messages to the error queue even if an error queue name is configured.
        /// </summary>
        public void DoNotSendMessagesToErrorQueue()
        {
            recoverabilityPolicy.SendFailedMessagesToErrorQueue = false;
        }

        /// <summary>
        /// Logs endpoint diagnostics information to the log. Diagnostics are logged on level <see cref="LogLevel.Info" />.
        /// </summary>
        public void LogDiagnostics()
        {
            AdvancedConfiguration.CustomDiagnosticsWriter((diagnostics, _) =>
            {
                LogManager.GetLogger("StartupDiagnostics").Info(diagnostics);
                return Task.CompletedTask;
            });
        }

        ServerlessTransport serverlessTransport;
        readonly ServerlessRecoverabilityPolicy recoverabilityPolicy = new ServerlessRecoverabilityPolicy();
        internal const string DefaultServiceBusConnectionName = "AzureWebJobsServiceBus";
    }
}
