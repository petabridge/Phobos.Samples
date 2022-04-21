using Akka.Actor;
using Akka.Streams;
using Akka.Streams.Actors;
using Akka.Streams.Implementation;
using OpenTracing;
using Reactive.Streams;

namespace Petabridge.Tracing.Kafka
{
    public sealed class PhobosActorRefSource<TOut> : SourceModule<(TOut, ITracer), IActorRef>
    {
        private readonly int _bufferSize;
        private readonly OverflowStrategy _overflowStrategy;

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="bufferSize">TBD</param>
        /// <param name="overflowStrategy">TBD</param>
        /// <param name="attributes">TBD</param>
        /// <param name="shape">TBD</param>
        public PhobosActorRefSource(int bufferSize, OverflowStrategy overflowStrategy, Attributes attributes, SourceShape<(TOut, ITracer)> shape) : base(shape)
        {
            _bufferSize = bufferSize;
            _overflowStrategy = overflowStrategy;
            Attributes = attributes;

            Label = $"PhobosActorRefSource({bufferSize}, {overflowStrategy})";
        }

        /// <summary>
        /// TBD
        /// </summary>
        public override Attributes Attributes { get; }

        /// <summary>
        /// TBD
        /// </summary>
        protected override string Label { get; }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="attributes">TBD</param>
        /// <returns>TBD</returns>
        public override IModule WithAttributes(Attributes attributes) 
            => new PhobosActorRefSource<TOut>(_bufferSize, _overflowStrategy, attributes, AmendShape(attributes));

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="shape">TBD</param>
        /// <returns>TBD</returns>
        protected override SourceModule<(TOut, ITracer), IActorRef> NewInstance(SourceShape<(TOut, ITracer)> shape) 
            => new PhobosActorRefSource<TOut>(_bufferSize, _overflowStrategy, Attributes, shape);

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="context">TBD</param>
        /// <param name="materializer">TBD</param>
        /// <returns>TBD</returns>
        public override IPublisher<(TOut, ITracer)> Create(MaterializationContext context, out IActorRef materializer)
        {
            var mat = (ActorMaterializer)context.Materializer;
            materializer = mat.ActorOf(context, PhobosActorRefSourceActor<TOut>.Props(_bufferSize, _overflowStrategy, mat.Settings));
            return new ActorPublisherImpl<(TOut, ITracer)>(materializer);
        }
    }
}