﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Vertex.Utils.Channels
{
    /// <summary>
    /// multi producer, single consumer channel
    /// </summary>
    /// <typeparam name="T">data type produced by producer</typeparam>
    public class ThreadChannel<T> : IMpscChannel<T>, ISequenceMpscChannel
    {
        private readonly Channel<T> buffer = Channel.CreateUnbounded<T>();
        private Func<List<T>, Task> consumer;
        private readonly List<ISequenceMpscChannel> consumerSequence = new List<ISequenceMpscChannel>();
        private Task<bool> waitToReadTask;
        private readonly ILogger logger;

        /// <summary>
        /// The maximum amount of data processed each time in batch data processing
        /// </summary>
        private int MaxBatchSize;

        /// <summary>
        /// Maximum delay of batch data reception
        /// </summary>
        private int MaxMillisecondsDelay;

        public ThreadChannel(ILogger<BufferBlockChannel<T>> logger, IOptions<ChannelOptions> options)
        {
            this.logger = logger;
            this.MaxBatchSize = options.Value.MaxBatchSize;
            this.MaxMillisecondsDelay = options.Value.MaxMillisecondsDelay;
        }

        public bool IsDisposed { get; private set; }

        public bool IsChildren { get; set; }
        public bool Running { get; private set; }

        public void BindConsumer(Func<List<T>, Task> consumer, bool active = true)
        {
            if (Interlocked.CompareExchange(ref this.consumer, consumer, null) is null)
            {
                this.consumer = consumer;
                if (active)
                    ThreadPool.UnsafeQueueUserWorkItem(async state => await this.ActiveAutoConsumer(), null);
            }
            else
            {
                throw new RebindConsumerException(this.GetType().Name);
            }
        }

        public void Config(int maxBatchSize, int maxMillisecondsDelay)
        {
            this.MaxBatchSize = maxBatchSize;
            this.MaxMillisecondsDelay = maxMillisecondsDelay;
        }

        public async ValueTask<bool> WriteAsync(T data)
        {
            if (this.consumer is null)
            {
                throw new ArgumentNullException(nameof(this.consumer));
            }

            if (!this.buffer.Writer.TryWrite(data))
            {
                await this.buffer.Writer.WriteAsync(data);
            }

            return true;
        }

        private async Task ActiveAutoConsumer()
        {
            Running = true;
            while (!this.IsDisposed)
            {
                try
                {
                    await this.WaitToReadAsync();
                    await this.ManualConsume();
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, ex.Message);
                }
            }
            Running = false;
        }

        public void Join(ISequenceMpscChannel channel)
        {
            if (channel.Running)
                throw new ArgumentException("not allowed to join the running channel");
            if (this.consumerSequence.IndexOf(channel) == -1)
            {
                channel.IsChildren = true;
                this.consumerSequence.Add(channel);
            }
        }

        public async Task ManualConsume()
        {
            if (this.waitToReadTask.IsCompletedSuccessfully && this.waitToReadTask.Result)
            {
                var dataList = new List<T>();
                var startTime = DateTimeOffset.UtcNow;
                while (this.buffer.Reader.TryRead(out var value))
                {
                    dataList.Add(value);
                    if (dataList.Count > this.MaxBatchSize)
                    {
                        break;
                    }
                    else if ((DateTimeOffset.UtcNow - startTime).TotalMilliseconds > this.MaxMillisecondsDelay)
                    {
                        break;
                    }
                }

                if (dataList.Count > 0)
                {
                    await this.consumer(dataList);
                }
            }

            foreach (var joinConsumer in this.consumerSequence)
            {
                await joinConsumer.ManualConsume();
            }
        }

        public async Task<bool> WaitToReadAsync()
        {
            this.waitToReadTask = this.buffer.Reader.WaitToReadAsync().AsTask();
            if (this.consumerSequence.Count == 0)
            {
                return await this.waitToReadTask;
            }
            else
            {
                var tasks = new Task<bool>[this.consumerSequence.Count + 1];
                for (int i = 0; i < consumerSequence.Count; i++)
                {
                    tasks[i] = consumerSequence[i].WaitToReadAsync();
                }
                tasks[^1] = this.waitToReadTask;
                return await await Task.WhenAny(tasks);
            }
        }

        public void Dispose()
        {
            this.IsDisposed = true;
            foreach (var joinConsumer in this.consumerSequence)
            {
                joinConsumer.Dispose();
            }

            this.buffer.Writer.Complete();
        }
    }
}
