using Microsoft.Extensions.Hosting;
using NServiceBus;

[assembly: NServiceBusTriggerFunction("ManualTestsV4Host")]

public class Program
{
    public static void Main()
    {
        var host = new HostBuilder()
            .ConfigureFunctionsWorkerDefaults()
            .UseNServiceBus("ManualTestsV4Host", "host=localhost")
            .Build();

        host.Run();
    }
}