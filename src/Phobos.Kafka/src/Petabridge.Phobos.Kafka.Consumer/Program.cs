using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Confluent.Kafka;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Petabridge.Phobos.Kafka.Consumer
{
    class Program
    {
        public static async Task Main(string[] args)
        {
            var kafkaHost = Environment.GetEnvironmentVariable(AkkaService.KafkaServiceHost);
            var kafkaPort = Environment.GetEnvironmentVariable(AkkaService.KafkaServicePort);
            var bootstrapServer = $"{kafkaHost}:{kafkaPort}";
            
            await WaitUntilKafkaIsReady(bootstrapServer);
            await CreateHostBuilder(args).Build().RunAsync();
        }
        
        private static async Task WaitUntilKafkaIsReady(string bootstrapServer)
        {
            var builder = new AdminClientBuilder(new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("bootstrap.servers", bootstrapServer)
            });
            using (var client = builder.Build())
            {
                var connected = false;
                while (!connected)
                {
                    try
                    {
                        await Task.Delay(1000);
                        Console.WriteLine("Trying to connect to kafka");
                        connected = true;
                        client.GetMetadata("demo", TimeSpan.FromSeconds(5));
                    }
                    catch
                    {
                        connected = false;
                    }
                }
                Console.WriteLine("Kafka is now available");
            }
        }

        public static IHostBuilder CreateHostBuilder(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
                .ConfigureSerilogLogging()
                .ConfigureWebHostDefaults(webBuilder => { webBuilder.UseStartup<Startup>(); });
        }
    }
}