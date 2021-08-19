using System;
using Akka.Actor;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Streams.Implementation.Stages;
using Akka.Streams.Kafka.Messages;
using Confluent.Kafka;
using OpenTracing;
using Phobos.Tracing;

namespace Petabridge.Phobos.Kafka.Producer
{
    public static class PhobosSource
    {
        public static SourceWithContext<ISpanContext, T, IActorRef> ActorRef<T>(int bufferSize, OverflowStrategy overflowStrategy)
        {
            if (bufferSize < 0) throw new ArgumentException("Buffer size must be greater than or equal 0", nameof(bufferSize));
            if (overflowStrategy == OverflowStrategy.Backpressure) throw new NotSupportedException("Backpressure overflow strategy is not supported");

            var flow = new Source<(T, ISpanContext), IActorRef>(
                new PhobosActorRefSource<T>(
                    bufferSize,
                    overflowStrategy, 
                    Attributes.CreateName("phobosActorRefSource"), 
                    Source.Shape<(T, ISpanContext)>("PhobosActorRefSource")));
            
            return new SourceWithContext<ISpanContext, T, IActorRef>(flow.Select(x => x));
        }

        public static Source<ProducerRecord<TKey, TValue>, IActorRef> WithTracing<TKey, TValue>(
            this SourceWithContext<ISpanContext, ProducerRecord<TKey, TValue>, IActorRef> source, ActorSystem system)
        {
            return source
                .AsSource()
                .Select(elem =>
                {
                    var (record, spanContext) = elem;
                    if (spanContext == null)
                        return record;

                    var serializer = system.Serialization.FindSerializerForType(typeof(SpanEnvelope));
                    var envelope = new SpanEnvelope("", spanContext, false);
                    var serialized = serializer.ToBinary(envelope);

                    record.Message.Headers ??= new Headers();
                    record.Message.Headers.Add("spanContext", serialized);
                    return record;
                });
        }
    }
}