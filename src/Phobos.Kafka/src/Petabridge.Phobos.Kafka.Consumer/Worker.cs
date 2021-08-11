using System;
using System.Threading;
using System.Threading.Tasks;
using Akka;
using Akka.Actor;
using Akka.Event;
using Akka.Serialization;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Streams.Kafka.Dsl;
using Akka.Streams.Kafka.Helpers;
using Akka.Streams.Kafka.Messages;
using Akka.Streams.Kafka.Settings;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTracing;
using OpenTracing.Util;
using Phobos.Tracing;

namespace Petabridge.Phobos.Kafka.Consumer
{
    public class Worker : IHostedService
    {
        private const string KafkaServiceHost = "KAFKA_SERVICE_HOST";
        private const string KafkaServicePort = "KAFKA_SERVICE_PORT";
        
        private const string Topic = "demo";
        private const string ConsumerGroup = "group";
        
        private readonly ILogger<Worker> _logger;
        private readonly AkkaActors _actors;
        private readonly ITracer _tracer;
        
        private ILoggingAdapter _log;
        private Serializer _serializer;
        private DrainingControl<NotUsed> _kafkaControl;
        
        public Worker(ILogger<Worker> logger, AkkaActors actors, ITracer tracer)
        {
            _logger = logger;
            _actors = actors;
            _tracer = tracer;
        }

        private async Task Business(ConsumeResult<Null, string> record)
        {
            var message = record.Message;

            IScope currentScope = null;
            if (message.Headers.TryGetLastBytes("spanContext", out var contextPayload))
            {
                var envelope = _serializer.FromBinary<SpanEnvelope>(contextPayload);
                var activeContext = envelope.ActiveSpan;
                currentScope = _tracer.BuildSpan("kafka-consumer-receive")
                    .AsChildOf(activeContext)
                    .StartActive();
            }
            
            _log.Info(                
                "Consumer: {ConsumerTopic}/{ConsumerPartition} {ConsumerOffset}: {ConsumerKafkaMessage}", 
                record.Topic, 
                record.Partition,
                record.Offset,
                record.Message.Value);

            
            _logger.LogInformation(
                "Consumer: {ConsumerTopic}/{ConsumerPartition} {ConsumerOffset}: {ConsumerKafkaMessage}", 
                record.Topic, 
                record.Partition,
                record.Offset,
                record.Message.Value);
            
            var resp = await _actors.ConsoleActor.Ask<string>($"[Consumer] hit from {message.Value}", TimeSpan.FromSeconds(5));
            _logger.LogWarning("[Consumer] Response is [{Response}]", resp);
            currentScope?.Dispose();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var system = _actors.Sys;
            _serializer = system.Serialization.FindSerializerForType(typeof(SpanEnvelope));
            _log = Logging.GetLogger(system, typeof(Worker));
            
            var materializer = system.Materializer();

            var kafkaHost = Environment.GetEnvironmentVariable(KafkaServiceHost);
            var kafkaPort = Environment.GetEnvironmentVariable(KafkaServicePort);
            
            var consumerSettings = ConsumerSettings<Null, string>.Create(system, null, null)
                .WithBootstrapServers($"{kafkaHost}:{kafkaPort}")
                .WithGroupId(ConsumerGroup);

            var subscription = Subscriptions.Topics(Topic);

            var committerDefaults = CommitterSettings.Create(system);

            _kafkaControl = KafkaConsumer.CommittableSource(consumerSettings, subscription)
                .SelectAsync(1, msg => 
                    Business(msg.Record).ContinueWith(done => (ICommittable) msg.CommitableOffset))
                .ToMaterialized(
                    Committer.Sink(committerDefaults.WithMaxBatch(1)), 
                    (ctrl, task) => DrainingControl<NotUsed>.Create((ctrl, task)))
                .Run(materializer);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _kafkaControl.DrainAndShutdown();
        }
    }
}