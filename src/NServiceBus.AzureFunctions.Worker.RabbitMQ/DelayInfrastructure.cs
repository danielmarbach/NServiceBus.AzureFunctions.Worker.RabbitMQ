namespace NServiceBus.AzureFunctions.Worker.RabbitMQ
{
    static class DelayInfrastructure
    {
        public const string DelayHeader = "NServiceBus.Transport.RabbitMQ.DelayInSeconds";
        public const string XDeathHeader = "x-death";
        public const string XFirstDeathExchangeHeader = "x-first-death-exchange";
        public const string XFirstDeathQueueHeader = "x-first-death-queue";
        public const string XFirstDeathReasonHeader = "x-first-death-reason";
    }
}