// -----------------------------------------------------------------------
//  <copyright file="ClusterAwareRequestExecuter.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Raven.Abstractions;
using Raven.Abstractions.Cluster;
using Raven.Abstractions.Connection;
using Raven.Abstractions.Data;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Logging;
using Raven.Abstractions.Replication;
using Raven.Abstractions.Util;
using Raven.Client.Connection.Async;
using Raven.Client.Connection.Implementation;
using Raven.Client.Extensions;
using Raven.Client.Metrics;

namespace Raven.Client.Connection.Request
{
    public class ClusterAwareRequestExecuter : IRequestExecuter
    {
        public TimeSpan WaitForLeaderTimeout { get; set; }= TimeSpan.FromSeconds(5);

        public TimeSpan ReplicationDestinationsTopologyTimeout { get; set; } = TimeSpan.FromSeconds(2);

        private readonly ManualResetEventSlim leaderNodeSelected = new ManualResetEventSlim();

        private Task refreshReplicationInformationTask;

        private volatile OperationMetadata leaderNode;

        private DateTime lastUpdate = DateTime.MinValue;

        private bool firstTime = true;

        private int readStripingBase;

        private static readonly ILog Log = LogManager.GetLogger(typeof(ClusterAwareRequestExecuter));

        public OperationMetadata LeaderNode
        {
            get
            {
                return leaderNode;
            }

        }

        /// <summary>
        /// Sets the leader node to a known leader that is not null and sets the leader selected event
        /// </summary>
        /// <param name="newLeader"></param>
        public void SetLeaderNodeToKnownLeader(OperationMetadata newLeader )
        {
            if (newLeader == null)
            {
                if (Log.IsDebugEnabled)
                {
                    Log.Debug($"An attempt to change the leader node to null from SetLeaderNodeToKnownLeader was detected.{Environment.NewLine}" +
                              $"{Environment.StackTrace}");
                }
                return;
            }
            if (Log.IsDebugEnabled)
            {
                var oldLeader = leaderNode == null ? "null" : leaderNode.ToString();
                Log.Debug($"Leader node is changing from {oldLeader} to {newLeader}");
            }
            leaderNode = newLeader;
            leaderNodeSelected.Set();
        }

        /// <summary>
        /// Sets the value of leader node to null and reset the leader node selected event
        /// </summary>
        /// <param name="prevValue">The condition value upon we decide if we make the set to null or not</param>
        /// <returns>true - if leader was set to null or was null already, otherwise returns false</returns>
        public bool SetLeaderNodeToNullIfPrevIsTheSame(OperationMetadata prevValue)
        {
            var realPrevValue = Interlocked.CompareExchange(ref leaderNode, null, prevValue);
            var res = realPrevValue == null || realPrevValue.Equals(prevValue) ;
            if (res && realPrevValue != null)
            {
                if (Log.IsDebugEnabled)
                {
                    Log.Debug($"Leader node is changing from null to null.");
                }
                leaderNodeSelected.Reset();
            }
            return res;
        }

        /// <summary>
        /// This will change the leader node to the given node and raise the leader changed event if the
        /// new leader was set to a value not equal to null.
        /// </summary>
        /// <param name="newLeader">The new leader to be set.</param>
        /// <param name="isRealLeader">An indication if this is a real leader or just the primary, this will affect if we raise the leader selected event.</param>
        /// <returns>true if the leader node was changed from null to the given value, otherwise returns false</returns>
        private bool SetLeaderNodeIfLeaderIsNull(OperationMetadata newLeader ,bool isRealLeader = true)
        {
            var changed = (Interlocked.CompareExchange(ref leaderNode, newLeader, null) == null);
            if (changed && isRealLeader && newLeader != null)
                leaderNodeSelected.Set();
            if (Log.IsDebugEnabled && changed)
            {
                Log.Debug($"Leader node is changing from null to {newLeader}, isRealLeader={isRealLeader}.");
            }
            return changed;
        }

