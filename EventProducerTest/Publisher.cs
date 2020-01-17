﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;

namespace EventProducerTest
{
    internal class Publisher
    {
        private static readonly ThreadLocal<Random> RandomNumberGenerator = new ThreadLocal<Random>(() => new Random(Interlocked.Increment(ref randomSeed)), false);

        private static int randomSeed = Environment.TickCount;

        private long currentSequence = 0;

        private Metrics Metrics { get; }

        private ConcurrentBag<Exception> ErrorsObserved { get; }

        private TestConfiguration Configuration { get; }

        public Publisher(TestConfiguration configuration,
                         Metrics metrics,
                         ConcurrentBag<Exception> errorsObserved)
        {
            Configuration = configuration;
            Metrics = metrics;
            ErrorsObserved = errorsObserved;
        }

        public async Task Start(CancellationToken cancellationToken)
        {
            var sendTasks = new List<Task>();

            while (!cancellationToken.IsCancellationRequested)
            {
                using var backgroundCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                try
                {
                    sendTasks.Clear();

                    var options = new EventHubProducerClientOptions
                    {
                        RetryOptions = new EventHubsRetryOptions
                        {
                            TryTimeout = Configuration.SendTimeout
                        }
                    };

                    var producer = new EventHubProducerClient(Configuration.EventHubsConnectionString, Configuration.EventHub, options);

                    await using (producer.ConfigureAwait(false))
                    {
                        // Create a set of background tasks to handle all but one of the concurrent sends.  The final
                        // send will be performed directly.

                        if (Configuration.ConcurrentSends > 1)
                        {
                            for (var index = 0; index < Configuration.ConcurrentSends - 1; ++index)
                            {
                                sendTasks.Add(Task.Run(async () =>
                                {
                                    while (!backgroundCancellationSource.Token.IsCancellationRequested)
                                    {
                                        await PerformSend(producer, backgroundCancellationSource.Token).ConfigureAwait(false);

                                        if ((Configuration.PublishingDelay.HasValue) && (Configuration.PublishingDelay.Value > TimeSpan.Zero))
                                        {
                                            await Task.Delay(Configuration.PublishingDelay.Value, backgroundCancellationSource.Token).ConfigureAwait(false);
                                        }
                                    }

                                }));
                            }
                        }

                        // Perform one of the sends in the foreground, which will allow easier detection of a
                        // processor-level issue.

                        while (!cancellationToken.IsCancellationRequested)
                        {
                            try
                            {
                                await PerformSend(producer, cancellationToken).ConfigureAwait(false);

                                if ((Configuration.PublishingDelay.HasValue) && (Configuration.PublishingDelay.Value > TimeSpan.Zero))
                                {
                                    await Task.Delay(Configuration.PublishingDelay.Value, cancellationToken).ConfigureAwait(false);
                                }
                            }
                            catch (TaskCanceledException)
                            {
                                backgroundCancellationSource.Cancel();
                                await Task.WhenAll(sendTasks).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    // No action needed.
                }
                catch (Exception ex) when
                    (ex is OutOfMemoryException
                    || ex is StackOverflowException
                    || ex is ThreadAbortException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // If this catch is hit, there's a problem with the producer itself; cancel the
                    // background sending and wait before allowing the producer to restart.

                    backgroundCancellationSource.Cancel();
                    await Task.WhenAll(sendTasks).ConfigureAwait(false);

                    Interlocked.Increment(ref Metrics.ProducerRestarted);
                    ErrorsObserved.Add(ex);
                }
            }
        }

        private async Task PerformSend(EventHubProducerClient producer,
                                       CancellationToken cancellationToken)
        {
            // Create the batch and generate a set of random events, keeping only those that were able to fit into the batch.
            // Because there is a side-effect of TryAdd in the statement, ensure that ToList is called to materialize the set
            // or the batch will be empty at send.

            using var batch = await producer.CreateBatchAsync().ConfigureAwait(false);

            var batchEvents = Enumerable
                .Range(0, Configuration.PublishBatchSize)
                .Select(_ => GenerateEvent(batch.MaximumSizeInBytes))
                .Where(item => batch.TryAdd(item))
                .ToList();

            // Publish the events and report them, capturing any failures specific to the send operation.

            try
            {
                if (batch.Count > 0)
                {
                    await producer.SendAsync(batch, cancellationToken).ConfigureAwait(false);

                    Interlocked.Add(ref Metrics.EventsPublished, batch.Count);
                    Interlocked.Add(ref Metrics.TotalPublshedSizeBytes, batch.SizeInBytes);
                    Interlocked.Increment(ref Metrics.BatchesPublished);
                    Interlocked.Increment(ref Metrics.TotalServiceOperations);
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (EventHubsException ex)
            {
                Interlocked.Increment(ref Metrics.TotalExceptions);
                Interlocked.Increment(ref Metrics.SendExceptions);
                ex.TrackMetrics(Metrics);
                ErrorsObserved.Add(ex);
            }
            catch (OperationCanceledException ex)
            {
                Interlocked.Increment(ref Metrics.TotalExceptions);
                Interlocked.Increment(ref Metrics.SendExceptions);
                Interlocked.Increment(ref Metrics.CanceledSendExceptions);
                ErrorsObserved.Add(ex);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref Metrics.TotalExceptions);
                Interlocked.Increment(ref Metrics.SendExceptions);
                Interlocked.Increment(ref Metrics.GeneralExceptions);
                ErrorsObserved.Add(ex);
            }
        }

        private EventData GenerateEvent(long maxMessageSize)
        {
            // Allow a chance to generate a large size event, otherwise, randomly
            // size within the normal range.

            long bodySize;

            if (RandomNumberGenerator.Value.NextDouble() < Configuration.LargeMessageRandomFactor)
            {
                bodySize = (Configuration.PublishingBodyMinBytes + (long)(RandomNumberGenerator.Value.NextDouble() * (maxMessageSize - Configuration.PublishingBodyMinBytes)));
            }
            else
            {
                bodySize = RandomNumberGenerator.Value.Next(Configuration.PublishingBodyMinBytes, Configuration.PublishingBodyRegularMaxBytes);
            }

            var body = new byte[bodySize];
            RandomNumberGenerator.Value.NextBytes(body);

            var eventData = new EventData(body);
            eventData.Properties[Property.Id] = Guid.NewGuid().ToString();
            eventData.Properties[Property.Sequence] = Interlocked.Increment(ref currentSequence);
            eventData.Properties[Property.PublishDate] = DateTimeOffset.UtcNow;

            return eventData;
        }
    }
}
