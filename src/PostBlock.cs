using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
namespace RustyPost;
public sealed class PostBlock : Block
{
    public override bool TryPlaceBlock(IWorldAccessor world,IPlayer byPlayer,ItemStack itemstack,BlockSelection selection,ref string failureCode)
    {
        if(!base.TryPlaceBlock(world,byPlayer,itemstack,selection,ref failureCode))return false;
        if(world.BlockAccessor.GetBlockEntity(selection.Position) is PostEntity be){be.Owner=byPlayer.PlayerUID;be.MarkDirty();}
        return true;
    }
    public override bool OnBlockInteractStart(IWorldAccessor world,IPlayer byPlayer,BlockSelection selection)
    {
        if(world.Api is ICoreClientAPI client)client.ModLoader.GetModSystem<PostSystem>().Open(selection.Position);
        return true;
    }
    public override string GetPlacedBlockInfo(IWorldAccessor world,BlockPos pos,IPlayer player)
    {
        var be=world.BlockAccessor.GetBlockEntity(pos) as PostEntity;
        return (Code.Path.StartsWith("mailbox")?"Personal mailbox":"Shipping bin")+"\n"+(be?.Owner==player.PlayerUID?"Yours - right-click to open":"Owned by another player");
    }
}
public sealed class PostEntity : BlockEntity
{
    public string Owner="";
    public override void ToTreeAttributes(ITreeAttribute tree){base.ToTreeAttributes(tree);tree.SetString("owner",Owner);}
    public override void FromTreeAttributes(ITreeAttribute tree,IWorldAccessor world){base.FromTreeAttributes(tree,world);Owner=tree.GetString("owner","");}
    public override void OnBlockRemoved(){if(Api?.Side==EnumAppSide.Server)Api.ModLoader.GetModSystem<PostSystem>().Removed(this);base.OnBlockRemoved();}
}
