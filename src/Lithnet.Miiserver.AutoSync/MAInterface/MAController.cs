﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Concurrent;
using Lithnet.Miiserver.Client;
using NLog;

namespace Lithnet.Miiserver.AutoSync
{
    internal class MAController
    {
        private const int SpinInterval = 250;

        private static Logger logger = LogManager.GetCurrentClassLogger();

        protected static ManualResetEvent GlobalStaggeredExecutionLock;
        protected static ManualResetEvent GlobalExclusiveOperationLock;
        protected static ManualResetEvent GlobalSynchronizationStepLock;
        protected static ConcurrentDictionary<Guid, ManualResetEvent> AllMaLocalOperationLocks;

        public delegate void SyncCompleteEventHandler(object sender, SyncCompleteEventArgs e);
        public static event SyncCompleteEventHandler SyncComplete;

        public delegate void RunProfileExecutionCompleteEventHandler(object sender, RunProfileExecutionCompleteEventArgs e);
        public event RunProfileExecutionCompleteEventHandler RunProfileExecutionComplete;

        public delegate void StateChangedEventHandler(object sender, MAStatusChangedEventArgs e);
        public event StateChangedEventHandler StateChanged;

        private ManualResetEvent localOperationLock;
        private ManualResetEvent serviceControlLock;
        private System.Timers.Timer importCheckTimer;
        private System.Timers.Timer unmanagedChangesCheckTimer;
        private TimeSpan importInterval;
        private CancellationTokenSource controllerCancellationTokenSource;
        private CancellationTokenSource jobCancellationTokenSource;
        private Dictionary<string, string> perProfileLastRunStatus;
        private ManagementAgent ma;
        private BlockingCollection<ExecutionParameters> pendingActions;
        private ExecutionParameterCollection pendingActionList;
        private MAControllerScript controllerScript;
        private Task internalTask;

        internal MAStatus InternalStatus;

        private List<IMAExecutionTrigger> ExecutionTriggers { get; }

        public MAControllerConfiguration Configuration { get; private set; }

        public string ExecutingRunProfile
        {
            get => this.InternalStatus.ExecutingRunProfile;
            private set
            {
                if (this.InternalStatus.ExecutingRunProfile != value)
                {
                    this.InternalStatus.ExecutingRunProfile = value;
                    this.RaiseStateChange();
                }
            }
        }

        public string ManagementAgentName => this.ma?.Name;

        public Guid ManagementAgentID => this.ma?.ID ?? Guid.Empty;

        public string Message
        {
            get => this.InternalStatus.Message;
            private set
            {
                if (this.InternalStatus.Message != value)
                {
                    this.InternalStatus.Message = value;
                    this.RaiseStateChange();
                }
            }
        }

        public string Detail
        {
            get => this.InternalStatus.Detail;
            private set
            {
                if (this.InternalStatus.Detail == value)
                {
                    return;
                }
                this.InternalStatus.Detail = value;
                this.RaiseStateChange();
            }
        }

        public bool HasSyncLock
        {
            get => this.InternalStatus.HasSyncLock;
            private set
            {
                if (this.InternalStatus.HasSyncLock != value)
                {
                    this.InternalStatus.HasSyncLock = value;
                    this.RaiseStateChange();
                }
            }
        }

        public bool HasForeignLock
        {
            get => this.InternalStatus.HasForeignLock;
            private set
            {
                if (this.InternalStatus.HasForeignLock != value)
                {
                    this.InternalStatus.HasForeignLock = value;
                    this.RaiseStateChange();
                }
            }
        }

        public bool HasExclusiveLock
        {
            get => this.InternalStatus.HasExclusiveLock;
            private set
            {
                if (this.InternalStatus.HasExclusiveLock != value)
                {
                    this.InternalStatus.HasExclusiveLock = value;
                    this.RaiseStateChange();
                }
            }
        }

        public ControlState ControlState
        {
            get => this.InternalStatus.ControlState;
            private set
            {
                if (this.InternalStatus.ControlState != value)
                {
                    this.InternalStatus.ControlState = value;
                    this.RaiseStateChange();
                }
            }
        }

        public ControllerState ExecutionState
        {
            get => this.InternalStatus.ExecutionState;
            private set
            {
                if (this.InternalStatus.ExecutionState != value)
                {
                    this.InternalStatus.ExecutionState = value;
                    this.RaiseStateChange();
                }
            }
        }

        static MAController()
        {
            MAController.GlobalSynchronizationStepLock = new ManualResetEvent(true);
            MAController.GlobalStaggeredExecutionLock = new ManualResetEvent(true);
            MAController.GlobalExclusiveOperationLock = new ManualResetEvent(true);
            MAController.AllMaLocalOperationLocks = new ConcurrentDictionary<Guid, ManualResetEvent>();
        }

        public MAController(ManagementAgent ma)
        {
            this.controllerCancellationTokenSource = new CancellationTokenSource();
            this.ma = ma;
            this.InternalStatus = new MAStatus() { ManagementAgentName = this.ma.Name, ManagementAgentID = this.ma.ID };
            this.ControlState = ControlState.Stopped;
            this.ExecutionTriggers = new List<IMAExecutionTrigger>();
            this.localOperationLock = new ManualResetEvent(true);
            this.serviceControlLock = new ManualResetEvent(true);
            MAController.AllMaLocalOperationLocks.TryAdd(this.ma.ID, this.localOperationLock);
            MAController.SyncComplete += this.MAController_SyncComplete;
        }

