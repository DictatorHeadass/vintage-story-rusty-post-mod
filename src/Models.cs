using ProtoBuf;
namespace RustyPost;
public sealed class PostConfig
{
    public int CollectionHour {get;set;}=6;
    public int DailyGearLimit {get;set;}=25;
    public int DailyItemLimit {get;set;}=128;
    public int BinSlots {get;set;}=16;
    public Dictionary<string,PriceOverride> Prices {get;set;}=new();
    public void Validate(){if(CollectionHour is <0 or >23||DailyGearLimit is <1 or >100000||DailyItemLimit is <1 or >100000||BinSlots is <1 or >64||Prices==null||Prices.Any(p=>p.Value==null||p.Value.MilliGears is <0 or >100000000))throw new InvalidDataException("Invalid Rusty Post configuration.");}
}
public sealed class PriceOverride {public bool Enabled {get;set;} public long MilliGears {get;set;}}
public sealed class ValueEntry
{
    public string Key {get;set;}=""; public string Code {get;set;}="";public string Name {get;set;}="";
    public int Score {get;set;} public long MilliGears {get;set;} public bool Sellable {get;set;}
    public string Category {get;set;}="";public string Evidence {get;set;}=""; public string Confidence {get;set;}="rule-derived";
}
public sealed class Shipment
{
    public string Id {get;set;}=Guid.NewGuid().ToString("N");public StoredStack Stack {get;set;}=new();
    public long DueDay {get;set;} public long UnitMilliGears {get;set;} public string Key {get;set;}="";
}
public sealed class BinRecord {public string Owner {get;set;}=""; public List<Shipment> Items {get;set;}=new();}
public sealed class MailAccount
{
    public long MilliGears {get;set;} public List<StoredStack> Returns {get;set;}=new();public List<string> Receipts {get;set;}=new();
    public long LimitDay {get;set;}=-1;public long SoldMilliGears {get;set;}public Dictionary<string,int> SoldItems {get;set;}=new();
}
public sealed class PostData
{
    public int Version {get;set;}=1;public Dictionary<string,BinRecord> Bins {get;set;}=new();public Dictionary<string,MailAccount> Mail {get;set;}=new();
}
[ProtoContract] public sealed class PostRequest
{
    [ProtoMember(1)] public string Action {get;set;}="open";[ProtoMember(2)]public int X {get;set;}[ProtoMember(3)]public int Y {get;set;}[ProtoMember(4)]public int Z {get;set;}[ProtoMember(5)]public int Dimension {get;set;}
    [ProtoMember(6)]public string Entry {get;set;}="";
}
[ProtoContract]public sealed class PostReply {[ProtoMember(1)]public string Json {get;set;}="";}
public sealed class PostView
{
    public PostRequest Position {get;set;}=new();public bool Mailbox {get;set;}public string Message {get;set;}="";public string Summary {get;set;}="";
    public List<PostRow> Rows {get;set;}=new();public List<string> Receipts {get;set;}=new();
}
public sealed class PostRow {public string Id {get;set;}="";public string Text {get;set;}="";}
public static class PostMath
{
    public static long NextCollection(double day,int hour)=>checked((long)Math.Floor(day-hour/24d)+1);
    public static long CurrentCollection(double day,int hour)=>checked((long)Math.Floor(day-hour/24d));
    public static string Gears(long milli)=>(milli/1000m).ToString("0.###",System.Globalization.CultureInfo.InvariantCulture);
    public static long Value(long unit,int count,double condition)=>checked((long)Math.Floor(unit*count*Math.Clamp(condition,0,1)));
}

public sealed class StoredStack {public string Code {get;set;}="";public byte[] Bytes {get;set;}=Array.Empty<byte>();}