        /// <summary>
        /// This method sets the leader node to null and reset the leader selected event.
        /// You should not use this method unless you're sure nobody can set the leader node 
        /// to some other value.
        /// </summary>
        public void SetLeaderNodeToNull()
        {
            leaderNode = null;
            leaderNodeSelected.Reset();
        }

        public List<OperationMetadata> NodeUrls
        {
            get
            {
                return Nodes
                    .Select(x => new OperationMetadata(x))
                    .ToList();
            }
        }

        public List<OperationMetadata> Nodes { get; private set; }

        public FailureCounters FailureCounters { get; private set; }

        public ClusterAwareRequestExecuter()
        {
            Nodes = new List<OperationMetadata>();
            FailureCounters = new FailureCounters();
        }

        public int GetReadStripingBase(bool increment)
        {
            return increment ? Interlocked.Increment(ref readStripingBase) : readStripingBase;
        }

        public ReplicationDestination[] FailoverServers { get; set; }

        public Task<T> ExecuteOperationAsync<T>(AsyncServerClient serverClient, HttpMethod method, int currentRequest, Func<OperationMetadata, IRequestTimeMetric, Task<T>> operation, CancellationToken token)
        {
            return ExecuteWithinClusterInternalAsync(serverClient, method, operation, token);
        }

        public Task UpdateReplicationInformationIfNeededAsync(AsyncServerClient serverClient, bool force = false)
        {
            var localLeaderNode = LeaderNode;
            var updateRecently = lastUpdate.AddMinutes(5) > SystemTime.UtcNow;
            if (force == false && updateRecently && localLeaderNode != null)
            {
                if (Log.IsDebugEnabled)
                    Log.Debug($"Will not update replication information because we have a leader:{localLeaderNode} and we recently updated the topology.");                
                return new CompletedTask();
            }
            //This will prevent setting leader node to null if it was updated already.
            if (SetLeaderNodeToNullIfPrevIsTheSame(localLeaderNode) == false)
                return new CompletedTask(); 

            return UpdateReplicationInformationForCluster(serverClient, new OperationMetadata(serverClient.Url, serverClient.PrimaryCredentials, null), operationMetadata =>
            {
                return serverClient.DirectGetReplicationDestinationsAsync(operationMetadata, null, timeout: ReplicationDestinationsTopologyTimeout).ContinueWith(t =>
                {
                    if (t.IsFaulted || t.IsCanceled)
                        return null;

                    return t.Result;
                });
            });
        }

        public void AddHeaders(HttpJsonRequest httpJsonRequest, AsyncServerClient serverClient, string currentUrl, bool withClusterFailoverHeader = false)
        {
            httpJsonRequest.AddHeader(Constants.Cluster.ClusterAwareHeader, "true");

            if (serverClient.convention.FailoverBehavior == FailoverBehavior.ReadFromAllWriteToLeader)
                httpJsonRequest.AddHeader(Constants.Cluster.ClusterReadBehaviorHeader, "All");

            if (withClusterFailoverHeader)
                httpJsonRequest.AddHeader(Constants.Cluster.ClusterFailoverBehaviorHeader, "true");
        }

        public void SetReadStripingBase(int strippingBase)
        {
            this.readStripingBase = strippingBase;
        }

