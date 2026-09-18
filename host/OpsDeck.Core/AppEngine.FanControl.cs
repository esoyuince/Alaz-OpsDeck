namespace OpsDeck.Core;

public sealed partial class AppEngine
{
    private void ObserveFanControlLine(string line)
    {
        if(!PanelFanControlRequest.TryParse(line,out var request)||request==null)return;
        fanControlRequests.Enqueue(request);
        log.Event("fan_control_requested",new{request=request.RequestId,mode=request.Mode.ToString(),speed=request.SpeedPercent});
    }

    private async Task FanControlLoop()
    {
        long nextRefresh=0;
        while(!stop.IsCancellationRequested)
        {
            if(fanControlRequests.TryDequeue(out var request))
            {
                var result=request.Mode==OmenFanMode.Manual
                    ?await fanControl.SetManualAsync(request.SpeedPercent??throw new InvalidOperationException("Manual fan request missing speed"),stop.Token)
                    :await fanControl.SetModeAsync(request.Mode,stop.Token);
                log.Event("fan_control_result",new{request=request.RequestId,requested=request.Mode.ToString(),speed=request.SpeedPercent,mode=result.Mode.ToString(),actual_speed=result.SpeedPercent,supported=result.Supported,status=result.Status});
                RecordOperational(new(
                    DateTimeOffset.UtcNow,
                    result.Supported?OperationalSeverity.Info:OperationalSeverity.Warning,
                    OperationalDomain.Device,
                    result.Supported?"FAN_CONTROL_CHANGED":"FAN_CONTROL_FAILED",
                    result.Supported?$"OMEN fan control -> {result.Mode}":result.Status));
                nextRefresh=Environment.TickCount64+15000;
                continue;
            }

            long now=Environment.TickCount64;
            if(now>=nextRefresh)
            {
                var result=await fanControl.RefreshAsync(stop.Token);
                if(!result.Supported)log.Event("fan_control_status",new{supported=false,status=result.Status});
                nextRefresh=Environment.TickCount64+15000;
            }
            await Task.Delay(250,stop.Token);
        }
    }
}
