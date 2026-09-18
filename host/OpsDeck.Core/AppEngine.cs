using System.Diagnostics;
using System.IO.Ports;
using System.Text.RegularExpressions;
using Microsoft.Win32;
namespace OpsDeck.Core;

public sealed partial class AppEngine : IAsyncDisposable
{
    private readonly HostConfig config;private readonly LocalSettings settings;private readonly SafeLog log;
    private static readonly System.Text.Encoding PanelSerialEncoding=System.Text.Encoding.ASCII;
    private readonly CancellationTokenSource stop=new();private readonly List<Task> tasks=[];private readonly object gate=new();
    private FleetState fleet=FleetState.Empty;private PcSample pc=new();private long pcStamp;
    private string serialStatus="Not started";private string panelEvidence="No panel evidence yet";
    private readonly SemaphoreSlim apiGate=new(2);private readonly string generation=Guid.NewGuid().ToString("N")[..8];private AccountState[] accounts;public AccountState[] Accounts=>Volatile.Read(ref accounts);
    private PanelReceipt panelReceipts=new();public PanelReceipt PanelReceipts=>Volatile.Read(ref panelReceipts);
    private Metric generalHosting=Metric.Setup();public Metric GeneralHosting=>Volatile.Read(ref generalHosting);
    private LinkHealthSnapshot linkHealth=LinkHealthSnapshot.Initial(DateTimeOffset.UtcNow);public LinkHealthSnapshot LinkHealth=>Volatile.Read(ref linkHealth);
    private string? wifiPairingKey,wifiPairingId,wifiTlsFingerprint;private WifiTlsIdentity? wifiTlsIdentity;private int wifiPairingAcked;
    private OperationalEventStore? operationalStore;public bool OperationalTimelineAvailable=>Volatile.Read(ref operationalStore)!=null;
    private TelemetryHistoryStore? telemetryHistory;public bool TelemetryHistoryAvailable=>Volatile.Read(ref telemetryHistory)!=null;
    private LifecycleHistoryStore? lifecycleHistory;public bool LifecycleHistoryAvailable=>Volatile.Read(ref lifecycleHistory)!=null;
    private AgentCommandStore? agentCommandStore;private AgentControlPlane? agentControl;private AgentHandoffStore? agentHandoffStore;private CodexAppServerRuntime? managedCodexRuntime;private AgentPayloadRegistry? agentPayloads;public bool AgentControlAvailable=>Volatile.Read(ref agentControl)!=null;
    private CodexUsageSnapshot codexUsage=CodexUsageSnapshot.Setup;private int codexTelemetryStarted;public CodexUsageSnapshot CodexUsage=>Volatile.Read(ref codexUsage);
    private ProcessSnapshot processSnapshot=ProcessSnapshot.Empty;public ProcessSnapshot Processes=>Volatile.Read(ref processSnapshot);
    public ManagedCodexSnapshot ManagedCodex=>Volatile.Read(ref managedCodexRuntime)?.Snapshot??new(SourceState.Setup,Activity:"NOT READY");
    public DesktopCommanderSnapshot DesktopCommander=>Fleet.Agents.RemoteDesktop??DesktopCommanderSnapshot.Setup;
    private PanelHealthSnapshot panelHealth=PanelHealthSnapshot.Empty;public PanelHealthSnapshot PanelHealth=>Volatile.Read(ref panelHealth);
    private EdgeNodeReading edgeNode=EdgeNodeReading.Setup;public EdgeNodeReading EdgeNode=>Volatile.Read(ref edgeNode);
    private int locked;private long samples;private SiteResult[] sites=[];
    public FleetState Fleet=>Volatile.Read(ref fleet);public PcSample Pc=>Volatile.Read(ref pc);
    public string SerialStatus=>Volatile.Read(ref serialStatus);public string PanelEvidence=>Volatile.Read(ref panelEvidence);
    public long Samples=>Interlocked.Read(ref samples);public SiteResult[] Sites=>Volatile.Read(ref sites);
    public bool Locked {get=>Volatile.Read(ref locked)!=0;set=>Volatile.Write(ref locked,value?1:0);}
    private void LinkOpened()=>Volatile.Write(ref linkHealth,LinkHealth.OnSerialOpen());
    private void LinkForward()=>Volatile.Write(ref linkHealth,LinkHealth.OnForward(DateTimeOffset.UtcNow));
    private void LinkRecovery(SerialRecoveryDecision decision)=>Volatile.Write(ref linkHealth,LinkHealth.OnRecovery(decision,DateTimeOffset.UtcNow));
    private void LinkError()=>Volatile.Write(ref linkHealth,LinkHealth.OnError());
    private void RecordOperational(OperationalEvent e)
    {
        var store=Volatile.Read(ref operationalStore);if(store==null)return;
        try{store.Add(e);log.Event("ops_event",new{severity=e.Severity.ToString(),domain=e.Domain.ToString(),code=e.Code,summary=e.Summary});}
        catch(Exception ex)when(ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException){log.Event("ops_timeline_write_failed",new{kind=ex.GetType().Name});}
    }
    public OperationalEvent[] ReadOperationalEvents(int limit=200)
    {
        var store=Volatile.Read(ref operationalStore);if(store==null)return [];
        try{return store.Read(limit);}catch(Exception ex)when(ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException){log.Event("ops_timeline_read_failed",new{kind=ex.GetType().Name});return [];}
    }
    public OperationalEvent[] ReadOperationalEvents(DateTimeOffset from,DateTimeOffset to,int limit=200)
    {
        var store=Volatile.Read(ref operationalStore);if(store==null)return [];
        try{return store.ReadRange(from,to,limit);}catch(Exception ex)when(ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException){log.Event("ops_timeline_range_read_failed",new{kind=ex.GetType().Name});return [];}
    }
    private AgentControlPlane RequireAgentControl()=>Volatile.Read(ref agentControl)??throw new InvalidOperationException("Agent control plane unavailable.");
    public AgentCommand? ReadAgentCommand(string requestId)=>Volatile.Read(ref agentCommandStore)?.Get(requestId);
    public AgentCommand[] ReadRecentAgentCommands(int limit=100)=>Volatile.Read(ref agentCommandStore)?.ReadRecent(limit)??[];
    public AgentCommandEvent[] ReadAgentCommandEvents(string requestId,int limit=100)=>Volatile.Read(ref agentCommandStore)?.ReadEvents(requestId,limit)??[];
    public AgentHandoffRecord[] ReadAgentHandoffs(int limit=100)=>Volatile.Read(ref agentHandoffStore)?.ReadRecent(limit)??[];
    public AgentCommand RequestAgentCommand(AgentCommandRequest request)
    {
        var command=RequireAgentControl().Request(request);RecordOperational(new(DateTimeOffset.UtcNow,OperationalSeverity.Info,OperationalDomain.Agent,"AGENT_COMMAND_REQUESTED",$"{command.Provider} {command.Action} command requested; approval required"));return command;
    }
    public AgentCommand RequestManagedCodexTask(string prompt,string workingDirectory,ManagedCodexSandbox sandbox=ManagedCodexSandbox.ReadOnly)
    {
        var now=DateTimeOffset.UtcNow;string nonce=Guid.NewGuid().ToString("N");string requestId="m6_"+nonce[..20],target="managed:"+nonce[..16],key="m6.submit:"+nonce;
        var payload=new ManagedCodexPayload(prompt,workingDirectory,sandbox);var registry=Volatile.Read(ref agentPayloads)??throw new InvalidOperationException("Managed Codex payload registry unavailable.");registry.Put(requestId,payload);
        try{return RequestAgentCommand(new(requestId,AgentProviderKind.Codex,AgentCommandAction.SubmitTask,target,key,now,now.AddMinutes(2),true));}catch{registry.Remove(requestId);throw;}
    }
    public AgentCommand RequestManagedCodexContinue(string target,string prompt)
    {
        var runtime=Volatile.Read(ref managedCodexRuntime)??throw new InvalidOperationException("Managed Codex runtime unavailable.");
        var now=DateTimeOffset.UtcNow;string nonce=Guid.NewGuid().ToString("N"),requestId="m6_"+nonce[..20];
        var payload=runtime.ContinuationPayload(target,prompt);var registry=Volatile.Read(ref agentPayloads)??throw new InvalidOperationException("Managed Codex payload registry unavailable.");registry.Put(requestId,payload);
        try{return RequestAgentCommand(new(requestId,AgentProviderKind.Codex,AgentCommandAction.ContinueTask,target,"m6.continue:"+nonce,now,now.AddMinutes(2),true));}catch{registry.Remove(requestId);throw;}
    }
    public AgentCommand RequestManagedCodexAction(string target,AgentCommandAction action)
    {
        if(action is not (AgentCommandAction.StopTask or AgentCommandAction.ResumeTask or AgentCommandAction.RetryTask))throw new ArgumentException("Unsupported managed Codex action.");
        var now=DateTimeOffset.UtcNow;string nonce=Guid.NewGuid().ToString("N");return RequestAgentCommand(new("m6_"+nonce[..20],AgentProviderKind.Codex,action,target,"m6.action:"+nonce,now,now.AddMinutes(2),true));
    }
    public AgentHandoffRecord CreateAgentHandoff(AgentProviderKind from,AgentProviderKind to,string target)
    {
        var store=Volatile.Read(ref agentHandoffStore)??throw new InvalidOperationException("Agent handoff store unavailable.");string id="handoff_"+Guid.NewGuid().ToString("N")[..20];var row=store.Create(id,from,to,target,DateTimeOffset.UtcNow);RecordOperational(new(DateTimeOffset.UtcNow,OperationalSeverity.Info,OperationalDomain.Agent,"AGENT_HANDOFF_CREATED",$"{from} -> {to} handoff metadata created"));return row;
    }
    public AgentHandoffRecord TransitionAgentHandoff(string id,AgentHandoffState state,string code)=>Volatile.Read(ref agentHandoffStore)?.Transition(id,state,code,DateTimeOffset.UtcNow)??throw new InvalidOperationException("Agent handoff store unavailable.");
    public AgentCommand ApproveAgentCommand(string requestId)
    {
        var command=RequireAgentControl().Approve(requestId);RecordOperational(new(DateTimeOffset.UtcNow,OperationalSeverity.Info,OperationalDomain.Agent,"AGENT_COMMAND_APPROVED",$"{command.Provider} {command.Action} command approved"));return command;
    }
    public AgentCommand RejectAgentCommand(string requestId)
    {
        var command=RequireAgentControl().Reject(requestId);if(command.Provider==AgentProviderKind.Codex&&command.Action is AgentCommandAction.SubmitTask or AgentCommandAction.ContinueTask)Volatile.Read(ref agentPayloads)?.Remove(requestId);RecordOperational(new(DateTimeOffset.UtcNow,OperationalSeverity.Info,OperationalDomain.Agent,"AGENT_COMMAND_REJECTED",$"{command.Provider} {command.Action} command rejected"));return command;
    }
    public async Task<AgentCommand> ExecuteAgentCommandAsync(string requestId,CancellationToken ct=default)
    {
        try{var command=await RequireAgentControl().ExecuteAsync(requestId,ct);RecordOperational(new(DateTimeOffset.UtcNow,command.State==AgentCommandState.Succeeded?OperationalSeverity.Info:OperationalSeverity.Error,OperationalDomain.Agent,"AGENT_COMMAND_RESULT",$"{command.Provider} {command.Action}: {command.State} / {command.ResultCode}"));return command;}
        catch(InvalidOperationException){RecordOperational(new(DateTimeOffset.UtcNow,OperationalSeverity.Warning,OperationalDomain.Agent,"AGENT_COMMAND_BLOCKED","Agent command execution blocked by control policy/provider gate"));throw;}
    }
    public UnifiedProviderSnapshot[] ReadUnifiedProviders()
    {
        var f=Fleet;var managed=ManagedCodex;var rdc=DesktopCommander;var task=f.Agents.Tasks;
        return [
            new(UnifiedProviderKind.ManagedCodex,"Managed Codex",managed.State,managed.Activity,$"Owned {managed.OwnedThreads} | reconcile {(managed.ReconciliationRequired?"required":"no")}",AgentCapability.SubmitTask|AgentCapability.StopTask|AgentCapability.ResumeTask|AgentCapability.RetryTask|AgentCapability.ContinueTask,managed.ObservedAt),
            new(UnifiedProviderKind.ChatGptMcp,"ChatGPT MCP",f.Agents.BridgeState,task?.State==SourceState.Ok?"TASK META":"BRIDGE "+f.Agents.BridgeState.ToString().ToUpperInvariant(),"OAuth-owned ChatGPT control remains external; OpsDeck observes bridge/task metadata only.",AgentCapability.None,f.Agents.ObservedAt),
            new(UnifiedProviderKind.RemoteDesktopCommander,"Remote Desktop Commander",rdc.State,rdc.ProcessCount>0?"LOCAL ACTIVE":"OFFLINE",rdc.TotalCalls.HasValue?$"{rdc.TotalCalls} calls | {rdc.Sessions??0} sessions":rdc.Detail,AgentCapability.None,rdc.ObservedAt)
        ];
    }
    public TelemetryEventContext ReadTelemetryEventContext(DateTimeOffset from,DateTimeOffset to,int limit=200)
        =>TelemetryEventContextEngine.Build(from,to,ReadOperationalEvents(from,to,limit),limit);
    public OperationalAssessment ReadOperationalAssessment()
    {
        var now=DateTimeOffset.UtcNow;return OperationalAssessmentEngine.Evaluate(CurrentOperationalSnapshot(now),ReadOperationalEvents(OperationalEventStore.MaxRead),now);
    }
    public AlertRollup ReadAlertRollup()
    {
        var now=DateTimeOffset.UtcNow;return AlertSemantics.Evaluate(ReadOperationalAssessment(),LinkHealth,Pc,PcIsFresh,PanelHealth,now);
    }
    public OperationalCorrelation[] ReadOperationalCorrelations()
    {
        var now=DateTimeOffset.UtcNow;return OperationalCorrelationEngine.Analyze(ReadOperationalEvents(250),now);
    }
    public TelemetryTrend ReadTelemetryTrend(TelemetryWindow window)
    {
        var now=DateTimeOffset.UtcNow;var store=Volatile.Read(ref telemetryHistory);
        if(store==null)return new(window,now-TelemetryWindows.Span(window),now,[],[],new(0,0,0,SourceState.NoData,0),0);
        try{return store.ReadSummary(window,now);}catch(Microsoft.Data.Sqlite.SqliteException){log.Event("telemetry_history_read_failed");return new(window,now-TelemetryWindows.Span(window),now,[],[],new(0,0,0,SourceState.Error,0),0);}
    }
    public TelemetryPoint[] ReadTelemetrySeries(TelemetryWindow window,int maxPoints=120)
    {
        var store=Volatile.Read(ref telemetryHistory);if(store==null)return [];
        try{return store.ReadSeries(window,DateTimeOffset.UtcNow,maxPoints);}catch(Microsoft.Data.Sqlite.SqliteException){log.Event("telemetry_series_read_failed");return [];}
    }
    public LifecycleTrend ReadLifecycleTrend(TelemetryWindow window)
    {
        var now=DateTimeOffset.UtcNow;var store=Volatile.Read(ref lifecycleHistory);
        if(store==null)return LifecycleHistoryEmpty(window,now);
        try{return store.ReadSummary(window,now);}catch(Microsoft.Data.Sqlite.SqliteException){log.Event("lifecycle_history_read_failed");return LifecycleHistoryEmpty(window,now);}
    }
    public OpsBrief ReadOpsBrief(TelemetryWindow window)
    {
        if(window is not (TelemetryWindow.H24 or TelemetryWindow.D7))throw new ArgumentException("Ops brief supports 24h or 7d only.");
        var telemetry=ReadTelemetryTrend(window);var lifecycle=ReadLifecycleTrend(window);var store=Volatile.Read(ref operationalStore);
        OperationalRangeSummary events=OperationalRangeSummary.Empty;OperationalEvent[] latest=[];
        if(store!=null)try{events=store.SummarizeRange(telemetry.From,telemetry.To);latest=store.ReadRange(telemetry.From,telemetry.To,10);}
        catch(Microsoft.Data.Sqlite.SqliteException){log.Event("ops_brief_event_read_failed");}
        return OpsBriefEngine.Build(window,telemetry.From,telemetry.To,events,telemetry,lifecycle,ReadAlertRollup(),latest);
    }
    public BaselineDeviationReport ReadBaselineDeviation()
    {
        var wall=DateTimeOffset.UtcNow;var now=DateTimeOffset.FromUnixTimeSeconds(wall.ToUnixTimeSeconds()/60*60);var recentFrom=now.AddMinutes(-15);var baselineTo=recentFrom;var baselineFrom=baselineTo.AddHours(-24);var store=Volatile.Read(ref telemetryHistory);var metrics=new List<DeviationMetric>();
        foreach(var d in TelemetryHistoryStore.MetricDescriptors){
            try{var baseline=store?.ReadMetricRange(d.Key,baselineFrom,baselineTo,2000)??[];var recent=store?.ReadMetricRange(d.Key,recentFrom,now,100)??[];metrics.Add(BaselineDeviationEngine.Analyze(d,baseline,recent));}
            catch(Microsoft.Data.Sqlite.SqliteException){metrics.Add(BaselineDeviationEngine.Analyze(d,[],[]));}
        }
        var ordered=metrics.OrderByDescending(x=>x.Sufficient).ThenByDescending(x=>x.Rank).ThenBy(x=>x.Label,StringComparer.Ordinal).ToArray();
        return new(baselineFrom,baselineTo,recentFrom,now,ordered);
    }
    private static LifecycleTrend LifecycleHistoryEmpty(TelemetryWindow window,DateTimeOffset now)
    {
        var empty=new RangeStat(null,null,null,null,0);return new(window,now-TelemetryWindows.Span(window),now,0,empty,empty,empty,empty,empty,empty,empty,0,0,-1,-1,0,0,0,0,SourceState.NoData);
    }
    private OperationalSnapshot CurrentOperationalSnapshot(DateTimeOffset now)
    {
        var f=Fleet;var l=LinkHealth;var t=f.Agents.Tasks;var currentSites=Sites;
        return new(Locked,PowerSuspended,l.State,l.Recovering,l.ReopenCount,l.RomProbeCount,l.RecoveredCount,l.LastAction,l.LastReason,
            f.Agents.CodexState,f.Agents.BridgeState,t?.State??SourceState.Setup,t?.Active??-1,t?.NeedsApproval??-1,t?.Blocked??-1,t?.ExpiredClaims??-1,
            Freshness.MetricState(f.Workers,now,180),Freshness.MetricState(f.D1,now,180),Freshness.MetricState(f.R2,now,900),Freshness.MetricState(f.Hosting,now,180),
            currentSites.Count(x=>x.Healthy),currentSites.Length);
    }
    public AppEngine(HostConfig config,LocalSettings settings,Func<string,string,HttpMessageHandler?>? historyHandlerFactory=null){config.Validate();this.config=config;this.settings=settings;this.historyHandlerFactory=historyHandlerFactory;log=new(Path.Combine(settings.DirectoryPath,"logs"));accounts=config.EffectiveAccounts().Select(p=>AccountState.Empty(p) with{Cost=ReadSavedCost(p)}).ToArray();}
    public void StartCodexTelemetry()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        if(Volatile.Read(ref started)==0)throw new InvalidOperationException("Host must be started before Codex telemetry.");
        if(Interlocked.Exchange(ref codexTelemetryStarted,1)!=0)return;
        lock(gate)tasks.Add(Task.Run(CodexUsageLoop));
    }
    private void Update(Func<FleetState,FleetState> transform){lock(gate)fleet=transform(fleet);}
    private void UpdateAccount(int slot,Func<AccountState,AccountState> transform)
    {lock(gate){var next=(AccountState[])accounts.Clone();next[slot]=transform(next[slot]);Volatile.Write(ref accounts,next);}}
    private void UpdateTotals()
    {
        var a=Accounts;var now=DateTimeOffset.UtcNow;
        Update(f=>f with{Workers=AccountAggregate.Combine(a,x=>x.Workers,now,180),D1=AccountAggregate.Combine(a,x=>x.D1,now,180),R2=AccountAggregate.Combine(a,x=>x.R2,now,900),Cost=BillingDisplay.CombineLastRead(a,now)});
    }
    public void Start(bool serial=true,bool wifiTelemetry=true)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        if(Interlocked.Exchange(ref started,1)!=0)throw new InvalidOperationException("Host already started");
        ReloadProjectMappings();
        try{
            var store=new OperationalEventStore(Path.Combine(settings.DirectoryPath,"ops-events.db"));Volatile.Write(ref operationalStore,store);
            RecordOperational(new(DateTimeOffset.UtcNow,OperationalSeverity.Info,OperationalDomain.Host,"HOST_STARTED","OpsDeck host started: M6.13-B / EdgeNode Read-Only"));
        }catch(Exception e)when(e is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException){log.Event("ops_timeline_unavailable",new{kind=e.GetType().Name});}
        try{Volatile.Write(ref telemetryHistory,new TelemetryHistoryStore(Path.Combine(settings.DirectoryPath,"telemetry-history.db")));}
        catch(Exception e)when(e is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException){log.Event("telemetry_history_unavailable",new{kind=e.GetType().Name});}
        try{Volatile.Write(ref lifecycleHistory,new LifecycleHistoryStore(Path.Combine(settings.DirectoryPath,"telemetry-history.db")));}
        catch(Exception e)when(e is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException){log.Event("lifecycle_history_unavailable",new{kind=e.GetType().Name});}
        try{
            var commandStore=new AgentCommandStore(Path.Combine(settings.DirectoryPath,"agent-control.db"));var handoffs=new AgentHandoffStore(Path.Combine(settings.DirectoryPath,"agent-handoff.db"));var payloads=new AgentPayloadRegistry();var runtime=new CodexAppServerRuntime(config,Path.Combine(settings.DirectoryPath,"managed-codex-ownership.json"));var provider=new ManagedCodexProvider(runtime,payloads);
            Volatile.Write(ref agentCommandStore,commandStore);Volatile.Write(ref agentHandoffStore,handoffs);Volatile.Write(ref agentPayloads,payloads);Volatile.Write(ref managedCodexRuntime,runtime);Volatile.Write(ref agentControl,new AgentControlPlane(commandStore,[provider]));
            RecordOperational(new(DateTimeOffset.UtcNow,OperationalSeverity.Info,OperationalDomain.Agent,"AGENT_CONTROL_READY","Agent control plane ready; managed Codex provider registered; ChatGPT/RDC observational"));
        }
        catch(Exception e)when(e is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException){log.Event("agent_control_unavailable",new{kind=e.GetType().Name});}
        StartPanelAgentControl();StartPanelCodexSessions();
        if(OperationalTimelineAvailable)tasks.Add(Task.Run(OperationalLoop));
        if(LifecycleHistoryAvailable)tasks.Add(Task.Run(LifecycleLoop));
        tasks.Add(Task.Run(PcLoop));tasks.Add(Task.Run(ProcessLoop));tasks.Add(Task.Run(AgentLoop));tasks.Add(Task.Run(HealthLoop));
        if(config.EdgeNodeEnabled)tasks.Add(Task.Run(EdgeNodeLoop));
        if(wifiTelemetry)
        {
            try
            {
                wifiPairingKey=settings.GetOrCreateWifiPairingKeyHex();wifiPairingId=WifiPairing.Id(wifiPairingKey);
                wifiTlsIdentity=WifiTlsIdentity.LoadOrCreate(settings.DirectoryPath);wifiTlsFingerprint=wifiTlsIdentity.Fingerprint;
                log.Event("wifi_tls_identity",new{fingerprint=wifiTlsFingerprint[..12],certificate_bytes=wifiTlsIdentity.CertificateDer.Length});
                var bindAddress=WifiNetworkBinding.ResolveIPv4(config.NetworkInterface);
                if(bindAddress==null)log.Event("wifi_bind_unavailable",new{network_interface=config.NetworkInterface});
                else
                {
                    tasks.Add(Task.Run(()=>new WifiTelemetryServer(bindAddress,wifiPairingKey,wifiTlsIdentity.Certificate,UsbPrimaryHealthy,BuildWifiTelemetryFrames,log).Run(stop.Token)));
                    tasks.Add(Task.Run(()=>WifiDiscoveryBeacon.Run(stop.Token)));
                }
            }
            catch(Exception e)when(e is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException or ArgumentException){log.Event("wifi_telemetry_unavailable",new{kind=e.GetType().Name});}
        }
        if(Accounts.Any(a=>a.Profile.Enabled))tasks.Add(Task.Run(PanelDetailsWarmLoop));
        if(serial)tasks.Add(Task.Run(SerialLoop));else serialStatus="Disabled for observation test";
        for(int slot=0;slot<Accounts.Length;slot++){
            int si=slot;var profile=Accounts[si].Profile;if(!profile.Enabled)continue;
            string? token=null;
            try{token=settings.ReadTokenForAccount(profile.AccountId);}
            catch(Exception e)when(e is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException){UpdateAccount(si,a=>a with{Workers=a.Workers.Failed(SourceState.Error,"Token unavailable"),D1=a.D1.Failed(SourceState.Error,"Token unavailable"),R2=a.R2.Failed(SourceState.Error,"Token unavailable"),Cost=a.Cost.Failed(SourceState.Error,"Token unavailable")});}
            if(string.IsNullOrEmpty(token)){UpdateAccount(si,a=>a with{Workers=a.Workers.Failed(SourceState.Error,"Token unavailable"),D1=a.D1.Failed(SourceState.Error,"Token unavailable"),R2=a.R2.Failed(SourceState.Error,"Token unavailable"),Cost=a.Cost.Failed(SourceState.Error,"Token unavailable")});continue;}
            var cf=new CloudflareClient(profile.AccountId,token);
            tasks.Add(Task.Run(async()=>{
                try{await Task.WhenAll(Poll(cf.Workers,m=>UpdateAccount(si,a=>a with{Workers=m}),()=>Accounts[si].Workers,60),
                    Poll(cf.D1,m=>UpdateAccount(si,a=>a with{D1=m}),()=>Accounts[si].D1,60),
                    Poll(cf.R2,m=>UpdateAccount(si,a=>a with{R2=m}),()=>Accounts[si].R2,300),
                    profile.BillingEnabled?Poll(cf.Cost,m=>PutCost(si,m),()=>Accounts[si].Cost,3600):Task.CompletedTask,
                    profile.BillingEnabled?Poll(cf.WorkersSubscription,m=>UpdateAccount(si,a=>a with{Subscription=m}),()=>Accounts[si].Subscription??Metric.Setup(),3600):Task.CompletedTask);}
                finally{cf.Dispose();}
            }));
        }
        log.Event("host_started",new{version="M6.13-B",serial,accounts=Accounts.Count(a=>a.Profile.Enabled)});
    }
    private bool UsbPrimaryHealthy()
    {
        var l=LinkHealth;return l.State==SourceState.Ok&&l.LastForwardAt is DateTimeOffset at&&(DateTimeOffset.UtcNow-at).TotalSeconds<8;
    }
    private string[] BuildWifiTelemetryFrames()
    {
        var now=DateTimeOffset.UtcNow;var result=new List<string>(12);if(PcIsFresh)result.Add(Pc.Wire());if(Processes.CollectedAt.HasValue)result.Add(Processes.Wire());
        result.Add(Fleet.Wire(now,Locked,LinkHealth,CodexUsage,ManagedCodex));var profiles=Accounts;for(int i=0;i<profiles.Length;i++)result.Add(profiles[i].Wire(i,profiles.Length,generation,now));
        result.Add(PanelAgentFrame());result.Add(PanelCodexSessionsFrame());result.Add(InventoryFrame(new PanelInventoryRequest()));return result.ToArray();
    }
    private async Task EdgeNodeLoop()
    {
        using var client=new EdgeNodeStatusClient(EdgeNodeEndpoint.Validate(config.EdgeNodeStatusUrl));
        SourceState last=SourceState.Setup;
        while(!stop.IsCancellationRequested)
        {
            EdgeNodeReading current;
            try{current=await client.Read(stop.Token);}catch(OperationCanceledException) when(stop.IsCancellationRequested){break;}
            Volatile.Write(ref edgeNode,current);
            if(current.State!=last){last=current.State;log.Event("edge_node_state",new{state=current.State.ToString(),detail=current.Detail});}
            try{await Task.Delay(TimeSpan.FromSeconds(5),stop.Token);}catch(OperationCanceledException) when(stop.IsCancellationRequested){break;}
        }
    }
    private async Task ProcessLoop()
    {
        var sampler=new ProcessSampler();using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));
        do{if(!PowerSuspended){try{Volatile.Write(ref processSnapshot,sampler.Sample());}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException){log.Event("process_collection_failed",new{kind=e.GetType().Name});}}}
        while(await timer.WaitForNextTickAsync(stop.Token));
    }
    private async Task LifecycleLoop()
    {
        while(!stop.IsCancellationRequested){
            try{Volatile.Read(ref lifecycleHistory)?.Add(LifecycleSample.Capture(PanelHealth,LinkHealth));}
            catch(Exception e)when(e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or System.ComponentModel.Win32Exception){log.Event("lifecycle_history_write_failed",new{kind=e.GetType().Name});}
            await Task.Delay(TimeSpan.FromMinutes(1),stop.Token);
        }
    }
    private async Task OperationalLoop()
    {
        var before=CurrentOperationalSnapshot(DateTimeOffset.UtcNow);using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));
        while(await timer.WaitForNextTickAsync(stop.Token)){
            var now=DateTimeOffset.UtcNow;var after=CurrentOperationalSnapshot(now);
            foreach(var e in OperationalDiff.Generate(before,after,now))RecordOperational(e);before=after;
        }
    }
    private async Task PcLoop()
    {
        using var sampler=new PcSampler(config);HistoryStore? history=null;List<PcSample> minute=[];long epoch=Interlocked.Read(ref resetEpoch);int sampleFailures=0;
        try{history=new(Path.Combine(settings.DirectoryPath,"metrics.db"));}catch(Exception e)when(e is Microsoft.Data.Sqlite.SqliteException or IOException){log.Event("history_unavailable");}
        try {
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));
            do {
                if(PowerSuspended){Interlocked.Exchange(ref pcStamp,0);continue;}
                long currentEpoch=Interlocked.Read(ref resetEpoch);
                if(epoch!=currentEpoch){sampler.ResetBaseline();minute.Clear();epoch=currentEpoch;}
                PcSample s;
                try{s=sampler.Sample();sampleFailures=0;}
                catch(Exception e)when(e is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or InvalidOperationException){
                    Interlocked.Exchange(ref pcStamp,0);Interlocked.Increment(ref resetEpoch);
                    if(++sampleFailures==1||sampleFailures%60==0)log.Event("pc_collection_failed",new{kind=e.GetType().Name});continue;
                }
                if(PowerSuspended||epoch!=Interlocked.Read(ref resetEpoch))continue;
                Volatile.Write(ref pc,s);Interlocked.Exchange(ref pcStamp,Stopwatch.GetTimestamp());Interlocked.Increment(ref samples);minute.Add(s);UpdateTotals();
                if(minute.Count>=60){
                    var stamp=DateTimeOffset.UtcNow;
                    try{history?.Add(stamp,minute);}catch(Microsoft.Data.Sqlite.SqliteException){log.Event("history_write_failed");}
                    try{Volatile.Read(ref telemetryHistory)?.Add(stamp,minute,LinkHealth);}catch(Microsoft.Data.Sqlite.SqliteException){log.Event("telemetry_history_write_failed");}
                    log.Event("pc_sample_window",new {samples=Samples,cpu=s.Cpu,intel_gpu=s.IntelGpu,gpu=s.Gpu,gpu_temp=s.GpuTemp,ram_gib=s.RamUsedGib,cpu_temp=s.CpuTemp,chassis_temp=s.ChassisTemp,fan1_rpm=s.Fan1Rpm,fan2_rpm=s.Fan2Rpm,volumes=s.Volumes});minute.Clear();
                }
            }while(await timer.WaitForNextTickAsync(stop.Token));
        }finally{history?.Dispose();}
    }
    private async Task AgentLoop()
    {
        using var sampler=new AgentSampler(config);using var timer=new PeriodicTimer(TimeSpan.FromSeconds(5));
        do {
            var s=await sampler.SampleAsync(stop.Token);Update(f=>f with{Agents=s});SweepAgentPayloads();
        }while(await timer.WaitForNextTickAsync(stop.Token));
    }
    private void SweepAgentPayloads()
    {
        var store=Volatile.Read(ref agentCommandStore);var registry=Volatile.Read(ref agentPayloads);if(store==null||registry==null)return;
        store.ExpireDue(DateTimeOffset.UtcNow);var keep=store.ReadRecent(500).Where(x=>x.Provider==AgentProviderKind.Codex&&(x.Action is AgentCommandAction.SubmitTask or AgentCommandAction.ContinueTask)&&(x.State is AgentCommandState.Requested or AgentCommandState.Approved)).Select(x=>x.RequestId);registry.Prune(keep);
    }
    private async Task CodexUsageLoop()
    {
        var sampler=new CodexTelemetrySampler(config);using var timer=new PeriodicTimer(TimeSpan.FromSeconds(CodexTelemetrySampler.SampleSeconds));
        do {
            var sample=await sampler.SampleAsync(stop.Token);Volatile.Write(ref codexUsage,sample);
            if(sample.State==SourceState.Ok)log.Event("codex_usage_observed",new{plan=sample.PlanType,lifetime_tokens=sample.LifetimeTokens,today_tokens=sample.TodayTokens,quotas=sample.EffectiveQuotas.Select(q=>new{q.Id,primary_used=q.Primary?.UsedPercent,secondary_used=q.Secondary?.UsedPercent})});
            else log.Event("codex_usage_unavailable",new{state=sample.State.ToString(),kind=sample.Detail});
        }while(await timer.WaitForNextTickAsync(stop.Token));
    }    private async Task HealthLoop()
    {
        var urls=HostingScope.Urls(config.Sites.Concat(Accounts.SelectMany(a=>a.Profile.Sites)));
        if(urls.Length==0)return;
        using var health=new HealthChecks();using var limiter=new SemaphoreSlim(3);
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(60));
        do {
            var results=await Task.WhenAll(urls.Select(async url=>{await limiter.WaitAsync(stop.Token);try{return await health.Check(url,stop.Token);}finally{limiter.Release();}}));
            Volatile.Write(ref sites,results);int up=results.Count(x=>x.Healthy);
            int generalCount=HostingScope.Urls(config.Sites).Length;
            int accountCount=HostingScope.Urls(Accounts.SelectMany(a=>a.Profile.Sites)).Length;
            string scope=$"ALL: G{generalCount} A{accountCount}";
            Update(f=>f with{Hosting=HostingScope.Summarize(urls,results,DateTimeOffset.UtcNow,scope,
                $"Genel {generalCount} + hesaplara baÄŸlÄ± {accountCount}; toplam {urls.Length} benzersiz URL (Ã¶rtÃ¼ÅŸen URL bir kez). Bu bilgisayardan HTTPS kontrolÃ¼, global uptime deÄŸil.")});
            Volatile.Write(ref generalHosting,HostingScope.Summarize(config.Sites,results,DateTimeOffset.UtcNow,"GENERAL HTTPS","YalnÄ±z genel HTTPS adresleri; hesaplara taÅŸÄ±nmadÄ±."));
            for(int i=0;i<Accounts.Length;i++){
                int si=i;var configured=Accounts[si].Profile.Sites;
                var metric=HostingScope.Summarize(configured,results,DateTimeOffset.UtcNow,"ACCOUNT HTTPS","YalnÄ±z bu hesaba baÄŸlanan HTTPS adresleri; genel adresler hariÃ§.");
                UpdateAccount(si,a=>a with{Hosting=metric});
            }
            log.Event("health_observed",new {healthy=up,total=results.Length});
        }while(await timer.WaitForNextTickAsync(stop.Token));
    }
    private async Task Poll(Func<CancellationToken,Task<Metric>> collect,Action<Metric> put,Func<Metric> current,int interval)
    {
        int failures=0;
        while(!stop.IsCancellationRequested){
            int delay=interval;
            try{await apiGate.WaitAsync(stop.Token);try{put(await collect(stop.Token));}finally{apiGate.Release();}failures=0;}
            catch(OperationCanceledException)when(stop.IsCancellationRequested){return;}
            catch(SourceFailure e){put(current().Failed(e.State,e.Message));failures++;delay=Math.Max(e.RetrySeconds,Math.Max(interval,Math.Min(1800,interval*(1<<Math.Min(failures,4)))));}
            catch(Exception e)when(e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or OverflowException){put(current().Failed(SourceState.Error,"Collection failed; no zero substitution"));failures++;delay=Math.Max(interval,Math.Min(1800,interval*(1<<Math.Min(failures,4))));}
            await Task.Delay(TimeSpan.FromSeconds(delay),stop.Token);
        }
    }
    public static bool IsExpectedUsbPort(string port)
    {
        using var root=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB\VID_1A86&PID_7522");
        if(root==null)return false;
        foreach(var instance in root.GetSubKeyNames())using(var key=root.OpenSubKey(instance+@"\Device Parameters"))
            if(string.Equals(key?.GetValue("PortName")as string,port,StringComparison.OrdinalIgnoreCase))return true;
        return false;
    }
    private static int CoreAckCount(PanelReceipt p)=>p.Pc+p.Status+p.Cloud+p.Inventory;
    private async Task SerialLoop()
    {
        int failures=0;var recovery=new SerialRecoveryPolicy();var serialLife=Stopwatch.StartNew();
        while(!stop.IsCancellationRequested){
            try {
                if(SerialPaused||PowerSuspended){Volatile.Write(ref serialStatus,"Paused / COM released");await Task.Delay(100,stop.Token);continue;}
                if(!IsExpectedUsbPort(config.Port)){LinkError();Volatile.Write(ref serialStatus,"Expected CH340K not found on "+config.Port);await Task.Delay(3000,stop.Token);continue;}
                using var port=new SerialPort(config.Port,115200){DtrEnable=false,RtsEnable=false,Handshake=Handshake.None,ReadTimeout=100,WriteTimeout=1000,NewLine="\n",Encoding=PanelSerialEncoding};
                port.Open();LinkOpened();Volatile.Write(ref panelReceipts,new PanelReceipt());failures=0;Volatile.Write(ref panelEvidence,"New connection / awaiting fresh device evidence");long serialEpoch=Interlocked.Read(ref resetEpoch);Volatile.Write(ref serialStatus,"USB open; waiting for device evidence");log.Event("serial_open");
                var timer=Stopwatch.StartNew();long nextPc=0,nextProcesses=0,nextStatus=0,nextInventory=0,nextPair=0,lastInventory=-5000,lastRx=-1,lastForward=0,lastPanelPackets=-1;bool forwardSeen=false;int framesSent=0;string pending="";string? lastChatWire=null;
                var inventoryQuery=new PanelInventoryRequest();
                PanelDetailsRequest? detailsQuery=null;long nextDetails=0,lastDetails=-5000,lastDetailsRequest=-20000;
                PanelOpsViewRequest? opsViewQuery=null;long nextOpsView=0,lastOpsView=-5000,lastOpsViewRequest=-20000;
                while(!stop.IsCancellationRequested&&!SerialPaused&&!PowerSuspended&&serialEpoch==Interlocked.Read(ref resetEpoch)){
                    if(port.BytesToRead>0){
                        pending+=port.ReadExisting();if(pending.Length>16384)pending="";
                        int newline;
                        while((newline=pending.IndexOf('\n'))>=0){string line=pending[..newline].Trim();pending=pending[(newline+1)..];
                            if(wifiPairingId is string pairId&&wifiTlsFingerprint is string tlsFp&&WifiPairing.TryAck(line,pairId,tlsFp,out string pairedDevice)){Interlocked.Exchange(ref wifiPairingAcked,1);log.Event("wifi_pairing_ack",new{device=pairedDevice,pair_id=pairId,tls_pin=tlsFp[..12]});continue;}
                            ObservePanelAgentLine(line);ObservePanelCodexLine(line);
                            if(PanelInventoryRequest.TryParse(line,out var requested)&&requested!=inventoryQuery){inventoryQuery=requested!;nextInventory=Math.Max(timer.ElapsedMilliseconds,lastInventory+1000);}
                            if(PanelDetailsRequest.TryParse(line,out var detailRequest)){lastDetailsRequest=timer.ElapsedMilliseconds;if(detailRequest!=detailsQuery){detailsQuery=detailRequest;nextDetails=Math.Max(timer.ElapsedMilliseconds,lastDetails+1000);}}
                            if(PanelOpsViewRequest.TryParse(line,out var opsRequest)){lastOpsViewRequest=timer.ElapsedMilliseconds;if(opsRequest!=opsViewQuery){opsViewQuery=opsRequest;nextOpsView=Math.Max(timer.ElapsedMilliseconds,lastOpsView+1000);}}
                            if(line.Length<=240&&!line.Any(char.IsControl)&&(line.Contains("Guru Meditation Error",StringComparison.Ordinal)||line.Contains("Task watchdog got triggered",StringComparison.OrdinalIgnoreCase)||line.Contains("Stack canary",StringComparison.OrdinalIgnoreCase)||line.Contains("stack overflow",StringComparison.OrdinalIgnoreCase)||line.Contains("panic'ed",StringComparison.OrdinalIgnoreCase)||line.StartsWith("abort()",StringComparison.Ordinal)||line.StartsWith("Backtrace:",StringComparison.Ordinal)))log.Event("panel_crash",new{evidence=line});
                            var match=Regex.Match(line,@"opsdeck(?:\.ui|\.wifi)?: ((?:PC_RX|STATUS_RX|CLOUD_RX|INVENTORY_RX|AGENT_CONTROL_RX|CODEX_SESSIONS_RX|CODEX_CHAT_RX|CODEX_CHAT_REQUEST|CODEX_UI|PROCESS_RX|INVENTORY_REQUEST|DETAILS_RX|DETAILS_REQUEST|OPSVIEW_RX|OPSVIEW_REQUEST|HEALTH|TLS_PERF|SOURCE_STATE|READY|PAGE|BOOT)[ A-Za-z0-9_=.:%-]{0,240})$");
                            if(match.Success){
                                string evidence=match.Groups[1].Value;var before=panelReceipts;var after=before.Observe(evidence);Volatile.Write(ref panelReceipts,after);long observedAt=timer.ElapsedMilliseconds;
                                if(CoreAckCount(after)>CoreAckCount(before)){bool wasRecovering=recovery.Recovering;recovery.ObserveForwardProgress();LinkForward();forwardSeen=true;lastForward=observedAt;if(wasRecovering)log.Event("serial_forward_recovered",new{via="ack"});}
                                if(PanelHealthSnapshot.TryParse(evidence,DateTimeOffset.UtcNow,out var health)){
                                    var previous=PanelHealth;Volatile.Write(ref panelHealth,health);
                                    if(PanelHealthSnapshot.IsReboot(previous,health))RecordOperational(new(DateTimeOffset.UtcNow,OperationalSeverity.Warning,OperationalDomain.Device,"PANEL_REBOOT",$"Panel uptime reset: {previous.UptimeS}s -> {health.UptimeS}s"));
                                    long packets=health.Packets;if(lastPanelPackets>=0&&packets>lastPanelPackets){bool wasRecovering=recovery.Recovering;recovery.ObserveForwardProgress();LinkForward();forwardSeen=true;lastForward=observedAt;if(wasRecovering)log.Event("serial_forward_recovered",new{via="health_packets",packets});}lastPanelPackets=packets;
                                }
                                lastRx=observedAt;Volatile.Write(ref panelEvidence,evidence);log.Event("panel",new {evidence});
                            }
                        }
                    }
                    long now=timer.ElapsedMilliseconds;
                    Volatile.Write(ref serialStatus,lastRx>=0&&now-lastRx<15000?"USB connected / device responding":"USB open / no recent device response");
                    if(Volatile.Read(ref wifiPairingAcked)==0&&wifiPairingKey is string pairKey&&wifiTlsIdentity is WifiTlsIdentity tlsId&&now>=nextPair){port.WriteLine(WifiPairing.PairFrame(pairKey,tlsId.CertificateDer));framesSent++;nextPair=now+5000;await Task.Delay(20,stop.Token);}
                    if(now>=nextPc&&PcIsFresh){string pcFrame=Pc.Wire();if(System.Text.Encoding.UTF8.GetByteCount(pcFrame)>3000)throw new InvalidOperationException("PC frame exceeds protocol budget");port.WriteLine(pcFrame);framesSent++;nextPc=now+1000;await Task.Delay(150,stop.Token);}
                    if(now>=nextProcesses&&Processes.CollectedAt.HasValue){string processFrame=Processes.Wire();if(System.Text.Encoding.UTF8.GetByteCount(processFrame)>3000)throw new InvalidOperationException("Process frame exceeds protocol budget");port.WriteLine(processFrame);framesSent++;nextProcesses=now+2000;await Task.Delay(70,stop.Token);}
                    if(now>=nextStatus){var msg=Fleet.Wire(DateTimeOffset.UtcNow,Locked,LinkHealth,CodexUsage,ManagedCodex);if(System.Text.Encoding.UTF8.GetByteCount(msg)>3000)throw new InvalidOperationException("Status frame exceeds protocol budget");port.WriteLine(msg);framesSent++;await Task.Delay(160,stop.Token);
                        var profiles=Accounts;
                        for(int i=0;i<profiles.Length;i++){string frame=profiles[i].Wire(i,profiles.Length,generation,DateTimeOffset.UtcNow);if(System.Text.Encoding.UTF8.GetByteCount(frame)>3000)throw new InvalidOperationException("Account frame too large");port.WriteLine(frame);framesSent++;await Task.Delay(160,stop.Token);}
                        string agentFrame=PanelAgentFrame();if(System.Text.Encoding.UTF8.GetByteCount(agentFrame)>3000)throw new InvalidOperationException("Agent control frame exceeds protocol budget");port.WriteLine(agentFrame);framesSent++;await Task.Delay(160,stop.Token);
                        string sessionsFrame=PanelCodexSessionsFrame();if(System.Text.Encoding.UTF8.GetByteCount(sessionsFrame)>3000)throw new InvalidOperationException("Codex sessions frame exceeds protocol budget");port.WriteLine(sessionsFrame);framesSent++;await Task.Delay(160,stop.Token);
                        nextStatus=timer.ElapsedMilliseconds+5000;}
                    string? chatFrame=PanelCodexChatFrame();if(chatFrame!=null&&!string.Equals(chatFrame,lastChatWire,StringComparison.Ordinal)){if(System.Text.Encoding.UTF8.GetByteCount(chatFrame)>3000)throw new InvalidOperationException("Codex chat frame exceeds protocol budget");port.WriteLine(chatFrame);framesSent++;lastChatWire=chatFrame;await Task.Delay(50,stop.Token);}
                    if(timer.ElapsedMilliseconds>=nextInventory){port.WriteLine(InventoryFrame(inventoryQuery));framesSent++;lastInventory=timer.ElapsedMilliseconds;nextInventory=lastInventory+5000;await Task.Delay(160,stop.Token);}
                    if(detailsQuery!=null&&timer.ElapsedMilliseconds-lastDetailsRequest<16000&&timer.ElapsedMilliseconds>=nextDetails){port.WriteLine(ReadPanelDetails(detailsQuery,DateTimeOffset.UtcNow).Wire(generation));framesSent++;lastDetails=timer.ElapsedMilliseconds;nextDetails=lastDetails+5000;await Task.Delay(160,stop.Token);}
                    if(opsViewQuery!=null&&timer.ElapsedMilliseconds-lastOpsViewRequest<16000&&timer.ElapsedMilliseconds>=nextOpsView){port.WriteLine(PanelOpsViewFrame(opsViewQuery));framesSent++;lastOpsView=timer.ElapsedMilliseconds;nextOpsView=lastOpsView+5000;await Task.Delay(120,stop.Token);}
                    now=timer.ElapsedMilliseconds;long deviceAge=lastRx<0?-1:now-lastRx;long forwardAge=forwardSeen?now-lastForward:now;
                    var decision=recovery.Evaluate(serialLife.ElapsedMilliseconds,now,deviceAge,forwardAge,framesSent);
                    if(decision.Action!=SerialRecoveryAction.None){
                        LinkRecovery(decision);
                        log.Event("serial_recovery",new{action=decision.Action.ToString(),reason=decision.Reason.ToString(),attempt=decision.Attempt,connection_age_ms=now,frames_sent=framesSent,last_device_age_ms=deviceAge,last_forward_age_ms=forwardAge,last_panel_packets=lastPanelPackets});
                        if(decision.Action==SerialRecoveryAction.RomProbeReset)
                            log.Event("serial_hardware_reset_suppressed",new{reason=decision.Reason.ToString(),attempt=decision.Attempt});
                        Volatile.Write(ref serialStatus,"Panel link stalled / reopening COM (hardware reset disabled)");
                        await Task.Delay(250,stop.Token);
                        break;
                    }
                    await Task.Delay(50,stop.Token);
                }
            }catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
            catch(Exception e)when(e is UnauthorizedAccessException or IOException or InvalidOperationException or TimeoutException or System.Security.SecurityException){
                failures++;LinkError();Volatile.Write(ref serialStatus,e is UnauthorizedAccessException?"COM port busy or permission denied":"USB unavailable; reconnecting");log.Event("serial_reconnect",new {attempt=failures,kind=e.GetType().Name});
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(10,1+failures)),stop.Token);
            }
        }
        Volatile.Write(ref serialStatus,"Stopped / COM released");
    }
    private ResourceInventory inventory=ResourceInventory.Empty;
    private Task<ResourceInventory>? discovery;
    private DateTimeOffset inventoryNotBefore;
    public ResourceInventory Inventory=>Volatile.Read(ref inventory);
    public Task<ResourceInventory> DiscoverResources(CancellationToken ct)
    {
        lock(gate) {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
            if(discovery is {IsCompleted:false})return discovery;
            if(Inventory.Sets.Length>0&&DateTimeOffset.UtcNow<inventoryNotBefore)return Task.FromResult(Inventory);
            discovery=Task.Run(()=>DiscoverCore(ct));return discovery;
        }
    }
    private async Task<ResourceInventory> DiscoverCore(CancellationToken external)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(external,stop.Token);var ct=linked.Token;
        var batches=await Task.WhenAll(Accounts.Select(async a=>{
            var p=a.Profile;var sets=new List<ResourceSet>();
            if(!p.Enabled)return Enum.GetValues<ResourceKind>().Select(k=>ResourceSet.Setup(p,k)).ToArray();
            string? token;
            try{token=settings.ReadTokenForAccount(p.AccountId);}
            catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException){token=null;}
            if(string.IsNullOrEmpty(token))return Enum.GetValues<ResourceKind>().Select(k=>ResourceSet.Setup(p,k) with{State=SourceState.Error,Detail="Saved account token unavailable"}).ToArray();
            using var client=new InventoryClient(p,token,apiGate);
            foreach(var kind in Enum.GetValues<ResourceKind>())sets.Add(await client.Read(kind,ct));
            return sets.ToArray();
        }));
        var result=new ResourceInventory(batches.SelectMany(x=>x).ToArray());
        lock(gate){Volatile.Write(ref inventory,result);inventoryNotBefore=DateTimeOffset.UtcNow.AddSeconds(Math.Max(60,result.Sets.Select(x=>x.RetrySeconds).DefaultIfEmpty().Max()));}
        log.Event("inventory_observed",new{sources=result.Sets.Length,complete_sources=result.Sets.Count(x=>x.Complete),resources=result.Items.Count()});return result;
    }

    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        stop.Cancel();Task[] waiting;lock(gate){waiting=discovery==null?tasks.ToArray():tasks.Append(discovery).ToArray();}try{await Task.WhenAll(waiting);}catch(OperationCanceledException){}finally{
            RecordOperational(new(DateTimeOffset.UtcNow,OperationalSeverity.Info,OperationalDomain.Host,"HOST_STOPPED","OpsDeck host stopped"));
            var store=Interlocked.Exchange(ref operationalStore,null);store?.Dispose();
            var historyStore=Interlocked.Exchange(ref telemetryHistory,null);historyStore?.Dispose();
            var lifeStore=Interlocked.Exchange(ref lifecycleHistory,null);lifeStore?.Dispose();
            Volatile.Write(ref agentControl,null);Volatile.Write(ref agentPayloads,null);
            var runtime=Interlocked.Exchange(ref managedCodexRuntime,null);if(runtime!=null)await runtime.DisposeAsync();
            var handoffs=Interlocked.Exchange(ref agentHandoffStore,null);handoffs?.Dispose();var commandStore=Interlocked.Exchange(ref agentCommandStore,null);commandStore?.Dispose();
            var tlsIdentity=Interlocked.Exchange(ref wifiTlsIdentity,null);tlsIdentity?.Dispose();apiGate.Dispose();stop.Dispose();log.Event("host_stopped");}
    }
}