        private async Task<T> ExecuteWithinClusterInternalAsync<T>(AsyncServerClient serverClient, HttpMethod method, Func<OperationMetadata, IRequestTimeMetric, Task<T>> operation, CancellationToken token, int numberOfRetries = 2, bool withClusterFailoverHeader = false)
        {
            token.ThrowIfCancellationRequested();
            
            var node = LeaderNode;
            if (node == null)
            {
#pragma warning disable 4014
                // If withClusterFailover set to true we will need to force the update and choose another leader.
                UpdateReplicationInformationIfNeededAsync(serverClient, force:withClusterFailoverHeader); // maybe start refresh task
#pragma warning restore 4014
                switch (serverClient.convention.FailoverBehavior)
                {
                    case FailoverBehavior.ReadFromAllWriteToLeaderWithFailovers:
                    case FailoverBehavior.ReadFromLeaderWriteToLeaderWithFailovers:
                        var waitResult = leaderNodeSelected.Wait(WaitForLeaderTimeout);
                        if(Log.IsDebugEnabled && waitResult == false)
                            Log.Debug($"Failover behavior is {serverClient.convention.FailoverBehavior}, waited for {WaitForLeaderTimeout.TotalSeconds} seconds and no leader was selected.");
                        break;
                    default:
                        if (leaderNodeSelected.Wait(WaitForLeaderTimeout) == false)
                        {
                            if (Log.IsDebugEnabled)
                                Log.Debug($"Failover behavior is {serverClient.convention.FailoverBehavior}, waited for {WaitForLeaderTimeout.TotalSeconds} seconds and no leader was selected.");
                            throw new InvalidOperationException($"Cluster is not in a stable state. No leader was selected, but we require one for making a request using {serverClient.convention.FailoverBehavior}.");
                        }
                        break;
                }

                node = LeaderNode;
            }

            switch (serverClient.convention.FailoverBehavior)
            {
                case FailoverBehavior.ReadFromAllWriteToLeader:
                    if (method == HttpMethods.Get)
                        node = GetNodeForReadOperation(node) ?? node;
                    break;
                case FailoverBehavior.ReadFromAllWriteToLeaderWithFailovers:
                    if (node == null)
                    {
                        return await HandleWithFailovers(operation, token,withClusterFailoverHeader).ConfigureAwait(false);
                        
                    }

                    if (method == HttpMethods.Get)
                        node = GetNodeForReadOperation(node) ?? node;
                    break;
                case FailoverBehavior.ReadFromLeaderWriteToLeaderWithFailovers:
                    if (node == null)
                    {
                        return await HandleWithFailovers(operation, token, withClusterFailoverHeader).ConfigureAwait(false);
                    }
                    break;
            }

            var operationResult = await TryClusterOperationAsync(node, operation, false, token).ConfigureAwait(false);

            if (operationResult.Success)
            {
                return operationResult.Result;
            }
            if(Log.IsDebugEnabled)
                Log.Debug($"Faield executing operation on node {node.Url} number of remaining retries: {numberOfRetries}.");

            //the value of the leader was changed since we took a snapshot of it and it is not null so we will try to run again without 
            // considering this a failure
            if(SetLeaderNodeToNullIfPrevIsTheSame(node) == false)
                return await ExecuteWithinClusterInternalAsync(serverClient, method, operation, token, numberOfRetries, withClusterFailoverHeader).ConfigureAwait(false);
            FailureCounters.IncrementFailureCount(node.Url);

            if (serverClient.convention.FailoverBehavior == FailoverBehavior.ReadFromLeaderWriteToLeaderWithFailovers
                || serverClient.convention.FailoverBehavior == FailoverBehavior.ReadFromAllWriteToLeaderWithFailovers)
            {
                withClusterFailoverHeader = true;
            }

            if (numberOfRetries <= 0)
            {
                throw new InvalidOperationException("Cluster is not reachable. Out of retries, aborting.", operationResult.Error );
            }

            return await ExecuteWithinClusterInternalAsync(serverClient, method, operation, token, numberOfRetries - 1, withClusterFailoverHeader).ConfigureAwait(false);
        }

        private OperationMetadata GetNodeForReadOperation(OperationMetadata node)
        {
            Debug.Assert(node != null);

            var nodes = new List<OperationMetadata>(NodeUrls);

            if (readStripingBase == -1)
                return LeaderNode;

            if (nodes.Count == 0)
                return null;


            var nodeIndex = readStripingBase % nodes.Count;
            var readNode = nodes[nodeIndex];
            if (ShouldExecuteUsing(readNode))
                return readNode;

            return node;
        }

