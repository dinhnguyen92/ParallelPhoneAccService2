using System;
using System.Net;
using System.Linq;
using Newtonsoft.Json;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace ParallelPhoneAccService2
{
    class Program
    {
        // Web service URLs and routes
        private const string ENDPOINT_URL = "https://appsheettest1.azurewebsites.net/sample/";
        private const string LIST_ROUTE = "list/";
        private const string LIST_TOKEN_ROUTE = "list?token=";
        private const string DETAIL_ROUTE = "detail/";

        // Number of accounts to collect
        private const int numRes = 5;

        // Reference object for thread-safe locking
        static readonly object thisLock = new object();

        enum Env { Debugging, Normal };

        // Set env to Debugging to display log messages
        // Set env to run the program at full speed
        private const Env env = Env.Debugging;

        // AutoResetEvent for signaling when to start processing IDlists
        private static AutoResetEvent hasIDLists = new AutoResetEvent(false);

        // AutoResetEvent for signaling when all accounts have been processed
        private static AutoResetEvent isFinished = new AutoResetEvent(false);

        static void Main(string[] args)
        {
            List<PhoneAcc> accounts = new List<PhoneAcc>(); // List for storing the final accounts to be displayed
            Queue<IDList> idListQueue = new Queue<IDList>(); // FIFO queue of IDLists retrieved from the web service
            bool allListRetrieved = false;

            // Start timing
            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();

            #region processIDListsTask

            // Create task to process acquired IDLists
            // Make the task wait on the hasIDLists event
            Task processIDListsTask = Task.Run(() =>
            {
                // As long as there's IDLists left in the IDList queue or in the web service database
                // Keep processing IDLists
                while (idListQueue.Count > 0 || !allListRetrieved)
                {
                    if (env == Env.Debugging)
                    {
                        Console.WriteLine("Thread {0}: aiting for IDLists to process", Thread.CurrentThread.ManagedThreadId);
                    }

                    // Wait on the hasIDLists event
                    // Only start the task if the event is raised
                    // This makes sure that the while loop is not a busy wait loop
                    // However, only wait if there's still ID lists yet to be downloaded
                    if (!allListRetrieved)
                    {
                        hasIDLists.WaitOne();
                    }
                  
                    // Since this code section is only reached if the hasIDLists event is raised
                    // it is not necessary to check if idListQueue.Count is larger than 0

                    // Get the oldest IDList retrieved
                    IDList list = idListQueue.Peek();

                    // Process each account in the IDList based on ID
                    Parallel.ForEach(list.result, (id) =>
                    {
                        string json = null;

                        using (WebClient webClient = new WebClient())
                        {
                            // Retrieve the account using ID
                            try
                            {
                                // Since the thread can only proceed once the json object is retrieved
                                // we can use DownloadString to download the json instead of DownloadStringAsync
                                // URL: endpoint + detail route + retrieved id of account
                                json = webClient.DownloadString(ENDPOINT_URL + DETAIL_ROUTE + id);
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e);
                            }
                        }

                        PhoneAcc acc = JsonConvert.DeserializeObject<PhoneAcc>(json);

                        if (env == Env.Debugging)
                        {
                            Console.WriteLine("Thread {0}: retrieved account: {1}", Thread.CurrentThread.ManagedThreadId, acc.ToString());
                        }

                        lock (thisLock)
                        {
                            // Add the retrieved account to the account list
                            accounts.Add(acc);
                        }
                    });

                    // Once all the IDs in the list has been processed
                    // use PLINQ to filter out [numRes] valid accounts with the smallest age
                    lock (thisLock)
                    {
                        accounts = (from results in accounts.AsParallel()
                                    where results.hasValidNumber()
                                    orderby results.age
                                    select results).Take(numRes).ToList();
                        // Since all the IDs in the list have been processed, pop the list out of the queue
                        idListQueue.Dequeue();
                    }
                }

                if (env == Env.Debugging)
                {
                    Console.WriteLine("Thread {0}: all accounts processed", Thread.CurrentThread.ManagedThreadId);
                }

                // Once all accounts have been processed
                isFinished.Set();
            });

            #endregion            

            #region downloadIDListsTask

            // Create task to download IDLists
            Task downloadIDListsTask = Task.Run(() =>
            {
                // The first download has a null token
                string token = null;

                do
                {
                    IDList list = downloadIDList(token);

                    // Update the token
                    if (string.IsNullOrEmpty(list.token))
                    {
                        token = null;
                    }
                    else
                    {
                        token = list.token;
                    }

                    // Add the list to the FIFO list queue
                    lock (thisLock)
                    {
                        idListQueue.Enqueue(list);
                    }

                    // Signal that there's at least 1 IDList in the IDList queue
                    hasIDLists.Set();

                    if (env == Env.Debugging)
                    {
                        Console.WriteLine("Thread {0}: downloaded list: {1}", Thread.CurrentThread.ManagedThreadId, list.ToString());
                    }
                }
                while (!string.IsNullOrEmpty(token));

                if (env == Env.Debugging)
                {
                    Console.WriteLine("Thread {0}: all IDLists downloaded", Thread.CurrentThread.ManagedThreadId);
                }

                // Once all IDLists have been retrieved
                lock (thisLock)
                {
                    allListRetrieved = true;
                }
            });

            #endregion

            #region processResultTask

            Task processResultTask = Task.Run(() => 
            {
                // Wait until the isFinished event is raised
                isFinished.WaitOne();

                // Sort the results by name
                accounts = accounts.OrderBy(account => account.name).ToList();

                // Stop timing
                stopWatch.Stop();

                long time = stopWatch.ElapsedMilliseconds;

                // Print out the result
                Console.WriteLine();
                Console.WriteLine("Time elapsed: {0} ms", time);
                Console.WriteLine(accounts.Count + " results sorted by name: ");
                for (int i = 0; i < accounts.Count; i++)
                {
                    Console.WriteLine(accounts[i].ToString());
                }
            });

            #endregion

            // Wait for return key to pause the console
            Console.ReadLine();
        }

        /// <summary>
        /// Query the web service to retrieve a single list of account IDs
        /// </summary>
        /// <param name="token">Token for retrieving ID list</param>
        /// <returns>The retrieved ID list</returns>
        private static IDList downloadIDList(string token)
        {
            string json = null;
            IDList list = null;

            // Retrieve a new list
            using (WebClient webClient = new WebClient())
            {
                // Download the json object asynchronously
                // Since only 1 token can be acquired at a time
                // It's impossible to download multiple lists at the same time
                // Hence DownloadString is used instead of DownloadStringAsync
                try
                {
                    string url = ENDPOINT_URL;

                    // If there's no token
                    if (string.IsNullOrEmpty(token))
                    {
                        url = url + LIST_ROUTE;
                    }
                    else
                    {
                        url = url + LIST_TOKEN_ROUTE + token;
                    }

                    json = webClient.DownloadString(url);
                    list = JsonConvert.DeserializeObject<IDList>(json);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }
            return list;
        }
    }
}