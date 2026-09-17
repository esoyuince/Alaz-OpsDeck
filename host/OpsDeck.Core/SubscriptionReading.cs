using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace OpsDeck.Core;
public sealed partial class CloudflareClient
{
    public async Task<Metric> WorkersSubscription(CancellationToken ct)
    {
        using var doc=await Request($"accounts/{account}/subscriptions",null,ct);
        return ParseWorkersSubscription(doc.RootElement,DateTimeOffset.UtcNow);
    }
    public static Metric ParseWorkersSubscription(JsonElement root,DateTimeOffset now)
    {
        var rows=root.GetProperty("result");
        if(rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>1000)throw new SourceFailure(SourceState.Error,"Invalid subscription list.");
        if(root.TryGetProperty("result_info",out var info)&&info.TryGetProperty("total_count",out var count)&&(!count.TryGetInt32(out var total)||total!=rows.GetArrayLength()))
            throw new SourceFailure(SourceState.Partial,"Subscription list incomplete; configured fee retained.");
        Metric? found=null;
        foreach(var row in rows.EnumerateArray())
        {
            string Text(JsonElement x,string key)=>x.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
            if(!row.TryGetProperty("rate_plan",out var plan)||plan.ValueKind!=JsonValueKind.Object)continue;
            // Only identifiable base Workers plans. Workers AI/Platforms are separate products.
            string name=Text(plan,"public_name").Trim();string id=Text(plan,"id").Trim();
            bool baseName=Regex.IsMatch(name,@"^(Cloudflare\s+)?Workers(\s+(Paid|Free|Standard|Bundled|Unbound))?(\s+Plan)?$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
            bool baseId=Regex.IsMatch(id,@"^workers([_-](paid|free|standard|bundled|unbound))?$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
            if(!baseName&&!(name.Length==0&&baseId))continue;
            if(Text(plan,"scope")=="zone"||Text(row,"state") is "Cancelled" or "Failed" or "Expired")continue;
            if(Text(row,"state") is not ("Paid" or "Provisioned")||Text(row,"frequency")!="monthly"||Text(row,"currency")!="USD")
                throw new SourceFailure(SourceState.Partial,"Workers subscription state/frequency/currency not supported.");
            double price=NonNegative(row,"price");if(price>1000000)throw new SourceFailure(SourceState.Error,"Invalid monthly price.");
            DateTimeOffset? Date(string key)
            {
                if(!row.TryGetProperty(key,out var value)||value.ValueKind==JsonValueKind.Null)return null;
                if(value.ValueKind!=JsonValueKind.String||!DateTimeOffset.TryParse(value.GetString(),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var date))
                    throw new SourceFailure(SourceState.Error,"Invalid subscription date.");
                return date;
            }
            var start=Date("current_period_start");var end=Date("current_period_end");
            if(start.HasValue&&end.HasValue&&end<=start)throw new SourceFailure(SourceState.Error,"Invalid subscription period.");
            if(end.HasValue&&end<=now)continue;
            if(start.HasValue&&start>now)throw new SourceFailure(SourceState.Partial,"Future subscription period.");
            if(found!=null)throw new SourceFailure(SourceState.Partial,"Multiple Workers subscriptions; no double counting.");
            found=new(SourceState.Ok,price,CollectedAt:now,Unit:"USD",PeriodStart:start,PeriodEnd:end,Note:"MONTHLY / API",Detail:"Active monthly Workers subscription from account subscriptions API. Not usage charges.");
        }
        return found??new(SourceState.NoData,CollectedAt:now,Detail:"No identifiable active monthly Workers subscription; no zero-price inference.");
    }
}