        private async Task<T> HandleWithFailovers<T>(Func<OperationMetadata, IRequestTimeMetric, Task<T>> operation, CancellationToken token, bool withClusterFailoverHeader)
        {
            var nodes = NodeUrls;
            for (var i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];

                // Have to be here more thread safe
                n.ClusterInformation.WithClusterFailoverHeader = withClusterFailoverHeader;
                if (ShouldExecuteUsing(n) == false)
                    continue;

                var hasMoreNodes = nodes.Count > i + 1;
                var result = await TryClusterOperationAsync(n, operation, hasMoreNodes, token).ConfigureAwait(false);
                if (result.Success)
                    return result.Result;
                if(Log.IsDebugEnabled)
                    Log.Debug($"Tried executing operation on failover server {n.Url} with no success.");
                FailureCounters.IncrementFailureCount(n.Url);
            }
 
            throw new InvalidOperationException("Cluster is not reachable. Executing operation on any of the nodes failed, aborting.");
        }

        private bool ShouldExecuteUsing(OperationMetadata operationMetadata)
        {
            var failureCounter = FailureCounters.GetHolder(operationMetadata.Url);
            if (failureCounter.Value <= 1) // can fail once
                return true;

            return false;
        }

        private async Task<AsyncOperationResult<T>> TryClusterOperationAsync<T>(OperationMetadata node, Func<OperationMetadata, IRequestTimeMetric, Task<T>> operation, bool avoidThrowing, CancellationToken token)
        {
            Debug.Assert(node != null);

            token.ThrowIfCancellationRequested();
            var shouldRetry = false;

            var operationResult = new AsyncOperationResult<T>();
            try
            {
                operationResult.Result = await operation(node, null).ConfigureAwait(false);
                operationResult.Success = true;
            }
            catch (Exception e)
            {
                bool wasTimeout;
                if (HttpConnectionHelper.IsServerDown(e, out wasTimeout))
                {
                    shouldRetry = true;
                    operationResult.WasTimeout = wasTimeout;
                    if(Log.IsDebugEnabled)
                        Log.Debug($"Operation failed because server {node.Url} is down.");
                }
                else
                {
                    var ae = e as AggregateException;
                    ErrorResponseException errorResponseException;
                    if (ae != null)
                        errorResponseException = ae.ExtractSingleInnerException() as ErrorResponseException;
                    else
                        errorResponseException = e as ErrorResponseException;

                    if (errorResponseException != null)
                    {
                        if (errorResponseException.StatusCode == HttpStatusCode.Redirect)
                        {
                            IEnumerable<string> values;
                            if (errorResponseException.Response.Headers.TryGetValues("Raven-Leader-Redirect", out values) == false
                                && values.Contains("true") == false)
                            {
                                throw new InvalidOperationException("Got 302 Redirect, but without Raven-Leader-Redirect: true header, maybe there is a proxy in the middle", e);
                            }
                            var redirectUrl = errorResponseException.Response.Headers.Location.ToString();
                            var newLeaderNode = Nodes.FirstOrDefault(n => n.Url.Equals(redirectUrl))?? new OperationMetadata(redirectUrl, node.Credentials, node.ClusterInformation);
                            SetLeaderNodeToKnownLeader(newLeaderNode);
                            if(Log.IsDebugEnabled)
                                Log.Debug($"Redirecting to {redirectUrl} because {node.Url} responded with 302-redirect.");
                            return await TryClusterOperationAsync(newLeaderNode, operation, avoidThrowing, token).ConfigureAwait(false);
                        }

                        if (errorResponseException.StatusCode == HttpStatusCode.ExpectationFailed)
                        {
                            if (Log.IsDebugEnabled)
                                Log.Debug($"Operation failed with status code {HttpStatusCode.ExpectationFailed}, will retry.");
                            shouldRetry = true;
                        }
                    }
                }

                if (shouldRetry == false && avoidThrowing == false)
                    throw;

                operationResult.Error = e;

            }

            if (operationResult.Success)
                FailureCounters.ResetFailureCount(node.Url);

            return operationResult;
        }

