using OpsDeck.Core;
namespace OpsDeck.Host;

public sealed class AgentTaskCenterForm : Form
{
    private readonly AppEngine engine;
    private readonly DataGridView providers=new(){Dock=DockStyle.Top,Height=190,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill};
    private readonly DataGridView commands=new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill};
    private readonly DataGridView handoffs=new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill};
    private readonly TextBox prompt=new(){Multiline=true,ScrollBars=ScrollBars.Vertical,Width=650,Height=82};
    private readonly TextBox cwd=new(){Width=470,Text=Environment.CurrentDirectory};
    private readonly CheckBox workspaceWrite=new(){Text="Workspace write",AutoSize=true};
    private readonly Label status=new(){AutoSize=true,Padding=new Padding(6),Text="Ready"};
    private readonly System.Windows.Forms.Timer timer=new(){Interval=1000};
    private string currentTarget="";

    public AgentTaskCenterForm(AppEngine engine)
    {
        this.engine=engine;Text="ALAZ OPSDECK — Agent / Tool Center";ClientSize=new Size(1120,760);MinimumSize=new Size(940,650);Font=new Font("Segoe UI",10);
        providers.Columns.Add("provider","Provider");providers.Columns.Add("state","State");providers.Columns.Add("status","Status");providers.Columns.Add("detail","Detail");providers.Columns.Add("caps","Control");
        var composer=new FlowLayoutPanel{Dock=DockStyle.Top,Height=170,Padding=new Padding(8),AutoScroll=true,FlowDirection=FlowDirection.LeftToRight,WrapContents=true};
        composer.Controls.Add(new Label{Text="Managed Codex prompt",AutoSize=true,Padding=new Padding(4,8,4,0)});composer.Controls.Add(prompt);
        composer.Controls.Add(new Label{Text="Working directory",AutoSize=true,Padding=new Padding(4,8,4,0)});composer.Controls.Add(cwd);composer.Controls.Add(workspaceWrite);
        var request=new Button{Text="Request task",AutoSize=true};var approve=new Button{Text="Approve selected",AutoSize=true};var execute=new Button{Text="Execute selected",AutoSize=true};
        var stop=new Button{Text="Request Stop",AutoSize=true};var resume=new Button{Text="Request Resume",AutoSize=true};var retry=new Button{Text="Request Retry",AutoSize=true};var handoff=new Button{Text="ChatGPT → Codex handoff",AutoSize=true};
        foreach(var b in new[]{request,approve,execute,stop,resume,retry,handoff})composer.Controls.Add(b);composer.Controls.Add(status);
        var tabs=new TabControl{Dock=DockStyle.Fill};var commandTab=new TabPage("Commands / audit");var handoffTab=new TabPage("Handoffs");commandTab.Controls.Add(commands);handoffTab.Controls.Add(handoffs);tabs.TabPages.Add(commandTab);tabs.TabPages.Add(handoffTab);
        Controls.Add(tabs);Controls.Add(composer);Controls.Add(providers);
        commands.Columns.Add("id","Request");commands.Columns.Add("provider","Provider");commands.Columns.Add("action","Action");commands.Columns.Add("target","Target");commands.Columns.Add("state","State");commands.Columns.Add("result","Result");commands.Columns.Add("updated","Updated");
        handoffs.Columns.Add("id","Handoff");handoffs.Columns.Add("from","From");handoffs.Columns.Add("to","To");handoffs.Columns.Add("target","Target");handoffs.Columns.Add("state","State");handoffs.Columns.Add("code","Code");handoffs.Columns.Add("updated","Updated");
        request.Click+=(_,_)=>RequestTask();approve.Click+=(_,_)=>ApproveSelected();execute.Click+=async(_,_)=>await ExecuteSelected();stop.Click+=(_,_)=>RequestAction(AgentCommandAction.StopTask);resume.Click+=(_,_)=>RequestAction(AgentCommandAction.ResumeTask);retry.Click+=(_,_)=>RequestAction(AgentCommandAction.RetryTask);handoff.Click+=(_,_)=>CreateHandoff();
        commands.SelectionChanged+=(_,_)=>{if(commands.SelectedRows.Count>0&&commands.SelectedRows[0].Cells[3].Value is string t&&t.StartsWith("managed:",StringComparison.Ordinal))currentTarget=t;};
        timer.Tick+=(_,_)=>RefreshAll();Shown+=(_,_)=>{RefreshAll();timer.Start();};FormClosed+=(_,_)=>timer.Dispose();
    }

    private void RequestTask()
    {
        try
        {
            var sandbox=workspaceWrite.Checked?ManagedCodexSandbox.WorkspaceWrite:ManagedCodexSandbox.ReadOnly;
            var row=engine.RequestManagedCodexTask(prompt.Text,cwd.Text.Trim(),sandbox);currentTarget=row.Target;status.Text=$"Requested {row.RequestId}; approval required.";RefreshAll();
        }
        catch(Exception e)when(e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException){status.Text="Request blocked: "+e.Message;}
    }
    private string? SelectedRequest()=>commands.SelectedRows.Count>0?commands.SelectedRows[0].Cells[0].Value?.ToString():null;
    private void ApproveSelected()
    {
        string? id=SelectedRequest();if(string.IsNullOrWhiteSpace(id)){status.Text="Select a command first.";return;}
        try{var row=engine.ApproveAgentCommand(id);status.Text=$"Approved {row.RequestId}. Execution is still separate.";RefreshAll();}
        catch(Exception e)when(e is InvalidOperationException or KeyNotFoundException){status.Text="Approval blocked: "+e.Message;}
    }
    private async Task ExecuteSelected()
    {
        string? id=SelectedRequest();if(string.IsNullOrWhiteSpace(id)){status.Text="Select an approved command first.";return;}
        try{status.Text="Executing approved command…";var row=await engine.ExecuteAgentCommandAsync(id);status.Text=$"{row.State}: {row.ResultCode}";currentTarget=row.Target;RefreshAll();}
        catch(Exception e)when(e is InvalidOperationException or KeyNotFoundException or IOException or UnauthorizedAccessException){status.Text="Execution blocked: "+e.Message;}
    }
    private void RequestAction(AgentCommandAction action)
    {
        if(string.IsNullOrWhiteSpace(currentTarget)){status.Text="Select an OpsDeck-managed Codex target first.";return;}
        try{var row=engine.RequestManagedCodexAction(currentTarget,action);status.Text=$"{action} requested; approve then execute.";SelectRequest(row.RequestId);RefreshAll();}
        catch(Exception e)when(e is ArgumentException or InvalidOperationException){status.Text="Action blocked: "+e.Message;}
    }
    private void CreateHandoff()
    {
        if(string.IsNullOrWhiteSpace(currentTarget)){status.Text="Select an OpsDeck-managed Codex target first.";return;}
        try{var h=engine.CreateAgentHandoff(AgentProviderKind.ChatGPT,AgentProviderKind.Codex,currentTarget);status.Text=$"Handoff metadata {h.HandoffId} created. No ChatGPT OAuth action was performed.";RefreshAll();}
        catch(Exception e)when(e is ArgumentException or InvalidOperationException){status.Text="Handoff blocked: "+e.Message;}
    }
    private void SelectRequest(string id)
    {foreach(DataGridViewRow row in commands.Rows)if(string.Equals(row.Cells[0].Value?.ToString(),id,StringComparison.Ordinal)){row.Selected=true;commands.CurrentCell=row.Cells[0];break;}}
    private void RefreshAll()
    {
        try
        {
            int p=0;foreach(var x in engine.ReadUnifiedProviders()){while(providers.Rows.Count<=p)providers.Rows.Add();providers.Rows[p++].SetValues(x.Name,x.State,x.Status,x.Detail,x.Capabilities==AgentCapability.None?"Observe":x.Capabilities.ToString());}while(providers.Rows.Count>p)providers.Rows.RemoveAt(providers.Rows.Count-1);
            var rows=engine.ReadRecentAgentCommands(100);int c=0;foreach(var x in rows){while(commands.Rows.Count<=c)commands.Rows.Add();commands.Rows[c++].SetValues(x.RequestId,x.Provider,x.Action,x.Target,x.State,x.ResultCode,x.UpdatedAt.ToLocalTime().ToString("HH:mm:ss"));}while(commands.Rows.Count>c)commands.Rows.RemoveAt(commands.Rows.Count-1);
            var hs=engine.ReadAgentHandoffs(100);int h=0;foreach(var x in hs){while(handoffs.Rows.Count<=h)handoffs.Rows.Add();handoffs.Rows[h++].SetValues(x.HandoffId,x.From,x.To,x.Target,x.State,x.Code,x.UpdatedAt.ToLocalTime().ToString("HH:mm:ss"));}while(handoffs.Rows.Count>h)handoffs.Rows.RemoveAt(handoffs.Rows.Count-1);
            var m=engine.ManagedCodex;if(!string.IsNullOrWhiteSpace(m.ActiveTarget))currentTarget=m.ActiveTarget;
        }
        catch(Exception e)when(e is InvalidOperationException or ObjectDisposedException){status.Text="Refresh unavailable: "+e.GetType().Name;}
    }
}
