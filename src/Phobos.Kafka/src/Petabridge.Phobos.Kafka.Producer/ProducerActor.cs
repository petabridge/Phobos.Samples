using System;
using System.Collections.Generic;
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
using Phobos.Tracing.Serialization;
using Reactive.Streams;

namespace Petabridge.Phobos.Kafka.Producer
{
    public class ProducerActor : ReceiveActor, IPublisher<(ISpanContext spanContext, string message)>
    {
        public const string KafkaServiceHost = "KAFKA_SERVICE_HOST";
        public const string KafkaServicePort = "KAFKA_SERVICE_PORT";
        
        private const string Topic = "demo";

        private readonly List<ISubscriber<(ISpanContext, string)>> _subscribers;

        public ProducerActor()
        {
            _subscribers = new List<ISubscriber<(ISpanContext, string)>>();
            
            var log = Context.GetLogger();
            
            var kafkaHost = Environment.GetEnvironmentVariable(KafkaServiceHost);
            var kafkaPort = Environment.GetEnvironmentVariable(KafkaServicePort);
            var bootstrapServer = $"{kafkaHost}:{kafkaPort}";
            
            var serializer = (TraceEnvelopeSerializer)Context.System.Serialization.FindSerializerForType(typeof(SpanEnvelope));
            var producerSettings = ProducerSettings<Null, string>.Create(Context.System, null, null)
                .WithBootstrapServers(bootstrapServer);

            var source = Source.FromPublisher(this)
                .Select(elem =>
                {
                    var record = new ProducerRecord<Null, string>(Topic, elem.message);
                    var spanContext = elem.spanContext;
                    if (spanContext != null)
                    {
                        try
                        {
                            var envelope = new SpanEnvelope("", spanContext, false);
                            var serialized = serializer.ToBinary(envelope);
                            
                            record.Message.Headers ??= new Headers();
                            record.Message.Headers.Add("spanContext", serialized);
                        }
                        catch (Exception e)
                        {
                            log.Error(e, "[Producer] Failed to extract active span context: {0}", e.Message);
                        }
                    }
                    return ProducerMessage.Single(record);
                })
                .Via(KafkaProducer.FlexiFlow<Null, string, NotUsed>(producerSettings))
                .Select(result =>
                {
                    var response = (Result<Null, string, NotUsed>) result;
                    log.Info(
                        $"[Producer] {response.Metadata.Topic}/{response.Metadata.Partition} {response.Metadata.Offset} received commit: {response.Metadata.Value}");
                    return result;
                });
            
            source
                .ToMaterialized(Sink.Ignore<IResults<Null, string, NotUsed>>(), Keep.None)
                .Run(Context.System.Materializer());

            Receive<string>(msg =>
            {
                log.Info($"[Producer] Sending {msg} to Kafka.");

                var spanContext = Context.GetInstrumentation().ActiveSpan?.Context;
                foreach (var subscriber in _subscribers)
                {
                    subscriber.OnNext((spanContext, msg));
                }
            });
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
    }
}