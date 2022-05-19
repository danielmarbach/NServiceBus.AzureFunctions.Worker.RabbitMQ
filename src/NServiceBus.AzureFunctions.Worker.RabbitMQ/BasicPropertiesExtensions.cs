namespace NServiceBus.AzureFunctions.Worker.RabbitMQ
{
    static class BasicPropertiesExtensions
    {
        public const string ConfirmationIdHeader = "NServiceBus.Transport.RabbitMQ.ConfirmationId";
        public const string UseNonPersistentDeliveryHeader = "NServiceBus.Transport.RabbitMQ.UseNonPersistentDelivery";
    }
}