        internal void RaiseStateChange()
        {
            Task.Run(() =>
            {
                try
                {
                    this.StateChanged?.Invoke(this, new MAStatusChangedEventArgs(this.InternalStatus, this.ma.Name));
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "Unable to relay state change");
                }
            }); // Using the global cancellation token here prevents the final state messages being received (see issue #80)
        }

        private void Setup(MAControllerConfiguration config)
        {
            if (!this.ma.Name.Equals(config.ManagementAgentName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Configuration was provided for the management agent {config.ManagementAgentName} for a controller configured for {this.ma.Name}");
            }

            this.Configuration = config;
            this.InternalStatus.ActiveVersion = config.Version;
            this.ControlState = config.Disabled ? ControlState.Disabled : ControlState.Stopped;
            this.controllerScript = new MAControllerScript(config);
            this.AttachTrigger(config.Triggers?.ToArray());
        }

        private void SetupUnmanagedChangesCheckTimer()
        {
            this.unmanagedChangesCheckTimer = new System.Timers.Timer();
            this.unmanagedChangesCheckTimer.Elapsed += this.UnmanagedChangesCheckTimer_Elapsed;
            this.unmanagedChangesCheckTimer.AutoReset = true;
            this.unmanagedChangesCheckTimer.Interval = Global.RandomizeOffset(RegistrySettings.UnmanagedChangesCheckInterval.TotalMilliseconds);
            this.unmanagedChangesCheckTimer.Start();
        }

        private void UnmanagedChangesCheckTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (this.ControlState != ControlState.Running)
            {
                return;
            }

            this.CheckAndQueueUnmanagedChanges();
        }

        private void SetupImportSchedule()
        {
            if (this.Configuration.AutoImportScheduling != AutoImportScheduling.Disabled)
            {
                if (this.Configuration.AutoImportScheduling == AutoImportScheduling.Enabled ||
                    (this.ma.ImportAttributeFlows.Select(t => t.ImportFlows).Count() >= this.ma.ExportAttributeFlows.Select(t => t.ExportFlows).Count()))
                {
                    this.importCheckTimer = new System.Timers.Timer();
                    this.importCheckTimer.Elapsed += this.ImportCheckTimer_Elapsed;
                    int importSeconds = this.Configuration.AutoImportIntervalMinutes > 0 ? this.Configuration.AutoImportIntervalMinutes * 60 : MAExecutionTriggerDiscovery.GetAverageImportIntervalMinutes(this.ma) * 60;
                    this.importInterval = new TimeSpan(0, 0, Global.RandomizeOffset(importSeconds));
                    this.importCheckTimer.Interval = this.importInterval.TotalMilliseconds;
                    this.importCheckTimer.AutoReset = true;
                    this.LogInfo($"Starting import interval timer. Imports will be queued if they have not been run for {this.importInterval}");
                    this.importCheckTimer.Start();
                }
                else
                {
                    this.LogInfo("Import schedule not enabled");
                }
            }
            else
            {
                this.LogInfo("Import schedule disabled");
            }
        }

        private void ImportCheckTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (this.ControlState != ControlState.Running)
            {
                return;
            }

