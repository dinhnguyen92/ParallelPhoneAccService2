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
        private const int env = (int)Env.Normal;

        static void Main(string[] args)
        {
            List<PhoneAcc> accounts = new List<PhoneAcc>(); // List for storing the final accounts to be displayed
            Queue<IDList> idListQueue = new Queue<IDList>(); // FIFO queue of IDLists retrieved from the web service

            bool allListRetrieved = false;

            // Create thread to download IDLists
            Thread idListThread = new Thread(() => acquireIDLists(ref idListQueue, ref allListRetrieved));

            // Start timing
            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();

            // Start thread to download IDLists
            idListThread.Start();

            // As long as there's IDLists left either in the web service or in the IDList queue
            // Keep processing accounts
            while (!allListRetrieved || idListQueue.Count > 0)
            {
                // If there are IDLists in the queue
                if (idListQueue.Count > 0)
                {
                    // Get the oldest IDList retrieved
                    IDList list = idListQueue.Peek();

                    // Process each ID in the IDList
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

                        if (env == (int)Env.Debugging)
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
            }

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


            // Wait for return key to pause the console
            Console.ReadLine();
        }

        /// <summary>
        /// Query the web service asynchronously to retrieve a single list of account IDs
        /// </summary>
        /// <param name="token">Token for retrieving ID list</param>
        /// <returns>The retrieved ID list</returns>
        private static IDList downloadIDListAsync(string token)
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
                    if (String.IsNullOrEmpty(token))
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

        /// <summary>
        /// As long as there are new tokens, download and add IDLists to the IDList queue
        /// Once all the lists have been downloaded, mark allListRetrieved as true
        /// </summary>
        /// <param name="idListQueue">IDList queue for storing downloaded IDLists</param>
        /// <param name="allListRetrieved">Boolean variable for indicating whether all lists have been downloaded</param>
        private static void acquireIDLists(ref Queue<IDList> idListQueue, ref bool allListRetrieved)
        {
            // The first download has a null token
            string token = null;

            do
            {
                IDList list = downloadIDListAsync(token);

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

                if (env == (int)Env.Debugging)
                {
                    Console.WriteLine("Thread {0}: downloaded list: {1}", Thread.CurrentThread.ManagedThreadId, list.ToString());
                }
            }
            while (!string.IsNullOrEmpty(token));

            // Mark that all lists have been downloaded
            lock (thisLock)
            {
                allListRetrieved = true;
            }
        }
    }
}