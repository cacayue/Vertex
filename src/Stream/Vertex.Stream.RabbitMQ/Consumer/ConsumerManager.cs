﻿using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vertex.Abstractions.Actor;
using Vertex.Abstractions.InnerService;
using Vertex.Stream.Common;
using Vertex.Utils;

namespace Vertex.Stream.RabbitMQ.Consumer
{
    public class ConsumerManager : IHostedService, IDisposable
    {
        private readonly ConcurrentDictionary<string, ConsumerRunner> consumerRunners = new ConcurrentDictionary<string, ConsumerRunner>();
        private readonly ILogger<ConsumerManager> logger;
        private readonly IServiceProvider provider;
        private readonly IGrainFactory grainFactory;
        private readonly List<QueueInfo> queues;
        private const int HoldTime = 20 * 1000;
        private const int MonitTime = 60 * 2 * 1000;
        private const int checkTime = 10 * 1000;

        private const int lockHoldingSeconds = 60;
        private int distributedMonitorTimeLock = 0;
        private int distributedHoldTimerLock = 0;
        private int heathCheckTimerLock = 0;

        public ConsumerManager(
            ILogger<ConsumerManager> logger,
            IGrainFactory grainFactory,
            IServiceProvider provider)
        {
            this.provider = provider;
            this.logger = logger;
            this.grainFactory = grainFactory;
            queues = new List<QueueInfo>();
            foreach (var assembly in AssemblyHelper.GetAssemblies(logger))
            {
                foreach (var type in assembly.GetTypes())
                {
                    var attributes = type.GetCustomAttributes(typeof(StreamSubAttribute), false);
                    if (attributes.Length > 0 && attributes.First() is StreamSubAttribute attribute)
                    {
                        foreach (var i in Enumerable.Range(0, attribute.Sharding + 1))
                        {
                            var stream = i == 0 ? attribute.Name : $"{attribute.Name}_{i}";
                            var interfaceType = type.GetInterfaces().Where(t => typeof(IFlowActor).IsAssignableFrom(t) && !t.IsGenericType && t != typeof(IFlowActor)).FirstOrDefault();
                            var queue = i == 0 ? $"{attribute.Name}_{attribute.Group}" : $"{attribute.Name}_{attribute.Group}_{i}";
                            var existQueue = queues.SingleOrDefault(q => q.Exchange == attribute.Name && q.Queue == queue && q.RoutingKey == stream);
                            if (existQueue == default)
                            {
                                queues.Add(new QueueInfo
                                {
                                    SubActorType = new List<Type> { interfaceType },
                                    Exchange = attribute.Name,
                                    RoutingKey = stream,
                                    Queue = queue,
                                });
                            }
                            else
                            {
                                existQueue.SubActorType.Add(interfaceType);
                            }
                        }
                    }
                }
            }
        }
        private ConcurrentDictionary<string, long> Runners { get; } = new ConcurrentDictionary<string, long>();
        private Timer HeathCheckTimer { get; set; }

        private Timer DistributedMonitorTime { get; set; }

        private Timer DistributedHoldTimer { get; set; }

        private async Task DistributedStart()
        {
            try
            {
                if (Interlocked.CompareExchange(ref this.distributedMonitorTimeLock, 1, 0) == 0)
                {
                    foreach (var queue in this.queues)
                    {
                        var key = queue.ToString();
                        if (!this.Runners.ContainsKey(key))
                        {
                            var weight = 100000 - this.Runners.Count;
                            var (isOk, lockId, expectMillisecondDelay) = await this.grainFactory.GetGrain<IWeightHoldLockActor>(key).Lock(weight, lockHoldingSeconds);
                            if (isOk)
                            {
                                if (this.Runners.TryAdd(key, lockId))
                                {
                                    var runner = new ConsumerRunner(this.provider, queue);
                                    this.consumerRunners.TryAdd(key, runner);
                                    await runner.Run();
                                }

                            }
                        }
                    }

                    Interlocked.Exchange(ref this.distributedMonitorTimeLock, 0);
                    if (this.logger.IsEnabled(LogLevel.Information))
                    {
                        this.logger.LogInformation("EventBus Background Service is working.");
                    }
                }
            }
            catch (Exception exception)
            {
                this.logger.LogError(exception.InnerException ?? exception, nameof(this.DistributedStart));
                Interlocked.Exchange(ref this.distributedMonitorTimeLock, 0);
            }
        }

        private async Task DistributedHold()
        {
            try
            {
                if (this.logger.IsEnabled(LogLevel.Information))
                {
                    this.logger.LogInformation("EventBus Background Service is holding.");
                }

                if (Interlocked.CompareExchange(ref this.distributedHoldTimerLock, 1, 0) == 0)
                {
                    foreach (var lockKV in this.Runners)
                    {
                        if (this.Runners.TryGetValue(lockKV.Key, out var lockId))
                        {
                            var holdResult = await this.grainFactory.GetGrain<IWeightHoldLockActor>(lockKV.Key).Hold(lockId, lockHoldingSeconds);
                            if (!holdResult)
                            {
                                if (this.consumerRunners.TryRemove(lockKV.Key, out var runner))
                                {
                                    runner.Close();
                                }

                                this.Runners.TryRemove(lockKV.Key, out var _);
                            }
                        }
                    }

                    Interlocked.Exchange(ref this.distributedHoldTimerLock, 0);
                }
            }
            catch (Exception exception)
            {
                this.logger.LogError(exception.InnerException ?? exception, nameof(this.DistributedHold));
                Interlocked.Exchange(ref this.distributedHoldTimerLock, 0);
            }
        }

        private async Task HeathCheck()
        {
            try
            {
                if (this.logger.IsEnabled(LogLevel.Debug))
                {
                    this.logger.LogDebug("EventBus Background Service is checking.");
                }

                if (Interlocked.CompareExchange(ref this.heathCheckTimerLock, 1, 0) == 0)
                {
                    await Task.WhenAll(this.consumerRunners.Values.Select(runner => runner.HeathCheck()));
                    Interlocked.Exchange(ref this.heathCheckTimerLock, 0);
                }
            }
            catch (Exception exception)
            {
                this.logger.LogError(exception.InnerException ?? exception, nameof(this.HeathCheck));
                Interlocked.Exchange(ref this.heathCheckTimerLock, 0);
            }
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (this.logger.IsEnabled(LogLevel.Debug))
            {
                this.logger.LogInformation("EventBus Background Service is starting.");
            }

            this.DistributedMonitorTime = new Timer(state => this.DistributedStart().Wait(), null, 1000, MonitTime);
            this.DistributedHoldTimer = new Timer(state => this.DistributedHold().Wait(), null, HoldTime, HoldTime);
            this.HeathCheckTimer = new Timer(state => { this.HeathCheck().Wait(); }, null, checkTime, checkTime);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (this.logger.IsEnabled(LogLevel.Information))
            {
                this.logger.LogInformation("EventBus Background Service is stopping.");
            }

            this.Dispose();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (this.logger.IsEnabled(LogLevel.Information))
            {
                this.logger.LogInformation("EventBus Background Service is disposing.");
            }

            foreach (var runner in this.consumerRunners.Values)
            {
                runner.Close();
            }

            this.DistributedMonitorTime?.Dispose();
            this.DistributedHoldTimer?.Dispose();
            this.HeathCheckTimer?.Dispose();
        }
    }
}
