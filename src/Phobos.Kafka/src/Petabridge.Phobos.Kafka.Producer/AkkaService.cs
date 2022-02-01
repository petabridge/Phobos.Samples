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
using Akka.Routing;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Streams.Kafka.Dsl;
using Akka.Streams.Kafka.Messages;
using Akka.Streams.Kafka.Settings;
using Akka.Util;
using App.Metrics.Timer;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using OpenTracing;
using Petabridge.Cmd.Cluster;
using Petabridge.Cmd.Host;
using Petabridge.Cmd.Remote;
using Phobos.Actor;
using Phobos.Tracing;
using Phobos.Tracing.Serialization;
using SerilogLogMessageFormatter = Akka.Logger.Serilog.SerilogLogMessageFormatter;

namespace Petabridge.Phobos.Kafka.Producer
{
    public sealed class ChildActor : ReceiveActor
    {
        private readonly ILoggingAdapter _log = Context.GetLogger();

        public ChildActor()
        {
            ReceiveAny(_ =>
            {
                _log.Info("[Producer] Received: {0}", _);
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
        private readonly IActorRef _producer;

        public ConsoleActor(IActorRef producer)
        {
            _producer = producer;
            
            Receive<string>(_ =>
            {
                // use the local metrics handle to record a timer duration for how long this block of code takes to execute
                Context.GetInstrumentation().Monitor.Timer.Time(new TimerOptions {Name = "ProcessingTime"}, () =>
                {
                    // start another span programmatically inside actor
                    using (var newSpan = Context.GetInstrumentation().Tracer.BuildSpan("Producer_SecondOp").StartActive())
                    {
                        var spanContext = Context.GetInstrumentation().ActiveSpan?.Context;
                        _producer.Forward(Tuple.Create(spanContext, _));
                        
                        var child = Context.ActorOf(Props.Create(() => new ChildActor()));
                        _log.Info("[Producer] Spawned {child}", child);

                        child.Forward(_);
                    }
                });
            });
        }
    }

    /// <summary>
    ///     To add some color to the traces
    /// </summary>
    public sealed class RouterForwarderActor : ReceiveActor
    {
        private readonly ILoggingAdapter _log = Context.GetLogger();
        private readonly IActorRef _routerActor;

        public RouterForwarderActor(IActorRef routerActor)
        {
            _routerActor = routerActor;
            
            Receive<string>(_ =>
            {
                _log.Info("[Producer] Received: {0}", _);
                _routerActor.Forward(_);
            });
        }
    }

    /// <summary>
    ///     Container for retaining actors
    /// </summary>
    public sealed class AkkaActors
    {
        public const string KafkaServiceHost = "KAFKA_SERVICE_HOST";
        public const string KafkaServicePort = "KAFKA_SERVICE_PORT";
        
        private const string Topic = "demo";

        public AkkaActors(ActorSystem sys)
        {
            Sys = sys;
            RouterActor = sys.ActorOf(Props.Empty.WithRouter(FromConfig.Instance), "echo");
            
            var log = Logging.GetLogger(sys, "KafkaProducerStream");
            
            var kafkaHost = Environment.GetEnvironmentVariable(KafkaServiceHost);
            var kafkaPort = Environment.GetEnvironmentVariable(KafkaServicePort);
            var bootstrapServer = $"{kafkaHost}:{kafkaPort}";
            var producerSettings = ProducerSettings<Null, string>.Create(sys, null, null)
                .WithBootstrapServers(bootstrapServer);

            ProducerActor = Source.ActorRef<Tuple<ISpanContext, string>>(128, OverflowStrategy.DropNew)
                .Select(tuple =>
                {
                    var (spanContext, message) = tuple;
                    var record = new ProducerRecord<Null, string>(Topic, null, message).WithTracing(sys, spanContext);
                    return ProducerMessage.Single(record);
                })
                .Via(KafkaProducer.FlexiFlow<Null, string, NotUsed>(producerSettings))
                .ToMaterialized(Sink.ForEach<IResults<Null, string, NotUsed>>(result =>
                {
                    var metadata = ((Result<Null, string, NotUsed>) result).Metadata;
                    log.Info($"[Producer] {metadata.Topic}/{metadata.Partition} {metadata.Offset} received commit: {metadata.Value}");
                }), Keep.Left)
                .Run(sys.Materializer());
            
            ConsoleActor = sys.ActorOf(Props.Create(() => new ConsoleActor(ProducerActor)), "console");
            RouterForwarderActor = sys.ActorOf(Props.Create(() => new RouterForwarderActor(RouterActor)), "fwd");
        }

        internal ActorSystem Sys { get; }

        public IActorRef ConsoleActor { get; }

        internal IActorRef RouterActor { get; }

        public IActorRef RouterForwarderActor { get; }
        
        public IActorRef ProducerActor { get; }

        private Source<IResults<TKey, TValue, NotUsed>, IActorRef> ProducerActorRefWithCommit<TKey, TValue>(
            ActorSystem system, 
            string topic, 
            ProducerSettings<TKey, TValue> producerSettings)
        {
            return Source.ActorRef<(ISpanContext spanContext, TKey key, TValue message)>(128, OverflowStrategy.DropNew)
                .Select(tuple =>
                {
                    var (spanContext, key, message) = tuple;
                    var record = new ProducerRecord<TKey, TValue>(topic, key, message).WithTracing(system, spanContext);
                    return ProducerMessage.Single(record);
                })
                .Via(KafkaProducer.FlexiFlow<TKey, TValue, NotUsed>(producerSettings));
        }
        
        private Source<IResults<TKey, TValue, NotUsed>, IActorRef> ProducerActorRefWithCommit<TKey, TValue>(
            ActorSystem system, 
            ProducerSettings<TKey, TValue> producerSettings)
        {
            return Source.ActorRef<(ISpanContext spanContext, ProducerRecord<TKey, TValue> record)>(128, OverflowStrategy.DropNew)
                .Select(tuple =>
                {
                    var (spanContext, record) = tuple;
                    return ProducerMessage.Single(record.WithTracing(system, spanContext));
                })
                .Via(KafkaProducer.FlexiFlow<TKey, TValue, NotUsed>(producerSettings));
        }
        
        private IActorRef ProducerActorRefWithoutCommit<TKey, TValue>(
            ActorSystem system, 
            string topic, 
            ProducerSettings<TKey, TValue> producerSettings)
        {
            return Source.ActorRef<(ISpanContext spanContext, TKey key, TValue message)>(128, OverflowStrategy.DropNew)
                .Select(tuple =>
                {
                    var (spanContext, key, message) = tuple;
                    return new ProducerRecord<TKey, TValue>(topic, key, message).WithTracing(system, spanContext);
                })
                .ToMaterialized(KafkaProducer.PlainSink(producerSettings), Keep.Left)
                .Run(system.Materializer());
        }
        
        private IActorRef ProducerActorRefWithoutCommit<TKey, TValue>(
            ActorSystem system, 
            ProducerSettings<TKey, TValue> producerSettings)
        {
            return Source.ActorRef<(ProducerRecord<TKey, TValue> message, ISpanContext spanContext)>(128, OverflowStrategy.DropNew)
                .Select(t => t.message.WithTracing(system, t.spanContext))
                .ToMaterialized(KafkaProducer.PlainSink(producerSettings), Keep.Left)
                .Run(system.Materializer());
        }
        
    }

    public class AkkaService : IHostedService
    {
        private readonly AkkaActors _actors;

        public AkkaService(AkkaActors actors, IServiceProvider services)
        {
            _actors = actors;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // start https://cmd.petabridge.com/ for diagnostics and profit
            var pbm = PetabridgeCmd.Get(_actors.Sys); // start Pbm
            pbm.RegisterCommandPalette(ClusterCommands.Instance);
            pbm.RegisterCommandPalette(RemoteCommands.Instance);
            pbm.Start(); // begin listening for PBM management commands

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return CoordinatedShutdown.Get(_actors.Sys).Run(CoordinatedShutdown.ClrExitReason.Instance);
        }
    }

    public static class ProducerRecordExtensions
    {
        public static ProducerRecord<TKey, TValue> WithTracing<TKey, TValue>(
            this ProducerRecord<TKey, TValue> record,
            ActorSystem system,
            ISpanContext spanContext)
        {
            if (spanContext == null)
                return record;
            
            var serializer = system.Serialization.FindSerializerForType(typeof(SpanEnvelope));
            var envelope = new SpanEnvelope("", spanContext, false);
            var serialized = serializer.ToBinary(envelope);

            record.Message.Headers ??= new Headers();
            record.Message.Headers.Add("spanContext", serialized);
            return record;
        }
    }
    
    public static class ProducerMessageWithTracing
    {
        public static IEnvelope<TKey, TValue, TPassThrough> Single<TKey, TValue, TPassThrough>(
            ProducerRecord<TKey, TValue> record,
            ActorSystem system,
            ISpanContext spanContext,
            TPassThrough passThrough)
        {
            if (spanContext == null)
                return new Message<TKey, TValue, TPassThrough>(record, passThrough);
            
            var serializer = system.Serialization.FindSerializerForType(typeof(SpanEnvelope));
            var envelope = new SpanEnvelope("", spanContext, false);
            var serialized = serializer.ToBinary(envelope);

            record.Message.Headers ??= new Headers();
            record.Message.Headers.Add("spanContext", serialized);
            return new Message<TKey, TValue, TPassThrough>(record, passThrough);
        }

        public static IEnvelope<TKey, TValue, NotUsed> Single<TKey, TValue>(
            ProducerRecord<TKey, TValue> record,
            ActorSystem system,
            ISpanContext spanContext)
            => Single(record, system, spanContext, NotUsed.Instance);

    }
}