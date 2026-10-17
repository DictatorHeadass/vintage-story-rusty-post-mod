using System.Text;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
namespace RustyPost;
public static class Valuation
{
    public static string Key(CollectibleObject c)=>(c is Block?"block:":"item:")+c.Code;
    public static Dictionary<string,ValueEntry> Generate(ICoreServerAPI api)
    {
        var entries=new Dictionary<string,ValueEntry>();
        foreach(var c in api.World.Collectibles.Where(c=>c?.Code!=null))entries[Key(c)]=Score(c,api);
        // Grid outputs use the cheapest resolved recipe to constrain bulk craft resale.
        for(int pass=0;pass<8;pass++)
        {
            bool changed=false;
            foreach(var recipe in api.World.GridRecipes)
            {
                var output=recipe.Output?.ResolvedItemStack;
                if(output==null||!entries.TryGetValue(Key(output.Collectible),out var target)||!target.Sellable)continue;
                var ingredients=recipe.ResolvedIngredients?.Where(i=>i!=null&&!i.IsTool).Select(i=>i!).ToArray();
                if(ingredients==null||ingredients.Length==0||ingredients.Any(i=>i.ResolvedItemStack==null))continue;
                long total=0;bool known=true;
                foreach(var i in ingredients)
                {
                    if(!entries.TryGetValue(Key(i.ResolvedItemStack!.Collectible),out var source)){known=false;break;}
                    total+=source.MilliGears*Math.Max(1,i.Quantity);
                }
                long cap=total/Math.Max(1,output.StackSize);
                if(known&&cap<target.MilliGears){target.MilliGears=cap;target.Evidence+="; grid-recipe resale cap";changed=true;}
            }
            if(!changed)break;
        }
        return entries;
    }
    private static ValueEntry Score(CollectibleObject c,ICoreServerAPI api)
    {
        string code=c.Code.Path;
        var e=new ValueEntry {Key=Key(c),Code=c.Code.ToString(),Name=new ItemStack(c).GetName(),Score=10,Category="Unclassified",Evidence="Base acquisition estimate; manual review required"};
        bool Has(params string[] parts)=>parts.Any(p=>code.Contains(p,StringComparison.Ordinal));
        if(Has("soil","sand","gravel","rock-","stone-","clay-","log-","leaves","stick","grass"))Set(2,"Common resources","Abundant terrain or gathered material");
        if(Has("plank","firewood","charcoal","peat","reed","flax","rope","linen","leather","resin","lime","salt","quartz"))Set(12,"Processed resources","Gathering, processing or growing effort");
        if(Has("coal-","sulfur","borax","cinnabar","alum","bauxite"))Set(24,"Minerals","Localized deposits and mining");
        if(c.NutritionProps!=null||Has("vegetable-","grain-","fruit-","berry-","mushroom-","bread-","meat-","fish-","egg"))Set(5,"Food","Renewable food production; freshness reduces payout");
        if(Has("bread-","pie-","cheese-","cured","jerky"))Set(18,"Prepared food","Additional cooking or preservation steps");
        if(Has("seed-"))Set(8,"Seeds","Crop propagation and seed acquisition");
        if(Has("copper"))Set(60,"Metal","Copper extraction and smelting");
        if(Has("tin","bismuth","zinc","lead")&&Has("ingot","ore-","nugget","metalbit"))Set(80,"Metal","Localized alloy metal deposits");
        if(Has("bronze"))Set(110,"Metal","Alloy sourcing and smelting");
        if(Has("iron"))Set(160,"Metal","Bloomery processing and forging");
        if(Has("silver","gold","electrum"))Set(260,"Precious metal","Scarcer deposits or alloy processing");
        if(Has("steel"))Set(350,"Metal","Advanced multi-stage steel production");
        if(Has("meteoriciron"))Set(450,"Metal","Limited meteorite deposits");
        if(Has("gem-","diamond","emerald","olivine","peridot"))Set(400,"Gemstones","Rare mineral discovery");
        if(Has("gear-temporal","temporalgear"))Set(800,"Rare objects","Rare loot or dangerous encounters");
        if(Has("anvil")){e.Score*=4;e.Evidence+="; substantial metal requirement";}
        if(Has("metalbit","nugget")){e.Score=Math.Max(1,e.Score/20);e.Evidence+="; small metal unit";}
        if(Has("ore-")){e.Score=Math.Max(2,e.Score/12);e.Evidence+="; raw ore unit, not a full ingot";}
        if(c.Tool!=null){e.Score=Math.Max(e.Score,30+Math.Max(0,c.ToolTier)*55);e.Category="Tools";e.Evidence+="; tool tier and crafting effort; durability reduces payout";e.Sellable=true;}
        if(c is Block b&&b.RequiredMiningTier>0){e.Score=Math.Max(e.Score,b.RequiredMiningTier*8);e.Evidence+="; mining tier "+b.RequiredMiningTier;}
        e.Score=Math.Clamp(e.Score,1,2000);e.MilliGears=e.Score*10L;
        bool hidden=c.Attributes?["handbook"]?["exclude"].AsBool(false)==true;
        bool container=c is Block containerBlock&&!string.IsNullOrEmpty(containerBlock.EntityClass);
        bool worldState=c is Block&&Has("crop-","sapling-","plant-","tallgrass","flower-","liquid","water","lava","fire","ice","snow","leaves","seashell","creature");
        if(hidden||container||worldState||(code=="air"||Has("creative","command","teleporter","portal","gear-rusty","gear-temporal","temporalgear","rot","compost","meal","crock","bucket","bowl","mold","mould","armorstand")))
        {e.Sellable=false;e.Evidence+="; excluded from default shipping (currency, stateful, hidden or unsuitable object)";}
        if(c.Code.Domain!="game"){e.Sellable=false;e.Confidence="unreviewed mod content";e.Evidence+="; modded item requires explicit admin price override";}
        return e;
        void Set(int score,string category,string evidence){e.Score=score;e.Category=category;e.Evidence=evidence;e.Sellable=true;}
    }
    public static void Export(string directory,Dictionary<string,ValueEntry> entries)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory,"value-catalog.json"),JsonConvert.SerializeObject(entries.Values.OrderBy(e=>e.Key),Formatting.Indented));
        string Csv(object value)=>"\""+value.ToString()!.Replace("\"","\"\"")+"\"";
        var csv=new StringBuilder("Key,Name,AcquisitionScore,GearsPerItem,DefaultSellable,Category,Confidence,Evidence\r\n");
        foreach(var e in entries.Values.OrderBy(e=>e.Key))csv.AppendLine(string.Join(",",new object[]{e.Key,e.Name,e.Score,PostMath.Gears(e.MilliGears),e.Sellable,e.Category,e.Confidence,e.Evidence}.Select(Csv)));
        File.WriteAllText(Path.Combine(directory,"value-catalog.csv"),csv.ToString());
    }
}
