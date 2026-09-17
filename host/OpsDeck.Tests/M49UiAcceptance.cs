using OpsDeck.Core;
using OpsDeck.Host;

namespace OpsDeck.Tests;

public static class M49UiAcceptance
{
    private static readonly string Account=new('a',32);
    private static readonly string QueueId=new('1',32);

    public static int Run(string mode)
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            try
            {
                ApplicationConfiguration.Initialize();
                if(mode is "--m49-ui-live-ai" or "--m49-ui-live-queues")RunLive(mode);
                else if(mode is "--m49-ui-fixture-ai" or "--m49-ui-fixture-queue" or "--m49-ui-fixture-block")RunFixture(mode);
                else throw new ArgumentException("Unknown M4.9 UI acceptance mode.");
            }
            catch(Exception e){failure=e;}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure!=null){Console.Error.WriteLine("M49_UI_FAILED "+failure.GetType().Name+": "+failure.Message);return 1;}
        Console.WriteLine("M49_UI_CLOSED "+mode);return 0;
    }

    private static void RunLive(string mode)
    {
        var settings=new LocalSettings();var engine=new AppEngine(settings.Load(),settings);
        try{Application.Run(mode.EndsWith("queues",StringComparison.Ordinal)?new QueuesForm(engine):new AnalyticsHistoryForm(engine));}
        finally{engine.DisposeAsync().AsTask().GetAwaiter().GetResult();}
    }

    private static void RunFixture(string mode)
    {
        string temp=Path.Combine(Path.GetTempPath(),"opsdeck-m49-ui-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var settings=new LocalSettings(temp);
            settings.Save(new HostConfig{Accounts=[new(){ProfileId="fixture",Name="M4.9 görsel fixture",AccountId=Account,Enabled=true}]});
            settings.SaveTokenForAccount(Account,"offline-ui-token-not-real");
            bool block=mode.EndsWith("block",StringComparison.Ordinal);
            var engine=new AppEngine(settings.Load(),settings,(_,_)=>new M49HistoryTests.Handler{Block=block});
            try
            {
                Form form=mode.EndsWith("queue",StringComparison.Ordinal)
                    ?new QueuesForm(engine)
                    :new AnalyticsHistoryForm(engine);
                Application.Run(form);
            }
            finally{engine.DisposeAsync().AsTask().GetAwaiter().GetResult();}
        }
        finally
        {
            string full=Path.GetFullPath(temp),root=Path.GetFullPath(Path.GetTempPath());
            if(full.StartsWith(root,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(full).StartsWith("opsdeck-m49-ui-",StringComparison.Ordinal)&&Directory.Exists(full))Directory.Delete(full,true);
        }
    }
}
