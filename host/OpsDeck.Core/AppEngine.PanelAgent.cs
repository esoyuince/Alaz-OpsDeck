using System.Collections.Concurrent;

namespace OpsDeck.Core;

public sealed partial class AppEngine
{
    private readonly PanelAgentAssembler panelAgentAssembler=new();
    private readonly ConcurrentQueue<PanelAgentInbound> panelAgentQueue=new();
    private readonly object panelAgentGate=new();
    private PanelAgentControlSnapshot panelAgentSnapshot=new();
    private PanelWorkspace[] panelAgentWorkspaces=[];
    private PanelAgentSubmit? panelPendingSubmit;
    private string panelPendingTarget="";
    private int panelPendingRequestId;
    private PanelAgentAction panelPendingAction;

    public PanelAgentControlSnapshot PanelAgentControl{get{lock(panelAgentGate)return panelAgentSnapshot;}}
    private void StartPanelAgentControl()
    {
        panelAgentWorkspaces=PanelWorkspaceCatalog.Discover();
        lock(panelAgentGate)panelAgentSnapshot=new(PanelAgentPhase.Idle,Result:"IDLE",UpdatedAt:DateTimeOffset.UtcNow,Workspaces:panelAgentWorkspaces);
        tasks.Add(Task.Run(PanelAgentControlLoop));
    }
    private void ObservePanelAgentLine(string line)
    {
        if(panelAgentAssembler.TryAccept(line,DateTimeOffset.UtcNow,out var inbound)&&inbound!=null)panelAgentQueue.Enqueue(inbound);
    }
    private async Task PanelAgentControlLoop()
    {
        while(!stop.IsCancellationRequested)
        {
            while(panelAgentQueue.TryDequeue(out var inbound))await HandlePanelAgentInbound(inbound);
            await Task.Delay(50,stop.Token);
        }
    }
    private async Task HandlePanelAgentInbound(PanelAgentInbound inbound)
    {
        if(Locked){SetPanelAgent(inbound.RequestId,PanelAgentAction.None,PanelAgentPhase.Failed,"LOCKED");return;}
        try
        {
            switch(inbound)
            {
                case PanelAgentSubmit submit: HandlePanelSubmit(submit);break;
                case PanelAgentActionRequest action: HandlePanelAction(action);break;
                case PanelAgentConfirm confirm: await HandlePanelConfirm(confirm);break;
                case PanelAgentCancel cancel: HandlePanelCancel(cancel);break;
            }
        }
        catch(Exception e)when(e is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException or UnauthorizedAccessException)
        {
            var action=panelPendingAction;SetPanelAgent(inbound.RequestId,action,PanelAgentPhase.Failed,"BLOCKED");ClearPanelPending();
            RecordOperational(new(DateTimeOffset.UtcNow,OperationalSeverity.Warning,OperationalDomain.Agent,"PANEL_AGENT_BLOCKED",$"Deck agent action blocked safely ({e.GetType().Name})"));
        }
    }
    private bool PanelBusy()=>PanelAgentControl.Phase is PanelAgentPhase.AwaitingConfirm or PanelAgentPhase.Executing;
    private void HandlePanelSubmit(PanelAgentSubmit submit)
    {
        if(PanelBusy())return;
        var workspace=panelAgentWorkspaces.FirstOrDefault(x=>x.Id==submit.WorkspaceId)??throw new InvalidOperationException("Unknown deck workspace.");
        new ManagedCodexPayload(submit.Prompt,workspace.Path,submit.Sandbox).Validate();
        panelPendingSubmit=submit;panelPendingTarget="";panelPendingRequestId=submit.RequestId;panelPendingAction=PanelAgentAction.Submit;
        SetPanelAgent(submit.RequestId,PanelAgentAction.Submit,PanelAgentPhase.AwaitingConfirm,"CONFIRM");
        RecordOperational(new(DateTimeOffset.UtcNow,OperationalSeverity.Info,OperationalDomain.Agent,"PANEL_AGENT_REQUESTED","Deck requested a managed Codex task; confirmation required"));
    }
    private void HandlePanelAction(PanelAgentActionRequest action)
    {
        if(PanelBusy())return;string target=ManagedCodex.ActiveTarget;
        if(string.IsNullOrWhiteSpace(target))throw new InvalidOperationException("No active managed Codex target.");
        panelPendingSubmit=null;panelPendingTarget=target;panelPendingRequestId=action.RequestId;panelPendingAction=action.Action;
        SetPanelAgent(action.RequestId,action.Action,PanelAgentPhase.AwaitingConfirm,"CONFIRM");
    }
    private async Task HandlePanelConfirm(PanelAgentConfirm confirm)
    {
        if(confirm.RequestId!=panelPendingRequestId||PanelAgentControl.Phase!=PanelAgentPhase.AwaitingConfirm)return;
        SetPanelAgent(confirm.RequestId,panelPendingAction,PanelAgentPhase.Executing,"EXECUTING");
        if(panelPendingAction==PanelAgentAction.Handoff)
        {
            if(string.IsNullOrWhiteSpace(panelPendingTarget))throw new InvalidOperationException("Deck handoff has no managed target.");
            var h=CreateAgentHandoff(AgentProviderKind.ChatGPT,AgentProviderKind.Codex,panelPendingTarget);
            h=TransitionAgentHandoff(h.HandoffId,AgentHandoffState.Ready,"READY");h=TransitionAgentHandoff(h.HandoffId,AgentHandoffState.Accepted,"ACCEPTED");TransitionAgentHandoff(h.HandoffId,AgentHandoffState.Completed,"COMPLETED");
            SetPanelAgent(confirm.RequestId,panelPendingAction,PanelAgentPhase.Succeeded,"COMPLETED");ClearPanelPending();return;
        }
        AgentCommand command;
        if(panelPendingAction==PanelAgentAction.Submit){var submit=panelPendingSubmit??throw new InvalidOperationException("Deck submit payload unavailable.");var workspace=panelAgentWorkspaces.FirstOrDefault(x=>x.Id==submit.WorkspaceId)??throw new InvalidOperationException("Unknown deck workspace.");command=RequestManagedCodexTask(submit.Prompt,workspace.Path,submit.Sandbox);}
        else{AgentCommandAction action=panelPendingAction switch{PanelAgentAction.Stop=>AgentCommandAction.StopTask,PanelAgentAction.Resume=>AgentCommandAction.ResumeTask,PanelAgentAction.Retry=>AgentCommandAction.RetryTask,_=>throw new ArgumentException("Unsupported deck action.")};if(string.IsNullOrWhiteSpace(panelPendingTarget))throw new InvalidOperationException("Deck action has no managed target.");command=RequestManagedCodexAction(panelPendingTarget,action);}
        ApproveAgentCommand(command.RequestId);var result=await ExecuteAgentCommandAsync(command.RequestId,stop.Token);
        SetPanelAgent(confirm.RequestId,panelPendingAction,result.State==AgentCommandState.Succeeded?PanelAgentPhase.Succeeded:PanelAgentPhase.Failed,string.IsNullOrWhiteSpace(result.ResultCode)?result.State.ToString().ToUpperInvariant():result.ResultCode);
        ClearPanelPending();
    }
    private void HandlePanelCancel(PanelAgentCancel cancel)
    {
        if(cancel.RequestId!=panelPendingRequestId||PanelAgentControl.Phase!=PanelAgentPhase.AwaitingConfirm)return;
        SetPanelAgent(cancel.RequestId,panelPendingAction,PanelAgentPhase.Rejected,"CANCELLED");ClearPanelPending();
    }
    private void SetPanelAgent(int requestId,PanelAgentAction action,PanelAgentPhase phase,string result)
    {
        string code=InventoryPaging.Label(string.IsNullOrWhiteSpace(result)?"UNKNOWN":result,32).ToUpperInvariant();
        lock(panelAgentGate)panelAgentSnapshot=new(phase,requestId,action,code,DateTimeOffset.UtcNow,panelAgentWorkspaces,BuildPanelHistory());
    }
    private void ClearPanelPending(){panelPendingSubmit=null;panelPendingTarget="";panelPendingRequestId=0;panelPendingAction=PanelAgentAction.None;}
    private PanelAgentHistory[] BuildPanelHistory()
    {
        var now=DateTimeOffset.UtcNow;return ReadRecentAgentCommands(4).Select(x=>new PanelAgentHistory(ActionOf(x.Action),x.State,
            string.IsNullOrWhiteSpace(x.ResultCode)?x.State.ToString().ToUpperInvariant():x.ResultCode,
            (int)Math.Clamp((now-x.UpdatedAt).TotalSeconds,0,604800))).ToArray();
    }
    private static PanelAgentAction ActionOf(AgentCommandAction action)=>action switch{AgentCommandAction.SubmitTask=>PanelAgentAction.Submit,AgentCommandAction.StopTask=>PanelAgentAction.Stop,AgentCommandAction.ResumeTask=>PanelAgentAction.Resume,AgentCommandAction.RetryTask=>PanelAgentAction.Retry,AgentCommandAction.ContinueTask=>PanelAgentAction.Continue,_=>PanelAgentAction.None};
    private string PanelAgentFrame()
    {
        PanelAgentControlSnapshot snapshot;lock(panelAgentGate)snapshot=panelAgentSnapshot with{Workspaces=panelAgentWorkspaces,History=BuildPanelHistory()};
        return snapshot.Wire(Locked,ManagedCodex);
    }
}