        private Task UpdateReplicationInformationForCluster(AsyncServerClient serverClient, OperationMetadata primaryNode, Func<OperationMetadata, Task<ReplicationDocumentWithClusterInformation>> getReplicationDestinationsTask)
        {
            lock (this)
            {
                var serverHash = ServerHash.GetServerHash(primaryNode.Url);

                var taskCopy = refreshReplicationInformationTask;
                if (taskCopy != null)
                    return taskCopy;

                if (firstTime)
                {
                    firstTime = false;

                    var nodes = ReplicationInformerLocalCache.TryLoadClusterNodesFromLocalCache(serverHash);
                    if (nodes != null)
                    {
                        Nodes = nodes;
                        var newLeaderNode = GetLeaderNode(Nodes);
                        if (newLeaderNode != null)
                        {
                            if (Log.IsDebugEnabled)
                            {
                                Log.Debug($"Fetched topology from cache, Leader is {LeaderNode}\n Nodes:" + string.Join(",", Nodes.Select(n => n.Url)));
                            }
                            SetLeaderNodeToKnownLeader(newLeaderNode);
                            return new CompletedTask();
                        }
                        if (Log.IsDebugEnabled)
                        {
                            Log.Debug($"Fetched topology from cache, no leader found.\n Nodes:" + string.Join(",", Nodes.Select(n => n.Url)));
                        } 
                        SetLeaderNodeToNull();
                    }
                }

                return refreshReplicationInformationTask = Task.Factory.StartNew(() =>
                {
                    var tryFailoverServers = false;
                    var triedFailoverServers = FailoverServers == null || FailoverServers.Length == 0;
                    for (;;)
                    {
                        //taking a snapshot so we could tell if the value changed while we fetch the topology
                        var prevLeader = LeaderNode;
                        var nodes = NodeUrls.ToHashSet();

                        if (tryFailoverServers == false)
                        {
                            if (nodes.Count == 0)
                                nodes.Add(primaryNode);
                        }
                        else
                        {
                            nodes.Add(primaryNode); // always check primary node during failover check

                            foreach (var failoverServer in FailoverServers)
                            {
                                var node = ConvertReplicationDestinationToOperationMetadata(failoverServer, ClusterInformation.NotInCluster);
                                if (node != null)
                                    nodes.Add(node);
                            }

                            triedFailoverServers = true;
                        }

                        var replicationDocuments = nodes
                            .Select(operationMetadata => new
                            {
                                Node = operationMetadata,
                                Task = getReplicationDestinationsTask(operationMetadata)
                            })
                            .ToArray();

                        var tasks = replicationDocuments
                            .Select(x => (Task)x.Task)
                            .ToArray();

                        var tasksCompleted = Task.WaitAll(tasks, ReplicationDestinationsTopologyTimeout);
                        if (Log.IsDebugEnabled && tasksCompleted == false)
                        {
                            Log.Debug($"During fetch topology {tasks.Count(t=>t.IsCompleted)} servers have responded out of {tasks.Length}");
                        }
                        replicationDocuments.ForEach(x =>
                        {
                            if (x.Task.IsCompleted && x.Task.Result != null)
                                FailureCounters.ResetFailureCount(x.Node.Url);
                        });

                        var newestTopology = replicationDocuments
                            .Where(x => x.Task.IsCompleted && x.Task.Result != null)
                            .OrderByDescending(x => x.Task.Result.Term)
                            .ThenByDescending(x =>
                            {
                                var index = x.Task.Result.ClusterCommitIndex;
                                return x.Task.Result.ClusterInformation.IsLeader ? index + 1 : index;
                            })
                            .FirstOrDefault();


                        if (newestTopology == null && FailoverServers != null && FailoverServers.Length > 0 && tryFailoverServers == false)
                            tryFailoverServers = true;

                        if (newestTopology == null && triedFailoverServers)
                        {
                            if (Log.IsDebugEnabled)
                                Log.Debug($"Fetching topology resulted with no topology, tried failoever servers, setting leader node to primary node ({primaryNode}).");
                            //if the leader Node is not null this means that somebody updated it, we don't want to overwrite it with the primary.
                            // i'm rasing the leader changed event although we don't have a real leader because some tests don't wait for leader but actually any node
                            //Todo: change back to: if (SetLeaderNodeIfLeaderIsNull(primaryNode, false) == false)
                            if (SetLeaderNodeIfLeaderIsNull(primaryNode) == false)
                            {
                                return;
                            }
                            
                            if(Nodes.Count == 0)
                                Nodes = new List<OperationMetadata>
                                {
                                    primaryNode
                                };
                            return;
                        }

                        if (newestTopology != null)
                        {
                            Nodes = GetNodes(newestTopology.Node, newestTopology.Task.Result);
                            var newLeader = newestTopology.Task.Result.ClusterInformation.IsLeader ?
                                Nodes.FirstOrDefault(n => n.Url == newestTopology.Node.Url) : null;

                            ReplicationInformerLocalCache.TrySavingClusterNodesToLocalCache(serverHash, Nodes);

                            if (newestTopology.Task.Result.ClientConfiguration != null)
                            {
                                if (newestTopology.Task.Result.ClientConfiguration.FailoverBehavior == null)
                                {
                                    if(Log.IsDebugEnabled)
                                        Log.Debug($"Server side failoever configuration is set to let client decide, client decided on {serverClient.convention.FailoverBehavior}. ");
                                    newestTopology.Task.Result.ClientConfiguration.FailoverBehavior = serverClient.convention.FailoverBehavior;
                                }
                                else if (Log.IsDebugEnabled)
                                {
                                    Log.Debug($"Server enforced failoever behavior {newestTopology.Task.Result.ClientConfiguration.FailoverBehavior}. ");
                                }
                                serverClient.convention.UpdateFrom(newestTopology.Task.Result.ClientConfiguration);
                            }                            
                            if (newLeader != null)
                            {
                                SetLeaderNodeToKnownLeader(newLeader);
                                return;
                            }
                            //here we try to set leader node to null but we might fail since it was changed.
                            //We just need to make sure that the leader node is not null and we can stop searching.
                            if(SetLeaderNodeToNullIfPrevIsTheSame(prevLeader) == false && LeaderNode != null)
                                return;
                        }

                        Thread.Sleep(500);
                    }
                }).ContinueWith(t =>
                {
                    lastUpdate = SystemTime.UtcNow;
                    refreshReplicationInformationTask = null;
                });
            }
        }

