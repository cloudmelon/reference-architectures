﻿namespace Taxi
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.EventHubs;
    using Newtonsoft.Json;
    using System.Threading.Tasks.Dataflow;


    class Program
    {
        private static async Task ReadData<T>(ICollection<string> pathList, Func<string, string, T> factory,
            ObjectPool<EventHubClient> pool, int randomSeed, AsyncConsole console,
            CancellationToken cancellationToken, int waittime, DataFormat dataFormat, CancellationTokenSource cts)
            where T : TaxiData
        {


            if (pathList == null)
            {
                throw new ArgumentNullException(nameof(pathList));
            }

            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            if (pool == null)
            {
                throw new ArgumentNullException(nameof(pool));
            }

            if (console == null)
            {
                throw new ArgumentNullException(nameof(console));
            }

            if (waittime > 0)
            {
                TimeSpan span = TimeSpan.FromMilliseconds(waittime);
                await Task.Delay(span);
            }

            string typeName = typeof(T).Name;
            Random random = new Random(randomSeed);

            // buffer block that holds the messages . consumer will fetch records from this block asynchronously.
            BufferBlock<T> buffer = new BufferBlock<T>(new DataflowBlockOptions()
            {
                BoundedCapacity = 100000
            });

            // consumer that sends the data to event hub asynchronoulsy.
            var consumer = new ActionBlock<T>(
             (t) =>
                {
                    using (var client = pool.GetObject())
                    {
                        return client.Value.SendAsync(new EventData(Encoding.UTF8.GetBytes(
                            t.GetData(dataFormat))), t.PartitionKey);
                    }
                },
                new ExecutionDataflowBlockOptions
                {
                    BoundedCapacity = 100000,
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = 100,
                }
            );

            // link the buffer to consumer .
            buffer.LinkTo(consumer, new DataflowLinkOptions()
            {
                PropagateCompletion = true
            });

            long messages = 0;


            var readTask = Task.Factory.StartNew(
                 async () =>
                 {

                     var shouldStop = false;
                     // iterate through the path list and act on each file from here on
                     foreach (var path in pathList)
                     {
                         ZipArchive archive = new ZipArchive(
                             File.OpenRead(path),
                             ZipArchiveMode.Read);

                         foreach (var entry in archive.Entries)
                         {
                             using (var reader = new StreamReader(entry.Open()))
                             {

                                 var header = reader.ReadLines()
                                     .First();
                                 // Start consumer
                                 var lines = reader.ReadLines()
                                      .Skip(1);


                                 // for each line , send to event hub
                                 foreach (var line in lines)
                                 {
                                     if (!cts.IsCancellationRequested)
                                     {
                                         await buffer.SendAsync(factory(line, header)).ConfigureAwait(false);
                                         if (++messages % 10000 == 0)
                                         {
                                             // random delay every 10000 messages are buffered ??
                                             await Task.Delay(random.Next(100, 1000))
                                                  .ConfigureAwait(false);
                                             await console.WriteLine($"Created {messages} records for {typeName}").ConfigureAwait(false);
                                         }
                                     }
                                     else
                                     {
                                         shouldStop = !shouldStop;
                                         break;
                                     }
                                 }
                             }

                             if (shouldStop)
                             {
                                 break;
                             }
                         }

                         if (shouldStop)
                         {
                             break;
                         }
                     }

                     buffer.Complete();
                     await Task.WhenAll(buffer.Completion, consumer.Completion);
                     await console.WriteLine($"Created total {messages} records for {typeName}").ConfigureAwait(false);
                 }
             );

            var waitTask = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        consumer.Completion.Wait();
                    }
                    catch (AggregateException ex)
                    {
                        cts.Cancel();
                        console.WriteLine(ex.InnerException.Message).ConfigureAwait(false);
                    }
                }
            );

            await waitTask;


        }


        private static (string RideConnectionString,
                        string FareConnectionString,
                        ICollection<String> RideDataFiles,
                        ICollection<String> TripDataFiles,
                        int MillisecondsToRun,
                        int MillisecondsToLead,
                        bool sendRideDataFirst) ParseArguments()
        {

            var rideConnectionString = Environment.GetEnvironmentVariable("RIDE_EVENT_HUB");
            var fareConnectionString = Environment.GetEnvironmentVariable("FARE_EVENT_HUB");
            var rideDataFilePath = Environment.GetEnvironmentVariable("RIDE_DATA_FILE_PATH");
            var numberOfMillisecondsToRun = (int.TryParse(Environment.GetEnvironmentVariable("SECONDS_TO_RUN"), out int outputSecondToRun) ? outputSecondToRun : 0) * 1000;
            var numberOfMillisecondsToLead = (int.TryParse(Environment.GetEnvironmentVariable("MINUTES_TO_LEAD"), out int outputMinutesToLead) ? outputMinutesToLead : 0) * 60000;
            var pushRideDataFirst = bool.TryParse(Environment.GetEnvironmentVariable("PUSH_RIDE_DATA_FIRST"), out Boolean outputPushRideDataFirst) ? outputPushRideDataFirst : false;

            rideConnectionString = "Endpoint=sb://asafinalruncosmosdatabaseeventhub.servicebus.windows.net/;SharedAccessKeyName=taxi-ride-asa-access-policy;SharedAccessKey=+RmZNmhKdEuHQo8E8Niju54XiN/LFeKjHuiGx43c0aQ=;EntityPath=taxi-ride";
            fareConnectionString = "Endpoint=sb://asafinalruncosmosdatabaseeventhub.servicebus.windows.net/;SharedAccessKeyName=taxi-fare-asa-access-policy;SharedAccessKey=BvFCHLegnzKM6FRWoJFm227DM0vSNoIMXDaoxj+qPwU=;EntityPath=taxi-fare";
            rideDataFilePath = "D:\\reference-architectures\\data\\streaming_asa\\onperm\\DataFile";
            if (string.IsNullOrWhiteSpace(rideConnectionString))
            {
                throw new ArgumentException("rideConnectionString must be provided");
            }

            if (string.IsNullOrWhiteSpace(fareConnectionString))
            {
                throw new ArgumentException("fareConnectionString must be provided");
            }

            if (string.IsNullOrWhiteSpace(rideDataFilePath))
            {
                throw new ArgumentException("rideDataFilePath must be provided");
            }

            if (!Directory.Exists(rideDataFilePath))
            {
                throw new ArgumentException("ride file path doesnot exists");
            }
            // get only the ride files in order. trip_data_1.zip gets read before trip_data_2.zip
            var rideDataFiles = Directory.EnumerateFiles(rideDataFilePath)
                                    .Where(p => Path.GetFileNameWithoutExtension(p).Contains("trip_data"))
                                    .OrderBy(p =>
                                    {
                                        var filename = Path.GetFileNameWithoutExtension(p);
                                        var indexString = filename.Substring(filename.LastIndexOf('_') + 1);
                                        var index = int.TryParse(indexString, out int i) ? i : throw new ArgumentException("tripdata file must be named in format trip_data_*.zip");
                                        return index;
                                    }).ToArray();

            // get only the fare files in order
            var fareDataFiles = Directory.EnumerateFiles(rideDataFilePath)
                            .Where(p => Path.GetFileNameWithoutExtension(p).Contains("trip_fare"))
                            .OrderBy(p =>
                            {
                                var filename = Path.GetFileNameWithoutExtension(p);
                                var indexString = filename.Substring(filename.LastIndexOf('_') + 1);
                                var index = int.TryParse(indexString, out int i) ? i : throw new ArgumentException("tripfare file must be named in format trip_fare_*.zip");
                                return index;
                            }).ToArray();

            if (rideDataFiles.Length == 0)
            {
                throw new ArgumentException($"trip data files at {rideDataFilePath} does not exist");
            }

            if (fareDataFiles.Length == 0)
            {
                throw new ArgumentException($"fare data files at {rideDataFilePath} does not exist");
            }

            return (rideConnectionString, fareConnectionString, rideDataFiles, fareDataFiles, numberOfMillisecondsToRun, numberOfMillisecondsToLead, pushRideDataFirst);
        }


        // blocking collection that helps to print to console the messages on progress on the read and send of files to event hub.
        private class AsyncConsole
        {
            private BlockingCollection<string> _blockingCollection = new BlockingCollection<string>();
            private CancellationToken _cancellationToken;
            private Task _writerTask;

            public AsyncConsole(CancellationToken cancellationToken = default(CancellationToken))
            {
                _cancellationToken = cancellationToken;
                _writerTask = Task.Factory.StartNew((state) =>
                {
                    var token = (CancellationToken)state;
                    string msg;
                    while (!token.IsCancellationRequested)
                    {
                        if (_blockingCollection.TryTake(out msg, 500))
                        {
                            Console.WriteLine(msg);
                        }
                    }

                    while (_blockingCollection.TryTake(out msg, 100))
                    {
                        Console.WriteLine(msg);
                    }
                }, _cancellationToken, TaskCreationOptions.LongRunning);
            }

            public Task WriteLine(string toWrite)
            {
                _blockingCollection.Add(toWrite);
                return Task.FromResult(0);
            }

            public Task WriterTask
            {
                get { return _writerTask; }
            }
        }

        //  start of the read task
        public static async Task<int> Main(string[] args)
        {
            try
            {
                var arguments = ParseArguments();
                var rideClient = EventHubClient.CreateFromConnectionString(
                    arguments.RideConnectionString
                );
                var fareClient = EventHubClient.CreateFromConnectionString(
                    arguments.FareConnectionString
                );


                CancellationTokenSource cts = arguments.MillisecondsToRun == 0 ? new CancellationTokenSource() :
                    new CancellationTokenSource(arguments.MillisecondsToRun);
                Console.CancelKeyPress += (s, e) =>
                {
                    //Console.WriteLine("Cancelling data generation");
                    cts.Cancel();
                    e.Cancel = true;
                };


                AsyncConsole console = new AsyncConsole(cts.Token);

                var rideClientPool = new ObjectPool<EventHubClient>(() => EventHubClient.CreateFromConnectionString(arguments.RideConnectionString), 100);
                var fareClientPool = new ObjectPool<EventHubClient>(() => EventHubClient.CreateFromConnectionString(arguments.FareConnectionString), 100);


                var numberOfMillisecondsToLead = arguments.MillisecondsToLead;
                var pushRideDataFirst = arguments.sendRideDataFirst;

                var rideTaskWaitTime = 0;
                var fareTaskWaitTime = 0;

                if (numberOfMillisecondsToLead > 0)
                {
                    if (!pushRideDataFirst)
                    {
                        rideTaskWaitTime = numberOfMillisecondsToLead;
                    }
                    else
                    {
                        fareTaskWaitTime = numberOfMillisecondsToLead;
                    }
                }


                var rideTask = ReadData<TaxiRide>(arguments.RideDataFiles,
                                        TaxiRide.FromString, rideClientPool, 100, console, cts.Token,
                                        rideTaskWaitTime, DataFormat.Json, cts);

                var fareTask = ReadData<TaxiFare>(arguments.TripDataFiles,
                    TaxiFare.FromString, fareClientPool, 200, console, cts.Token,
                    fareTaskWaitTime, DataFormat.Csv, cts);


                await Task.WhenAll(rideTask, fareTask, console.WriterTask);
                Console.WriteLine("Data generation complete");
            }
            catch (ArgumentException ae)
            {
                Console.WriteLine(ae.Message);
                return 1;
            }

            return 0;
        }
    }
}






