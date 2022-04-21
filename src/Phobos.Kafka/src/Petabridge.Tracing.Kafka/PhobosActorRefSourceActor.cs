using System;
using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Akka.Streams.Actors;
using OpenTracing;
using Phobos.Actor;

namespace Petabridge.Tracing.Kafka
{
    internal class PhobosActorRefSourceActor<T> : Akka.Streams.Actors.ActorPublisher<(T, ITracer)>
    {
        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="bufferSize">TBD</param>
        /// <param name="overflowStrategy">TBD</param>
        /// <param name="settings">TBD</param>
        /// <exception cref="NotSupportedException">
        /// This exception is thrown when the specified <paramref name="overflowStrategy"/> is <see cref="Akka.Streams.OverflowStrategy.Backpressure"/>.
        /// </exception>
        /// <returns>TBD</returns>
        public static Props Props(int bufferSize, OverflowStrategy overflowStrategy, ActorMaterializerSettings settings)
        {
            if (overflowStrategy == OverflowStrategy.Backpressure)
                throw new NotSupportedException("Backpressure overflow strategy not supported");

            var maxFixedBufferSize = settings.MaxFixedBufferSize;
            return Akka.Actor.Props.Create(() => new PhobosActorRefSourceActor<T>(bufferSize, overflowStrategy, maxFixedBufferSize));
        }

        /// <summary>
        /// TBD
        /// </summary>
        protected readonly IBuffer<(T, ITracer)> Buffer;

        /// <summary>
        /// TBD
        /// </summary>
        public readonly int BufferSize;
        /// <summary>
        /// TBD
        /// </summary>
        public readonly OverflowStrategy OverflowStrategy;
        private ILoggingAdapter _log;

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="bufferSize">TBD</param>
        /// <param name="overflowStrategy">TBD</param>
        /// <param name="maxFixedBufferSize">TBD</param>
        public PhobosActorRefSourceActor(int bufferSize, OverflowStrategy overflowStrategy, int maxFixedBufferSize)
        {
            BufferSize = bufferSize;
            OverflowStrategy = overflowStrategy;
            Buffer = bufferSize != 0 ?  Kafka.Buffer.Create<(T, ITracer)>(bufferSize, maxFixedBufferSize) : null;
        }

        /// <summary>
        /// TBD
        /// </summary>
        protected ILoggingAdapter Log => _log ??= Context.GetLogger();

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="message">TBD</param>
        /// <returns>TBD</returns>
        protected override bool Receive(object message)
        {
            var handled = DefaultReceive(message) || RequestElement(message) || (message is T msg && ReceiveElement(msg));
            if(!handled)
                Log.Warning($"Received {message}, and I don't know what to do with it.");
            return handled;
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="message">TBD</param>
        /// <returns>TBD</returns>
        protected bool DefaultReceive(object message)
        {
            if (message is Cancel)
                Context.Stop(Self);
            else if (message is Status.Success)
            {
                if (BufferSize == 0 || Buffer.IsEmpty)
                    Context.Stop(Self);  // will complete the stream successfully
                else
                    Context.Become(DrainBufferThenComplete);
            }
            else if (message is Status.Failure && IsActive)
                OnErrorThenStop(((Status.Failure)message).Cause);
            else
                return false;
            return true;
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="message">TBD</param>
        /// <returns>TBD</returns>
        protected virtual bool RequestElement(object message)
        {
            if (message is Request)
            {
                // totalDemand is tracked by base
                if (BufferSize != 0)
                    while (TotalDemand > 0L && !Buffer.IsEmpty)
                        OnNext(Buffer.Dequeue());

                return true;
            }

            return false;
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="element">TBD</param>
        /// <returns>TBD</returns>
        protected virtual bool ReceiveElement(T element)
        {
            if (IsActive)
            {
                var tracer = Context.GetInstrumentation().Tracer;
                var message = (element, tracer);
                
                if (TotalDemand > 0L)
                    OnNext(message);
                else if (BufferSize == 0)
                    Log.Debug("Dropping element because there is no downstream demand: [{0}]", element);
                else if (!Buffer.IsFull)
                    Buffer.Enqueue(message);
                else
                {
                    switch (OverflowStrategy)
                    {
                        case OverflowStrategy.DropHead:
                            Log.Debug("Dropping the head element because buffer is full and overflowStrategy is: [DropHead]");
                            Buffer.DropHead();
                            Buffer.Enqueue(message);
                            break;
                        case OverflowStrategy.DropTail:
                            Log.Debug("Dropping the tail element because buffer is full and overflowStrategy is: [DropTail]");
                            Buffer.DropTail();
                            Buffer.Enqueue(message);
                            break;
                        case OverflowStrategy.DropBuffer:
                            Log.Debug("Dropping all the buffered elements because buffer is full and overflowStrategy is: [DropBuffer]");
                            Buffer.Clear();
                            Buffer.Enqueue(message);
                            break;
                        case OverflowStrategy.DropNew:
                            // do not enqueue new element if the buffer is full
                            Log.Debug("Dropping the new element because buffer is full and overflowStrategy is: [DropNew]");
                            break;
                        case OverflowStrategy.Fail:
                            Log.Error("Failing because buffer is full and overflowStrategy is: [Fail]");
                            OnErrorThenStop(new BufferOverflowException($"Buffer overflow, max capacity was ({BufferSize})"));
                            break;
                        case OverflowStrategy.Backpressure:
                            // there is a precondition check in Source.actorRefSource factory method
                            Log.Debug("Backpressuring because buffer is full and overflowStrategy is: [Backpressure]");
                            break;
                    }
                }

                return true;
            }

            return false;
        }

        private bool DrainBufferThenComplete(object message)
        {
            if (message is Cancel)
                Context.Stop(Self);
            else if (message is Status.Failure failure && IsActive)
            {
                // errors must be signaled as soon as possible,
                // even if previously valid completion was requested via Status.Success
                OnErrorThenStop(failure.Cause);
            }
            else if (message is Request)
            {
                // totalDemand is tracked by base
                while (TotalDemand > 0L && !Buffer.IsEmpty)
                    OnNext(Buffer.Dequeue());

                if (Buffer.IsEmpty)
                    Context.Stop(Self); // will complete the stream successfully
            }
            else if (IsActive)
                Log.Debug(
                    "Dropping element because Status.Success received already, only draining already buffered elements: [{0}] (pending: [{1}])",
                    message, Buffer.Used);
            else
                return false;

            return true;
        }

    }
}