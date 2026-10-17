using System.Text;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
namespace RustyPost;
public sealed partial class PostSystem : ModSystem
{
    internal ICoreServerAPI server=null!;
    internal PostConfig Config=new();internal PostData Data=new();internal Dictionary<string,ValueEntry> Catalog=new();
    private IServerNetworkChannel channel=null!;private bool ready;private long tick;
    private readonly Dictionary<string,long> requests=new();
    private const string DataKey="rustypost:v1";
    public override void Start(ICoreAPI api)
    {
        api.RegisterBlockClass("RustyPostBlock",typeof(PostBlock));
        api.RegisterBlockEntityClass("RustyPostEntity",typeof(PostEntity));
    }
    public override void StartServerSide(ICoreServerAPI api)
    {
        server=api;Config=api.LoadModConfig<PostConfig>("rustypost.json")??new();Config.Validate();api.StoreModConfig(Config,"rustypost.json");
        channel=api.Network.RegisterChannel("rustypost").RegisterMessageType<PostRequest>().RegisterMessageType<PostReply>();
        channel.SetMessageHandler<PostRequest>((p,r)=>api.Event.EnqueueMainThreadTask(()=>Handle(p,r),"rustypost-request"));
        api.Event.SaveGameLoaded+=()=>{var bytes=api.WorldManager.SaveGame.GetData(DataKey);Data=bytes==null?new():JsonConvert.DeserializeObject<PostData>(Encoding.UTF8.GetString(bytes))??throw new InvalidDataException("Missing shipping data");if(Data.Version!=1)throw new InvalidDataException("Unsupported shipping data version");};
        api.Event.GameWorldSave+=Save;
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame,()=>{
            Catalog=Valuation.Generate(api);Valuation.Export(Path.Combine(api.GetOrCreateDataPath("ModConfig"),"rustypost"),Catalog);ready=true;Collect();
            api.Logger.Notification("[Rusty Post] Catalog contains {0} collectibles; {1} enabled for default sales.",Catalog.Count,Catalog.Values.Count(e=>e.Sellable));
        });
        tick=api.Event.RegisterGameTickListener(_=>{if(ready)Collect();},1000);
        api.Event.PlayerDisconnect+=p=>requests.Remove(p.PlayerUID);
        api.Event.CanPlaceOrBreakBlock+=(IServerPlayer p,BlockSelection s,out string reason)=>{
            reason="";if(api.World.BlockAccessor.GetBlockEntity(s.Position) is not PostEntity be||be.Owner.Length==0||be.Owner==p.PlayerUID||p.HasPrivilege(Privilege.controlserver))return true;
            reason="This belongs to another player.";return false;
        };
        api.ChatCommands.Create("shippingadmin").RequiresPrivilege(Privilege.controlserver).WithDescription("Rusty Post configuration")
            .BeginSubCommand("reload").WithDescription("Reload sale prices and collection settings")
            .HandleWith(_=>{try{var c=api.LoadModConfig<PostConfig>("rustypost.json")??throw new InvalidDataException("Missing config");c.Validate();Config=c;return TextCommandResult.Success("Rusty Post configuration reloaded. Deposited goods keep their quoted base price.");}catch(Exception ex){return TextCommandResult.Error(ex.Message);}}).EndSubCommand();
    }
    internal void Save(){if(server!=null)server.WorldManager.SaveGame.StoreData(DataKey,Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(Data)));}
    internal static string BinKey(BlockPos pos)=>$"{pos.dimension}:{pos.X}:{pos.Y}:{pos.Z}";
    internal MailAccount Account(string uid){if(!Data.Mail.TryGetValue(uid,out var a))Data.Mail[uid]=a=new();return a;}
    internal StoredStack Pack(ItemStack s)=>new(){Code=s.Collectible.Code.ToString(),Bytes=s.ToBytes()};
    internal ItemStack Unpack(StoredStack stored)
    {
        var stack=new ItemStack(stored.Bytes);
        var c=stack.Class==EnumItemClass.Block?(CollectibleObject?)server.World.GetBlock(new AssetLocation(stored.Code)):server.World.GetItem(new AssetLocation(stored.Code));
        if(c==null)throw new InvalidOperationException("Item unavailable: "+stored.Code);
        stack.Id=c.Id;if(!stack.ResolveBlockOrItem(server.World))throw new InvalidOperationException("Cannot restore "+stored.Code);return stack;
    }
    internal static IEnumerable<ItemSlot> Slots(IPlayer player)
    {
        foreach(var name in new[]{"hotbar","backpack"})
        {
            var inv=player.InventoryManager.GetOwnInventory(name);if(inv==null)continue;
            foreach(var slot in inv)if(slot is not ItemSlotBackpack)yield return slot;
        }
    }
    internal void Transfer(IServerPlayer player,Action action)
    {
        string snapshot=JsonConvert.SerializeObject(Data);
        var slots=Slots(player).Distinct().Select(s=>(Slot:s,Stack:s.Itemstack?.Clone())).ToArray();
        try{action();foreach(var s in slots)s.Slot.MarkDirty();Save();}
        catch{Data=JsonConvert.DeserializeObject<PostData>(snapshot)!;foreach(var s in slots){s.Slot.Itemstack=s.Stack;try{s.Slot.MarkDirty();}catch{}}Save();throw;}
    }
    private bool PutInEmptySlot(IServerPlayer player,ItemStack stack)
    {
        var slot=Slots(player).FirstOrDefault(s=>s.Empty&&!s.Inventory.PutLocked&&s.CanHold(new DummySlot(stack))&&s.GetRemainingSlotSpace(stack)>=stack.StackSize);
        if(slot==null)return false;slot.Itemstack=stack;return true;
    }
    internal bool SalePrice(ItemStack stack,out long price)
    {
        price=0;
        // Containers cannot sell their contents indirectly, even with a price override.
        if(stack.Attributes.HasAttribute("contents")||stack.Attributes.HasAttribute("inventory")||stack.Collectible is Block b&&!string.IsNullOrEmpty(b.EntityClass)||stack.Collectible.Code.Path=="gear-rusty")return false;
        string key=Valuation.Key(stack.Collectible);
        if(Config.Prices.TryGetValue(key,out var custom)){price=custom.MilliGears;return custom.Enabled&&price>0;}
        if(!Catalog.TryGetValue(key,out var entry)||!entry.Sellable)return false;
        price=entry.MilliGears;return price>0;
    }
    internal double Condition(ItemStack stack)
    {
        double factor=1;
        if(stack.Collectible.GetMaxDurability(stack)>0)factor=Math.Clamp((double)stack.Collectible.GetRemainingDurability(stack)/stack.Collectible.GetMaxDurability(stack),0,1);
        var inv=new InventoryGeneric(1,"rustypost-valuation",server);inv[0].Itemstack=stack;
        var state=stack.Collectible.UpdateAndGetTransitionState(server.World,inv[0],EnumTransitionType.Perish);
        if(inv[0].Itemstack==null||inv[0].Itemstack!.Collectible!=stack.Collectible)return 0;
        if(state!=null)factor*=state.FreshHours>0?Math.Clamp(state.FreshHoursLeft/state.FreshHours,0,1):0;
        return factor;
    }
    internal string Deposit(IServerPlayer player,BinRecord bin)
    {
        if(bin.Items.Count>=Config.BinSlots)return "The shipping bin is full. Remove a stack first.";
        var slot=player.InventoryManager.ActiveHotbarSlot;
        if(slot.Empty||slot.Inventory.TakeLocked)return "Hold the stack you want to ship in your hotbar.";
        var stack=slot.Itemstack.Clone();
        if(!SalePrice(stack,out long price))return "This item has no enabled shipping price. Your items were kept.";
        if(Condition(stack)<=0)return "This item is spoiled or worn out and has no sale value.";
        Transfer(player,()=>{bin.Items.Add(new(){Stack=Pack(stack),Key=Valuation.Key(stack.Collectible),UnitMilliGears=price,DueDay=PostMath.NextCollection(server.World.Calendar.TotalDays,Config.CollectionHour)});slot.Itemstack=null;});
        return "Stack deposited. Food value may fall before collection. You can withdraw it until collection.";
    }
    internal void Collect()
    {
        long day=PostMath.CurrentCollection(server.World.Calendar.TotalDays,Config.CollectionHour);
        var due=Data.Bins.Values.SelectMany(b=>b.Items.Where(s=>s.DueDay<=day).Select(s=>(Bin:b,Shipment:s))).OrderBy(x=>x.Shipment.DueDay).ToArray();
        if(due.Length==0)return;
        string snapshot=JsonConvert.SerializeObject(Data);
        try
        {
            foreach(var (bin,shipment) in due)
            {
                ItemStack stack;try{stack=Unpack(shipment.Stack);}catch{continue;}
                var mail=Account(bin.Owner);
                if(shipment.DueDay>mail.LimitDay){mail.LimitDay=shipment.DueDay;mail.SoldMilliGears=0;mail.SoldItems.Clear();}
                double condition=Condition(stack);
                long unit=PostMath.Value(shipment.UnitMilliGears,1,condition);
                int count=unit<=0?0:Math.Min(stack.StackSize,Math.Min(Math.Max(0,Config.DailyItemLimit-mail.SoldItems.GetValueOrDefault(shipment.Key)),(int)Math.Min(int.MaxValue,Math.Max(0,Config.DailyGearLimit*1000L-mail.SoldMilliGears)/unit)));
                long value=checked(unit*count);
                mail.MilliGears=checked(mail.MilliGears+value);mail.SoldMilliGears+=value;mail.SoldItems[shipment.Key]=mail.SoldItems.GetValueOrDefault(shipment.Key)+count;
                mail.Receipts.Insert(0,$"Day {shipment.DueDay}: {count} x {stack.GetName()} - {PostMath.Gears(value)} gears"+(count<stack.StackSize?". Unsold remainder returned.":"."));
                if(mail.Receipts.Count>100)mail.Receipts.RemoveRange(100,mail.Receipts.Count-100);
                if(count<stack.StackSize){stack.StackSize-=count;mail.Returns.Add(Pack(stack));}
                bin.Items.Remove(shipment);
            }
            Save();
        }
        catch(Exception ex){Data=JsonConvert.DeserializeObject<PostData>(snapshot)!;server.Logger.Error("[Rusty Post] Collection rolled back: {0}",ex);}
    }
    internal string Claim(IServerPlayer player)
    {
        var mail=Account(player.PlayerUID);long gears=mail.MilliGears/1000;
        if(gears<1)return "No whole gears yet. Fractions carry forward to your next shipment.";
        var item=server.World.GetItem(new AssetLocation("game:gear-rusty"))??throw new InvalidOperationException("Rusty gears unavailable");int count=(int)Math.Min(gears,item.MaxStackSize);
        string result="Free one inventory slot for payment.";
        Transfer(player,()=>{if(PutInEmptySlot(player,new ItemStack(item,count))){mail.MilliGears-=count*1000L;result=$"Collected {count} rusty gears. Any remaining balance is still in your mailbox.";}});return result;
    }
    internal void Removed(PostEntity entity)
    {
        if(!Data.Bins.Remove(BinKey(entity.Pos),out var bin))return;
        var mail=Account(bin.Owner);mail.Returns.AddRange(bin.Items.Select(s=>s.Stack));Save();
    }
    private void Handle(IServerPlayer player,PostRequest r)
    {
        if(!ready||r==null||r.Action==null||r.Action.Length>20||r.Entry==null||r.Entry.Length>64)return;
        long now=server.World.ElapsedMilliseconds;if(requests.TryGetValue(player.PlayerUID,out long last)&&now-last<200)return;requests[player.PlayerUID]=now;
        var pos=new BlockPos(r.X,r.Y,r.Z,r.Dimension);
        if(!player.Entity.Alive||player.Entity.Pos.Dimension!=r.Dimension||player.Entity.Pos.SquareDistanceTo(pos.X+0.5,pos.Y+0.5,pos.Z+0.5)>36)return;
        if(server.World.BlockAccessor.GetBlockEntity(pos) is not PostEntity be||be.Owner!=player.PlayerUID&&!player.HasPrivilege(Privilege.controlserver))return;
        if(!server.World.Claims.TryAccess(player,pos,EnumBlockAccessFlags.Use))return;
        string message="";
        try
        {
            if(be.Owner!=player.PlayerUID&&r.Action!="open")message="Admin inspection only. Only the owner can move items or collect payment.";
            else if(be.Block.Code.Path.StartsWith("mailbox"))
            {
                if(r.Action=="claim")message=Claim(player);
                else if(r.Action=="return")
                {
                    var mail=Account(player.PlayerUID);
                    if(!int.TryParse(r.Entry,out int index)||index<0||index>=mail.Returns.Count)return;
                    var stack=Unpack(mail.Returns[index]);
                    message="Free one inventory slot for the returned stack.";
                    Transfer(player,()=>{if(PutInEmptySlot(player,stack)){mail.Returns.RemoveAt(index);message="Returned items collected.";}});
                }
            }
            else
            {
                if(!Data.Bins.TryGetValue(BinKey(pos),out var bin))Data.Bins[BinKey(pos)]=bin=new(){Owner=be.Owner};
                if(r.Action=="deposit")message=Deposit(player,bin);
                else if(r.Action=="withdraw")
                {
                    var shipment=bin.Items.FirstOrDefault(s=>s.Id==r.Entry);if(shipment==null)return;
                    var stack=Unpack(shipment.Stack);message="Free one inventory slot for this stack.";
                    Transfer(player,()=>{if(PutInEmptySlot(player,stack)){bin.Items.Remove(shipment);message="Stack withdrawn.";}});
                }
            }
        }
        catch(Exception ex){server.Logger.Error("[Rusty Post] Request failed: {0}",ex);message="Could not complete that action. Check the server log.";}
        channel.SendPacket(new PostReply{Json=JsonConvert.SerializeObject(View(be,r,message))},player);
    }
    internal PostView View(PostEntity be,PostRequest position,string message)
    {
        var v=new PostView {Position=position,Mailbox=be.Block.Code.Path.StartsWith("mailbox"),Message=message};
        if(v.Mailbox)
        {
            var mail=Account(be.Owner);v.Summary=$"Ready to collect: {mail.MilliGears/1000} gears\nFractional balance: {PostMath.Gears(mail.MilliGears%1000)} gears";v.Receipts=mail.Receipts.Take(12).ToList();
            for(int i=0;i<mail.Returns.Count;i++){string name;try{name=Unpack(mail.Returns[i]).GetName();}catch{name=mail.Returns[i].Code+" (missing mod)";}v.Rows.Add(new(){Id=i.ToString(),Text=name});}
        }
        else
        {
            var bin=Data.Bins.GetValueOrDefault(BinKey(be.Pos));long estimate=0;
            if(bin!=null)foreach(var s in bin.Items)
            {
                try{var stack=Unpack(s.Stack);long value=PostMath.Value(s.UnitMilliGears,1,Condition(stack))*stack.StackSize;estimate+=value;v.Rows.Add(new(){Id=s.Id,Text=$"{stack.StackSize} x {stack.GetName()} - {PostMath.Gears(value)} gears"});}
                catch{v.Rows.Add(new(){Id=s.Id,Text=s.Stack.Code+" (missing mod)"});}
            }
            v.Summary=$"Estimated payout: {PostMath.Gears(estimate)} gears\nCollection at {Config.CollectionHour:00}:00 each game day. Food value falls with freshness.\nLimits: {Config.DailyGearLimit} gears and {Config.DailyItemLimit} of each item per player per day.\nUnsold items go to your mailbox. Deposit the stack selected in your hotbar.";
        }
        return v;
    }
    public override void Dispose(){if(server!=null)server.Event.UnregisterGameTickListener(tick);dialog?.Dispose();base.Dispose();}
}
