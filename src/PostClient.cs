using Newtonsoft.Json;
using VSUX.Framework;
using VSUX.Framework.Api;
using VSUX.Framework.Components;
using VSUX.Framework.Composition;
using VSUX.Framework.Theming;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
namespace RustyPost;
public sealed partial class PostSystem
{
    private ICoreClientAPI? client;private IClientNetworkChannel? clientChannel;private PostDialog? dialog;
    private bool waiting;private long requestSerial;
    public override void StartClientSide(ICoreClientAPI api)
    {
        client=api;
        clientChannel=api.Network.RegisterChannel("rustypost").RegisterMessageType<PostRequest>().RegisterMessageType<PostReply>();
        clientChannel.SetMessageHandler<PostReply>(r=>api.Event.EnqueueMainThreadTask(()=>{
            waiting=false;var view=JsonConvert.DeserializeObject<PostView>(r.Json);if(view==null)return;
            dialog??=new PostDialog(api,this);dialog.Update(view);if(!dialog.IsOpened())dialog.TryOpen();
        },"rustypost-ui"));
    }
    internal void Open(BlockPos p)=>Send(new(){X=p.X,Y=p.Y,Z=p.Z,Dimension=p.dimension});
    internal void Send(PostRequest request)
    {
        if(waiting||clientChannel?.Connected!=true)return;
        waiting=true;long serial=++requestSerial;clientChannel.SendPacket(request);
        client?.Event.RegisterCallback(_=>{if(requestSerial==serial)waiting=false;},5000);
    }
}
public sealed class PostDialog : GuiDialog
{
    private readonly PostSystem system;private readonly IVsuxApi ux;
    private PostView view=new();private bool disposed,queued;private int page;private double scale=1;
    public PostDialog(ICoreClientAPI api,PostSystem system):base(api){this.system=system;ux=api.ModLoader.GetModSystem<VsuxFrameworkModSystem>().Api;ux.Themes.CurrentThemeChanged+=Theme;}
    public override string ToggleKeyCombinationCode=>null!;
    private void Theme(VsuxTheme _){if(IsOpened())Rebuild();}
    internal void Update(PostView v){if(v.Position.X!=view.Position.X||v.Position.Y!=view.Position.Y||v.Position.Z!=view.Position.Z)page=0;view=v;Compose();}
    private ElementBounds B(double x,double y,double w,double h)=>ElementBounds.Fixed(x*scale,y*scale,w*scale,h*scale);
    private void Rebuild(){if(queued||disposed)return;queued=true;capi.Event.EnqueueMainThreadTask(()=>{queued=false;if(!disposed)Compose();},"rustypost-compose");}
    private void Action(string action,string entry="")=>system.Send(new(){Action=action,Entry=entry,X=view.Position.X,Y=view.Position.Y,Z=view.Position.Z,Dimension=view.Position.Dimension});
    private void Compose()
    {
        SingleComposer?.Dispose();
        scale=Math.Max(0.4,Math.Min(1,Math.Min((capi.Render.FrameWidth/RuntimeEnv.GUIScale-70)/900d,(capi.Render.FrameHeight/RuntimeEnv.GUIScale-90)/700d)));
        var root=ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        var bg=ElementBounds.Fill.WithFixedPadding(16*scale);bg.BothSizing=ElementSizing.FitToChildren;
        var c=capi.Gui.CreateCompo("rustypost",root).AddVsux(ux,new VsuxWindow(bg)).AddDialogTitleBar(view.Mailbox?"Rusty Post - Your mailbox":"Rusty Post - Shipping bin",()=>TryClose()).BeginChildElements(bg);
        void Text(string s,double x,double y,double w,double h,VsuxTypographyRole role=VsuxTypographyRole.Body){if(!string.IsNullOrWhiteSpace(s))c.AddVsux(ux,new VsuxText(Ascii(s),B(x,y,w,h),CairoFont.WhiteSmallishText().WithFontSize((float)(16*scale))){TypographyRole=role});}
        void Button(string label,System.Action action,double x,double y,double w,bool primary=false)=>c.AddVsux(ux,new VsuxButton(label,B(x,y,w,38),()=>{action();return true;},variant:primary?VsuxButtonVariant.Primary:VsuxButtonVariant.Neutral));
        Text(view.Summary,0,45,900,126,VsuxTypographyRole.Accent);
        Button(view.Mailbox?"Collect payment":"Ship held stack",()=>Action(view.Mailbox?"claim":"deposit"),0,185,270,true);
        Button("Refresh",()=>Action("open"),290,185,160);
        if(!view.Mailbox)Text("Choose a hotbar stack, then Ship held stack. Withdraw with its button below.",0,238,900,50,VsuxTypographyRole.Muted);
        else Text("Returned goods are kept here when a bin is broken or a sale hits a limit.",0,238,900,50,VsuxTypographyRole.Muted);
        page=Math.Clamp(page,0,Math.Max(0,(view.Rows.Count-1)/6));
        for(int i=0;i<Math.Min(6,view.Rows.Count-page*6);i++)
        {
            var row=view.Rows[page*6+i];Text(row.Text,8,302+i*44,690,40);
            Button(view.Mailbox?"Collect items":"Withdraw",()=>Action(view.Mailbox?"return":"withdraw",row.Id),714,298+i*44,186);
        }
        if(view.Rows.Count==0)Text(view.Mailbox?"No returned goods waiting.":"Your bin is empty. Hold an item to get started.",8,302,870,45,VsuxTypographyRole.Muted);
        Button("Previous",()=>{page=Math.Max(0,page-1);Rebuild();},0,572,145);
        Button("Next",()=>{page++;Rebuild();},165,572,145);
        Text($"Page {page+1} / {Math.Max(1,(view.Rows.Count+5)/6)}",340,578,540,30,VsuxTypographyRole.Muted);
        if(view.Mailbox&&view.Receipts.Count>0)Text("Latest sale: "+view.Receipts[0],0,622,900,40,VsuxTypographyRole.Muted);
        Text(view.Message,0,666,900,34,VsuxTypographyRole.Muted);
        SingleComposer=c.EndChildElements().Compose(false);
    }
    private static string Ascii(string value)=>new(value.Select(c=>c=='\n'||c>=32&&c<=126?c:'?').ToArray());
    public override void Dispose(){disposed=true;ux.Themes.CurrentThemeChanged-=Theme;base.Dispose();}
}
