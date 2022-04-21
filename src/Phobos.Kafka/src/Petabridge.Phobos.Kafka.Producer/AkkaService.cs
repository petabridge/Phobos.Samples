﻿// -----------------------------------------------------------------------
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
using Akka.Util.Internal;
using App.Metrics.Timer;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using OpenTracing;
using Petabridge.Cmd.Cluster;
using Petabridge.Cmd.Host;
using Petabridge.Cmd.Remote;
using Petabridge.Tracing.Kafka;
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
                _log.Info("[Producer][ChildActor] Received: {0}", _);
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
        private readonly AtomicCounter _counter;

        public ConsoleActor(IActorRef producer)
        {
            _producer = producer;
            _counter = new AtomicCounter();
            
            Receive<string>(message =>
            {
                // use the local metrics handle to record a timer duration for how long this block of code takes to execute
                Context.GetInstrumentation().Monitor.Timer.Time(new TimerOptions {Name = "ProcessingTime"}, () =>
                {
                    // start another span programmatically inside actor
                    using (var newSpan = Context.GetInstrumentation().Tracer.BuildSpan("Producer_SecondOp").StartActive())
                    {
                        _producer.Forward(message);

                        var id = _counter.GetAndIncrement();
                        var child = Context.ActorOf(Props.Create(() => new ChildActor()), $"Child_{id}");
                        _log.Info("[Producer][ConsoleActor] Spawned {child}", child);

                        child.Forward(message);
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
                _log.Info("[Producer][RouterForwarderActor] Received: {0}", _);
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
            
            ProducerActor = PhobosSource.ActorRef<string>(128, OverflowStrategy.DropNew)
                .Select(msg => new ProducerRecord<Null, string>(Topic, null, msg))
                .WithTracing(Sys)
                .Select(record => ProducerMessage.Single(record))
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

}