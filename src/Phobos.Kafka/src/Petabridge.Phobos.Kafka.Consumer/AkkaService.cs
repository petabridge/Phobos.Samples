// -----------------------------------------------------------------------
// <copyright file="AkkaService.cs" company="Petabridge, LLC">
//      Copyright (C) 2015 - 2021 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using Akka;
using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Streams.Kafka.Dsl;
using Akka.Streams.Kafka.Helpers;
using Akka.Streams.Kafka.Messages;
using Akka.Streams.Kafka.Settings;
using App.Metrics.Timer;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTracing;
using Petabridge.Cmd.Cluster;
using Petabridge.Cmd.Host;
using Petabridge.Cmd.Remote;
using Phobos.Actor;
using Phobos.Tracing;
using Phobos.Tracing.Serialization;
using SerilogLogMessageFormatter = Akka.Logger.Serilog.SerilogLogMessageFormatter;

namespace Petabridge.Phobos.Kafka.Consumer
{
    public sealed class ChildActor : ReceiveActor
    {
        private readonly ILoggingAdapter _log = Context.GetLogger();

        public ChildActor()
        {
            ReceiveAny(_ =>
            {
                _log.Info("Consumer child received: {0}", _);
                Sender.Tell(_);
                Self.Tell(PoisonPill.Instance);
            });
        }

        protected override void PreRestart(Exception reason, object message)
        {
            // re-send the message that caused us to crash so we can reprocess
            Self.Tell(message, Sender);
        }
    }

    public sealed class ConsoleActor : ReceiveActor
    {
        private readonly ILoggingAdapter _log = Context.GetLogger(SerilogLogMessageFormatter.Instance);

        public ConsoleActor()
        {
            Receive<string>(_ =>
            {
                // use the local metrics handle to record a timer duration for how long this block of code takes to execute
                Context.GetInstrumentation().Monitor.Timer.Time(new TimerOptions {Name = "ProcessingTime"}, () =>
                {
                    // start another span programmatically inside actor
                    using (var newSpan = Context.GetInstrumentation().Tracer.BuildSpan("Consumer_SecondOp").StartActive())
                    {
                        var child = Context.ActorOf(Props.Create(() => new ChildActor()));
                        child.Forward(_);
                    }
                });
            });
        }
    }

    /// <summary>
    ///     Container for retaining actors
    /// </summary>
    public sealed class AkkaActors
    {
        public AkkaActors(ActorSystem sys)
        {
            Sys = sys;
            ConsoleActor = sys.ActorOf(Props.Create(() => new ConsoleActor()), "console");
        }

        internal ActorSystem Sys { get; }

        public IActorRef ConsoleActor { get; }
    }

    public class AkkaService : IHostedService
    {
        public const string KafkaServiceHost = "KAFKA_SERVICE_HOST";
        public const string KafkaServicePort = "KAFKA_SERVICE_PORT";
        
        private const string Topic = "demo";
        private const string ConsumerGroup = "group";
        
        private readonly ILogger<AkkaService> _logger;
        private readonly AkkaActors _actors;
        private readonly ITracer _tracer;
        
        private ILoggingAdapter _log;
        private TraceEnvelopeSerializer _serializer;
        private DrainingControl<NotUsed> _kafkaControl;

        public AkkaService(AkkaActors actors, IServiceProvider services, ILogger<AkkaService> logger, ITracer tracer)
        {
            _logger = logger;
            _actors = actors;
            _tracer = tracer;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var system = _actors.Sys;
            _serializer = (TraceEnvelopeSerializer) system.Serialization.FindSerializerForType(typeof(SpanEnvelope));
            _log = Logging.GetLogger(system, typeof(AkkaService));
            
            // start https://cmd.petabridge.com/ for diagnostics and profit
            var pbm = PetabridgeCmd.Get(system); // start Pbm
            pbm.RegisterCommandPalette(RemoteCommands.Instance);
            pbm.RegisterCommandPalette(ClusterCommands.Instance);
            pbm.Start(); // begin listening for PBM management commands

            var kafkaHost = Environment.GetEnvironmentVariable(KafkaServiceHost);
            var kafkaPort = Environment.GetEnvironmentVariable(KafkaServicePort);
            var bootstrapServer = $"{kafkaHost}:{kafkaPort}";

            var consumerSettings = ConsumerSettings<Null, string>.Create(system, null, null)
                .WithBootstrapServers(bootstrapServer)
                .WithGroupId(ConsumerGroup);

            var subscription = Subscriptions.Topics(Topic);

            var committerDefaults = CommitterSettings.Create(system);

            _kafkaControl = KafkaConsumer.CommittableSource(consumerSettings, subscription)
                .WithTracing(system, _tracer)
                .SelectAsync(1, msg => 
                    Business(msg.Record).ContinueWith(done => (ICommittable) msg.CommitableOffset))
                .ToMaterialized(
                    Committer.Sink(committerDefaults.WithMaxBatch(1)), 
                    (ctrl, task) => DrainingControl<NotUsed>.Create((ctrl, task)))
                .Run(system.Materializer());
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _kafkaControl.DrainAndShutdown();
            await CoordinatedShutdown.Get(_actors.Sys).Run(CoordinatedShutdown.ClrExitReason.Instance);
        }
        
        private async Task Business(ConsumeResult<Null, string> record)
        {
            _logger.LogInformation(
                "[Consumer] {ConsumerKafkaTopic}/{ConsumerKafkaPartition} {ConsumerKafkaOffset} received: [{ConsumerKafkaMessage}]", 
                record.Topic, 
                record.Partition,
                record.Offset,
                record.Message.Value);

            var message = record.Message;
            var response = await _actors.ConsoleActor.Ask<string>($"hit from {message.Value}", TimeSpan.FromSeconds(5));
            _logger.LogInformation("[Consumer] Child response: [{ConsumerChildResponse}]", response);
        }
    }

    public static class KafkaExtensions
    {
        public static Source<CommittableMessage<TKey, TValue>, IControl> WithTracing<TKey, TValue>(
            this Source<CommittableMessage<TKey, TValue>, IControl> source, 
            ActorSystem system,
            ITracer tracer)
        {
            return source.Select(msg =>
            {
                if (msg.Record.Message.Headers.TryGetLastBytes("spanContext", out var contextPayload))
                {
                    var serializer =
                        (TraceEnvelopeSerializer) system.Serialization.FindSerializerForType(typeof(SpanEnvelope));
                    var envelope =
                        (SpanEnvelope) serializer.FromBinary(contextPayload, TraceEnvelopeSerializer.WithTraceManifest);
                    var activeContext = envelope.ActiveSpan;

                    tracer.BuildSpan("kafka-consumer-receive")
                        .AsChildOf(activeContext)
                        .StartActive();
                }

                return msg;
            });
        }
    }
}