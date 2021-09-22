using System;
using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Streams.Kafka.Helpers;
using Akka.Streams.Kafka.Messages;
using Confluent.Kafka;
using Google.Protobuf;
using OpenTracing;
using OpenTracing.Propagation;
using Petabridge.Tracing.Kafka.Proto;

namespace Petabridge.Tracing.Kafka
{
    public static class PhobosSource
    {
        public static SourceWithContext<ITracer, T, IActorRef> ActorRef<T>(int bufferSize, OverflowStrategy overflowStrategy)
        {
            if (bufferSize < 0) throw new ArgumentException("Buffer size must be greater than or equal 0", nameof(bufferSize));
            if (overflowStrategy == OverflowStrategy.Backpressure) throw new NotSupportedException("Backpressure overflow strategy is not supported");

            var flow = new Source<(T, ITracer), IActorRef>(
                new PhobosActorRefSource<T>(
                    bufferSize,
                    overflowStrategy, 
                    Attributes.CreateName("phobosActorRefSource"), 
                    Source.Shape<(T, ITracer)>("PhobosActorRefSource")));
            
            return new SourceWithContext<ITracer, T, IActorRef>(flow.Select(x => x));
        }

        public static Source<ProducerRecord<TKey, TValue>, IActorRef> WithTracing<TKey, TValue>(
            this SourceWithContext<ITracer, ProducerRecord<TKey, TValue>, IActorRef> source, ActorSystem system)
        {
            var logger = Logging.GetLogger(system, source);
            
            return source
                .AsSource()
                .Select(elem =>
                {
                    var (record, tracer) = elem;
                    if (tracer?.ActiveSpan?.Context == null)
                        return record;

                    var spanContext = tracer.ActiveSpan.Context;
                    var spanContextProto = new SpanContextProto();
                    try
                    {
                        tracer.Inject(spanContext, BuiltinFormats.TextMap, new TextMapInjectAdapter(spanContextProto.TextFormat));
                    }
                    catch (Exception)
                    {
                        logger.Warning($"Failed to serialize SpanContext. Tracer {tracer.GetType()} does not support TextMap inject.");
                        return record;
                    }

                    var payload = spanContextProto.ToByteArray();
                    record.Message.Headers ??= new Headers();
                    record.Message.Headers.Add("spanContext", payload);

                    return record;
                });
        }
        
        public static Source<CommittableMessage<TKey, TValue>, IControl> WithTracing<TKey, TValue>(
            this Source<CommittableMessage<TKey, TValue>, IControl> source, ITracer tracer, ActorSystem system)
        {
            var logger = Logging.GetLogger(system, source);
            
            return source.Select(msg =>
            {
                if (!msg.Record.Message.Headers.TryGetLastBytes("spanContext", out var contextPayload)) 
                    return msg;

                var spanContextProto = SpanContextProto.Parser.ParseFrom(contextPayload);
                var extractor = new TextMapExtractAdapter(spanContextProto.TextFormat);
                try
                {
                    var spanContext = tracer.Extract(BuiltinFormats.TextMap, extractor);

                    tracer.BuildSpan("kafka-consumer-receive")
                        .AsChildOf(spanContext)
                        .StartActive();
                }
                catch (Exception)
                {
                    logger.Warning($"Failed to deserialize SpanContext. Tracer {tracer.GetType()} does not support TextMap extract");
                }
                
                return msg;
            });
        }
        
    }
}