        private static OperationMetadata GetLeaderNode(IEnumerable<OperationMetadata> nodes)
        {
            return nodes.FirstOrDefault(x => x.ClusterInformation != null && x.ClusterInformation.IsLeader);
        }

        private static List<OperationMetadata> GetNodes(OperationMetadata node, ReplicationDocumentWithClusterInformation replicationDocument)
        {
            var nodes = replicationDocument.Destinations
                .Select(x => ConvertReplicationDestinationToOperationMetadata(x, x.ClusterInformation))
                .Where(x => x != null)
                .ToList();

            nodes.Add(new OperationMetadata(node.Url, node.Credentials, replicationDocument.ClusterInformation));

            return nodes;
        }

        private static OperationMetadata ConvertReplicationDestinationToOperationMetadata(ReplicationDestination destination, ClusterInformation clusterInformation)
        {
            var url = string.IsNullOrEmpty(destination.ClientVisibleUrl) ? destination.Url : destination.ClientVisibleUrl;
            if (string.IsNullOrEmpty(url) || destination.CanBeFailover() == false)
                return null;

            if (string.IsNullOrEmpty(destination.Database))
                return new OperationMetadata(url, destination.Username, destination.Password, destination.Domain, destination.ApiKey, clusterInformation);

            return new OperationMetadata(MultiDatabase.GetRootDatabaseUrl(url).ForDatabase(destination.Database), destination.Username, destination.Password, destination.Domain, destination.ApiKey, clusterInformation);
        }

        public IDisposable ForceReadFromMaster()
        {
            var strippingBase = readStripingBase;
            readStripingBase = -1;
            return new DisposableAction(() => { readStripingBase = strippingBase; });
        }

        public event EventHandler<FailoverStatusChangedEventArgs> FailoverStatusChanged = delegate { };
    }
}
