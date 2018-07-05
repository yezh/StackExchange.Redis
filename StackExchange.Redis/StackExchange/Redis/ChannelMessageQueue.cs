﻿using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace StackExchange.Redis
{
    /// <summary>
    /// Represents a message that is broadcast via pub/sub
    /// </summary>
    public readonly struct ChannelMessage
    {
        internal ChannelMessage(RedisChannel channel, RedisValue value)
        {
            Channel = channel;
            Value = value;
        }

        /// <summary>
        /// The channel that the message was broadcast to
        /// </summary>
        public RedisChannel Channel { get; }
        /// <summary>
        /// The value that was broadcast
        /// </summary>
        public RedisValue Value { get; }
    }


    /// <summary>
    /// Represents a message queue of pub/sub notifications
    /// </summary>
    /// <remarks>To create a ChannelMessageQueue, use ISubscriber.Subscribe[Async](RedisKey)</remarks>
    public sealed class ChannelMessageQueue
    {
        private readonly Channel<ChannelMessage> _channel;
        private readonly RedisChannel _redisChannel;
        private ISubscriber _parent;

        /// <summary>
        /// Indicates if all messages that will be received have been drained from this channel
        /// </summary>
        public bool IsComplete { get; private set; }

        internal ChannelMessageQueue(RedisChannel redisChannel, ISubscriber parent)
        {
            _redisChannel = redisChannel;
            _parent = parent;
            _channel = Channel.CreateUnbounded<ChannelMessage>(s_ChannelOptions);
            _channel.Reader.Completion.ContinueWith(
                (t, state) => ((ChannelMessageQueue)state).IsComplete = true, this, TaskContinuationOptions.ExecuteSynchronously);
        }
        static readonly UnboundedChannelOptions s_ChannelOptions = new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = false,
            AllowSynchronousContinuations = false,
        };
        internal void Subscribe(CommandFlags flags) => _parent.Subscribe(_redisChannel, HandleMessage, flags);
        internal Task SubscribeAsync(CommandFlags flags) => _parent.SubscribeAsync(_redisChannel, HandleMessage, flags);

        private void HandleMessage(RedisChannel channel, RedisValue value)
        {
            var writer = _channel.Writer;
            if (channel.IsNull && value.IsNull) // see ForSyncShutdown
            {
                writer.TryComplete();
            }
            else
            {
                writer.TryWrite(new ChannelMessage(channel, value));
            }
        }
            

        /// <summary>
        /// Consume a message from the channel
        /// </summary>
        public ValueTask<ChannelMessage> ReadAsync(CancellationToken cancellationToken = default)
            => _channel.Reader.ReadAsync(cancellationToken);

        internal void UnsubscribeImpl(Exception error = null, CommandFlags flags = CommandFlags.None)
        {
            var parent = _parent;
            if (parent != null)
            {
                _parent.UnsubscribeAsync(_redisChannel, HandleMessage, flags);
                _parent = null;
                _channel.Writer.TryComplete(error);
            }
        }
        internal async Task UnsubscribeAsyncImpl(Exception error = null, CommandFlags flags = CommandFlags.None)
        {
            var parent = _parent;
            if (parent != null)
            {
                await _parent.UnsubscribeAsync(_redisChannel, HandleMessage, flags);
                _parent = null;
                _channel.Writer.TryComplete(error);
            }
        }

        internal static bool IsOneOf(Action<RedisChannel, RedisValue> handler)
        {
            try
            {
                return handler != null && handler.Target is ChannelMessageQueue
                    && handler.Method.Name == nameof(HandleMessage);
            } catch
            {
                return false;
            }
        }

        /// <summary>
        /// Stop receiving messages on this channel
        /// </summary>
        public void Unsubscribe(CommandFlags flags = CommandFlags.None) => UnsubscribeImpl(null, flags);
        /// <summary>
        /// Stop receiving messages on this channel
        /// </summary>
        public Task UnsubscribeAsync(CommandFlags flags = CommandFlags.None) => UnsubscribeAsyncImpl(null, flags);
    }
}