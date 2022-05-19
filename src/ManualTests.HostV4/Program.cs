using Microsoft.Extensions.Hosting;
using NServiceBus;

[assembly: NServiceBusTriggerFunction("ManualTestsV4Host")]

public class Program
{
    public static void Main()
    {
        var host = new HostBuilder()
            .ConfigureFunctionsWorkerDefaults()
            .UseNServiceBus("ManualTestsV4Host", "amqp://user:PASSWORD@192.168.0.15:5672")
            .Build();

        host.Run();
    }
}