            this.AddPendingActionIfNotQueued(new ExecutionParameters(this.Configuration.ScheduledImportRunProfileName), "Import timer");
        }

        private void ResetImportTimerOnImport()
        {
            if (this.importCheckTimer != null)
            {
                this.Trace($"Resetting import timer for {this.importInterval}");
                this.importCheckTimer.Stop();
                this.importCheckTimer.Start();
            }
        }

        public void AttachTrigger(params IMAExecutionTrigger[] triggers)
        {
            if (triggers == null)
            {
                throw new ArgumentNullException(nameof(triggers));
            }

            foreach (IMAExecutionTrigger trigger in triggers)
            {
                this.ExecutionTriggers.Add(trigger);
            }
        }

        private void StartTriggers()
        {
            foreach (IMAExecutionTrigger t in this.ExecutionTriggers)
            {
                try
                {
                    this.LogInfo($"Registering execution trigger '{t.DisplayName}'");
                    t.Message += this.NotifierTriggerMessage;
                    t.Error += this.NotifierTriggerError;
                    t.TriggerFired += this.NotifierTriggerFired;
                    t.Start(this.ManagementAgentName);
                }
                catch (Exception ex)
                {
                    this.LogError(ex, $"Could not start execution trigger {t.DisplayName}");
                }
            }
        }

        private void NotifierTriggerMessage(object sender, TriggerMessageEventArgs e)
        {
            IMAExecutionTrigger t = (IMAExecutionTrigger)sender;
            this.LogInfo($"{t.DisplayName}: {e.Message}\n{e.Details}");
        }

        private void NotifierTriggerError(object sender, TriggerMessageEventArgs e)
        {
            IMAExecutionTrigger t = (IMAExecutionTrigger)sender;
            this.LogError($"{t.DisplayName}: ERROR: {e.Message}\n{e.Details}");
        }

        private void StopTriggers()
        {
            foreach (IMAExecutionTrigger t in this.ExecutionTriggers)
            {
                try
                {
                    this.LogInfo($"Unregistering execution trigger '{t.DisplayName}'");
                    t.TriggerFired -= this.NotifierTriggerFired;
                    t.Stop();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    this.LogError(ex, $"Could not stop execution trigger {t.DisplayName}");
                }
            }
        }

        private void QueueFollowupActions(RunDetails d)
        {
            this.QueueFollowUpActionsExport(d);
            this.QueueFollowUpActionsImport(d);
            this.QueueFollowUpActionsSync(d);
        }

        private void QueueFollowUpActionsExport(RunDetails d)
        {
            if (this.CanConfirmExport())
            {
                if (d.HasUnconfirmedExports())
                {
                    this.Trace("Unconfirmed exports in last run");
                    this.AddPendingActionIfNotQueued(new ExecutionParameters(this.Configuration.ConfirmingImportRunProfileName), d.RunProfileName, true);
                }
            }
        }

        private void QueueFollowUpActionsImport(RunDetails d)
        {
            if (d.HasStagedImports())
            {
                this.Trace("Staged imports in last run");
                this.AddPendingActionIfNotQueued(new ExecutionParameters(this.Configuration.DeltaSyncRunProfileName), d.RunProfileName, true);
            }
        }

        private void QueueFollowUpActionsSync(RunDetails d)
        {
            SyncCompleteEventHandler registeredHandlers = MAController.SyncComplete;

            if (registeredHandlers == null)
            {
                this.Trace("No sync event handlers were registered");
                return;
            }

            foreach (StepDetails s in d.StepDetails)
            {
                if (!s.StepDefinition.IsSyncStep)
                {
                    continue;
                }

                foreach (OutboundFlowCounters item in s.OutboundFlowCounters)
                {
                    if (!item.HasChanges)
                    {
                        this.Trace($"No outbound changes detected for {item.ManagementAgent}");
                        continue;
                    }

                    SyncCompleteEventArgs args = new SyncCompleteEventArgs
                    {
                        SendingMAName = this.ma.Name,
                        TargetMA = item.MAID
                    };

                    this.Trace($"Sending outbound change notification for MA {item.ManagementAgent}");
                    registeredHandlers(this, args);
                }
            }
        }

        private void LogInfo(string message)
        {
            logger.Info($"{this.ma.Name}: {message}");
            this.Detail = message;
        }

        private void LogWarn(string message)
        {
            logger.Warn($"{this.ma.Name}: {message}");
            this.Detail = message;
        }

        private void LogWarn(Exception ex, string message)
        {
            logger.Warn(ex, $"{this.ma.Name}: {message}");
            this.Detail = message;
        }

        private void LogError(string message)
        {
            logger.Error($"{this.ma.Name}: {message}");
            this.Detail = message;
        }

        private void LogError(Exception ex, string message)
        {
            logger.Error(ex, $"{this.ma.Name}: {message}");
            this.Detail = message;
        }

        private void Trace(string message)
        {
            logger.Trace(message);
        }

        private void Wait(TimeSpan duration, string name, CancellationTokenSource ts)
        {
            ts.Token.ThrowIfCancellationRequested();
            this.Trace($"SLEEP: {name}: {duration}");
            ts.Token.WaitHandle.WaitOne(duration);
            ts.Token.ThrowIfCancellationRequested();
        }

        private void Wait(ManualResetEvent mre, string name, CancellationTokenSource ts)
        {
            this.Trace($"LOCK: WAIT: {name}");
            WaitHandle.WaitAny(new[] { mre, ts.Token.WaitHandle });
            ts.Token.ThrowIfCancellationRequested();
            this.Trace($"LOCK: CLEARED: {name}");
        }

        private void WaitAndTakeLock(ManualResetEvent mre, string name, CancellationTokenSource ts)
        {
            bool gotLock = false;

            try
            {
                this.Trace($"SYNCOBJECT: WAIT: {name}");
                while (!gotLock)
                {
                    gotLock = Monitor.TryEnter(mre, MAController.SpinInterval);
                    ts.Token.ThrowIfCancellationRequested();
                }

                this.Trace($"SYNCOBJECT: LOCKED: {name}");

                this.Wait(mre, name, ts);
                this.TakeLockUnsafe(mre, name, ts);
            }
            finally
            {
                if (gotLock)
                {
                    Monitor.Exit(mre);
                    this.Trace($"SYNCOBJECT: UNLOCKED: {name}");
                }
            }
        }

        private void Wait(WaitHandle[] waitHandles, string name, CancellationTokenSource ts)
        {
            this.Trace($"LOCK: WAIT: {name}");
            while (!WaitHandle.WaitAll(waitHandles, 1000))
            {
                ts.Token.ThrowIfCancellationRequested();
            }

            ts.Token.ThrowIfCancellationRequested();
            this.Trace($"LOCK: CLEARED: {name}");
        }

        private void TakeLockUnsafe(ManualResetEvent mre, string name, CancellationTokenSource ts)
        {
            this.Trace($"LOCK: TAKE: {name}");
            mre.Reset();
            ts.Token.ThrowIfCancellationRequested();
        }

        private void ReleaseLock(ManualResetEvent mre, string name)
        {
            this.Trace($"LOCK: RELEASE: {name}");
            mre.Set();
        }

        private void RaiseRunProfileComplete(string runProfileName, string lastStepStatus, int runNumber, DateTime? startTime, DateTime? endTime)
        {
            Task.Run(() =>
            {
                try
                {
                    this.RunProfileExecutionComplete?.Invoke(this, new RunProfileExecutionCompleteEventArgs(this.ManagementAgentName, runProfileName, lastStepStatus, runNumber, startTime, endTime));
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "Unable to relay run profile complete notification");
                }
            }, this.controllerCancellationTokenSource.Token);
        }

        private void Execute(ExecutionParameters e, CancellationTokenSource ts)
        {
            try
            {
                ts.Token.ThrowIfCancellationRequested();

                this.ExecutionState = ControllerState.Waiting;
                this.ExecutingRunProfile = e.RunProfileName;
                ts.Token.ThrowIfCancellationRequested();

                if (this.ma.RunProfiles[e.RunProfileName].RunSteps.Any(t => t.IsImportStep))
                {
                    this.Trace("Import step detected. Resetting timer");
                    this.ResetImportTimerOnImport();
                }

                int count = 0;
                RunDetails r = null;

                while (count <= RegistrySettings.RetryCount || RegistrySettings.RetryCount < 0)
                {
                    ts.Token.ThrowIfCancellationRequested();
                    string result = null;

                    try
                    {
                        count++;
                        this.UpdateExecutionStatus(ControllerState.Running, "Executing");
                        this.LogInfo($"Executing {e.RunProfileName}");

                        try
                        {
                            result = this.ma.ExecuteRunProfile(e.RunProfileName, ts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            result = result ?? "canceled";
                        }
                    }
                    catch (MAExecutionException ex)
                    {
                        result = ex.Result;
                    }
                    finally
                    {
                        this.LogInfo($"{e.RunProfileName} returned {result}");
                        this.UpdateExecutionStatus(ControllerState.Processing, "Evaluating run results");
                    }

                    if (ts.IsCancellationRequested)
                    {
                        this.LogInfo($"The run profile {e.RunProfileName} was canceled");
                        return;
                    }

                    this.Wait(RegistrySettings.PostRunInterval, nameof(RegistrySettings.PostRunInterval), ts);

                    this.Trace("Getting run results");
                    r = this.ma.GetLastRun();
                    this.Trace("Got run results");

                    this.RaiseRunProfileComplete(r.RunProfileName, r.LastStepStatus, r.RunNumber, r.StartTime, r.EndTime);

                    if (RegistrySettings.RetryCodes.Contains(result))
                    {
                        this.Trace($"Operation is retryable. {count} attempt{count.Pluralize()} made");

                        if (count > RegistrySettings.RetryCount && RegistrySettings.RetryCount >= 0)
                        {
                            this.LogInfo($"Aborting run profile after {count} attempt{count.Pluralize()}");
                            break;
                        }

                        this.UpdateExecutionStatus(ControllerState.Waiting, "Waiting to retry operation");

                        int interval = Global.RandomizeOffset(RegistrySettings.RetrySleepInterval.TotalMilliseconds * count);
                        this.Trace($"Sleeping thread for {interval}ms before retry");
                        this.Wait(TimeSpan.FromMilliseconds(interval), nameof(RegistrySettings.RetrySleepInterval), ts);
                        this.LogInfo("Retrying operation");
                    }
                    else
                    {
                        this.Trace($"Result code '{result}' was not listed as retryable");
                        break;
                    }
                }

                if (r != null)
                {
                    this.PerformPostRunActions(r);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (System.Management.Automation.RuntimeException ex)
            {
                if (ex.InnerException is UnexpectedChangeException changeException)
                {
                    this.ProcessUnexpectedChangeException(changeException);
                }
                else
                {
                    this.LogError(ex, $"Controller encountered an error executing run profile {this.ExecutingRunProfile}");
                }
            }
            catch (UnexpectedChangeException ex)
            {
                this.ProcessUnexpectedChangeException(ex);
            }
            catch (Exception ex)
            {
                this.LogError(ex, $"Controller encountered an error executing run profile {this.ExecutingRunProfile}");
            }
            finally
            {
                this.UpdateExecutionStatus(ControllerState.Idle, null, null);
            }
        }

        private CancellationTokenSource CreateJobTokenSource()
        {
            this.jobCancellationTokenSource = new CancellationTokenSource();
            return CancellationTokenSource.CreateLinkedTokenSource(this.controllerCancellationTokenSource.Token, this.jobCancellationTokenSource.Token);
        }

        private void WaitOnUnmanagedRun()
        {
            if (this.ma.IsIdle())
            {
                return;
            }

            try
            {
                this.UpdateExecutionStatus(ControllerState.Running, "Unmanaged run in progress", this.ma.ExecutingRunProfileName);
                CancellationTokenSource linkedToken = this.CreateJobTokenSource();

                this.Trace("Unmanaged run in progress");
                this.WaitAndTakeLock(this.localOperationLock, nameof(this.localOperationLock), linkedToken);

                this.LogInfo($"Waiting on unmanaged run {this.ma.ExecutingRunProfileName} to finish");

                if (this.ma.RunProfiles[this.ma.ExecutingRunProfileName].RunSteps.Any(t => t.IsSyncStep))
                {
                    this.Trace("Getting sync lock for unmanaged run");

                    try
                    {
                        this.WaitAndTakeLock(MAController.GlobalSynchronizationStepLock, nameof(MAController.GlobalSynchronizationStepLock), linkedToken);
                        this.HasSyncLock = true;
                        this.ma.Wait(linkedToken.Token);
                    }
                    finally
                    {
                        if (this.HasSyncLock)
                        {
                            this.ReleaseLock(MAController.GlobalSynchronizationStepLock, nameof(MAController.GlobalSynchronizationStepLock));
                            this.HasSyncLock = false;
                        }
                    }
                }
                else
                {
                    this.ma.Wait(linkedToken.Token);
                }

                this.UpdateExecutionStatus(ControllerState.Processing, "Evaluating run results");
                linkedToken.Token.ThrowIfCancellationRequested();

                using (RunDetails ur = this.ma.GetLastRun())
                {
                    this.RaiseRunProfileComplete(ur.RunProfileName, ur.LastStepStatus, ur.RunNumber, ur.StartTime, ur.EndTime);
                    this.PerformPostRunActions(ur);
                }
            }
            finally
            {
                this.ReleaseLock(this.localOperationLock, nameof(this.localOperationLock));
                this.Trace("Unmanaged run complete");
                this.UpdateExecutionStatus(ControllerState.Idle, null, null);
            }
        }

        private void PerformPostRunActions(RunDetails r)
        {
            this.TrySendMail(r);
            this.controllerScript.ExecutionComplete(r);

            if (this.controllerScript.HasStoppedMA)
            {
                this.Stop(false);
                return;
            }

            this.QueueFollowupActions(r);
        }

        private void ProcessUnexpectedChangeException(UnexpectedChangeException ex)
        {
            if (ex.ShouldTerminateService)
            {
                this.LogWarn($"Controller script indicated that service should immediately stop. Run profile {this.ExecutingRunProfile}");
                if (AutoSyncService.ServiceInstance == null)
                {
                    Environment.Exit(1);
                }
                else
                {
                    AutoSyncService.ServiceInstance.Stop();
                }
            }
            else
            {
                this.LogWarn($"Controller indicated that management agent controller should stop further processing on this MA. Run Profile {this.ExecutingRunProfile}");
                this.Stop(false);
            }
        }

        public void Start(MAControllerConfiguration config)
        {
            if (this.ControlState == ControlState.Running)
            {
                this.Trace($"Ignoring request to start {config.ManagementAgentName} as it is already running");
                return;
            }

            if (this.ControlState != ControlState.Stopped && this.ControlState != ControlState.Disabled)
            {
                throw new InvalidOperationException($"Cannot start a controller that is in the {this.ControlState} state");
            }

            this.controllerCancellationTokenSource = new CancellationTokenSource();

            if (config.Version == 0 || config.IsMissing || config.Disabled)
            {
                logger.Info($"Ignoring start request as management agent {config.ManagementAgentName} is disabled or unconfigured");
                this.ControlState = ControlState.Disabled;
                return;
            }

            try
            {
                logger.Info($"Preparing to start controller for {config.ManagementAgentName}");

                this.WaitAndTakeLock(this.serviceControlLock, nameof(this.serviceControlLock), this.controllerCancellationTokenSource);

                this.ControlState = ControlState.Starting;

                this.pendingActionList = new ExecutionParameterCollection();
                this.pendingActions = new BlockingCollection<ExecutionParameters>(this.pendingActionList);
                this.perProfileLastRunStatus = new Dictionary<string, string>();

                this.Setup(config);

                this.LogInfo($"Starting controller");

                this.internalTask = new Task(() =>
                {
                    try
                    {
                        Thread.CurrentThread.SetThreadName($"Execution thread for {this.ManagementAgentName}");
                        this.controllerCancellationTokenSource.Token.ThrowIfCancellationRequested();
                        this.Init();
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        this.LogError(ex, "The controller encountered a unrecoverable error");
                    }
                }, this.controllerCancellationTokenSource.Token);

                this.internalTask.Start();

                this.ControlState = ControlState.Running;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "An error occurred starting the controller");
                this.Stop(false);
                this.Message = $"Startup error: {ex.Message}";
            }
            finally
            {
                this.ReleaseLock(this.serviceControlLock, nameof(this.serviceControlLock));
            }
        }

        private void TryCancelRun()
        {
            try
            {
                if (this.ma != null && !this.ma.IsIdle())
                {
                    this.LogInfo("Requesting sync engine to terminate run");
                    this.ma.StopAsync();
                }
                else
                {
                    this.LogInfo("Canceling current job");
                    this.jobCancellationTokenSource?.Cancel();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                this.LogError(ex, "Cannot cancel run");
            }
        }

        public void Stop(bool cancelRun)
        {
            try
            {
                this.WaitAndTakeLock(this.serviceControlLock, nameof(this.serviceControlLock), this.controllerCancellationTokenSource ?? new CancellationTokenSource());

                if (this.ControlState == ControlState.Stopped || this.ControlState == ControlState.Disabled)
                {
                    if (cancelRun)
                    {
                        this.TryCancelRun();
                    }

                    return;
                }

                if (this.ControlState == ControlState.Stopping)
                {
                    return;
                }

                this.ControlState = ControlState.Stopping;

                this.LogInfo("Stopping controller");
                this.pendingActions?.CompleteAdding();
                this.controllerCancellationTokenSource?.Cancel();

                this.StopTriggers();

                this.LogInfo("Stopped execution triggers");

                if (this.internalTask != null && !this.internalTask.IsCompleted)
                {
                    this.LogInfo("Waiting for cancellation to complete");
                    if (this.internalTask.Wait(TimeSpan.FromSeconds(30)))
                    {
                        this.LogInfo("Cancellation completed");
                    }
                    else
                    {
                        this.LogWarn("Controller internal task did not stop in the allowed time");
                    }
                }

                if (cancelRun)
                {
                    this.TryCancelRun();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.Error(ex, "An error occurred stopping the controller");
                this.Message = $"Stop error: {ex.Message}";
            }
            finally
            {
                this.importCheckTimer?.Stop();
                this.ExecutionTriggers.Clear();

                this.pendingActionList = null;
                this.pendingActions = null;
                this.internalTask = null;
                this.InternalStatus.Clear();
                this.ControlState = ControlState.Stopped;
                this.ReleaseLock(this.serviceControlLock, nameof(this.serviceControlLock));
            }
        }

        public void CancelRun()
        {
            this.TryCancelRun();
        }

        private void UpdateExecutionQueueState()
        {
            string items = this.GetQueueItemNames(false);

            if (items != this.InternalStatus.ExecutionQueue)
            {
                this.InternalStatus.ExecutionQueue = items;
                this.RaiseStateChange();
            }
        }

        private void UpdateExecutionStatus(ControllerState state, string message)
        {
            this.InternalStatus.ExecutionState = state;
            this.InternalStatus.Message = message;
            this.RaiseStateChange();
        }

        private void UpdateExecutionStatus(ControllerState state, string message, string executingRunProfile)
        {
            this.InternalStatus.ExecutionState = state;
            this.InternalStatus.Message = message;
            this.InternalStatus.ExecutingRunProfile = executingRunProfile;
            this.RaiseStateChange();
        }

        private void UpdateExecutionStatus(ControllerState state, string message, string executingRunProfile, string executionQueue)
        {
            this.InternalStatus.ExecutionState = state;
            this.InternalStatus.Message = message;
            this.InternalStatus.ExecutingRunProfile = executingRunProfile;
            this.InternalStatus.ExecutionQueue = executionQueue;
            this.RaiseStateChange();
        }

        private void Init()
        {
            try
            {
                this.WaitOnUnmanagedRun();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "An error occurred in an unmanaged run");
            }

            this.controllerCancellationTokenSource.Token.ThrowIfCancellationRequested();

            this.CheckAndQueueUnmanagedChanges();

            this.controllerCancellationTokenSource.Token.ThrowIfCancellationRequested();

            this.StartTriggers();

            this.SetupImportSchedule();

            this.SetupUnmanagedChangesCheckTimer();

            this.controllerCancellationTokenSource.Token.ThrowIfCancellationRequested();

            try
            {
                this.LogInfo("Starting action processing queue");
                this.UpdateExecutionStatus(ControllerState.Idle, null, null);

                // ReSharper disable once InconsistentlySynchronizedField
                foreach (ExecutionParameters action in this.pendingActions.GetConsumingEnumerable(this.controllerCancellationTokenSource.Token))
                {
                    this.controllerCancellationTokenSource.Token.ThrowIfCancellationRequested();

                    this.UpdateExecutionStatus(ControllerState.Waiting, "Staging run", action.RunProfileName, this.GetQueueItemNames(false));

                    if (this.controllerScript.SupportsShouldExecute)
                    {
                        this.Message = "Asking controller script for execution permission";

                        if (!this.controllerScript.ShouldExecute(action.RunProfileName))
                        {
                            this.LogWarn($"Controller script indicated that run profile {action.RunProfileName} should not be executed");
                            continue;
                        }
                    }

                    this.SetExclusiveMode(action);
                    this.TakeLocksAndExecute(action);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                this.LogInfo("Stopped action processing queue");
            }
        }

        private void TakeLocksAndExecute(ExecutionParameters action)
        {
            ConcurrentBag<ManualResetEvent> otherLocks = new ConcurrentBag<ManualResetEvent>();

            try
            {
                this.WaitOnUnmanagedRun();

                this.jobCancellationTokenSource = this.CreateJobTokenSource();

                this.UpdateExecutionStatus(ControllerState.Waiting, "Waiting for lock holder to finish", action.RunProfileName);
                this.Wait(MAController.GlobalExclusiveOperationLock, nameof(MAController.GlobalExclusiveOperationLock), this.jobCancellationTokenSource);

                if (action.Exclusive)
                {
                    this.Message = "Waiting to take lock";
                    this.LogInfo($"Entering exclusive mode for {action.RunProfileName}");

                    // Signal all controllers to wait before running their next job
                    this.WaitAndTakeLock(MAController.GlobalExclusiveOperationLock, nameof(MAController.GlobalExclusiveOperationLock), this.jobCancellationTokenSource);
                    this.HasExclusiveLock = true;

                    this.Message = "Waiting for other MAs to finish";
                    this.LogInfo("Waiting for all MAs to complete");
                    // Wait for all  MAs to finish their current job
                    this.Wait(MAController.AllMaLocalOperationLocks.Values.ToArray<WaitHandle>(), nameof(MAController.AllMaLocalOperationLocks), this.jobCancellationTokenSource);
                }

                if (this.StepRequiresSyncLock(action.RunProfileName))
                {
                    this.Message = "Waiting to take lock";
                    this.LogInfo("Waiting to take sync lock");
                    this.WaitAndTakeLock(MAController.GlobalSynchronizationStepLock, nameof(MAController.GlobalSynchronizationStepLock), this.jobCancellationTokenSource);
                    this.HasSyncLock = true;
                }

                // If another operation in this controller is already running, then wait for it to finish before taking the lock for ourselves
                this.Message = "Waiting to take lock";
                this.WaitAndTakeLock(this.localOperationLock, nameof(this.localOperationLock), this.jobCancellationTokenSource);

                if (this.Configuration.LockManagementAgents != null)
                {
                    List<Task> tasks = new List<Task>();

                    foreach (string managementAgent in this.Configuration.LockManagementAgents)
                    {
                        Guid? id = Global.FindManagementAgent(managementAgent, Guid.Empty);

                        if (id == null)
                        {
                            this.LogInfo($"Cannot take lock for management agent {managementAgent} as the management agent cannot be found");
                            continue;
                        }

                        if (id == this.ManagementAgentID)
                        {
                            this.Trace("Not going to wait on own lock!");
                            continue;
                        }

                        tasks.Add(Task.Run(() =>
                        {
                            Thread.CurrentThread.SetThreadName($"Get localOperationLock on {managementAgent} for {this.ManagementAgentName}");
                            ManualResetEvent h = MAController.AllMaLocalOperationLocks[id.Value];
                            this.WaitAndTakeLock(h, $"localOperationLock for {managementAgent}", this.jobCancellationTokenSource);
                            otherLocks.Add(h);
                            this.HasForeignLock = true;
                        }, this.jobCancellationTokenSource.Token));
                    }

                    if (tasks.Any())
                    {
                        this.Message = $"Waiting to take locks";
                        Task.WaitAll(tasks.ToArray(), this.jobCancellationTokenSource.Token);
                    }
                }

                // Grab the staggered execution lock, and hold for x seconds
                // This ensures that no MA can start within x seconds of another MA
                // to avoid deadlock conditions
                this.Message = "Preparing to start management agent";
                bool tookStaggerLock = false;
                try
                {
                    this.WaitAndTakeLock(MAController.GlobalStaggeredExecutionLock, nameof(MAController.GlobalStaggeredExecutionLock), this.jobCancellationTokenSource);
                    tookStaggerLock = true;
                    this.Wait(RegistrySettings.ExecutionStaggerInterval, nameof(RegistrySettings.ExecutionStaggerInterval), this.jobCancellationTokenSource);
                }
                finally
                {
                    if (tookStaggerLock)
                    {
                        this.ReleaseLock(MAController.GlobalStaggeredExecutionLock, nameof(MAController.GlobalStaggeredExecutionLock));
                    }
                }

                this.Execute(action, this.jobCancellationTokenSource);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                this.UpdateExecutionStatus(ControllerState.Idle, null, null);

                // Reset the local lock so the next operation can run
                this.ReleaseLock(this.localOperationLock, nameof(this.localOperationLock));

                if (this.HasSyncLock)
                {
                    this.ReleaseLock(MAController.GlobalSynchronizationStepLock, nameof(MAController.GlobalSynchronizationStepLock));
                    this.HasSyncLock = false;
                }

                if (this.HasExclusiveLock)
                {
                    // Reset the global lock so pending operations can run
                    this.ReleaseLock(MAController.GlobalExclusiveOperationLock, nameof(MAController.GlobalExclusiveOperationLock));
                    this.HasExclusiveLock = false;
                }

                if (otherLocks.Any())
                {
                    foreach (ManualResetEvent e in otherLocks)
                    {
                        this.ReleaseLock(e, "foreign localOperationLock");
                    }

                    this.HasForeignLock = false;
                }
            }
        }

        private void SetExclusiveMode(ExecutionParameters action)
        {
            if (Program.ActiveConfig.Settings.RunMode == RunMode.Exclusive)
            {
                action.Exclusive = true;
            }
            else if (Program.ActiveConfig.Settings.RunMode == RunMode.Supported)
            {
                if (this.IsSyncStep(action.RunProfileName))
                {
                    action.Exclusive = true;
                }
            }
        }

        private bool StepRequiresSyncLock(string runProfileName)
        {
            if (this.IsSyncStep(runProfileName))
            {
                return true;
            }

            if (this.ma.Category == "FIM")
            {
                if (this.ma.RunProfiles[runProfileName].RunSteps.Any(t => t.Type == RunStepType.DeltaImport))
                {
                    return true;
                }

                if (this.ma.RunProfiles[runProfileName].RunSteps.Any(t => t.Type == RunStepType.Export))
                {
                    if (RegistrySettings.GetSyncLockForFimMAExport)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool IsSyncStep(string runProfileName)
        {
            return this.ma.RunProfiles[runProfileName].RunSteps.Any(t => t.IsSyncStep);
        }

        private void CheckAndQueueUnmanagedChanges()
        {
            try
            {
                this.Trace("Checking for unmanaged changes");

                // If another operation in this controller is already running, then wait for it to finish
                this.WaitAndTakeLock(this.localOperationLock, nameof(this.localOperationLock), this.controllerCancellationTokenSource);

                if (this.ShouldExportPendingChanges())
                {
                    this.AddPendingActionIfNotQueued(new ExecutionParameters(this.Configuration.ExportRunProfileName), "Pending export check");
                }

                if (this.ShouldConfirmExport())
                {
                    this.AddPendingActionIfNotQueued(new ExecutionParameters(this.Configuration.ConfirmingImportRunProfileName), "Unconfirmed export check");
                }

                if (this.ma.HasPendingImports())
                {
                    this.AddPendingActionIfNotQueued(new ExecutionParameters(this.Configuration.DeltaSyncRunProfileName), "Staged import check");
                }
            }
            finally
            {
                // Reset the local lock so the next operation can run
                this.ReleaseLock(this.localOperationLock, nameof(this.localOperationLock));
            }
        }

        private void MAController_SyncComplete(object sender, SyncCompleteEventArgs e)
        {
            if (e.TargetMA != this.ma.ID)
            {
                this.Trace($"Ignoring sync complete message from {e.SendingMAName} for {e.TargetMA}");

                return;
            }

            this.Trace($"Got sync complete message from {e.SendingMAName} for {e.TargetMA}");

            if (this.CanExport())
            {
                ExecutionParameters p = new ExecutionParameters(this.Configuration.ExportRunProfileName);
                this.AddPendingActionIfNotQueued(p, "Synchronization on " + e.SendingMAName);
            }
            else
            {
                this.Trace($"Dropping synchronization complete message from {e.SendingMAName} because MA cannot export");
            }
        }

        private void NotifierTriggerFired(object sender, ExecutionTriggerEventArgs e)
        {
            IMAExecutionTrigger trigger = null;

            try
            {
                trigger = (IMAExecutionTrigger)sender;

                if (string.IsNullOrWhiteSpace(e.Parameters.RunProfileName))
                {
                    if (e.Parameters.RunProfileType == MARunProfileType.None)
                    {
                        this.LogWarn($"Received empty run profile from trigger {trigger.DisplayName}");
                        return;
                    }
                }

                this.AddPendingActionIfNotQueued(e.Parameters, trigger.DisplayName);
            }
            catch (Exception ex)
            {
                this.LogError(ex, $"The was an unexpected error processing an incoming trigger from {trigger?.DisplayName}");
            }
        }

        internal void AddPendingActionIfNotQueued(string runProfileName, string source)
        {
            this.AddPendingActionIfNotQueued(new ExecutionParameters(runProfileName), source);
        }

        internal void AddPendingActionIfNotQueued(ExecutionParameters p, string source, bool runNext = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(p.RunProfileName))
                {
                    if (p.RunProfileType == MARunProfileType.None)
                    {
                        this.Trace("Dropping pending action request as no run profile name or run profile type was specified");
                        this.Detail = $"{source} did not specify a run profile";
                        this.RaiseStateChange();
                        return;
                    }

                    p.RunProfileName = this.Configuration.GetRunProfileName(p.RunProfileType);
                }

                if (this.pendingActions.ToArray().Contains(p))
                {
                    if (runNext && this.pendingActions.Count > 1)
                    {
                        this.LogInfo($"Moving {p.RunProfileName} to the front of the execution queue");
                        this.pendingActionList.MoveToFront(p);
                    }
                    else
                    {
                        this.Trace($"Ignoring queue request for {p.RunProfileName} as it already exists in the queue");
                        this.Detail = $"{p.RunProfileName} requested by {source} was ignored because the run profile was already queued";
                    }

                    return;
                }

                // Removing this as it may caused changes to go unseen. e.g an import is in progress, 
                // a snapshot is taken, but new items become available during the import of the snapshot

                //if (p.RunProfileName.Equals(this.ExecutingRunProfile, StringComparison.OrdinalIgnoreCase))
                //{
                //    this.Trace($"Ignoring queue request for {p.RunProfileName} as it is currently executing");
                //    return;
                //}

                this.Trace($"Got queue request for {p.RunProfileName}");

                if (runNext)
                {
                    this.pendingActions.Add(p, this.controllerCancellationTokenSource.Token);
                    this.pendingActionList.MoveToFront(p);
                    this.LogInfo($"Added {p.RunProfileName} to the front of the execution queue (triggered by: {source})");
                }
                else
                {
                    this.pendingActions.Add(p, this.controllerCancellationTokenSource.Token);
                    this.LogInfo($"Added {p.RunProfileName} to the execution queue (triggered by: {source})");
                }

                //this.Detail = $"{p.RunProfileName} added by {source}";

                this.UpdateExecutionQueueState();

                this.LogInfo($"Current queue: {this.GetQueueItemNames()}");
            }
            catch (OperationCanceledException)
            { }
            catch (Exception ex)
            {
                this.LogError(ex, $"An unexpected error occurred while adding the pending action {p?.RunProfileName}. The event has been discarded");
            }
        }

        private string GetQueueItemNames(bool includeExecuting = true)
        {
            // ToArray is implemented by BlockingCollection and allows an approximate copy of the data to be made in 
            // the event an add or remove is in progress. Other functions such as ToList are generic and can cause
            // collection modified exceptions when enumerating the values

            string queuedNames = string.Join(",", this.pendingActions.ToArray().Select(t => t.RunProfileName));

            if (includeExecuting && this.ExecutingRunProfile != null)
            {
                string current = this.ExecutingRunProfile + "*";

                if (string.IsNullOrWhiteSpace(queuedNames))
                {
                    return current;
                }
                else
                {
                    return string.Join(",", current, queuedNames);
                }
            }
            else
            {
                return queuedNames;
            }
        }

        private bool HasUnconfirmedExportsInLastRun()
        {
            return this.ma.GetLastRun()?.StepDetails?.FirstOrDefault()?.HasUnconfirmedExports() ?? false;
        }

        private bool ShouldExportPendingChanges()
        {
            return this.CanExport() && this.ma.HasPendingExports();
        }

        private bool CanExport()
        {
            return !string.IsNullOrWhiteSpace(this.Configuration.ExportRunProfileName);
        }

        private bool CanConfirmExport()
        {
            return !string.IsNullOrWhiteSpace(this.Configuration.ConfirmingImportRunProfileName);
        }

        private bool ShouldConfirmExport()
        {
            return this.CanConfirmExport() && this.HasUnconfirmedExportsInLastRun();
        }

        private void TrySendMail(RunDetails r)
        {
            try
            {
                this.SendMail(r);
            }
            catch (Exception ex)
            {
                this.LogError(ex, "Send mail failed");
            }
        }

        private void SendMail(RunDetails r)
        {
            if (this.perProfileLastRunStatus.ContainsKey(r.RunProfileName))
            {
                if (this.perProfileLastRunStatus[r.RunProfileName] == r.LastStepStatus)
                {
                    if (!Program.ActiveConfig.Settings.MailSendAllErrorInstances)
                    {
                        // The last run returned the same return code. Do not send again.
                        return;
                    }
                }
                else
                {
                    this.perProfileLastRunStatus[r.RunProfileName] = r.LastStepStatus;
                }
            }
            else
            {
                this.perProfileLastRunStatus.Add(r.RunProfileName, r.LastStepStatus);
            }

            if (!MAController.ShouldSendMail(r))
            {
                return;
            }

            MessageSender.SendMessage($"{r.MAName} {r.RunProfileName}: {r.LastStepStatus}", MessageBuilder.GetMessageBody(r));
        }

        private static bool ShouldSendMail(RunDetails r)
        {
            if (!MessageSender.CanSendMail())
            {
                return false;
            }

            return Program.ActiveConfig.Settings.MailIgnoreReturnCodes == null ||
                !Program.ActiveConfig.Settings.MailIgnoreReturnCodes.Contains(r.LastStepStatus, StringComparer.OrdinalIgnoreCase);
        }
    }
}