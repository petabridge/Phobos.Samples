using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Akka;
using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Streams.Kafka.Dsl;
using Akka.Streams.Kafka.Messages;
using Akka.Streams.Kafka.Settings;
using Confluent.Kafka;
using OpenTracing;
using Phobos.Actor;
using Phobos.Tracing;
using Reactive.Streams;

namespace Petabridge.Phobos.Kafka.Producer
{
    public class ProducerActor : ReceiveActor, IPublisher<(ISpanContext spanContext, string message)>, IWithUnboundedStash
    {
        private const string KafkaServiceHost = "KAFKA_SERVICE_HOST";
        private const string KafkaServicePort = "KAFKA_SERVICE_PORT";
        
        private const string Topic = "demo";

        private readonly string _bootstrapServer;
        private readonly ILoggingAdapter _log;
        private readonly List<ISubscriber<(ISpanContext, string)>> _subscribers;

        private Source<IResults<Null, string, NotUsed>, NotUsed> _source; 
        
        public ProducerActor()
        {
            _log = Context.GetLogger();
            _subscribers = new List<ISubscriber<(ISpanContext, string)>>();
            
            var kafkaHost = Environment.GetEnvironmentVariable(KafkaServiceHost);
            var kafkaPort = Environment.GetEnvironmentVariable(KafkaServicePort);
            _bootstrapServer = $"{kafkaHost}:{kafkaPort}";
            
            var serializer = Context.System.Serialization.FindSerializerForType(typeof(SpanEnvelope));
            var producerSettings = ProducerSettings<Null, string>.Create(Context.System, null, null)
                .WithBootstrapServers(_bootstrapServer);

            _source = Source.FromPublisher(this)
                .Select(elem =>
                {
                    var record = new ProducerRecord<Null, string>(Topic, elem.message);
                    var spanContext = elem.spanContext;
                    if (spanContext != null)
                    {
                        _log.Info("Extracting SpanContext");
                        var envelope = new SpanEnvelope("", spanContext, false);
                        var serialized = serializer.ToBinary(envelope);
                        record.Message.Headers.Add("spanContext", serialized);
                    }
                    return ProducerMessage.Single(record);
                })
                .Via(KafkaProducer.FlexiFlow<Null, string, NotUsed>(producerSettings))
                .Select(result =>
                {
                    var response = (Result<Null, string, NotUsed>) result;
                    _log.Info($"Producer: {response.Metadata.Topic}/{response.Metadata.Partition} {response.Metadata.Offset}: {response.Metadata.Value}");
                    return result;
                });
            
            Become(Initializing());
        }

        private Receive Initializing() => msg =>
        {
            switch (msg)
            {
                case Initialized _:
                    _source
                        .ToMaterialized(Sink.Ignore<IResults<Null, string, NotUsed>>(), Keep.None)
                        .Run(Context.System.Materializer());
                    
                    Become(Processing());
                    Stash.UnstashAll();
                    return true;
                
                default:
                    Stash.Stash();
                    return true;
            }
        };

        private Receive Processing() => msg =>
        {
            if (msg is string str)
            {
                _log.Info($"[Producer] Sending {msg} to Kafka.");

                var spanContext = Context.GetInstrumentation().ActiveSpan?.Context;
                foreach (var subscriber in _subscribers)
                {
                    subscriber.OnNext((spanContext, str));
                }

                return true;
            }

            return false;
        };

        public IStash Stash { get; set; }
        
        protected override void PreStart()
        {
            WaitUntilKafkaIsReady(_bootstrapServer).PipeTo(Self);
        }
        
        private async Task<Initialized> WaitUntilKafkaIsReady(string bootstrapServer)
        {
            var builder = new AdminClientBuilder(new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("bootstrap.servers", bootstrapServer)
            });
            var client = builder.Build();

            var connected = false;
            while (!connected)
            {
                try
                {
                    connected = true;
                    client.GetMetadata(Topic, TimeSpan.FromSeconds(5));
                }
                catch
                {
                    connected = false;
                }
            }
            
            return Initialized.Instance;
        }
        
        protected override void PostStop()
        {
            foreach (var subscriber in _subscribers)
            {
                subscriber.OnComplete();
            }
        }

        public void Subscribe(ISubscriber<(ISpanContext, string)> subscriber)
        {
            _subscribers.Add(subscriber);
        }

        internal class Initialized
        {
            public static readonly Initialized Instance = new Initialized();
            private Initialized(){ }
        }
    }
}